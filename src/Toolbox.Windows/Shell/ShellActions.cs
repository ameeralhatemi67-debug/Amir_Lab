using System.Diagnostics;
using System.Runtime.InteropServices;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Windows.Shell;

/// <summary>Explorer integration for results: open, reveal (selecting several files) and recycle.</summary>
internal static partial class ShellActions
{
    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr ILCreateFromPathW(string path);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(IntPtr pidl);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, [In] IntPtr[]? apidl, uint flags);

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Open failed: {ex.Message}");
        }
    }

    /// <summary>Opens the containing folder with the given files selected (all must share a folder).</summary>
    public static void Reveal(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var folder = IOPath.GetDirectoryName(paths[0])!;
        var folderPidl = ILCreateFromPathW(folder);
        var items = paths.Where(p => string.Equals(IOPath.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase))
            .Select(ILCreateFromPathW).Where(p => p != IntPtr.Zero).ToArray();
        try
        {
            if (folderPidl == IntPtr.Zero || SHOpenFolderAndSelectItems(folderPidl, (uint)items.Length, items, 0) != 0)
                Process.Start("explorer.exe", $"/select,\"{paths[0]}\"");
        }
        finally
        {
            foreach (var p in items) ILFree(p);
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
        }
    }

    /// <summary>Moves files to the Recycle Bin (recoverable). Returns the number moved.</summary>
    public static int Recycle(IEnumerable<string> paths)
    {
        int moved = 0;
        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path)) continue;
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                moved++;
            }
            catch (Exception ex)
            {
                Log.Warn($"Recycle failed for an output: {ex.Message}");
            }
        }
        return moved;
    }
}

/// <summary>Brings our window to the front even though the drop happened while Explorer had focus.</summary>
internal static partial class Foreground
{
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
    private static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(IntPtr hwnd);

    public static void Activate(System.Windows.Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (SetForegroundWindow(hwnd))
        {
            window.Activate();
            return;
        }
        var foreground = GetForegroundWindow();
        uint foregroundThread = GetWindowThreadProcessId(foreground, out _);
        uint current = GetCurrentThreadId();
        if (foregroundThread != current) AttachThreadInput(current, foregroundThread, true);
        BringWindowToTop(hwnd);
        SetForegroundWindow(hwnd);
        if (foregroundThread != current) AttachThreadInput(current, foregroundThread, false);
        window.Activate();
    }
}
