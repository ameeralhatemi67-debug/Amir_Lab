using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Toolbox.Windows.Interop;

namespace Toolbox.Windows.Theme;

internal enum Backdrop
{
    /// <summary>Windows 11 acrylic (for windows the user works in).</summary>
    Acrylic,

    /// <summary>Opaque themed surface (for never-activated windows such as toasts, where acrylic would look inactive).</summary>
    Solid,
}

/// <summary>
/// Borderless themed window: acrylic or solid backdrop, rounded corners, dark-mode aware frame,
/// Escape to close, optional no-activate behaviour for notifications.
/// </summary>
internal class GlassWindow : Window
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_TRANSIENTWINDOW = 3;
    private const int DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    private bool _resizable;

    public GlassWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        ApplyChrome();
        SetResourceReference(ForegroundProperty, "Brush.Text");
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && CloseOnEscape)
            {
                e.Handled = true;
                OnEscape();
            }
        };
    }

    public Backdrop BackdropKind { get; set; } = Backdrop.Acrylic;

    /// <summary>Never take focus when shown or clicked (notifications).</summary>
    public bool NoActivate { get; set; }

    public bool Resizable
    {
        get => _resizable;
        set
        {
            _resizable = value;
            ResizeMode = value ? ResizeMode.CanResize : ResizeMode.NoResize;
            ApplyChrome();
        }
    }

    /// <summary>True when the acrylic backdrop is active.</summary>
    public bool HasBackdrop { get; private set; }

    protected bool CloseOnEscape { get; set; } = true;

    protected virtual void OnEscape() => Close();

    /// <summary>Lets the user move the window by dragging an element (typically the header).</summary>
    protected void MakeDragHandle(UIElement element)
    {
        element.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed && e.ClickCount == 1 && e.OriginalSource == element)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
            }
        };
    }

    private (Native.POINT Anchor, double OffsetDip)? _placement;

    /// <summary>
    /// Positions the window centred horizontally on a screen point (physical pixels), offset vertically,
    /// and kept fully inside that monitor's work area. Applied in physical pixels once the size is known.
    /// </summary>
    public void PlaceNear(Native.POINT anchor, double offsetYDip = 0)
    {
        _placement = (anchor, offsetYDip);
        if (IsLoaded) ApplyPlacement();
        else
        {
            Left = -32000;
            Top = -32000;
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        ApplyPlacement();
    }

    protected void ApplyPlacement()
    {
        if (_placement is not { } p) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var rect)) return;
        var monitor = ScreenGeometry.MonitorAt(p.Anchor);
        var (left, top) = ScreenGeometry.PlaceNear(p.Anchor, rect.Width, rect.Height, p.OffsetDip, monitor);
        Native.SetWindowPos(hwnd, IntPtr.Zero, left, top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (NoActivate)
        {
            var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE);
            style |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
        }

        var source = HwndSource.FromHwnd(hwnd);
        int darkValue = ThemeResources.Current.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));
        int corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        bool supportsBackdrop = Environment.OSVersion.Version.Build >= 22621;
        if (BackdropKind == Backdrop.Acrylic && supportsBackdrop && source?.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            HasBackdrop = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0;
        }
        else if (supportsBackdrop)
        {
            int none = DWMSBT_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }
        if (!HasBackdrop) SetResourceReference(BackgroundProperty, "Brush.SurfaceSolid");
    }

    private void ApplyChrome()
    {
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            GlassFrameThickness = new Thickness(-1),
            ResizeBorderThickness = new Thickness(_resizable ? 6 : 0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
    }
}
