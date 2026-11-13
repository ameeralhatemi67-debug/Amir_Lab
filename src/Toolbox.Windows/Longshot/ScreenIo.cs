using System.Runtime.InteropServices;
using Toolbox.Processing.Capture;

namespace Toolbox.Windows.Longshot;

/// <summary>A rectangle on the screen in physical pixels (virtual-screen coordinates).</summary>
internal readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;

    public bool Overlaps(PixelRect o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public bool Contains(PixelPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public PixelPoint Center => new(CenterX, CenterY);

    public PixelRect Inflate(int by) => new(X - by, Y - by, Width + 2 * by, Height + 2 * by);

    /// <summary>The part of this rectangle inside <paramref name="o"/> (empty when they don't meet).</summary>
    public PixelRect Intersect(PixelRect o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? new PixelRect(l, t, r - l, b - t) : default;
    }

    /// <summary>The point moved inside this rectangle, at least <paramref name="margin"/> px from its edges where it fits.</summary>
    public PixelPoint Clamp(PixelPoint p, int margin = 0)
    {
        int mx = Math.Min(margin, Width / 2), my = Math.Min(margin, Height / 2);
        return new(Math.Clamp(p.X, X + mx, Math.Max(X + mx, Right - 1 - mx)), Math.Clamp(p.Y, Y + my, Math.Max(Y + my, Bottom - 1 - my)));
    }

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect From(Interop.Native.RECT r) => new(r.Left, r.Top, r.Width, r.Height);
}

/// <summary>A point on the screen in physical pixels.</summary>
internal readonly record struct PixelPoint(int X, int Y)
{
    public static PixelPoint From(Interop.Native.POINT p) => new(p.X, p.Y);

    public Interop.Native.POINT ToPoint() => new() { X = X, Y = Y };
}

/// <summary>Where frames come from (the real screen, or a simulated page in tests).</summary>
internal interface IScreenSource : IDisposable
{
    Frame Grab();
}

/// <summary>What scrolls the content (the mouse wheel, or a simulation in tests).</summary>
internal interface IScroller
{
    /// <param name="at">Where on the screen to turn the wheel: what is under it scrolls.</param>
    /// <param name="unit">Wheel delta per notch: 120 is one notch of a normal mouse wheel.</param>
    void Scroll(PixelPoint at, int notches, bool down, int unit);
}

/// <summary>
/// Copies a screen rectangle with GDI (BitBlt with CAPTUREBLT, so layered windows are included).
/// Windows marked "exclude from capture" (Longshot's own panel and frame) are left out by Windows.
/// </summary>
internal sealed partial class GdiScreenSource : IScreenSource
{
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [LibraryImport("user32.dll")] private static partial IntPtr GetDC(IntPtr hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [LibraryImport("user32.dll")] private static partial int GetSystemMetrics(int index);

    private readonly PixelRect _rect;
    private readonly IntPtr _screen, _memory, _bitmap, _old, _bits;

    public GdiScreenSource(PixelRect rect)
    {
        _rect = rect;
        _screen = GetDC(IntPtr.Zero);
        _memory = CreateCompatibleDC(_screen);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = rect.Width, biHeight = -rect.Height, // top-down rows
            biPlanes = 1, biBitCount = 32,
        };
        _bitmap = CreateDIBSection(_memory, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Could not create a capture buffer.");
        _old = SelectObject(_memory, _bitmap);
    }

    public Frame Grab()
    {
        if (!BitBlt(_memory, 0, 0, _rect.Width, _rect.Height, _screen, _rect.X, _rect.Y, SRCCOPY | CAPTUREBLT))
            throw new InvalidOperationException("The screen could not be read.");
        var pixels = new byte[_rect.Width * _rect.Height * 4];
        Marshal.Copy(_bits, pixels, 0, pixels.Length);
        return new Frame(_rect.Width, _rect.Height, pixels);
    }

    /// <summary>The whole virtual screen (all monitors) and where it starts.</summary>
    public static (Frame Frame, PixelRect Bounds) CaptureVirtualScreen()
    {
        var bounds = VirtualScreen;
        using var source = new GdiScreenSource(bounds);
        return (source.Grab(), bounds);
    }

    public static PixelRect VirtualScreen => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public void Dispose()
    {
        SelectObject(_memory, _old);
        DeleteObject(_bitmap);
        DeleteDC(_memory);
        ReleaseDC(IntPtr.Zero, _screen);
    }
}

/// <summary>
/// Scrolls with real mouse-wheel input: the pointer is moved to the scroll point, the wheel turned,
/// and the pointer put back (so hover effects aren't captured). If one of Amir Lap's own windows is
/// at that point (the panel, a grip on the frame), the wheel would go to it, so a spot just beside
/// it is used instead.
/// </summary>
internal sealed partial class WheelScroller : IScroller
{
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(Interop.Native.POINT point);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    /// <summary>True when the window at the point belongs to Amir Lap (fully see-through parts don't count).</summary>
    internal static bool IsOurs(PixelPoint p)
    {
        var hwnd = WindowFromPoint(p.ToPoint());
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    /// <summary>The point, or the nearest spot around it that isn't covered by one of our windows.</summary>
    internal static PixelPoint Clear(PixelPoint at)
    {
        if (!IsOurs(at)) return at;
        foreach (int r in new[] { 28, 56, 90 })
            foreach (var (dx, dy) in new[] { (0, r), (0, -r), (r, 0), (-r, 0) })
            {
                var p = new PixelPoint(at.X + dx, at.Y + dy);
                if (!IsOurs(p)) return p;
            }
        return at;
    }

    public void Scroll(PixelPoint at, int notches, bool down, int unit)
    {
        Interop.Native.GetCursorPos(out var old);
        at = Clear(at);
        SetCursorPos(at.X, at.Y);
        Thread.Sleep(30);
        var input = new Interop.Native.INPUT[1];
        input[0].type = Interop.Native.INPUT_MOUSE;
        for (int i = 0; i < notches; i++)
        {
            input[0].u.mi = new Interop.Native.MOUSEINPUT { mouseData = unchecked((uint)(down ? -unit : unit)), dwFlags = MOUSEEVENTF_WHEEL };
            Interop.Native.SendInput(1, input, Marshal.SizeOf<Interop.Native.INPUT>());
            Thread.Sleep(12);
        }
        Thread.Sleep(60); // let the wheel reach the window under the box before the pointer leaves it
        SetCursorPos(old.X, old.Y);
    }
}

/// <summary>Window styles shared by Longshot's helper windows.</summary>
internal static partial class CaptureWindows
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    /// <summary>Leaves the window out of every screenshot (Longshot's own, and other apps').</summary>
    public static bool ExcludeFromCapture(IntPtr hwnd) =>
        Environment.GetEnvironmentVariable("AMIRLAP_SHOW_IN_CAPTURES") == "1" || SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);

    /// <summary>Never takes focus when clicked, and has no taskbar button (the panel and frame).</summary>
    public static void MakeNoActivate(IntPtr hwnd)
    {
        var style = (long)Interop.Native.GetWindowLongPtr(hwnd, Interop.Native.GWL_EXSTYLE);
        style |= Interop.Native.WS_EX_TOOLWINDOW | Interop.Native.WS_EX_NOACTIVATE;
        Interop.Native.SetWindowLongPtr(hwnd, Interop.Native.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Mouse clicks and the wheel pass through to the window below.</summary>
    public static void MakeClickThrough(IntPtr hwnd)
    {
        var style = (long)Interop.Native.GetWindowLongPtr(hwnd, Interop.Native.GWL_EXSTYLE);
        style |= WS_EX_TRANSPARENT | WS_EX_LAYERED | Interop.Native.WS_EX_TOOLWINDOW | Interop.Native.WS_EX_NOACTIVATE;
        Interop.Native.SetWindowLongPtr(hwnd, Interop.Native.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Moves and sizes a window in physical pixels, on top, without activating it.</summary>
    public static void Place(IntPtr hwnd, PixelRect r) =>
        Interop.Native.SetWindowPos(hwnd, Interop.Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Interop.Native.SWP_NOACTIVATE | Interop.Native.SWP_SHOWWINDOW);

    public static bool EscapePressed() => (Interop.Native.GetAsyncKeyState(Interop.Native.VK_ESCAPE) & 0x8000) != 0;
}
