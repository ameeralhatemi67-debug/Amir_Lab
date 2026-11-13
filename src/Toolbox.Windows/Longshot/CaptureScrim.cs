using System.Runtime.InteropServices;
using System.Windows.Threading;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot;

/// <summary>
/// A dark see-through layer over every screen, with a hole where the capture is (or, while picking,
/// where the pointer is). It makes plain what will be in the shot. Clicks and the wheel go straight
/// through it, it never takes focus and it is left out of every screenshot. It is a plain Win32
/// window (a region and one alpha value, no drawing), so moving the hole costs nothing even across
/// several large monitors.
/// </summary>
internal sealed partial class CaptureScrim : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    private const uint LWA_ALPHA = 0x2;
    private const int RGN_DIFF = 4;
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    private const uint WM_NCHITTEST = 0x84, WM_MOUSEACTIVATE = 0x21;
    private const int HTTRANSPARENT = -1, MA_NOACTIVATE = 3;
    private const string ClassName = "AmirLap.LongshotScrim";

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRectRgn(int l, int t, int r, int b);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    [LibraryImport("gdi32.dll")]
    private static partial int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateSolidBrush(uint color);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string? name);

    private static readonly WndProc Proc = (hwnd, msg, wParam, lParam) => msg switch
    {
        WM_NCHITTEST => HTTRANSPARENT,
        WM_MOUSEACTIVATE => MA_NOACTIVATE,
        _ => DefWindowProc(hwnd, msg, wParam, lParam),
    };
    private static bool _registered;

    private readonly IntPtr _hwnd;
    private readonly PixelRect _bounds; // the whole virtual screen
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private double _alpha, _from, _to;
    private DateTime _fadeStart;
    private int _fadeMs;
    private PixelRect? _hole;

    public CaptureScrim()
    {
        _bounds = GdiScreenSource.VirtualScreen;
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = GetModuleHandle(null),
                hbrBackground = CreateSolidBrush(0x00140F0C), // #0C0F14 (COLORREF is 0x00BBGGRR)
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc); // fails harmlessly if it already exists
            _registered = true;
        }
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            ClassName, "Longshot dim", WS_POPUP, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;
        CaptureWindows.ExcludeFromCapture(_hwnd);
        SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);
        _fade.Tick += (_, _) => FadeStep();
    }

    /// <summary>How dark it is at full strength (0–255).</summary>
    public byte Strength { get; set; } = 96;

    /// <summary>Shows it (fading in) with a hole at <paramref name="hole"/>, or dims everything when null.</summary>
    public void Show(PixelRect? hole)
    {
        if (_hwnd == IntPtr.Zero) return;
        SetHole(hole);
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        FadeTo(Strength, 220);
    }

    public void Hide(int ms = 160) => FadeTo(0, ms, hideAtEnd: true);

    /// <summary>Moves the hole (rounded a little, like the frame). Null: no hole.</summary>
    public void SetHole(PixelRect? hole)
    {
        if (_hwnd == IntPtr.Zero || hole == _hole) return;
        _hole = hole;
        var region = CreateRectRgn(0, 0, _bounds.Width, _bounds.Height);
        if (hole is { IsEmpty: false } area)
        {
            double scale = Interop.ScreenGeometry.MonitorAt(area.Center.ToPoint()).Scale;
            int radius = (int)Math.Round(12 * scale);
            var h = area.Inflate((int)Math.Ceiling(4 * scale)); // its rounded corners stay outside the area
            var cut = CreateRoundRectRgn(h.X - _bounds.X, h.Y - _bounds.Y, h.Right - _bounds.X + 1, h.Bottom - _bounds.Y + 1, radius, radius);
            CombineRgn(region, region, cut, RGN_DIFF);
            DeleteObject(cut);
        }
        if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region); // on success Windows owns the region
    }

    private bool _hideAtEnd;

    private void FadeTo(double alpha, int ms, bool hideAtEnd = false)
    {
        if (_hwnd == IntPtr.Zero) return;
        _hideAtEnd = hideAtEnd;
        if (Motion.IsReduced || ms <= 0)
        {
            _fade.Stop();
            Apply(alpha);
            if (hideAtEnd) ShowWindow(_hwnd, SW_HIDE);
            return;
        }
        _from = _alpha;
        _to = alpha;
        _fadeMs = ms;
        _fadeStart = DateTime.UtcNow;
        _fade.Start();
    }

    private void FadeStep()
    {
        double t = Math.Clamp((DateTime.UtcNow - _fadeStart).TotalMilliseconds / _fadeMs, 0, 1);
        Apply(_from + (_to - _from) * Motion.Out.Ease(t));
        if (t < 1) return;
        _fade.Stop();
        if (_hideAtEnd) ShowWindow(_hwnd, SW_HIDE);
    }

    private void Apply(double alpha)
    {
        _alpha = alpha;
        SetLayeredWindowAttributes(_hwnd, 0, (byte)Math.Clamp(Math.Round(alpha), 0, 255), LWA_ALPHA);
    }

    public void Dispose()
    {
        _fade.Stop();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
