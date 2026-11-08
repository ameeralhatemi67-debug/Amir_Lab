using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Toolbox.Core.Pulse;

namespace Toolbox.Windows.Pulse;

/// <summary>
/// The quick runs Claude Code starts: read what it sends on standard input, update the state file,
/// and for the status line print its text. Never fails Claude Code: errors end quietly with 0.
/// </summary>
internal static partial class PulseEntry
{
    public static int Run(string mode)
    {
        try
        {
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            var state = ClaudeStateStore.DefaultPath;
            if (mode == ClaudeCodeConnection.StatusArg)
            {
                using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
                var connection = new ClaudeCodeConnection(ClaudeCodeConnection.DefaultSettingsPath, Environment.ProcessPath ?? "", ClaudeCodeConnection.DefaultDataDir);
                return PulseBridge.StatusLine(input, output, state, connection.PreviousStatusCommand, RunPrevious, Report);
            }
            return PulseBridge.Event(input, state, SessionWindow, Report);
        }
        catch (Exception ex)
        {
            Report(ex); // whatever happens, Claude Code carries on
            return 0;
        }
    }

    /// <summary>Notes a problem in pulseridge.log (kept small) for troubleshooting.</summary>
    private static void Report(Exception ex)
    {
        try
        {
            var log = Path.Combine(ClaudeCodeConnection.DefaultDataDir, "bridge.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            if (File.Exists(log) && new FileInfo(log).Length > 64 * 1024) File.Delete(log);
            File.AppendAllText(log, $"{DateTimeOffset.Now:u} {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Runs the status line the user had before Pulse, with the same input, and returns what it
    /// printed. Claude Code on Windows runs commands with Git Bash when it is there, so that is tried
    /// first; otherwise cmd. Gives up after three seconds.
    /// </summary>
    private static string? RunPrevious(string command, string json)
    {
        string? bash = Environment.GetEnvironmentVariable("CLAUDE_CODE_GIT_BASH_PATH") is { Length: > 0 } b && File.Exists(b) ? b : FindOnPath("bash.exe");
        var info = bash is not null
            ? new ProcessStartInfo(bash) { ArgumentList = { "-c", command } }
            : new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", command } };
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            process.StandardInput.Write(json);
            process.StandardInput.Close();
            var text = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return text.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    // ---- the session's window ---------------------------------------------------------------

    private const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    private static partial int GetWindowTextLength(IntPtr hwnd);

    /// <summary>
    /// The window the session runs in: the nearest process above this one (through the shell and
    /// Claude Code) that has a visible main window, such as Windows Terminal, VS Code or the Claude app.
    /// </summary>
    private static long SessionWindow()
    {
        var parents = new Dictionary<uint, uint>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return 0;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32FirstW(snapshot, ref entry))
                do parents[entry.th32ProcessID] = entry.th32ParentProcessID;
                while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        var chain = new List<uint>();
        uint pid = (uint)Environment.ProcessId;
        for (int i = 0; i < 14 && parents.TryGetValue(pid, out var parent) && parent != 0 && !chain.Contains(parent); i++)
        {
            chain.Add(parent);
            pid = parent;
        }
        if (chain.Count == 0) return 0;

        var windows = new Dictionary<uint, IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, 4 /* GW_OWNER */) != IntPtr.Zero || GetWindowTextLength(hwnd) == 0) return true;
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (chain.Contains(owner) && !windows.ContainsKey(owner)) windows[owner] = hwnd;
            return true;
        }, IntPtr.Zero);
        foreach (var p in chain)
            if (windows.TryGetValue(p, out var hwnd)) return hwnd.ToInt64();
        return 0;
    }
}
