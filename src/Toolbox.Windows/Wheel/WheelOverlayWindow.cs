using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Toolbox.Windows.Interop;

namespace Toolbox.Windows.Wheel;

/// <summary>
/// Transparent, topmost, never-activated window that hosts the wheel and acts as the OLE drop
/// target. It is created once and kept off-screen while idle so revealing it is instant.
/// </summary>
internal sealed class WheelOverlayWindow : Window
{
    private const double WheelMargin = 22;

    // Alpha 0 pixels of a layered window are click-through (and drop-through); alpha 1/255 is
    // invisible but makes Windows route the drag to us.
    private static readonly Brush CatchBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)));
    private static readonly Brush PassBrush = Freeze(new SolidColorBrush(Colors.Transparent));

    private readonly Grid _root;
    private readonly ScaleTransform _scale = new(1, 1);
    private IntPtr _hwnd;
    private int _animationGeneration;

    public WheelOverlayWindow(double diameter)
    {
        Title = "File Toolbox wheel";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Focusable = false;
        AllowDrop = true;
        SizeDip = diameter + WheelMargin * 2;
        Width = SizeDip;
        Height = SizeDip;
        Left = -32000;
        Top = -32000;

        Menu = new RadialMenu(diameter)
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _scale,
            IsHitTestVisible = true,
        };
        _root = new Grid { Background = PassBrush, AllowDrop = true };
        _root.Children.Add(Menu);
        Content = _root;
    }

    public RadialMenu Menu { get; }
    public double SizeDip { get; }
    public bool IsOnScreen { get; private set; }
    public IntPtr Handle => _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        style |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Makes the whole window rectangle a drop target (invisibly) or lets input pass through.</summary>
    public void SetCatching(bool catching) => _root.Background = catching ? CatchBrush : PassBrush;

    /// <summary>Centres the window on a screen point (physical pixels), kept inside that monitor's work area.</summary>
    public void MoveCenteredOn(Native.POINT point)
    {
        var monitor = ScreenGeometry.MonitorAt(point);
        PlaceOnce(point, monitor);
        // Moving between monitors with different scaling makes WPF resize the window during the
        // first move; place again with the final size so the wheel is centred exactly.
        if (Native.GetWindowRect(_hwnd, out var rect) && Math.Abs(rect.Width - SizeDip * monitor.Scale) > 1)
            PlaceOnce(point, monitor);
        IsOnScreen = true;
    }

    public void MoveOffscreen()
    {
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, -32000, -32000, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        IsOnScreen = false;
    }

    public void AnimateIn(bool reducedMotion)
    {
        _animationGeneration++;
        var duration = TimeSpan.FromMilliseconds(reducedMotion ? 90 : 170);
        Menu.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(reducedMotion ? 90 : 120)));
        if (reducedMotion)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            _scale.ScaleX = _scale.ScaleY = 1;
            return;
        }
        var ease = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.62, 1, duration) { EasingFunction = ease });
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.62, 1, duration) { EasingFunction = ease });
    }

    public void AnimateOut(bool reducedMotion, Action completed)
    {
        int generation = ++_animationGeneration;
        var duration = TimeSpan.FromMilliseconds(reducedMotion ? 60 : 110);
        var fade = new DoubleAnimation(0, duration);
        fade.Completed += (_, _) =>
        {
            if (generation == _animationGeneration) completed();
        };
        Menu.BeginAnimation(OpacityProperty, fade);
        if (!reducedMotion)
        {
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, duration));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, duration));
        }
    }

    /// <summary>Hides the wheel immediately without animation (used before a new probe).</summary>
    public void ResetVisual()
    {
        _animationGeneration++;
        Menu.IsHitTestVisible = true;
        Menu.BeginAnimation(OpacityProperty, null);
        Menu.Opacity = 0;
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _scale.ScaleX = _scale.ScaleY = 0.62;
    }

    /// <summary>Physical-pixel screen position of a point in wheel coordinates.</summary>
    public Point WheelToScreen(Point wheelPoint) => Menu.PointToScreen(wheelPoint);

    private void PlaceOnce(Native.POINT point, MonitorArea monitor)
    {
        int sizePx = (int)Math.Round(SizeDip * monitor.Scale);
        var (left, top) = ScreenGeometry.ClampCentered(point, sizePx, monitor.WorkArea);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, left, top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
