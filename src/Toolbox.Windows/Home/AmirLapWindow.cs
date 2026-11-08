using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Toolbox.Core;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Home;

/// <summary>
/// The Amir Lap window, in the glass design: a real resizable window (minimize, maximize, snap
/// layouts, resize from any edge) with the app's own title bar, a glass sidebar with a sliding
/// pill, and solid pages: Dashboard, one page per tool, General and About. White in light mode,
/// black in dark mode, English or Arabic (right-to-left).
/// </summary>
internal sealed partial class AmirLapWindow : GlassWindow
{
    public const string DashboardPage = "dashboard";
    public const string GeneralPage = "general";
    public const string AboutPage = "about";
    private const double TitleHeight = 54;
    private const string GitHubUrl = "https://github.com/ameeralhatemi67-debug/Amir_Lab";

    private readonly ToolboxHost _host;
    private readonly ToolRegistry _tools;
    private readonly Grid _root = new();
    private readonly Dictionary<string, RadioButton> _nav = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ellipse> _dots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IIdleMotion> _shellMotion = [];
    private readonly List<IIdleMotion> _pageMotion = [];
    private ScrollViewer _scroll = null!;
    private Grid _navHost = null!;
    private readonly TranslateTransform _pillMove = new();
    private Button _maxButton = null!;
    private ContentControl _maxGlyph = null!;
    private FrameworkElement? _themeGlyph;
    private UniformGrid? _toolGrid;
    private string _page = DashboardPage;
    private bool _motionRunning;
    private bool _maxHover;
    private bool _rebuilding;
    private WindowState _restoreState = WindowState.Normal;

    public AmirLapWindow(ToolboxHost host, ToolRegistry tools)
    {
        _host = host;
        _tools = tools;
        UseNativeFrame(TitleHeight);
        FollowsGlassSetting = true;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1180, work.Width * 0.92);
        Height = Math.Min(790, work.Height * 0.92);
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = _root;
        Rebuild();

        _tools.Changed += OnToolsChanged;
        Activated += (_, _) => UpdateMotion();
        Deactivated += (_, _) => UpdateMotion();
        StateChanged += (_, _) => OnStateChanged();
        Closed += (_, _) =>
        {
            _tools.Changed -= OnToolsChanged;
            StopMotion(_shellMotion);
            StopMotion(_pageMotion);
        };
    }

    public string CurrentPage => _page;

    /// <summary>Brings the window back from the taskbar, as it was (normal or maximized).</summary>
    public void RestoreFromMinimized()
    {
        if (WindowState == WindowState.Minimized) WindowState = _restoreState;
    }

    protected override void OnEscape()
    {
        if (ShortcutBox.Recording is { } box) box.CancelRecording();
        else Close();
    }

    public void ShowPage(string id)
    {
        if (!_nav.TryGetValue(id, out var item)) item = _nav[DashboardPage];
        if (item.IsChecked == true) Render(id);
        else item.IsChecked = true; // Checked → Render
    }

    /// <summary>
    /// Rebuilds everything (after a language, theme, accent or glass change), keeping the page and
    /// scroll position. <paramref name="spinThemeIcon"/> plays the title-bar icon's entrance.
    /// </summary>
    public void Rebuild(bool spinThemeIcon = false)
    {
        double offset = _scroll?.VerticalOffset ?? 0;
        StopMotion(_shellMotion);
        StopMotion(_pageMotion);
        _shellMotion.Clear();
        _nav.Clear();
        _dots.Clear();
        _root.Children.Clear();
        _root.FlowDirection = Lang.Flow;
        Title = Lang.T("Amir Lap", "أمير لاب");

        var tint = new Border();
        tint.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
        _root.Children.Add(tint);
        var ambient = new AmbientLight(ThemeResources.Lap) { Width = 520, HorizontalAlignment = HorizontalAlignment.Left };
        _shellMotion.Add(ambient);
        _root.Children.Add(ambient);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(BuildTitleBar());
        var body = BuildBody();
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        _root.Children.Add(layout);

        UpdateDots();
        _rebuilding = true;
        ShowPage(_page);
        _rebuilding = false;
        var scroll = _scroll!;
        if (offset > 0) Dispatcher.BeginInvoke(() => scroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        if (spinThemeIcon && _themeGlyph is not null) SpinIn(_themeGlyph);
        _motionRunning = false;
        UpdateMotion();
        OnStateChanged();
    }

    // ---- title bar ---------------------------------------------------------------------------

    private UIElement BuildTitleBar()
    {
        var bar = new DockPanel { Margin = new Thickness(18, 0, 8, 0), LastChildFill = false };

        var captions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var min = Caption("Lap.Caption", CaptionGlyph("M3,8 H13"), Lang.T("Minimize", "تصغير"));
        min.Click += (_, _) => WindowState = WindowState.Minimized;
        _maxGlyph = new ContentControl { Focusable = false, IsTabStop = false };
        _maxButton = Caption("Lap.Caption", _maxGlyph, "");
        _maxButton.Click += (_, _) => ToggleMaximize(); // keyboard and tests; the mouse goes through the snap-layout hook
        var close = Caption("Lap.Caption.Close", CaptionGlyph("M4,4 L12,12 M12,4 L4,12"), Lang.T("Close", "إغلاق"));
        close.Click += (_, _) => Close();
        // Windows puts these on the left in Arabic, so the row mirrors with the layout.
        captions.Children.Add(min);
        captions.Children.Add(_maxButton);
        captions.Children.Add(close);
        DockPanel.SetDock(captions, Dock.Right);
        bar.Children.Add(captions);

        var chips = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bool dark = ThemeResources.Current.IsDark;
        _themeGlyph = SettingsPage.Glyph(dark ? "lap-sun" : "lap-moon", 17, stroke: 1.8);
        var theme = Chip(_themeGlyph, Lang.T(dark ? "Switch to light" : "Switch to dark", dark ? "التبديل إلى الفاتح" : "التبديل إلى الداكن"));
        theme.Click += (_, _) => Change(s => s.Theme = ThemeResources.Current.IsDark ? "light" : "dark");
        var langText = new TextBlock { Text = Lang.IsArabic ? "EN" : "ع", FontSize = Lang.IsArabic ? 12.5 : 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, Lang.IsArabic ? 0 : -2, 0, 0) };
        var lang = Chip(langText, Lang.IsArabic ? "English" : "العربية");
        lang.Margin = new Thickness(6, 0, 0, 0);
        lang.Click += (_, _) => Change(s => s.Language = Lang.IsArabic ? "en" : "ar");
        chips.Children.Add(theme);
        chips.Children.Add(lang);
        DockPanel.SetDock(chips, Dock.Right);
        bar.Children.Add(chips);

        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = AppIcon.Render(52, ThemeResources.Lap.Accent), Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = Lang.T("Amir Lap", "أمير لاب"), Style = SettingsPage.Res("Lap.Text"), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(brand, Dock.Left);
        bar.Children.Add(brand);
        return bar;
    }

    private static Button Chip(object content, string name)
    {
        var b = new Button { Style = SettingsPage.Res("Lap.Chip"), Content = content, ToolTip = name };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        return b;
    }

    private static Button Caption(string style, object glyph, string name)
    {
        var b = new Button { Style = SettingsPage.Res(style), Content = glyph };
        if (name.Length > 0)
        {
            b.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(b, name);
        }
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        return b;
    }

    private static Path CaptionGlyph(string data, double thickness = 1.25)
    {
        var p = new Path { Data = Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, SnapsToDevicePixels = true };
        p.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding { Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty), RelativeSource = System.Windows.Data.RelativeSource.Self });
        return p;
    }

    private void SpinIn(FrameworkElement glyph)
    {
        var rotate = new RotateTransform(-90);
        var scale = new ScaleTransform(0.4, 0.4);
        glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        glyph.RenderTransform = new TransformGroup { Children = { scale, rotate } };
        Motion.FromTo(rotate, RotateTransform.AngleProperty, -90, 0, 520, Motion.Spring);
        Motion.FromTo(scale, ScaleTransform.ScaleXProperty, 0.4, 1, 520, Motion.Spring);
        Motion.FromTo(scale, ScaleTransform.ScaleYProperty, 0.4, 1, 520, Motion.Spring);
        Motion.FromTo(glyph, OpacityProperty, 0, 1, 260);
    }

    // ---- sidebar and content -------------------------------------------------------------------

    private UIElement BuildBody()
    {
        var body = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(BuildSidebar());

        _scroll = new ScrollViewer
        {
            Style = SettingsPage.Res("Lap.Scroll"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(32, 28, 32, 36),
            Focusable = false,
        };
        _scroll.SizeChanged += (_, _) => FitToolGrid();
        var content = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Child = _scroll };
        content.SetResourceReference(Border.BackgroundProperty, "Brush.Content");
        content.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        Grid.SetColumn(content, 2);
        body.Children.Add(content);
        return body;
    }

    private UIElement BuildSidebar()
    {
        var t = ThemeResources.Lap;
        var side = new Grid();
        var glass = new Border { CornerRadius = new CornerRadius(16) };
        glass.SetResourceReference(Border.BackgroundProperty, "Brush.Glass");
        side.Children.Add(glass);

        var glare = new GlareSweep(t.Glare, t.GlareSoft, 16);
        _shellMotion.Add(glare);
        side.Children.Add(glare);

        var rim = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        rim.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        side.Children.Add(rim);
        var top = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(1), IsHitTestVisible = false };
        top.SetResourceReference(Border.BorderBrushProperty, "Brush.RimTop");
        side.Children.Add(top);

        // Hover: the glass edge nearest the pointer catches the light.
        var edgeBrush = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, RadiusX = 150, RadiusY = 150, Center = new Point(118, 0), GradientOrigin = new Point(118, 0) };
        edgeBrush.GradientStops.Add(new GradientStop(t.EdgeLight, 0));
        edgeBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, t.EdgeLight.R, t.EdgeLight.G, t.EdgeLight.B), 0.7));
        var edge = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1.5), BorderBrush = edgeBrush, Opacity = 0, IsHitTestVisible = false };
        side.Children.Add(edge);
        side.MouseEnter += (_, _) => Motion.To(edge, OpacityProperty, t.GlassOff ? 0.6 : 1, 350);
        side.MouseLeave += (_, _) => Motion.To(edge, OpacityProperty, 0, 400);
        side.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(edge);
            edgeBrush.Center = p;
            edgeBrush.GradientOrigin = p;
        };

        var dock = new DockPanel { Margin = new Thickness(12), LastChildFill = true };
        var tip = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 12, 0, 0) };
        tip.SetResourceReference(Border.BackgroundProperty, "Brush.Pill");
        tip.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        var tipText = new StackPanel();
        tipText.Children.Add(new TextBlock { Text = Lang.T("Tip", "نصيحة"), Style = SettingsPage.Res("Lap.Text"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        var key = _host.Settings.Current.ActivationKey;
        tipText.Children.Add(new TextBlock { Text = Lang.T($"Drag any file and tap {key} to open the File Toolbox wheel.", $"اسحب أي ملف واضغط {key} لفتح عجلة أدوات الملفات."), Style = SettingsPage.Res("Lap.Help"), Margin = new Thickness(0) });
        tip.Child = tipText;
        DockPanel.SetDock(tip, Dock.Bottom);
        dock.Children.Add(tip);

        _navHost = new Grid();
        var pill = new Border { Height = 40, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(0, 1, 0, 0), RenderTransform = _pillMove, IsHitTestVisible = false };
        pill.SetResourceReference(Border.BackgroundProperty, "Brush.Pill");
        pill.SetResourceReference(Border.BorderBrushProperty, "Brush.RimTop");
        pill.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Color = t.Shadow, Opacity = t.IsDark ? 0.35 : 0.07 };
        var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(7, 0, 0, 0) };
        bar.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        pill.Child = bar;
        _navHost.Children.Add(pill);

        var list = new StackPanel();
        list.Children.Add(NavItem(DashboardPage, Lang.T("Dashboard", "لوحة التحكم"), "lap-dashboard", dot: false));
        list.Children.Add(GroupLabel(Lang.T("Tools", "الأدوات")));
        foreach (var tool in _tools.Tools) list.Children.Add(NavItem(tool.Id, tool.Name, tool.Icon, dot: true));
        list.Children.Add(GroupLabel(Lang.T("App", "التطبيق")));
        list.Children.Add(NavItem(GeneralPage, Lang.T("General", "عام"), "lap-general", dot: false));
        list.Children.Add(NavItem(AboutPage, Lang.T("About", "حول"), "lap-about", dot: false));
        _navHost.Children.Add(list);
        dock.Children.Add(_navHost);
        side.Children.Add(dock);
        return side;
    }

    private static TextBlock GroupLabel(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(18, 10, 18, 6) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        return t;
    }

    private RadioButton NavItem(string id, string text, string icon, bool dot)
    {
        var row = new DockPanel();
        var glyph = SettingsPage.Glyph(icon, 18);
        glyph.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        if (dot)
        {
            var e = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(e, Dock.Right);
            row.Children.Add(e);
            _dots[id] = e;
        }
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new RadioButton { Style = SettingsPage.Res("Lap.Nav"), GroupName = "amirlap-nav-" + GetHashCode(), Content = row };
        Fx.SetNudgeTarget(item, glyph);
        System.Windows.Automation.AutomationProperties.SetName(item, text);
        item.Checked += (_, _) =>
        {
            Render(id);
            MovePill(item, animate: IsVisible && !_rebuilding);
        };
        _nav[id] = item;
        return item;
    }

    private void MovePill(FrameworkElement item, bool animate)
    {
        void Move()
        {
            if (!item.IsArrangeValid || item.ActualHeight <= 0) return;
            double y = item.TranslatePoint(new Point(0, 0), _navHost).Y;
            Motion.To(_pillMove, TranslateTransform.YProperty, y, animate ? 550 : 0, Motion.Settle);
        }
        if (item.IsArrangeValid && item.ActualHeight > 0) Move();
        else Dispatcher.BeginInvoke(Move, DispatcherPriority.Loaded);
    }

    private void UpdateDots()
    {
        foreach (var tool in _tools.Tools)
        {
            if (!_dots.TryGetValue(tool.Id, out var dot)) continue;
            bool on = _tools.IsEnabled(tool);
            dot.SetResourceReference(Shape.FillProperty, on ? "Brush.Success" : "Brush.LapTrack");
            dot.ToolTip = on ? Lang.T($"{tool.Name} is on", $"{tool.Name} قيد التشغيل") : Lang.T($"{tool.Name} is off", $"{tool.Name} متوقفة");
        }
    }

    private void OnToolsChanged()
    {
        UpdateDots();
        if (_page == DashboardPage) Render(DashboardPage, keepScroll: true, animate: false);
    }

    private void Render(string id, bool keepScroll = false, bool animate = true)
    {
        _page = id;
        double offset = _scroll.VerticalOffset;
        StopMotion(_pageMotion);
        _pageMotion.Clear();
        _toolGrid = null;
        var page = new SettingsPage(_host);
        if (id == DashboardPage) BuildDashboard(page);
        else if (id == GeneralPage) BuildGeneral(page);
        else if (id == AboutPage) BuildAbout(page);
        else if (_tools.Find(id) is { } tool) BuildTool(page, tool);
        _scroll.Content = page.Root;
        FitToolGrid();
        if (keepScroll) _scroll.ScrollToVerticalOffset(offset);
        else _scroll.ScrollToTop();
        if (animate)
        {
            var (_, move) = Fx.Transforms(page.Root);
            Motion.FromTo(move, TranslateTransform.YProperty, 10, 0, 420);
            Motion.FromTo(page.Root, OpacityProperty, 0, 1, 420);
        }
        if (_motionRunning) foreach (var m in _pageMotion) m.SetRunning(true);
    }

    private void FitToolGrid()
    {
        if (_toolGrid is not null) _toolGrid.Columns = _scroll.ActualWidth is > 0 and < 720 ? 1 : 2;
    }

    // ---- pages -------------------------------------------------------------------------------

    private void BuildDashboard(SettingsPage page)
    {
        page.Title(Lang.T("Dashboard", "لوحة التحكم"), Lang.T("Your tools at a glance.", "أدواتك في لمحة."));

        _toolGrid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -16, -16) };
        foreach (var tool in _tools.Tools)
        {
            var card = ToolCard(tool);
            card.Margin = new Thickness(0, 0, 16, 16);
            _toolGrid.Children.Add(card);
        }
        page.Add(_toolGrid);

        page.Section(Lang.T("Shortcuts", "الاختصارات"));
        page.Card();
        var settings = page.Current;
        foreach (var tool in _tools.Tools)
        {
            bool on = _tools.IsEnabled(tool);
            string where = on ? tool.Name : Lang.T($"{tool.Name} (off)", $"{tool.Name} (متوقفة)");
            foreach (var command in tool.Commands)
            {
                var chord = _tools.ChordOf(tool, command);
                FrameworkElement right = (on, chord, _tools.StateOf(tool, command)) switch
                {
                    (_, null, _) => Muted(Lang.T("Not set", "غير معيّن")),
                    (false, { } c, _) => Faded(SettingsPage.Keys(c.Parts())),
                    (true, { } c, HotkeyState.Taken) => TakenShortcut(tool, command, c),
                    (true, { } c, HotkeyState.Invalid) => Warning(Lang.T($"{c} can't be used as a shortcut", $"لا يمكن استخدام {c} كاختصار")),
                    (true, { } c, _) => SettingsPage.Keys(c.Parts()),
                };
                page.Row(command.Label, right, where);
            }
            foreach (var gesture in tool.Gestures(settings))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(SettingsPage.Keys(gesture.Keys.Take(gesture.Keys.Count - 1)));
                line.Children.Add(SettingsPage.Badge(gesture.Keys[^1]).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
                page.Row(gesture.Title, on ? line : Faded(line), where);
            }
        }

        page.Section(Lang.T("Recent", "الأحدث"));
        page.Card();
        var recent = RecentItems();
        if (recent.Count == 0)
            page.Note(Lang.T("Nothing yet. Files you convert and long screenshots you take show up here.", "لا شيء بعد. ستظهر هنا الملفات التي تحوّلها واللقطات الطويلة التي تلتقطها."));
        foreach (var item in recent) page.Item(RecentRow(item));
    }

    private Grid ToolCard(ToolModule tool)
    {
        bool on = _tools.IsEnabled(tool);
        var stack = new StackPanel();

        var head = new DockPanel();
        var sw = SettingsPage.Switch(on, v => _tools.SetEnabled(tool, v));
        System.Windows.Automation.AutomationProperties.SetName(sw, Lang.T($"{tool.Name} on or off", $"تشغيل {tool.Name} أو إيقافها"));
        DockPanel.SetDock(sw, Dock.Right);
        head.Children.Add(sw);
        var tile = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 12, 0), Child = SettingsPage.Glyph(tool.Icon, 21, "Brush.Accent") };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        head.Children.Add(new TextBlock { Text = tool.Name, Style = SettingsPage.Res("Lap.Text"), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(head);

        var summary = new TextBlock { Text = tool.Summary, Style = SettingsPage.Res("Lap.Text"), FontSize = 13, Margin = new Thickness(0, 14, 0, 0), MinHeight = 38 };
        summary.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        stack.Children.Add(summary);

        UIElement preview = tool.Id switch
        {
            FileToolboxTool.ToolId => new WheelPreview(),
            PulseTool.ToolId => new Pulse.PulsePreview(132).With(p => p.Update(_host.Settings.Current.Pulse)),
            _ => new LongshotPreview(),
        };
        if (preview is IIdleMotion motion) _pageMotion.Add(motion);
        var stage = new Border { Height = 132, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(0, 14, 0, 0), Child = preview, Opacity = on ? 1 : 0.4, ClipToBounds = true };
        stage.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
        stage.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        stack.Children.Add(stage);

        var foot = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(SettingsPage.Button(Lang.T("Settings", "الإعدادات"), () => ShowPage(tool.Id), ghost: true));
        if (on)
        {
            var open = SettingsPage.Button(tool.Id switch { LongshotTool.ToolId => Lang.T("Capture", "التقاط"), PulseTool.ToolId => Lang.T("Show dock", "إظهار الشريط"), _ => Lang.T("Open", "فتح") }, tool.Open, accent: true);
            open.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(open);
        }
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        FrameworkElement? keys = null;
        if (tool.Commands.Count > 0 && _tools.ChordOf(tool, tool.Commands[0]) is { } chord)
            keys = SettingsPage.Keys(chord.Parts());
        else if (tool.Gestures(_host.Settings.Current) is [var gesture, ..])
        {
            // A tool without a shortcut shows how it is reached instead ("Shift" "tap").
            var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(SettingsPage.Keys(gesture.Keys.Take(gesture.Keys.Count - 1)));
            line.Children.Add(SettingsPage.Badge(gesture.Keys[^1]).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
            keys = line;
        }
        if (keys is not null)
        {
            DockPanel.SetDock(keys, Dock.Left);
            foot.Children.Add(on ? keys : Faded(keys));
        }
        stack.Children.Add(foot);

        return LapCard.Create(stack, new Thickness(18), lift: true);
    }

    /// <summary>A shortcut another program holds: say so, and offer a free one in one click.</summary>
    private FrameworkElement TakenShortcut(ToolModule tool, ToolCommand command, Core.Settings.KeyChord chord)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var warn = Warning(Lang.T("Taken by another app", "يستخدمه تطبيق آخر"));
        warn.ToolTip = Lang.T($"Another program already uses {chord}.", $"برنامج آخر يستخدم {chord}.");
        warn.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(warn);
        if (_tools.SuggestFree(tool, command) is { } free)
        {
            var fix = SettingsPage.Button(Lang.T("Use ", "استخدم ") + free, () => _tools.SetShortcut(tool, command, free.ToString()), accent: true);
            fix.Margin = new Thickness(12, 0, 0, 0);
            row.Children.Add(fix);
        }
        return row;
    }

    private sealed record Recent(string Path, string Detail, DateTimeOffset When, string Icon);

    private List<Recent> RecentItems()
    {
        var items = new List<Recent>();
        try
        {
            foreach (var entry in _host.History.ReadRecent(12))
            {
                var output = entry.Items.SelectMany(i => i.Outputs).FirstOrDefault();
                if (output is null) continue;
                string ext = IOPath.GetExtension(output.Path).ToLowerInvariant();
                string icon = ext is ".pdf" or ".docx" or ".txt" ? "lap-doc" : "lap-image";
                items.Add(new Recent(output.Path, entry.Title, entry.Finished ?? entry.Started, icon));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
        if (_tools.Find(LongshotTool.ToolId) is LongshotTool longshot)
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(longshot.Folder);
                if (dir.Exists)
                    foreach (var f in dir.EnumerateFiles("Longshot *.*").Where(f => f.Extension is ".png" or ".jpg").OrderByDescending(f => f.LastWriteTimeUtc).Take(4))
                        items.Add(new Recent(f.FullName, Lang.T($"Long screenshot · {SizeText(f.Length)}", $"لقطة طويلة · \u202A{SizeText(f.Length)}\u202C"), f.LastWriteTime, "lap-longshot"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        return items.OrderByDescending(i => i.When).Take(4).ToList();
    }

    private static string SizeText(long bytes) => bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.0} MB" : $"{Math.Max(1, bytes >> 10)} KB";

    private static UIElement RecentRow(Recent item)
    {
        var grid = new DockPanel();
        var when = new TextBlock { Text = Lang.Ago(item.When, DateTimeOffset.Now), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        when.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        DockPanel.SetDock(when, Dock.Right);
        grid.Children.Add(when);
        var tile = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 12, 0), Child = SettingsPage.Glyph(item.Icon, 18, "Brush.Text2") };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
        tile.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        DockPanel.SetDock(tile, Dock.Left);
        grid.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(SettingsPage.Ltr(new TextBlock { Text = IOPath.GetFileName(item.Path), Style = SettingsPage.Res("Lap.Text"), FontSize = 13, FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }));
        text.Children.Add(new TextBlock { Text = item.Detail, Style = SettingsPage.Res("Lap.Help"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
        grid.Children.Add(text);

        var open = new Button { Style = SettingsPage.Res("Lap.Button.Ghost"), Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = double.NaN, Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0), FontWeight = FontWeights.Normal, ToolTip = item.Path };
        Fx.SetLift(open, 0);
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { open.ToolTip = ex.Message; }
        };
        return open;
    }

    private void BuildTool(SettingsPage page, ToolModule tool)
    {
        bool on = _tools.IsEnabled(tool);
        var sw = SettingsPage.Switch(on, v => _tools.SetEnabled(tool, v));
        System.Windows.Automation.AutomationProperties.SetName(sw, Lang.T($"Turn on {tool.Name}", $"تشغيل {tool.Name}"));
        page.Header(tool.Icon, tool.Name, tool.Summary, sw);
        if (!on) page.Note(Lang.T("It's off: its shortcuts are released and it uses no memory or keyboard watching.", "إنها متوقفة: اختصاراتها محررة ولا تستهلك ذاكرة ولا تراقب لوحة المفاتيح."));

        if (tool.Commands.Count > 0 || tool.Gestures(page.Current).Count > 0)
        {
            page.Section(Lang.T("Shortcuts", "الاختصارات"));
            page.Card();
            foreach (var command in tool.Commands)
                page.Row(command.Label, new ShortcutBox(_tools, tool, command), command.DefaultShortcut is { } d ? Lang.T($"Default: {d}", $"الافتراضي: {d}") : null);
            foreach (var gesture in tool.Gestures(page.Current))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(SettingsPage.Keys(gesture.Keys.Take(gesture.Keys.Count - 1)));
                line.Children.Add(SettingsPage.Badge(gesture.Keys[^1]).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
                page.Row(gesture.Title, line, Lang.T("Change the key below.", "غيّر المفتاح في الأسفل."));
            }
        }

        tool.BuildSettings(page);
    }

    private void BuildGeneral(SettingsPage page)
    {
        var s = page.Current;
        page.Header("lap-general", Lang.T("General", "عام"), Lang.T("Settings for Amir Lap as a whole.", "إعدادات أمير لاب بالكامل."));

        page.Section(Lang.T("Appearance", "المظهر"));
        page.Card();
        page.Row(Lang.T("Theme", "السمة"), SettingsPage.Choice(["light", "dark", "system"], s.Theme, v => Change(x => x.Theme = v),
                v => v switch { "light" => Lang.T("Light", "فاتح"), "dark" => Lang.T("Dark", "داكن"), _ => Lang.T("Windows", "حسب ويندوز") }),
            Lang.T("White with light glass, or black with dark glass.", "أبيض مع زجاج فاتح، أو أسود مع زجاج داكن."));
        page.Row(Lang.T("Accent colour", "لون التمييز"), Swatches(s.Accent), AccentName(s.Accent));
        page.Toggle(Lang.T("Reduce transparency", "تقليل الشفافية"), ThemeResources.Lap.GlassOff, on => Change(x => x.ReduceTransparency = on ? "on" : "off"),
            Lang.T("Glass turns solid for easier reading.", "يصبح الزجاج معتمًا لقراءة أسهل."));
        page.Toggle(Lang.T("Reduce motion", "تقليل الحركة"), Motion.Reduced(s.ReduceMotion), on => Change(x => x.ReduceMotion = on ? "on" : "off"),
            Lang.T("Keeps animations to a minimum.", "يقلل الحركات إلى الحد الأدنى."));

        page.Section(Lang.T("Language", "اللغة"));
        page.Card();
        page.Row(Lang.T("App language", "لغة التطبيق"), SettingsPage.Choice(["en", "ar"], s.Language, v => Change(x => x.Language = v), v => v == "ar" ? "العربية" : "English"),
            Lang.T("Arabic switches the whole layout to right-to-left.", "العربية تقلب التخطيط بالكامل من اليمين إلى اليسار."));

        page.Section(Lang.T("Start-up and tray", "بدء التشغيل والإشعارات"));
        page.Card();
        page.Toggle(Lang.T("Start with Windows", "التشغيل مع ويندوز"), ShellIntegration.IsStartWithWindows, on =>
        {
            ShellIntegration.SetStartWithWindows(on);
            page.Change(x => x.StartWithWindows = on);
        }, Lang.T("Starts quietly in the notification area, so your tools and shortcuts are always ready.", "يبدأ بهدوء في منطقة الإشعارات، فتكون أدواتك واختصاراتك جاهزة دائمًا."));
        page.Row(Lang.T("Clicking the tray icon opens", "النقر على أيقونة الإشعارات يفتح"), SettingsPage.Choice(["home", "palette"], s.TrayClick, v => page.Change(x => x.TrayClick = v),
                v => v == "home" ? Lang.T("This window", "هذه النافذة") : Lang.T("File Toolbox", "أدوات الملفات")),
            Lang.T("Right-click the tray icon for every tool's menu.", "انقر بزر الماوس الأيمن على الأيقونة لقائمة كل أداة."));

        page.Section(Lang.T("Privacy", "الخصوصية"));
        page.Card();
        page.Row(Lang.T("Everything stays on this computer", "كل شيء يبقى على هذا الحاسوب"),
            SettingsPage.Button(Lang.T("Open data folder", "فتح مجلد البيانات"), () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true })),
            Lang.T("No account, no uploads, no tracking. Settings, history, recipes and logs live in the data folder.", "بلا حساب ولا رفع ملفات ولا تتبّع. الإعدادات والسجل والوصفات والسجلات في مجلد البيانات."));
    }

    private static readonly (string Key, Color Color, string En, string Ar)[] AccentChoices =
    [
        ("blue", Palette.BrandBlue, "Blue", "أزرق"),
        ("windows", Palette.AccentFor("windows"), "Windows accent", "لون ويندوز"),
        ("teal", Palette.AccentFor("teal"), "Teal", "أخضر مزرق"),
        ("violet", Palette.AccentFor("violet"), "Violet", "بنفسجي"),
    ];

    private static string AccentName(string key) => AccentChoices.FirstOrDefault(c => c.Key == key) is { En: not null } c ? Lang.T(c.En, c.Ar) : key;

    private FrameworkElement Swatches(string current)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (key, color, en, ar) in AccentChoices)
        {
            bool selected = key == current;
            var ring = new Ellipse { Width = 38, Height = 38, StrokeThickness = 2, Stroke = new SolidColorBrush(color), Opacity = selected ? 1 : 0 };
            var dot = new Ellipse { Width = 28, Height = 28, Fill = new SolidColorBrush(color) };
            var shine = new Ellipse { Width = 28, Height = 28, Fill = new LinearGradientBrush(Color.FromArgb(0x59, 0xFF, 0xFF, 0xFF), Color.FromArgb(0, 0xFF, 0xFF, 0xFF), 90), Stroke = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)), StrokeThickness = 1 };
            var face = new Grid { Width = 38, Height = 38, Background = Brushes.Transparent, Children = { ring, dot, shine } };
            var b = new Button { Template = (ControlTemplate)Application.Current.Resources["Lap.SwitchTemplate"], Content = face, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0), ToolTip = Lang.T(en, ar), FocusVisualStyle = (Style)Application.Current.Resources["Lap.Focus"] };
            System.Windows.Automation.AutomationProperties.SetName(b, Lang.T(en, ar));
            var scale = Fx.Transforms(face).Scale;
            b.MouseEnter += (_, _) => { Motion.To(scale, ScaleTransform.ScaleXProperty, 1.08, 300, Motion.Spring); Motion.To(scale, ScaleTransform.ScaleYProperty, 1.08, 300, Motion.Spring); };
            b.MouseLeave += (_, _) => { Motion.To(scale, ScaleTransform.ScaleXProperty, 1, 300); Motion.To(scale, ScaleTransform.ScaleYProperty, 1, 300); };
            b.Click += (_, _) => Change(x => x.Accent = key);
            row.Children.Add(b);
        }
        return row;
    }

    private void BuildAbout(SettingsPage page)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 36, 0, 0) };
        var t = ThemeResources.Lap;
        var logo = new Grid { Width = 132, Height = 132, HorizontalAlignment = HorizontalAlignment.Center };
        logo.Children.Add(new Image { Source = AppIcon.Render(264, t.Accent), Width = 132, Height = 132 });
        var glint = new GlareSweep(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF), 132 * 18 / 64.0, bandWidth: 48, periodSeconds: 6, sweepMs: 1500);
        _pageMotion.Add(glint);
        logo.Children.Add(glint);
        logo.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 36, ShadowDepth = 14, Direction = 270, Color = t.Shadow, Opacity = t.IsDark ? 0.6 : 0.22 };
        stack.Children.Add(logo);
        stack.Children.Add(new TextBlock { Text = Lang.T("Amir Lap", "أمير لاب"), Style = SettingsPage.Res("Lap.PageTitle"), FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 6) });
        var tagline = new TextBlock { Text = Lang.T("Your desktop tools in one place.", "أدواتك المكتبية في مكان واحد."), Style = SettingsPage.Res("Lap.Text"), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center };
        tagline.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        stack.Children.Add(tagline);
        var version = typeof(AmirLapWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        stack.Children.Add(new TextBlock { Text = Lang.T($"Version {version}", $"الإصدار {version}") + " · " + string.Join(Lang.T(", ", "، "), _tools.Tools.Select(x => x.Name)), Style = SettingsPage.Res("Lap.Help"), FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 22, 0, 0) };
        buttons.Children.Add(SettingsPage.Button(Lang.T("View on GitHub", "عرض على GitHub"), () => Process.Start(new ProcessStartInfo(GitHubUrl) { UseShellExecute = true }), ghost: true));
        var general = SettingsPage.Button(Lang.T("General settings", "الإعدادات العامة"), () => ShowPage(GeneralPage), accent: true);
        general.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(general);
        stack.Children.Add(buttons);
        page.Add(stack);
    }

    // ---- window states, snap layouts, motion ---------------------------------------------------

    private void Change(Action<Core.Settings.AppSettings> edit)
    {
        var next = _host.Settings.Current.Clone();
        edit(next);
        _host.Settings.Save(next);
    }

    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnStateChanged()
    {
        if (WindowState != WindowState.Minimized) _restoreState = WindowState;
        bool max = WindowState == WindowState.Maximized;
        // A maximized window reaches past the screen edge by its frame; keep the content on screen,
        // and the whole title bar draggable.
        _root.Margin = max ? MaximizedInset() : new Thickness(0);
        if (WindowChrome.GetWindowChrome(this) is { IsFrozen: false } chrome) chrome.CaptionHeight = TitleHeight + _root.Margin.Top;
        _maxGlyph.Content = max
            ? CaptionGlyph("M3,5.5 H10.5 V13 H3 Z M5.5,5.5 V3 H13 V10.5 H10.5", 1.15)
            : CaptionGlyph("M3.5,3.5 H12.5 V12.5 H3.5 Z", 1.15);
        var name = max ? Lang.T("Restore", "استعادة") : Lang.T("Maximize", "تكبير");
        _maxButton.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(_maxButton, name);
        SetMaxHover(false);
        UpdateMotion();
    }

    private Thickness MaximizedInset()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return new Thickness(8);
        uint dpi = Native.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        int frame = GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi) + GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
        double dip = frame * 96.0 / dpi;
        return new Thickness(dip);
    }

    private void UpdateMotion()
    {
        bool run = IsActive && WindowState != WindowState.Minimized && IsVisible && !Motion.IsReduced;
        if (run == _motionRunning) return;
        _motionRunning = run;
        foreach (var m in _shellMotion) m.SetRunning(run);
        foreach (var m in _pageMotion) m.SetRunning(run);
    }

    private static void StopMotion(List<IIdleMotion> list)
    {
        foreach (var m in list) m.SetRunning(false);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        OnStateChanged();
    }

    /// <summary>
    /// Tells Windows where the maximize button is, so hovering it opens the Windows 11 snap
    /// layouts, and handles its click (Windows reports it as a non-client button).
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_NCHITTEST = 0x0084, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2, WM_NCMOUSELEAVE = 0x02A2, HTMAXBUTTON = 9;
        switch (msg)
        {
            case WM_NCHITTEST:
                if (OverMaxButton(lParam))
                {
                    SetMaxHover(true);
                    handled = true;
                    return new IntPtr(HTMAXBUTTON);
                }
                SetMaxHover(false);
                break;
            case WM_NCLBUTTONDOWN when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                _maxButton.SetResourceReference(BackgroundProperty, "Brush.Line2");
                return IntPtr.Zero;
            case WM_NCLBUTTONUP when wParam.ToInt32() == HTMAXBUTTON:
                handled = true;
                ToggleMaximize();
                return IntPtr.Zero;
            case WM_NCMOUSELEAVE:
                SetMaxHover(false);
                break;
        }
        return IntPtr.Zero;
    }

    private bool OverMaxButton(IntPtr lParam)
    {
        if (_maxButton is not { IsVisible: true } || PresentationSource.FromVisual(_maxButton) is null) return false;
        long v = lParam.ToInt64();
        var point = new Point((short)(v & 0xFFFF), (short)((v >> 16) & 0xFFFF));
        try
        {
            var a = _maxButton.PointToScreen(new Point(0, 0));
            var b = _maxButton.PointToScreen(new Point(_maxButton.ActualWidth, _maxButton.ActualHeight));
            return new Rect(a, b).Contains(point);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void SetMaxHover(bool on)
    {
        if (_maxButton is null || on == _maxHover) return;
        _maxHover = on;
        if (on)
        {
            _maxButton.SetResourceReference(BackgroundProperty, "Brush.Line");
            _maxButton.SetResourceReference(ForegroundProperty, "Brush.Text");
        }
        else
        {
            _maxButton.ClearValue(BackgroundProperty);
            _maxButton.ClearValue(ForegroundProperty);
        }
    }

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CXPADDEDBORDER = 92;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int nIndex, uint dpi);

    // ---- small helpers -----------------------------------------------------------------------

    private static FrameworkElement Faded(FrameworkElement element)
    {
        element.Opacity = 0.45;
        return element;
    }

    private static TextBlock Muted(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        return t;
    }

    private static TextBlock Warning(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        return t;
    }
}
