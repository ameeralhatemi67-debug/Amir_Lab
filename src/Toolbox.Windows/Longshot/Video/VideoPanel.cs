using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Toolbox.Core.Settings;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot.Video;

/// <summary>What a recording covers.</summary>
internal enum VideoTarget { Window, Region, Screen }

/// <summary>Longshot's two modes, as one switch at the top of both panels.</summary>
internal static class ModeSwitch
{
    public static Border Build(bool video, Action<bool> chosen)
    {
        var bar = new Border { Style = SettingsPage.Res("Lap.SegmentBar"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
        var grid = new UniformGrid { Rows = 1 };
        string group = "longshot-mode-" + Guid.NewGuid().ToString("N");
        foreach (var (isVideo, icon, text, tip) in new[]
        {
            (false, "lap-longshot", Lang.T("Screenshot", "لقطة طويلة"), Lang.T("A long screenshot: a picture of a whole page, scrolled for you", "لقطة طويلة: صورة لصفحة كاملة تُمرَّر لك")),
            (true, "video", Lang.T("Video", "فيديو"), Lang.T("Record what happens in the area, as an MP4", "تسجيل ما يحدث في المنطقة كملف MP4")),
        })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(SettingsPage.Glyph(icon, 13, stroke: 1.9).With(g => { g.Margin = new Thickness(0, 0, 5, 0); g.FlowDirection = FlowDirection.LeftToRight; }));
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Style = SettingsPage.Res("Lap.Segment"), GroupName = group, Content = content, ToolTip = tip, IsChecked = isVideo == video, HorizontalContentAlignment = HorizontalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(rb, text);
            rb.Click += (_, _) => { if (isVideo != video) chosen(isVideo); };
            grid.Children.Add(rb);
        }
        bar.Child = grid;
        return bar;
    }
}

/// <summary>
/// Longshot's panel in Video mode, in the same glass as the screenshot panel: what to record
/// (window, region or the whole screen), how (30 or 60 fps, the pointer, click rings, a countdown),
/// the size, and a big Record button. It stays out of recordings and never takes focus.
/// </summary>
internal sealed class VideoPanel : Window
{
    private const double Shadow = 14, Radius = 22;
    private readonly ScreenBackdrop _backdrop = new();
    private readonly TextBlock _info = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 4, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 0), MinHeight = 34 };
    private readonly Dictionary<VideoTarget, RadioButton> _targets = [];
    private readonly Func<LongshotSettings> _settings;
    private readonly Action<Action<LongshotSettings>> _save;
    private IntPtr _hwnd;
    private PixelRect? _beside;
    private PixelRect? _area;

    public VideoPanel(Func<LongshotSettings> settings, Action<Action<LongshotSettings>> save, string? shortcut)
    {
        _settings = settings;
        _save = save;
        Title = "Longshot video";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.Height;
        Width = 316 + 2 * Shadow;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        SetResourceReference(ForegroundProperty, "Brush.Text");
        RecordButton = new Button { Style = SettingsPage.Res("Lap.Button.Accent"), Height = 40, HorizontalAlignment = HorizontalAlignment.Stretch };
        CloseButton = new Button { Style = SettingsPage.Res("Lap.Caption.Close"), Width = 34, Height = 30, Content = SettingsPage.Glyph("close", 12, stroke: 2.2), ToolTip = Lang.T("Cancel", "إلغاء") };
        Content = Build(shortcut);
        LocationChanged += (_, _) => RefreshBackdrop();
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(() => { PlaceAtSpot(); RefreshBackdrop(); }, DispatcherPriority.Render);
    }

    public Button RecordButton { get; }
    public Button CloseButton { get; }

    /// <summary>Window, Region or Screen was pressed.</summary>
    public event Action<VideoTarget>? TargetChosen;

    /// <summary>The Long screenshot mode was chosen.</summary>
    public event Action? ScreenshotChosen;

    /// <summary>For tests: keeps the panel off the screen.</summary>
    internal static bool KeepOffScreen { get; set; }

    internal string InfoText => _info.Text;

    internal string StatusText => _status.Text;

    /// <summary>For tests: which target is shown as chosen.</summary>
    internal VideoTarget? ShownTarget => _targets.FirstOrDefault(t => t.Value.IsChecked == true) is { Value: not null } t ? t.Key : null;

    /// <summary>For tests: the target buttons.</summary>
    internal RadioButton TargetButton(VideoTarget kind) => _targets[kind];

    public void SetTarget(VideoTarget kind)
    {
        foreach (var (k, b) in _targets) b.IsChecked = k == kind;
    }

    /// <summary>The size line: what will be recorded ("1,280 × 720 px · 30 fps").</summary>
    public void SetArea(PixelRect area)
    {
        _area = area;
        _info.Text = $"{area.Width & ~1:N0} × {area.Height & ~1:N0} px · {_settings().VideoFps} fps";
    }

    public void SetStatus(string text) => _status.Text = text;

    public void SetBusy(bool busy, string? text = null)
    {
        RecordButton.IsEnabled = !busy;
        if (text is not null) _status.Text = text;
    }

    public void PlaceBeside(PixelRect area)
    {
        _beside = area;
        if (IsLoaded) PlaceAtSpot();
    }

    private UIElement Build(string? shortcut)
    {
        var dark = ThemeResources.Lap.IsDark;
        var root = new Grid();
        var shadow = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = dark ? 0.6 : 0.25 } };
        shadow.SetResourceReference(Border.BackgroundProperty, "Brush.Content");
        root.Children.Add(shadow);
        root.Children.Add(_backdrop);
        var tint = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius) };
        tint.SetResourceReference(Border.BackgroundProperty, "Brush.Glass");
        root.Children.Add(tint);
        root.Children.Add(new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), Background = GlassTexture.Grain(dark), Opacity = dark ? 0.45 : 0.6, IsHitTestVisible = false });
        var rim = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        rim.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        root.Children.Add(rim);

        var stack = new StackPanel { Margin = new Thickness(Shadow + 16, Shadow + 12, Shadow + 16, Shadow + 14), FlowDirection = Lang.Flow };
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.SizeAll };
        DockPanel.SetDock(CloseButton, Dock.Right);
        head.Children.Add(CloseButton);
        var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 10, 0), Child = SettingsPage.Glyph("video", 17, "Brush.Accent").With(g => g.FlowDirection = FlowDirection.LeftToRight) };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        head.Children.Add(new TextBlock { Text = Lang.T("Longshot", "اللقطة الطويلة"), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is DependencyObject d && IsInside(d, CloseButton)) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        stack.Children.Add(head);
        stack.Children.Add(ModeSwitch.Build(video: true, v => { if (!v) ScreenshotChosen?.Invoke(); }));

        // What to record.
        var bar = new Border { Style = SettingsPage.Res("Lap.SegmentBar"), Margin = new Thickness(0, 0, 0, 12) };
        var segments = new UniformGrid { Rows = 1 };
        string group = "video-target-" + Guid.NewGuid().ToString("N");
        foreach (var (kind, icon, text, tip) in new[]
        {
            (VideoTarget.Window, "lap-window", Lang.T("Window", "نافذة"), Lang.T("The whole window in front", "النافذة الأمامية كاملة")),
            (VideoTarget.Region, "lap-region", Lang.T("Region", "تحديد"), Lang.T("Drag to draw the area to record", "اسحب لرسم المنطقة المراد تسجيلها")),
            (VideoTarget.Screen, "fullscreen", Lang.T("Screen", "الشاشة"), Lang.T("The whole screen the pointer is on", "الشاشة كاملة حيث المؤشر")),
        })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(SettingsPage.Glyph(icon, 13, stroke: 1.9).With(g => { g.Margin = new Thickness(0, 0, 4, 0); g.FlowDirection = FlowDirection.LeftToRight; }));
            content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Style = SettingsPage.Res("Lap.Segment"), GroupName = group, Content = content, ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(rb, text);
            rb.Click += (_, _) => TargetChosen?.Invoke(kind);
            _targets[kind] = rb;
            segments.Children.Add(rb);
        }
        bar.Child = segments;
        stack.Children.Add(bar);

        // How.
        var s = _settings();
        stack.Children.Add(Option(Lang.T("Frames a second", "إطارات في الثانية"), SettingsPage.Choice(["30", "60"], s.VideoFps == 60 ? "60" : "30", v => { _save(x => x.VideoFps = v == "60" ? 60 : 30); RefreshInfo(); })));
        stack.Children.Add(Option(Lang.T("Show the pointer", "إظهار المؤشر"), SettingsPage.Switch(s.VideoCursor, on => _save(x => x.VideoCursor = on))));
        stack.Children.Add(Option(Lang.T("Show clicks", "إظهار النقرات"), SettingsPage.Switch(s.VideoClicks, on => _save(x => x.VideoClicks = on))));
        stack.Children.Add(Option(Lang.T("Count 3, 2, 1 first", "العدّ 3، 2، 1 أولًا"), SettingsPage.Switch(s.VideoCountdown, on => _save(x => x.VideoCountdown = on))));

        stack.Children.Add(SettingsPage.Ltr(_info));
        _status.Style = SettingsPage.Res("Lap.Help");
        stack.Children.Add(_status);

        var record = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        record.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5) });
        record.Children.Add(new TextBlock { Text = Lang.T("Record", "تسجيل"), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        RecordButton.Content = record;
        RecordButton.Margin = new Thickness(0, 10, 0, 0);
        RecordButton.ToolTip = Lang.T("Start recording the framed area", "بدء تسجيل المنطقة المؤطّرة");
        stack.Children.Add(RecordButton);
        var hint = new TextBlock
        {
            Text = Lang.T("No sound is recorded yet. ", "لا يُسجَّل الصوت بعد. ") + (shortcut is { Length: > 0 } ? Lang.T($"{shortcut} starts recording, and again stops it.", $"⁦{shortcut}⁩ يبدأ التسجيل، ومرة أخرى يوقفه.") : ""),
            Style = SettingsPage.Res("Lap.Help"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 10, 0, 0),
        };
        stack.Children.Add(hint);
        root.Children.Add(stack);
        return root;
    }

    private void RefreshInfo()
    {
        if (_area is { } a) SetArea(a);
    }

    private static FrameworkElement Option(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        control.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 });
        return row;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeNoActivate(_hwnd);
        CaptureWindows.ExcludeFromCapture(_hwnd);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PlaceAtSpot();
        RefreshBackdrop();
    }

    private void RefreshBackdrop()
    {
        if (_hwnd != IntPtr.Zero && !KeepOffScreen) _backdrop.Refresh(_hwnd, Shadow, Radius);
    }

    private void PlaceAtSpot()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (KeepOffScreen) { Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); return; }
        if (_beside is not { } area || !Native.GetWindowRect(_hwnd, out var r)) return;
        var spot = CapturePanel.Spot(area, r.Width, r.Height);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, spot.X, spot.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private static bool IsInside(DependencyObject? d, DependencyObject container)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == container) return true;
        return false;
    }
}

/// <summary>
/// While recording: a small glass pill just outside the area with the time (a red dot breathing),
/// Pause/Resume, Stop and Discard. It stays out of the recording and never takes focus, so keys
/// keep going to the app being recorded.
/// </summary>
internal sealed class RecordingPill : Window
{
    private readonly TextBlock _time = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI") };
    private readonly Border _dot = new() { Width = 11, Height = 11, CornerRadius = new CornerRadius(5.5), Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private IntPtr _hwnd;
    private PixelRect _area;

    public RecordingPill()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000;
        Top = -32000;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        SetResourceReference(ForegroundProperty, "Brush.Text");
        PauseButton = Small("lap-stop", Lang.T("Pause", "إيقاف مؤقت"));
        StopButton = new Button { Style = SettingsPage.Res("Lap.Button.Accent"), Height = 32, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(6, 0, 0, 0), Content = Lang.T("Stop", "إيقاف"), ToolTip = Lang.T("Stop and save", "إيقاف وحفظ") };
        DiscardButton = Small("trash", Lang.T("Discard this recording", "حذف هذا التسجيل"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = Lang.Flow };
        row.Children.Add(_dot);
        row.Children.Add(SettingsPage.Ltr(_time));
        row.Children.Add(PauseButton);
        row.Children.Add(DiscardButton);
        row.Children.Add(StopButton);
        var card = new Border { Child = row, CornerRadius = new CornerRadius(22), Padding = new Thickness(14, 6, 6, 6), Margin = new Thickness(12), BorderThickness = new Thickness(1), Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.4 } };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        Content = card;
        if (!Motion.IsReduced)
            _dot.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        SizeChanged += (_, _) => Place();
    }

    public Button PauseButton { get; }
    public Button StopButton { get; }
    public Button DiscardButton { get; }

    internal static bool KeepOffScreen { get; set; }

    /// <summary>For tests: the time shown.</summary>
    internal string TimeText => _time.Text;

    private static Button Small(string icon, string tip)
    {
        var b = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 34, Height = 32, Content = SettingsPage.Glyph(icon, 14, stroke: 2), ToolTip = tip, Margin = new Thickness(6, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        return b;
    }

    public void SetTime(TimeSpan t, bool paused)
    {
        PauseButton.IsEnabled = StopButton.IsEnabled = true;
        DiscardButton.ToolTip = Lang.T("Discard this recording", "حذف هذا التسجيل");
        _time.Text = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        PauseButton.Content = SettingsPage.Glyph(paused ? "lap-play" : "pause", 14, stroke: 2);
        PauseButton.ToolTip = paused ? Lang.T("Resume", "متابعة") : Lang.T("Pause", "إيقاف مؤقت");
        _dot.Background = new SolidColorBrush(paused ? Color.FromRgb(0xF5, 0x9E, 0x0B) : Color.FromRgb(0xEF, 0x44, 0x44));
    }

    /// <summary>Before recording starts: "Starting in 3" (Discard goes back, nothing else to press yet).</summary>
    public void SetCountdown(int left)
    {
        _time.Text = Lang.T($"Starting in {left}", $"يبدأ بعد {left}");
        PauseButton.IsEnabled = StopButton.IsEnabled = false;
        DiscardButton.ToolTip = Lang.T("Don't record (back to the panel)", "عدم التسجيل (العودة إلى اللوحة)");
    }

    public void SetSaving()
    {
        _time.Text = Lang.T("Saving…", "جارٍ الحفظ…");
        PauseButton.IsEnabled = StopButton.IsEnabled = DiscardButton.IsEnabled = false;
    }

    public void PlaceNear(PixelRect area)
    {
        _area = area;
        Place();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeNoActivate(_hwnd);
        CaptureWindows.ExcludeFromCapture(_hwnd);
        Place();
    }

    /// <summary>Below the area, centred; above it when there is no room below; inside its bottom edge when it fills the screen.</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return;
        if (KeepOffScreen) { Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE); return; }
        var monitor = ScreenGeometry.MonitorAt(_area.Center.ToPoint());
        var work = PixelRect.From(monitor.WorkArea);
        int x = Math.Clamp(_area.CenterX - r.Width / 2, work.X, Math.Max(work.X, work.Right - r.Width));
        int y = _area.Bottom + 4;
        if (y + r.Height > work.Bottom) y = _area.Y - r.Height - 4;
        if (y < work.Y) y = _area.Bottom - r.Height - 8;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
