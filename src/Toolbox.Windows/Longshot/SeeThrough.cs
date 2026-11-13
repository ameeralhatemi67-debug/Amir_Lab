using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Toolbox.Windows.Longshot;

/// <summary>
/// See-through without a layered window. A WPF window with AllowsTransparency is "layered": Windows
/// copies its whole picture from the graphics card back to memory on every change, so a frame the
/// size of the capture area made dragging, resizing and its glow slow (owner's video, 13 Nov 2026;
/// docs/LESSONS.md). Here the window is an ordinary one whose see-through pixels are left to the
/// desktop compositor (its frame extended over the whole window, no backdrop), and its shape (what of
/// it is there at all, drawn and clickable) is a window region: the inside of a frame is a hole, so
/// clicks and the wheel go straight to the app below.
/// </summary>
internal static partial class SeeThrough
{
    private struct Margins { public int Left, Right, Top, Bottom; }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_DONOTROUND = 1, DWMSBT_NONE = 1;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [LibraryImport("dwmapi.dll")] private static partial int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
    [LibraryImport("dwmapi.dll")] private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [LibraryImport("dwmapi.dll")] private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    /// <summary>Call from OnSourceInitialized, before the window is first drawn.</summary>
    public static void Enable(Window window, IntPtr hwnd)
    {
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int none = DWMSBT_NONE, square = DWMWCP_DONOTROUND;
        uint noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));
    }
}

/// <summary>A window's shape in device pixels (from its top-left corner), built from rectangles and ellipses.</summary>
internal sealed partial class WindowShape : IDisposable
{
    private const int RGN_OR = 2, RGN_DIFF = 4;

    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);
    [LibraryImport("gdi32.dll")] private static partial int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("user32.dll")] private static partial int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    private IntPtr _region = CreateRectRgn(0, 0, 0, 0);

    /// <summary>For tests: the rectangles and ellipses added and cut, in order.</summary>
    internal List<(bool Add, bool Round, Rect Box)> Parts { get; } = [];

    public WindowShape Add(Rect r, double corner = 0) => Combine(true, corner > 0 ? CreateRoundRectRgn(L(r.Left), L(r.Top), L(r.Right) + 1, L(r.Bottom) + 1, L(corner * 2), L(corner * 2)) : CreateRectRgn(L(r.Left), L(r.Top), L(r.Right), L(r.Bottom)), r, false);

    public WindowShape Cut(Rect r) => Combine(false, CreateRectRgn(L(r.Left), L(r.Top), L(r.Right), L(r.Bottom)), r, false);

    public WindowShape AddEllipse(Rect r) => Combine(true, CreateEllipticRgn(L(r.Left), L(r.Top), L(r.Right) + 1, L(r.Bottom) + 1), r, true);

    public WindowShape CutEllipse(Rect r) => Combine(false, CreateEllipticRgn(L(r.Left), L(r.Top), L(r.Right) + 1, L(r.Bottom) + 1), r, true);

    private static int L(double v) => (int)Math.Round(v);

    private WindowShape Combine(bool add, IntPtr part, Rect r, bool round)
    {
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) { DeleteObject(part); return this; }
        CombineRgn(_region, _region, part, add ? RGN_OR : RGN_DIFF);
        DeleteObject(part);
        Parts.Add((add, round, r));
        return this;
    }

    /// <summary>Gives the shape to the window (Windows owns the region from then on).</summary>
    public void ApplyTo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || _region == IntPtr.Zero) return;
        if (SetWindowRgn(hwnd, _region, true) != 0) _region = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_region != IntPtr.Zero) DeleteObject(_region);
        _region = IntPtr.Zero;
    }
}
