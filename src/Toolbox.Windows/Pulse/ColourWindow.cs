using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Toolbox.Core.Pulse;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse;

/// <summary>
/// The colour picker's Pro window, in the same see-through glass as the Longshot panel. It opens
/// beside the point that was picked, on whichever side has room (picked near the bottom: above it;
/// near the right edge: to its left), and shows the colour large with its nearest CSS name, nine
/// lighter and darker steps, every code (a click copies one; the star chooses the one a pick
/// copies), contrast with white and black, saved colours and the recent picks. It stays on top
/// until closed and is left out of screenshots.
/// </summary>
internal sealed class ColourWindow : Window
{
    private const double Shadow = 14;
    private const double Radius = 22;
    private const double Inner = 336;
    private const double Swatch = 26;

    private readonly Func<PulseSettings> _settings;
    private readonly Action<Action<PulseSettings>> _save;
    private readonly Action _pickAgain;
    private readonly ScreenBackdrop _backdrop = new();
    private readonly Border _hero = new() { CornerRadius = new CornerRadius(16), Height = 96, Padding = new Thickness(16, 12, 12, 12), Cursor = Cursors.Hand };
    private readonly TextBlock _heroHex = new() { FontSize = 24, FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _heroName = new() { FontSize = 12.5, Opacity = 0.85, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
    private readonly Border _copied = new() { CornerRadius = new CornerRadius(9), Padding = new Thickness(8, 3, 9, 3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0, IsHitTestVisible = false };
    private readonly TextBlock _copiedText = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold };
    private readonly UniformGrid _steps = new() { Rows = 1, Margin = new Thickness(0, 10, 0, 0), Height = 26 };
    private readonly StackPanel _codes = new();
    private readonly Grid _contrast = new();
    private readonly WrapPanel _saved = new();
    private readonly TextBlock _savedEmpty = new() { Text = Lang.T("Colours you save stay here.", "الألوان التي تحفظها تبقى هنا."), FontSize = 12, Margin = new Thickness(2, 0, 0, 6) };
    private readonly WrapPanel _recent = new();
    private readonly TextBlock _recentCount = new() { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), FlowDirection = FlowDirection.LeftToRight };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly DispatcherTimer _keepUp = new() { Interval = TimeSpan.FromSeconds(1) };
    private IntPtr _hwnd;
    private Rgb _colour;
    private PixelPoint? _pickedAt;
    private bool _movedByUser;
    private bool _placing;
    private DateTime _lastBackdrop;

    public ColourWindow(Func<PulseSettings> settings, Action<Action<PulseSettings>> save, Action pickAgain)
    {
        _settings = settings;
        _save = save;
        _pickAgain = pickAgain;
        Title = Lang.T("Colour", "اللون");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        SetResourceReference(ForegroundProperty, "Brush.Text");
        Content = Build();

        _keepUp.Tick += (_, _) => RefreshBackdrop();
        LocationChanged += (_, _) =>
        {
            if (!_placing && IsLoaded) _movedByUser = true;
            if ((DateTime.UtcNow - _lastBackdrop).TotalMilliseconds > 33) RefreshBackdrop();
        };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(RefreshBackdrop, DispatcherPriority.Render);
        IsVisibleChanged += (_, _) => { if (IsVisible) _keepUp.Start(); else _keepUp.Stop(); };
        Closed += (_, _) => _keepUp.Stop();
        KeyDown += OnKey;
    }

    /// <summary>The colour shown.</summary>
    public Rgb Colour => _colour;

    /// <summary>For tests: the frosted glass (to give it a large picture and check the window keeps its size).</summary>
    internal ScreenBackdrop Backdrop => _backdrop;

    /// <summary>For tests: keeps the window off the screen (and does not read the screen behind it).</summary>
    internal static bool KeepOffScreen { get; set; }

    /// <summary>A new pick: shows it beside where it was picked (unless the window was moved by hand) and says what was copied.</summary>
    public void ShowPicked(Rgb colour, PixelPoint at, string? copiedFormat)
    {
        _pickedAt = at;
        _movedByUser = false;
        Select(colour);
        if (copiedFormat is not null) Flash(Lang.T($"Copied {Colours.Label(copiedFormat)}", $"نُسخ {Colours.Label(copiedFormat)}"));
        if (!IsVisible) Show();
        else Place();
        Activate();
    }

    /// <summary>Shows a colour (a pick, a step, a saved or recent one) without copying it.</summary>
    public void Select(Rgb colour)
    {
        _colour = colour;
        var c = ToColor(colour);
        var text = ToColor(Colours.TextOn(colour));
        _hero.Background = new SolidColorBrush(c);
        _heroHex.Foreground = _heroName.Foreground = new SolidColorBrush(text);
        _heroHex.Text = colour.Hex;
        var (name, exact) = Colours.NearestName(colour);
        _heroName.Text = exact ? name : "≈ " + name;
        _copied.Background = new SolidColorBrush(Color.FromArgb(0x33, text.R, text.G, text.B));
        _copiedText.Foreground = new SolidColorBrush(text);
        _hero.ToolTip = Lang.T($"Click to copy {Colours.Label(DefaultFormat)}", $"انقر لنسخ {Colours.Label(DefaultFormat)}");
        RenderSteps();
        RenderCodes();
        RenderContrast();
        RenderLists();
    }

    /// <summary>The saved and recent colours changed (or the settings did).</summary>
    public void RenderLists()
    {
        var s = _settings();
        _saved.Children.Clear();
        foreach (var hex in s.ColourSaved)
            if (Rgb.TryParse(hex, out var c)) _saved.Children.Add(SwatchButton(c, saved: true));
        _savedEmpty.Visibility = _saved.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _recent.Children.Clear();
        foreach (var hex in s.ColourHistory)
            if (Rgb.TryParse(hex, out var c)) _recent.Children.Add(SwatchButton(c, saved: false));
        _recentCount.Text = $"{_recent.Children.Count} / {Colours.HistorySize(s.ColourHistorySize)}";
    }

    private string DefaultFormat => Colours.Formats.Contains(_settings().ColourCopy) ? _settings().ColourCopy : Colours.Hex;

    // ---- building ----------------------------------------------------------------------------------

    private UIElement Build()
    {
        var dark = ThemeResources.Lap.IsDark;
        var root = new Grid { FlowDirection = Lang.Flow };
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
        var top = new Border { Margin = new Thickness(Shadow + 1), CornerRadius = new CornerRadius(Radius - 1), BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false };
        top.SetResourceReference(Border.BorderBrushProperty, "Brush.RimTop");
        root.Children.Add(top);

        var dock = new DockPanel { Margin = new Thickness(Shadow + 16, Shadow + 12, Shadow + 16, Shadow + 14), Width = Inner };

        // Title (drag to move), pick again, close.
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.SizeAll };
        var close = new Button { Style = SettingsPage.Res("Lap.Caption.Close"), Width = 34, Height = 30, Content = SettingsPage.Glyph("close", 12, stroke: 2.2), ToolTip = Lang.T("Close (Esc)", "إغلاق (Esc)") };
        close.Click += (_, _) => Close();
        System.Windows.Automation.AutomationProperties.SetName(close, Lang.T("Close", "إغلاق"));
        var again = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 34, Height = 30, Content = SettingsPage.Glyph("lap-picker", 16, stroke: 1.9), ToolTip = Lang.T("Pick another colour", "التقاط لون آخر"), Margin = new Thickness(0, 0, 2, 0) };
        again.Click += (_, _) => _pickAgain();
        System.Windows.Automation.AutomationProperties.SetName(again, Lang.T("Pick another colour", "التقاط لون آخر"));
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(again, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(again);
        var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 10, 0), Child = SettingsPage.Glyph("lap-colour", 17, "Brush.Accent") };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        head.Children.Add(new TextBlock { Text = Lang.T("Colour", "اللون"), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is DependencyObject d && (IsInside(d, close) || IsInside(d, again))) return;
            try { DragMove(); } catch (InvalidOperationException) { }
            RefreshBackdrop();
        };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        var body = new StackPanel();

        // The colour, large.
        var heroGrid = new Grid();
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        words.Children.Add(_heroHex);
        words.Children.Add(_heroName);
        heroGrid.Children.Add(words);
        _copied.Child = _copiedText;
        heroGrid.Children.Add(_copied);
        _hero.Child = heroGrid;
        _hero.MouseLeftButtonUp += (_, _) => Copy(DefaultFormat);
        body.Children.Add(_hero);
        body.Children.Add(_steps);

        body.Children.Add(Section(Lang.T("Codes", "الرموز")));
        body.Children.Add(_codes);

        body.Children.Add(Section(Lang.T("Contrast", "التباين")));
        _contrast.ColumnDefinitions.Add(new ColumnDefinition());
        _contrast.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        _contrast.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(_contrast);

        var saveThis = SmallButton("lap-plus", Lang.T("Save", "حفظ"), Lang.T("Keep this colour", "الاحتفاظ بهذا اللون"), SaveCurrent);
        body.Children.Add(Section(Lang.T("Saved", "المحفوظة"), saveThis));
        _savedEmpty.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        body.Children.Add(_savedEmpty);
        body.Children.Add(_saved);

        var clear = SmallButton("lap-x", Lang.T("Clear", "مسح"), Lang.T("Forget the recent colours (saved ones stay)", "نسيان الألوان الأخيرة (المحفوظة تبقى)"), () =>
        {
            _save(p => p.ColourHistory = []);
            RenderLists();
        });
        _recentCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        body.Children.Add(Section(Lang.T("Recent", "الأخيرة"), clear, _recentCount));
        body.Children.Add(_recent);

        // Pro mode on or off, right here.
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var sw = SettingsPage.Switch(_settings().ColourMode == "pro", on => _save(p => p.ColourMode = on ? "pro" : "basic"));
        sw.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(sw, Dock.Right);
        footer.Children.Add(sw);
        var footText = new TextBlock { Text = Lang.T("Open this window after every pick", "فتح هذه النافذة بعد كل التقاط"), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        footText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        footer.Children.Add(footText);
        body.Children.Add(footer);

        _scroll.Content = body;
        _scroll.Style = SettingsPage.Res("Lap.Scroll");
        dock.Children.Add(_scroll);
        root.Children.Add(dock);
        return root;
    }

    private static FrameworkElement Section(string title, FrameworkElement? action = null, FrameworkElement? extra = null)
    {
        var row = new DockPanel { Margin = new Thickness(2, 14, 0, 6), LastChildFill = false };
        var t = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        DockPanel.SetDock(t, Dock.Left);
        row.Children.Add(t);
        if (extra is not null) { DockPanel.SetDock(extra, Dock.Left); row.Children.Add(extra); }
        if (action is not null) { DockPanel.SetDock(action, Dock.Right); row.Children.Add(action); }
        return row;
    }

    private static Button SmallButton(string icon, string text, string tip, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(SettingsPage.Glyph(icon, 11, stroke: 2.2).With(g => g.Margin = new Thickness(0, 0, 5, 0)));
        content.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = SettingsPage.Res("Lap.Button.Ghost"), Content = content, ToolTip = tip, Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, -4, 0, -4) };
        b.Click += (_, _) => click();
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    private void RenderSteps()
    {
        _steps.Children.Clear();
        var steps = Colours.Steps(_colour);
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            bool first = i == 0, last = i == steps.Count - 1, middle = i == steps.Count / 2;
            var cell = new Border
            {
                Background = new SolidColorBrush(ToColor(step)),
                CornerRadius = new CornerRadius(first ? 8 : 0, last ? 8 : 0, last ? 8 : 0, first ? 8 : 0),
                Cursor = Cursors.Hand,
                ToolTip = step.Hex,
                FlowDirection = FlowDirection.LeftToRight,
            };
            if (middle)
            {
                cell.BorderThickness = new Thickness(2);
                cell.BorderBrush = new SolidColorBrush(ToColor(Colours.TextOn(step)));
                cell.CornerRadius = new CornerRadius(4);
            }
            cell.MouseLeftButtonUp += (_, _) => { if (step != _colour) Select(step); };
            _steps.Children.Add(cell);
        }
        _steps.FlowDirection = FlowDirection.LeftToRight; // light to dark, whatever the language
    }

    private void RenderCodes()
    {
        _codes.Children.Clear();
        int n = 1;
        foreach (var format in Colours.Formats)
        {
            bool isDefault = format == DefaultFormat;
            var row = new Grid { Height = 30 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(66) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            var label = new TextBlock { Text = Colours.Label(format), FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
            var value = new TextBlock { Text = Colours.Format(_colour, format), FontSize = 12.5, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(value, 1);
            var star = new Button
            {
                Style = SettingsPage.Res("Lap.Caption"),
                Width = 28,
                Height = 26,
                ToolTip = isDefault ? Lang.T("A pick copies this code", "الالتقاط ينسخ هذا الرمز") : Lang.T("Copy this code when picking", "نسخ هذا الرمز عند الالتقاط"),
                Content = StarGlyph(isDefault),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(star, Lang.T($"Copy {Colours.Label(format)} when picking", $"نسخ {Colours.Label(format)} عند الالتقاط"));
            star.Click += (_, e) =>
            {
                _save(p => p.ColourCopy = format);
                RenderCodes();
                _hero.ToolTip = Lang.T($"Click to copy {Colours.Label(format)}", $"انقر لنسخ {Colours.Label(format)}");
                e.Handled = true;
            };
            Grid.SetColumn(star, 3);
            // Quick copy: the icon turns into a tick for a moment.
            var copy = new Button
            {
                Style = SettingsPage.Res("Lap.Caption"),
                Width = 28,
                Height = 26,
                ToolTip = Lang.T($"Copy {Colours.Label(format)}", $"نسخ {Colours.Label(format)}"),
                Content = SettingsPage.Glyph("lap-copy", 14, "Brush.Text2", stroke: 1.8),
                VerticalAlignment = VerticalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(copy, Lang.T($"Copy {Colours.Label(format)}", $"نسخ {Colours.Label(format)}"));
            copy.Click += (_, e) =>
            {
                Copy(format);
                copy.Content = SettingsPage.Glyph("lap-check", 14, "Brush.Success", stroke: 2.2);
                var back = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
                back.Tick += (_, _) => { back.Stop(); copy.Content = SettingsPage.Glyph("lap-copy", 14, "Brush.Text2", stroke: 1.8); };
                back.Start();
                e.Handled = true;
            };
            Grid.SetColumn(copy, 2);
            row.Children.Add(label);
            row.Children.Add(value);
            row.Children.Add(copy);
            row.Children.Add(star);
            var box = new Border { Child = row, CornerRadius = new CornerRadius(9), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Lang.T($"Click to copy ({n})", $"انقر للنسخ ({n})") };
            box.MouseEnter += (_, _) => box.SetResourceReference(Border.BackgroundProperty, "Brush.NavHover");
            box.MouseLeave += (_, _) => box.Background = Brushes.Transparent;
            box.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject d && (IsInside(d, star) || IsInside(d, copy))) return;
                Copy(format);
                Pulse(box);
            };
            _codes.Children.Add(box);
            n++;
        }
    }

    private static FrameworkElement StarGlyph(bool filled)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Icons.Get("lap-star"),
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
        };
        path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, filled ? "Brush.Accent" : "Brush.Text3");
        if (filled) path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Brush.Accent");
        return path;
    }

    private void RenderContrast()
    {
        _contrast.Children.Clear();
        var white = new Rgb(255, 255, 255);
        var black = new Rgb(0, 0, 0);
        AddChip(white, Lang.T("on white", "على الأبيض"), 0);
        AddChip(black, Lang.T("on black", "على الأسود"), 2);

        void AddChip(Rgb ground, string where, int column)
        {
            double ratio = Colours.Contrast(_colour, ground);
            string grade = Colours.Grade(ratio);
            bool light = ground == white;
            var ink = light ? Color.FromRgb(0x15, 0x17, 0x1C) : Color.FromRgb(0xF2, 0xF3, 0xF6);
            var gradeColour = grade switch
            {
                "AAA" or "AA" => light ? Color.FromRgb(0x15, 0x80, 0x3D) : Color.FromRgb(0x5E, 0xE0, 0x8F),
                "AA large" => light ? Color.FromRgb(0x9D, 0x5D, 0x00) : Color.FromRgb(0xFC, 0xE1, 0x00),
                _ => light ? Color.FromRgb(0xB4, 0x23, 0x18) : Color.FromRgb(0xFF, 0x8A, 0x80),
            };
            var row = new DockPanel();
            var sample = new TextBlock { Text = "Aa", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(ToColor(_colour)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FlowDirection = FlowDirection.LeftToRight };
            DockPanel.SetDock(sample, Dock.Left);
            row.Children.Add(sample);
            var facts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            facts.Children.Add(new TextBlock { Text = ratio.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " : 1", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(ink), FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left });
            facts.Children.Add(new TextBlock { Text = grade == "Fail" ? Lang.T("Fails", "لا يجتاز") : grade, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(gradeColour), HorizontalAlignment = HorizontalAlignment.Left });
            row.Children.Add(facts);
            var chip = new Border
            {
                Child = row,
                Background = new SolidColorBrush(light ? Colors.White : Color.FromRgb(0x0B, 0x0B, 0x0E)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 10, 8),
                BorderThickness = new Thickness(1),
                ToolTip = Lang.T($"As text {where}: {grade} for normal text", $"كنص {where}: {grade} للنص العادي"),
            };
            chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
            Grid.SetColumn(chip, column);
            _contrast.Children.Add(chip);
        }
    }

    /// <summary>A saved or recent colour: a click shows it; on hover a small badge saves it (recent) or removes it (saved).</summary>
    private FrameworkElement SwatchButton(Rgb c, bool saved)
    {
        var face = new Border
        {
            Width = Swatch,
            Height = Swatch,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(ToColor(c)),
            BorderThickness = new Thickness(c == _colour ? 2 : 1),
            Cursor = Cursors.Hand,
        };
        if (c == _colour) face.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        else face.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        var badge = new Border
        {
            Width = 15,
            Height = 15,
            CornerRadius = new CornerRadius(7.5),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -5, -5, 0),
            Visibility = Visibility.Hidden,
            Cursor = Cursors.Hand,
            Child = SettingsPage.Glyph(saved ? "lap-x" : "lap-plus", 9, stroke: 2.6, brush: Brushes.White),
            ToolTip = saved ? Lang.T("Remove from saved", "إزالة من المحفوظة") : Lang.T("Save", "حفظ"),
        };
        badge.SetResourceReference(Border.BackgroundProperty, saved ? "Brush.Danger" : "Brush.Accent");
        var cell = new Grid { Margin = new Thickness(0, 0, 7, 7), ToolTip = c.Hex, Background = Brushes.Transparent };
        cell.Children.Add(face);
        cell.Children.Add(badge);
        cell.MouseEnter += (_, _) => badge.Visibility = Visibility.Visible;
        cell.MouseLeave += (_, _) => badge.Visibility = Visibility.Hidden;
        badge.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (saved) _save(p => p.ColourSaved = p.ColourSaved.Where(h => !string.Equals(h, c.Hex, StringComparison.OrdinalIgnoreCase)).ToList());
            else Save(c);
            RenderLists();
        };
        face.MouseLeftButtonUp += (_, _) => Select(c);
        return cell;
    }

    private void SaveCurrent()
    {
        Save(_colour);
        RenderLists();
        Flash(Lang.T("Saved", "حُفظ"));
    }

    private void Save(Rgb c) =>
        _save(p => p.ColourSaved = [c.Hex, .. p.ColourSaved.Where(h => !string.Equals(h, c.Hex, StringComparison.OrdinalIgnoreCase)).Take(Colours.HistoryMax - 1)]);

    // ---- copying -----------------------------------------------------------------------------------

    private void Copy(string format)
    {
        var code = Colours.Format(_colour, format);
        try { Clipboard.SetText(code); }
        catch (COMException) { return; }
        Flash(Lang.T($"Copied {Colours.Label(format)}", $"نُسخ {Colours.Label(format)}"));
    }

    private void Flash(string text)
    {
        _copiedText.Text = text;
        if (Motion.IsReduced) { _copied.BeginAnimation(OpacityProperty, null); _copied.Opacity = 1; return; }
        var show = new DoubleAnimationUsingKeyFrames();
        show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        show.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1900))));
        _copied.BeginAnimation(OpacityProperty, show);
    }

    private static void Pulse(Border box)
    {
        if (Motion.IsReduced) return;
        var wash = ThemeResources.Lap.Accent;
        var brush = new SolidColorBrush(Color.FromArgb(0x40, wash.R, wash.G, wash.B));
        box.Background = brush;
        brush.BeginAnimation(SolidColorBrush.OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromMilliseconds(150) });
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { Copy(DefaultFormat); e.Handled = true; return; }
        int index = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (index >= 0 && index < Colours.Formats.Count) { Copy(Colours.Formats[index]); e.Handled = true; }
    }

    // ---- placing -----------------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.ExcludeFromCapture(_hwnd); // the glass reads the screen under it; picking again never sees it
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Place();
        RefreshBackdrop();
    }

    /// <summary>Beside the picked point on its screen, on the side that has room (see <see cref="Spot"/>).</summary>
    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        _placing = true;
        try
        {
            if (KeepOffScreen)
            {
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                return;
            }
            if (_movedByUser || _pickedAt is not { } at) return;
            var monitor = ScreenGeometry.MonitorAt(at.ToPoint());
            var work = PixelRect.From(monitor.WorkArea);
            // Never taller than the screen: the body scrolls instead.
            double maxDip = Math.Max(300, work.Height / monitor.Scale - 16);
            if (Math.Abs(MaxHeight - maxDip) > 1) { MaxHeight = maxDip; UpdateLayout(); }
            if (!Native.GetWindowRect(_hwnd, out var r)) return;
            // The glass sits inside a transparent margin (for its shadow): measure the gap from the glass.
            int margin = (int)Math.Round(Shadow * monitor.Scale);
            var spot = Spot(at, r.Width, r.Height, work, (int)Math.Round(12 * monitor.Scale), margin);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, spot.X, spot.Y, 0, 0, Native.SWP_NOSIZE);
            RefreshBackdrop();
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>
    /// Where the window goes for a pick at <paramref name="at"/> (physical pixels): to the right and
    /// below it when there is room in <paramref name="work"/>, otherwise to the left and/or above,
    /// so picking near the bottom puts it above, near the top below, near the right edge to the
    /// left, near the left edge to the right. <paramref name="margin"/> is the window's own
    /// transparent edge (the glass starts that far in), which may run past the screen.
    /// </summary>
    internal static (int X, int Y) Spot(PixelPoint at, int width, int height, PixelRect work, int gap, int margin = 0)
    {
        var glass = new Size(Math.Max(1, width - 2 * margin), Math.Max(1, height - 2 * margin));
        var area = new Rect(work.X, work.Y, work.Width, work.Height);
        var p = ColourPicker.Beside(new Point(at.X, at.Y), glass, area, gap);
        return ((int)Math.Round(p.X) - margin, (int)Math.Round(p.Y) - margin);
    }

    private void RefreshBackdrop()
    {
        _lastBackdrop = DateTime.UtcNow;
        if (_hwnd != IntPtr.Zero && !KeepOffScreen && IsVisible) _backdrop.Refresh(_hwnd, Shadow, Radius);
    }

    private static bool IsInside(DependencyObject? d, DependencyObject container)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == container) return true;
        return false;
    }

    private static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);
}
