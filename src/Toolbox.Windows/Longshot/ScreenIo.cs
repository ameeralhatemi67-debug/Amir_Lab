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
/// <para>
/// Only for apps that can't scroll an area by themselves (<see cref="IAreaScroller"/> comes first).
/// The pointer is borrowed for about a tenth of a second, so: never while a mouse button is held,
/// and a press meanwhile is held back (<see cref="PointerGuard"/>). Without that, a click on the
/// panel landing in that moment went to the page, and the pointer jumping back made it a drag that
/// selected the page's text (owner's video, 13 Nov 2026; docs/LESSONS.md).
/// </para>
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

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(IntPtr hwnd, [Out] char[] text, int max);

    /// <summary>
    /// True when the window at the point belongs to Amir Lap (holes in a window's shape don't count,
    /// nor does the ring that shows what is about to be picked: clicks there still pick).
    /// </summary>
    internal static bool IsOurs(PixelPoint p)
    {
        var hwnd = WindowFromPoint(p.ToPoint());
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != (uint)Environment.ProcessId) return false;
        var title = new char[32];
        int n = GetWindowText(hwnd, title, title.Length);
        return new string(title, 0, Math.Max(0, n)) != "Longshot pick";
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
        PointerGuard.WaitForButtonsUp(TimeSpan.FromSeconds(3));
        PointerGuard.Borrowing = true;
        try
        {
            Interop.Native.GetCursorPos(out var old);
            at = Clear(at);
            SetCursorPos(at.X, at.Y);
            Thread.Sleep(25);
            var input = new Interop.Native.INPUT[1];
            input[0].type = Interop.Native.INPUT_MOUSE;
            for (int i = 0; i < notches; i++)
            {
                input[0].u.mi = new Interop.Native.MOUSEINPUT { mouseData = unchecked((uint)(down ? -unit : unit)), dwFlags = MOUSEEVENTF_WHEEL };
                Interop.Native.SendInput(1, input, Marshal.SizeOf<Interop.Native.INPUT>());
                Thread.Sleep(12);
            }
            Thread.Sleep(50); // let the wheel reach the window under the box before the pointer leaves it
            SetCursorPos(old.X, old.Y);
        }
        finally
        {
            PointerGuard.Borrowing = false;
        }
    }
}

/// <summary>
/// While Longshot borrows the pointer to turn the wheel, mouse presses are held back (swallowed with
/// their release), so a click can't land on the page under the scroll point and become a drag that
/// selects its text. Installed on the UI thread for the length of a capture.
/// </summary>
internal static class PointerGuard
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;
    private const int VK_LBUTTON = 1, VK_RBUTTON = 2, VK_MBUTTON = 4;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    private static readonly HookProc Proc = OnMouse;
    private static IntPtr _hook;
    private static int _users;
    private static volatile bool _borrowing;
    private static bool _left, _right, _middle; // a press was held back: hold back its release too

    /// <summary>Set while the pointer is borrowed.</summary>
    public static bool Borrowing { get => _borrowing; set => _borrowing = value; }

    /// <summary>For tests: how many presses were held back.</summary>
    internal static int HeldBack { get; private set; }

    public static void Install()
    {
        if (_users++ > 0) return;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, Proc, Interop.Native.GetModuleHandle(null), 0);
    }

    public static void Uninstall()
    {
        if (_users == 0 || --_users > 0) return;
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _left = _right = _middle = false;
    }

    public static bool ButtonsDown() =>
        (Interop.Native.GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (Interop.Native.GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0 || (Interop.Native.GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

    /// <summary>Waits (up to <paramref name="limit"/>) until no mouse button is held: a drag in progress isn't interrupted.</summary>
    public static void WaitForButtonsUp(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (ButtonsDown() && DateTime.UtcNow < until) Thread.Sleep(15);
    }

    private static IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            switch ((int)wParam)
            {
                case WM_LBUTTONDOWN when _borrowing: _left = true; HeldBack++; return 1;
                case WM_RBUTTONDOWN when _borrowing: _right = true; HeldBack++; return 1;
                case WM_MBUTTONDOWN when _borrowing: _middle = true; HeldBack++; return 1;
                case WM_LBUTTONUP when _left: _left = false; return 1;
                case WM_RBUTTONUP when _right: _right = false; return 1;
                case WM_MBUTTONUP when _middle: _middle = false; return 1;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
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
