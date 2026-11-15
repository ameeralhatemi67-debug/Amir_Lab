using System.Runtime.InteropServices;
using Toolbox.Core.Sound;
using Toolbox.Windows.Audio;

namespace Toolbox.Windows.Sound;

/// <summary>
/// The app in front, for the volume hotkeys: the program of the foreground window, with Store
/// apps looked up behind their frame (ApplicationFrameHost draws the window, the app makes the sound).
/// </summary>
internal static partial class ForegroundApp
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int EnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr data);

    /// <summary>The foreground window and its program (key, path), or nothing when no app is in front.</summary>
    public static (IntPtr Window, string Key, string? Path, int ProcessId)? Find()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        GetWindowThreadProcessId(window, out uint pid);
        var path = ProcessNames.ImagePath((int)pid);
        if (path is not null && SoundApps.KeyOf(path) == "applicationframehost.exe")
        {
            uint inner = 0;
            EnumChildWindows(window, (child, _) =>
            {
                GetWindowThreadProcessId(child, out uint p);
                if (p != pid) { inner = p; return 0; }
                return 1;
            }, IntPtr.Zero);
            if (inner != 0)
            {
                pid = inner;
                path = ProcessNames.ImagePath((int)pid);
            }
        }
        if (path is null) return null;
        return (window, SoundApps.KeyOf(path), path, (int)pid);
    }

    /// <summary>
    /// The app among <paramref name="apps"/> that the foreground program is: the same program, or
    /// the one whose process made the window (some apps play from a helper process of their own).
    /// </summary>
    public static SoundApp? Match(IReadOnlyList<SoundApp> apps, string key, int processId) =>
        apps.FirstOrDefault(a => a.Key == key) ?? apps.FirstOrDefault(a => a.ProcessId == processId);
}
