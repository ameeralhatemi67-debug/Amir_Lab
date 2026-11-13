using System.Runtime.InteropServices;
using System.Windows.Automation;
using Toolbox.Core.Diagnostics;
using Toolbox.Windows.Interop;

namespace Toolbox.Windows.Longshot;

/// <summary>What a capture covers: a whole window, one scroll area inside it, or a region drawn by hand.</summary>
internal enum TargetKind { Window, ScrollArea, Region }

/// <summary>A capture target: the area, where the wheel turns, and what kind of thing it is.</summary>
internal readonly record struct CaptureTarget(TargetKind Kind, PixelRect Area, PixelPoint Anchor);

/// <summary>
/// Finds what is at a point on the screen: the top-level window (its visible frame, kept inside its
/// screen's work area), and the innermost area there that scrolls up and down, read from UI
/// Automation (what screen readers use; browsers, Electron apps, Office and most Windows apps
/// expose their scroll areas through it). Reading UI Automation can be slow or hang in a busy app,
/// so callers use <see cref="ScrollAreaAtAsync"/>, which gives up after a timeout.
/// </summary>
internal static partial class TargetFinder
{
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint GA_ROOT = 2;
    public const int MinWidth = 120, MinHeight = 80;

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Native.POINT point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(IntPtr hwnd, [Out] char[] name, int max);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Native.RECT value, int size);

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    /// <summary>The window in front, or null when it is the desktop, the taskbar, minimized or Amir Lap itself.</summary>
    public static (IntPtr Hwnd, PixelRect Rect)? ForegroundWindow() => Usable(GetForegroundWindow());

    /// <summary>The top-level window at a point (the same rules as <see cref="ForegroundWindow"/>).</summary>
    public static (IntPtr Hwnd, PixelRect Rect)? WindowAt(PixelPoint p)
    {
        var hwnd = WindowFromPoint(p.ToPoint());
        return hwnd == IntPtr.Zero ? null : Usable(GetAncestor(hwnd, GA_ROOT));
    }

    private static (IntPtr, PixelRect)? Usable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || IsIconic(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return null;
        var name = new char[64];
        int length = GetClassName(hwnd, name, name.Length);
        if (length > 0 && ShellClasses.Contains(new string(name, 0, length))) return null;
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var bounds, Marshal.SizeOf<Native.RECT>()) != 0) return null;
        var window = PixelRect.From(bounds);
        var work = PixelRect.From(ScreenGeometry.MonitorAt(new Native.POINT { X = window.CenterX, Y = window.CenterY }).WorkArea);
        var inside = window.Intersect(work);
        return inside.Width >= MinWidth && inside.Height >= MinHeight ? (hwnd, inside) : null;
    }

    /// <summary>The work area of the screen under a point (used when no window is in front).</summary>
    public static PixelRect WorkAreaAt(PixelPoint p) => PixelRect.From(ScreenGeometry.MonitorAt(p.ToPoint()).WorkArea);

    /// <summary>
    /// The innermost area at the point that can scroll up and down (what of it shows, cut by the page
    /// and window around it), or null (nothing there scrolls, the app doesn't say, or it is one of
    /// Amir Lap's own windows). Blocks while UI Automation answers: call it off the UI thread.
    /// </summary>
    public static PixelRect? ScrollAreaAt(PixelPoint p) => ScrollTargets.At(p)?.Area;

    /// <summary>
    /// <see cref="ScrollAreaAt"/> on a worker thread, giving up after <paramref name="timeout"/>.
    /// Browsers turn their accessibility tree on when first asked, so a first empty answer from one
    /// is asked again once.
    /// </summary>
    public static async Task<PixelRect?> ScrollAreaAtAsync(PixelPoint p, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        for (int tries = 0; tries < 2; tries++)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            var lookup = Task.Run(() => ScrollAreaAt(p));
            if (await Task.WhenAny(lookup, Task.Delay(left)).ConfigureAwait(true) != lookup) return null;
            if (lookup.Result is { } found) return found;
            if (tries == 0 && !IsBrowser(p)) return null;
            await Task.Delay(180).ConfigureAwait(true);
        }
        return null;
    }

    internal static bool IsBrowser(PixelPoint p)
    {
        if (WindowAt(p) is not { } window) return false;
        var name = new char[64];
        int length = GetClassName(window.Hwnd, name, name.Length);
        var cls = length > 0 ? new string(name, 0, length) : "";
        return cls.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal) || cls == "MozillaWindowClass";
    }
}
