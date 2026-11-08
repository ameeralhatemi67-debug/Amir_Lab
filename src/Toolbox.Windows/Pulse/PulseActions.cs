using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Windows.Pulse;

/// <summary>
/// Opens what a tile stands for: an installed app found by its Start-menu name (the same apps the
/// Start menu lists), or its website when the app is not installed. Opening a website is left to
/// the browser; Amir Lap itself never goes online.
/// </summary>
internal static partial class PulseLaunch
{
    private static List<(string Name, string Id)>? _apps;

    /// <summary>Start-menu apps (name and app id), read once.</summary>
    private static List<(string Name, string Id)> Apps()
    {
        if (_apps is not null) return _apps;
        var list = new List<(string, string)>();
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is not null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name, id = item.Path;
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) list.Add((name, id));
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or UnauthorizedAccessException)
        {
            Log.Warn($"Pulse could not list the installed apps: {ex.Message}");
        }
        return _apps = list;
    }

    /// <summary>An installed app's id: by exact name first, then by an id that starts with <paramref name="idPrefix"/>.</summary>
    public static string? FindApp(string name, string? idPrefix = null)
    {
        var apps = Apps();
        var byName = apps.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (byName.Id is not null) return byName.Id;
        if (idPrefix is not null)
        {
            var byId = apps.FirstOrDefault(a => a.Id.StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase));
            if (byId.Id is not null) return byId.Id;
        }
        return null;
    }

    public static void Claude() => AppOrSite(FindApp("Claude", "Claude_"), "https://claude.ai");

    public static void ChatGpt() => AppOrSite(FindApp("ChatGPT", "OpenAI.ChatGPT"), "https://chatgpt.com");

    /// <summary>Codex: its own app when installed (OpenAI ships Codex inside an "OpenAI.Codex" app), else its web page.</summary>
    public static void Codex() => AppOrSite(FindApp("Codex", "OpenAI.Codex"), "https://chatgpt.com/codex");

    public static void AppOrSite(string? appId, string site)
    {
        if (appId is not null) Open("explorer.exe", "shell:AppsFolder\\" + appId);
        else Open(site);
    }

    /// <summary>Opens a file, folder, program or web address the way Explorer would.</summary>
    public static bool Open(string target, string? arguments = null)
    {
        try
        {
            var info = new ProcessStartInfo(target) { UseShellExecute = true };
            if (arguments is not null) info.Arguments = arguments;
            Process.Start(info);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warn($"Pulse could not open {target}: {ex.Message}");
            return false;
        }
    }

    // ---- bring a session's window forward ---------------------------------------------------------

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr hwnd);

    /// <summary>Brings a window (a session's terminal) to the front. False when it no longer exists.</summary>
    public static bool Focus(long handle)
    {
        var hwnd = new IntPtr(handle);
        if (handle == 0 || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
        if (SetForegroundWindow(hwnd)) return true;
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint current = GetCurrentThreadId();
        if (foregroundThread != current) AttachThreadInput(current, foregroundThread, true);
        BringWindowToTop(hwnd);
        bool ok = SetForegroundWindow(hwnd);
        if (foregroundThread != current) AttachThreadInput(current, foregroundThread, false);
        return ok;
    }
}

/// <summary>Processor, memory and battery, read from Windows (for the PC status tile).</summary>
internal static partial class SystemStatus
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint Low, High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static ulong _lastIdle, _lastTotal;

    /// <summary>Share of processor time in use since the last call (0 on the first).</summary>
    public static double Cpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        ulong total = kernel.Value + user.Value; // kernel time includes idle time
        ulong dIdle = idle.Value - _lastIdle, dTotal = total - _lastTotal;
        bool first = _lastTotal == 0;
        _lastIdle = idle.Value;
        _lastTotal = total;
        return first || dTotal == 0 ? 0 : Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    public static double Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
    }

    /// <summary>Battery charge in percent and whether it is charging, or null on a PC without a battery.</summary>
    public static (int Percent, bool Charging)? Battery()
    {
        if (!GetSystemPowerStatus(out var s) || s.BatteryFlag == 128 || s.BatteryLifePercent > 100) return null;
        return (s.BatteryLifePercent, s.ACLineStatus == 1);
    }
}
