using System.Runtime.InteropServices;

namespace Toolbox.Windows.Longshot.Video;

/// <summary>Where recorded frames come from: the real screen, or made-up frames in tests.</summary>
internal interface IFrameGrabber : IDisposable
{
    int Width { get; }
    int Height { get; }

    /// <summary>Fills <paramref name="into"/> (Width × Height × 4 bytes, BGRx, rows top-down) with the current frame.</summary>
    void Grab(byte[] into);
}

/// <summary>
/// Copies one screen rectangle for a recording, with the pointer drawn in (BitBlt leaves it out)
/// and, when wanted, a ring that grows and fades where a mouse button was pressed. Longshot's own
/// windows (frame, pill, dim layer) are left out of captures by Windows.
/// </summary>
internal sealed partial class RegionGrabber : IFrameGrabber
{
    private const uint SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
    private const int CURSOR_SHOWING = 1, DI_NORMAL = 3, VK_LBUTTON = 1, VK_RBUTTON = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize, flags;
        public IntPtr hCursor;
        public Interop.Native.POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    [LibraryImport("user32.dll")] private static partial IntPtr GetDC(IntPtr hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateCompatibleDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] private static partial IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(IntPtr obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(IntPtr hdc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetCursorInfo(ref CURSORINFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int key);

    private readonly PixelRect _area;
    private readonly bool _cursor, _clicks;
    private readonly double _scale;
    private readonly IntPtr _screen, _memory, _bitmap, _old, _bits;
    private readonly Dictionary<IntPtr, (int X, int Y)> _hotspots = [];
    private readonly List<(int X, int Y, DateTime At)> _rings = [];
    private bool _wasDown;

    public RegionGrabber(PixelRect area, bool cursor, bool clicks, double scale = 1)
    {
        _area = area;
        _cursor = cursor;
        _clicks = clicks;
        _scale = scale;
        _screen = GetDC(IntPtr.Zero);
        _memory = CreateCompatibleDC(_screen);
        var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = area.Width, biHeight = -area.Height, biPlanes = 1, biBitCount = 32 };
        _bitmap = CreateDIBSection(_memory, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Could not create a recording buffer.");
        _old = SelectObject(_memory, _bitmap);
    }

    public int Width => _area.Width;
    public int Height => _area.Height;

    public void Grab(byte[] into)
    {
        BitBlt(_memory, 0, 0, _area.Width, _area.Height, _screen, _area.X, _area.Y, SRCCOPY | CAPTUREBLT);
        Interop.Native.POINT at = default;
        bool showing = false;
        if (_cursor || _clicks)
        {
            var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(ref info))
            {
                at = info.ptScreenPos;
                showing = (info.flags & CURSOR_SHOWING) != 0;
                if (_cursor && showing && info.hCursor != IntPtr.Zero)
                {
                    var (hx, hy) = Hotspot(info.hCursor);
                    DrawIconEx(_memory, at.X - hx - _area.X, at.Y - hy - _area.Y, info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                }
            }
        }
        Marshal.Copy(_bits, into, 0, _area.Width * _area.Height * 4);
        if (_clicks) DrawRings(into, at);
    }

    private (int X, int Y) Hotspot(IntPtr cursor)
    {
        if (_hotspots.TryGetValue(cursor, out var h)) return h;
        h = (0, 0);
        if (GetIconInfo(cursor, out var info))
        {
            h = (info.xHotspot, info.yHotspot);
            if (info.hbmMask != IntPtr.Zero) DeleteObject(info.hbmMask);
            if (info.hbmColor != IntPtr.Zero) DeleteObject(info.hbmColor);
        }
        _hotspots[cursor] = h;
        return h;
    }

    /// <summary>A ring where a button went down, growing and fading over 450 ms.</summary>
    private void DrawRings(byte[] px, Interop.Native.POINT at)
    {
        bool down = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        var now = DateTime.UtcNow;
        if (down && !_wasDown) _rings.Add((at.X - _area.X, at.Y - _area.Y, now));
        _wasDown = down;
        _rings.RemoveAll(r => (now - r.At).TotalMilliseconds > 450);
        foreach (var (x, y, when) in _rings)
        {
            double t = (now - when).TotalMilliseconds / 450.0;
            DrawRing(px, Width, Height, x, y, (10 + 18 * t) * _scale, 3 * _scale, (byte)(200 * (1 - t)));
        }
    }

    /// <summary>A yellow ring blended into BGRx pixels (also used by tests).</summary>
    internal static void DrawRing(byte[] px, int width, int height, int cx, int cy, double radius, double thickness, byte alpha)
    {
        int r = (int)Math.Ceiling(radius + thickness);
        for (int y = Math.Max(0, cy - r); y <= Math.Min(height - 1, cy + r); y++)
            for (int x = Math.Max(0, cx - r); x <= Math.Min(width - 1, cx + r); x++)
            {
                double d = Math.Abs(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) - radius);
                if (d > thickness) continue;
                double a = alpha / 255.0 * Math.Clamp(thickness - d, 0, 1);
                int i = (y * width + x) * 4;
                px[i] = (byte)(px[i] * (1 - a) + 0x00 * a);
                px[i + 1] = (byte)(px[i + 1] * (1 - a) + 0xD4 * a);
                px[i + 2] = (byte)(px[i + 2] * (1 - a) + 0xFF * a);
            }
    }

    public void Dispose()
    {
        SelectObject(_memory, _old);
        DeleteObject(_bitmap);
        DeleteDC(_memory);
        ReleaseDC(IntPtr.Zero, _screen);
    }
}
