using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Toolbox.Core.Sound;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Sound;

/// <summary>
/// The small bar near the top of the screen after a volume hotkey ("Valorant ▬▬▬▬ 45%"), like
/// Windows' own volume bar. It never takes focus, so a game in front keeps the keyboard, and it fades
/// away a moment after the last press.
/// </summary>
internal sealed class LevelBar : Window
{
    private static LevelBar? _shown;

    private readonly Border _card = new();
    private readonly ContentControl _icon = new() { Width = 30, Height = 30, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = SoundViews.Text("", 12.5, weight: FontWeights.SemiBold);
    private readonly TextBlock _pct = SoundViews.Text("", 13.5, weight: FontWeights.Bold);
    private readonly Border _fill = new() { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly Grid _track = new() { Height = 6, Margin = new Thickness(0, 7, 0, 0) };
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromMilliseconds(1300) };
    private double _level;

    private LevelBar()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true; // small, so a layered window is cheap here
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        FlowDirection = Lang.Flow;

        var right = new StackPanel { Width = 210, VerticalAlignment = VerticalAlignment.Center };
        var top = new DockPanel();
        DockPanel.SetDock(_pct, Dock.Right);
        top.Children.Add(_pct);
        top.Children.Add(_name);
        right.Children.Add(top);
        var trackBack = new Border { CornerRadius = new CornerRadius(3) };
        trackBack.SetResourceReference(Border.BackgroundProperty, "Brush.LapTrack");
        _fill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        _track.Children.Add(trackBack);
        _track.Children.Add(_fill);
        _track.SizeChanged += (_, _) => _fill.Width = _track.ActualWidth * _level;
        right.Children.Add(_track);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(right);
        _card.Child = row;
        _card.Padding = new Thickness(14, 11, 16, 12);
        _card.CornerRadius = new CornerRadius(16);
        _card.BorderThickness = new Thickness(1);
        _card.Margin = new Thickness(16, 10, 16, 22);
        _card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        _card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        _card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Direction = 270, Opacity = ThemeResources.Lap.IsDark ? 0.5 : 0.18 };
        Content = _card;
        _hide.Tick += (_, _) =>
        {
            _hide.Stop();
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(Motion.IsReduced ? 1 : 260));
            fade.Completed += (_, _) => { if (!_hide.IsEnabled) Hide(); };
            BeginAnimation(OpacityProperty, fade);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureWindows.MakeNoActivate(new WindowInteropHelper(this).Handle);
    }

    /// <summary>Shows (or updates) the bar for an app, on the screen of the window in front.</summary>
    public static void Show(SoundApp app, IntPtr foreground)
    {
        _shown ??= new LevelBar();
        _shown.Present(app, foreground, null);
    }

    /// <summary>A short message instead of a level ("Valorant hasn't made a sound yet").</summary>
    public static void Say(string name, string? exePath, string message, IntPtr foreground)
    {
        _shown ??= new LevelBar();
        _shown.Present(new SoundApp("", name, exePath, false, false, 0, false, 0, 0), foreground, message);
    }

    /// <summary>The bar as it would show, not shown (tests and pictures).</summary>
    internal static LevelBar Preview(SoundApp app, string? message = null)
    {
        var bar = new LevelBar();
        bar.Fill(app, message);
        return bar;
    }

    private void Present(SoundApp app, IntPtr foreground, string? message)
    {
        Fill(app, message);
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
        if (!IsVisible)
        {
            Left = -32000;
            Top = -32000;
            base.Show();
        }
        UpdateLayout();
        Place(foreground);
        _hide.Stop();
        _hide.Start();
    }

    private void Fill(SoundApp app, string? message)
    {
        FlowDirection = Lang.Flow;
        _icon.Content = SoundViews.AppIcon(app.Key, SoundHub.NameOf(app), app.ExePath, app.IsSystem, 30);
        _name.Text = SoundHub.NameOf(app);
        _level = message is null && !app.Muted ? Math.Clamp(app.Volume, 0, 1) : 0;
        _pct.Text = message is null ? SoundViews.PercentText(app.Volume, app.Muted) : "";
        _pct.SetResourceReference(TextBlock.ForegroundProperty, app.Muted ? "Brush.Danger" : "Brush.Text");
        _track.Visibility = message is null ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) _name.Text = message;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _fill.Width = _track.ActualWidth * _level;
    }

    /// <summary>Top centre of the monitor that holds the window in front (or the pointer).</summary>
    private void Place(IntPtr foreground)
    {
        Native.POINT point;
        if (foreground != IntPtr.Zero && Native.GetWindowRect(foreground, out var r))
            point = new Native.POINT { X = (r.Left + r.Right) / 2, Y = (r.Top + r.Bottom) / 2 };
        else Native.GetCursorPos(out point);
        var monitor = ScreenGeometry.MonitorAt(point);
        var work = monitor.WorkArea;
        double scale = monitor.Scale <= 0 ? 1 : monitor.Scale;
        double width = ActualWidth * scale;
        int x = (int)(work.Left + (work.Right - work.Left - width) / 2);
        int y = monitor.Bounds.Top + (int)(36 * scale);
        Native.SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); // HWND_TOPMOST, SWP_NOSIZE | SWP_NOACTIVATE
    }
}
