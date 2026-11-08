using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Toolbox.Core.Settings;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Home;

/// <summary>
/// Builds one page of the Amir Lap window in the glass design: a header, then headed groups of
/// setting rows in cards. Every change is saved immediately (there is no OK button).
/// </summary>
internal sealed class SettingsPage
{
    private readonly ToolboxHost _host;
    private StackPanel? _card;

    public SettingsPage(ToolboxHost host)
    {
        _host = host;
    }

    public StackPanel Root { get; } = new() { Margin = new Thickness(0, 0, 0, 8) };

    public AppSettings Current => _host.Settings.Current;

    public void Change(Action<AppSettings> edit)
    {
        var next = Current.Clone();
        edit(next);
        _host.Settings.Save(next);
    }

    // ---- structure ---------------------------------------------------------------------------

    /// <summary>A page title without an icon tile (the Dashboard).</summary>
    public void Title(string title, string summary)
    {
        var text = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        text.Children.Add(new TextBlock { Text = title, Style = Res("Lap.PageTitle") });
        text.Children.Add(new TextBlock { Text = summary, Style = Res("Lap.Text"), FontSize = 14, Margin = new Thickness(0, 4, 0, 0) }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2")));
        Root.Children.Add(text);
        _card = null;
    }

    /// <summary>Accent tile with the page's icon, title and summary; <paramref name="trailing"/> (a switch) on the far side.</summary>
    public void Header(string icon, string title, string summary, FrameworkElement? trailing = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 22), LastChildFill = true };
        row.Children.Add(Docked(AccentTile(icon, 52, 26), Dock.Left));
        if (trailing is not null)
        {
            trailing.VerticalAlignment = VerticalAlignment.Center;
            trailing.Margin = new Thickness(16, 0, 0, 0);
            row.Children.Add(Docked(trailing, Dock.Right));
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        text.Children.Add(new TextBlock { Text = title, Style = Res("Lap.PageTitle") });
        var sub = new TextBlock { Text = summary, Style = Res("Lap.Text"), FontSize = 14, Margin = new Thickness(0, 4, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        text.Children.Add(sub);
        row.Children.Add(text);
        Root.Children.Add(row);
        _card = null;
    }

    public void Section(string title)
    {
        Root.Children.Add(new TextBlock { Text = title, Style = Res("Lap.Section") });
        _card = null;
    }

    /// <summary>Starts a new card; the following rows go into it.</summary>
    public void Card()
    {
        _card = new StackPanel();
        Root.Children.Add(LapCard.Create(_card, new Thickness(0)));
    }

    private StackPanel Target
    {
        get
        {
            if (_card is null) Card();
            return _card!;
        }
    }

    /// <summary>Label (and help) on one side, the control on the other.</summary>
    public void Row(string label, FrameworkElement control, string? help = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 140 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Label(label, help));
        control.VerticalAlignment = VerticalAlignment.Center;
        control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        if (string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetName(control)))
            System.Windows.Automation.AutomationProperties.SetName(control, label);
        AddRow(grid);
    }

    /// <summary>Label above, the control below at full width (text boxes, folders).</summary>
    public void Stacked(string label, UIElement control, string? help = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(label, help));
        if (control is FrameworkElement fe) fe.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(control);
        AddRow(stack);
    }

    public void Toggle(string label, bool value, Action<bool> changed, string? help = null) =>
        Row(label, Switch(value, changed), help);

    public void Note(string text)
    {
        var t = new TextBlock { Text = text, Style = Res("Lap.Text"), FontSize = 13 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        AddRow(t);
    }

    /// <summary>Adds an element to the current card, under a separator, without row padding (lists of buttons).</summary>
    public void Item(UIElement element)
    {
        var target = Target;
        var row = new Border { Padding = new Thickness(6), Child = element, BorderThickness = new Thickness(0, target.Children.Count == 0 ? 0 : 1, 0, 0) };
        row.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        target.Children.Add(row);
    }

    public void Add(UIElement element)
    {
        Root.Children.Add(element);
        _card = null;
    }

    private static StackPanel Label(string label, string? help)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Text = label, Style = Res("Lap.Text"), FontWeight = FontWeights.Medium });
        if (help is not null) left.Children.Add(new TextBlock { Text = help, Style = Res("Lap.Help") });
        return left;
    }

    private void AddRow(UIElement content)
    {
        var target = Target;
        var row = new Border { Padding = new Thickness(18, 14, 18, 14), Child = content, BorderThickness = new Thickness(0, target.Children.Count == 0 ? 0 : 1, 0, 0) };
        row.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        target.Children.Add(row);
    }

    // ---- controls ----------------------------------------------------------------------------

    public static GelSwitch Switch(bool value, Action<bool> changed)
    {
        var sw = new GelSwitch { IsChecked = value };
        sw.Checked += (_, _) => Safe(() => changed(true), sw);
        sw.Unchecked += (_, _) => Safe(() => changed(false), sw);
        return sw;
    }

    public static FrameworkElement Choice(string[] options, string current, Action<string> changed, Func<string, string>? label = null)
    {
        var bar = new Border { Style = Res("Lap.SegmentBar") };
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var group = Guid.NewGuid().ToString("N");
        foreach (var option in options)
        {
            var rb = new RadioButton { Content = label?.Invoke(option) ?? option, Style = Res("Lap.Segment"), GroupName = group, IsChecked = string.Equals(option, current, StringComparison.OrdinalIgnoreCase) };
            rb.Checked += (_, _) => changed(option);
            stack.Children.Add(rb);
        }
        if (stack.Children.Count > 0 && stack.Children[^1] is FrameworkElement last) last.Margin = new Thickness(0);
        bar.Child = stack;
        return bar;
    }

    public static FrameworkElement Slider(int min, int max, int value, Action<int> changed)
    {
        var grid = new Grid { Width = 240 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        var slider = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = value.ToString(CultureInfo.InvariantCulture), Style = Res("Lap.Text"), FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { text.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture); changed((int)e.NewValue); };
        grid.Children.Add(slider);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>A button: secondary by default, the screen's main action with <paramref name="accent"/>, outlined with <paramref name="ghost"/>.</summary>
    public static Button Button(string text, Action click, bool accent = false, bool ghost = false)
    {
        var b = new Button { Content = text, Style = Res(accent ? "Lap.Button.Accent" : ghost ? "Lap.Button.Ghost" : "Lap.Button") };
        b.Click += (_, _) => click();
        return b;
    }

    public static TextBox TextBox(string text, double width = double.NaN)
    {
        var box = new TextBox { Text = text, Style = (Style)Application.Current.Resources["Lap.TextBox"], Width = width };
        return box;
    }

    /// <summary>Shortcut shown as key caps ("Ctrl" "Alt" "F"), always left-to-right.</summary>
    public static FrameworkElement Keys(IEnumerable<string> parts, double fontSize = 12)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        foreach (var part in parts)
        {
            var t = new TextBlock { Text = part, FontSize = fontSize, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
            stack.Children.Add(new Border { Style = (Style)Application.Current.Resources["Lap.KeyCap"], Child = t });
        }
        if (stack.Children.Count > 0 && stack.Children[^1] is FrameworkElement last) last.Margin = new Thickness(0);
        return stack;
    }

    /// <summary>A small rounded label ("tap", "Default").</summary>
    public static Border Badge(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        var b = new Border { Height = 24, Padding = new Thickness(10, 0, 10, 0), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Child = t, VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return b;
    }

    /// <summary>Text that stays left-to-right inside an Arabic layout (file names, folders).</summary>
    public static TextBlock Ltr(TextBlock t)
    {
        t.FlowDirection = FlowDirection.LeftToRight;
        if (Lang.IsArabic) t.TextAlignment = TextAlignment.Right;
        return t;
    }

    /// <summary>Old-style icon (stretched to its box), used by File Toolbox's own windows.</summary>
    public static Path Icon(string key, double size, bool accent = false)
    {
        var p = new Path { Data = Icons.Get(key), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        p.SetResourceReference(Shape.StrokeProperty, accent ? "Brush.Accent" : "Brush.Text");
        return p;
    }

    /// <summary>
    /// A line icon drawn on its 24-unit grid and scaled to <paramref name="size"/>. Without a brush it
    /// takes the text colour of whatever holds it (so it follows hover and selection).
    /// </summary>
    public static FrameworkElement Glyph(string key, double size, string? brushKey = null, double stroke = 1.7, Brush? brush = null)
    {
        var p = new Path { Data = Icons.Get(key), StrokeThickness = stroke, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        if (brush is not null) p.Stroke = brush;
        else if (brushKey is null) p.SetBinding(Shape.StrokeProperty, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), RelativeSource = RelativeSource.Self });
        else p.SetResourceReference(Shape.StrokeProperty, brushKey);
        return new Viewbox { Width = size, Height = size, Child = new Canvas { Width = 24, Height = 24, Children = { p } }, IsHitTestVisible = false };
    }

    /// <summary>The page header's tile: accent gradient, light edge on top, white icon, coloured glow.</summary>
    public static FrameworkElement AccentTile(string icon, double size, double glyph)
    {
        var a = ThemeResources.Lap.Accent;
        var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.31), Child = Glyph(icon, glyph, stroke: 1.8, brush: Brushes.White) };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentTile");
        var shine = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.31), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF)), IsHitTestVisible = false };
        var glow = new Border
        {
            Width = size - 8,
            Height = size - 8,
            CornerRadius = new CornerRadius(size * 0.3),
            Background = new SolidColorBrush(a),
            Margin = new Thickness(0, 8, 0, 0),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Color = a, Opacity = ThemeResources.Lap.IsDark ? 0.5 : 0.35 },
        };
        return new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Top, Children = { glow, tile, shine } };
    }

    public static Style Res(string key) => (Style)Application.Current.Resources[key];

    private static UIElement Docked(UIElement e, Dock dock)
    {
        DockPanel.SetDock(e, dock);
        return e;
    }

    private static void Safe(Action action, FrameworkElement control)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            control.ToolTip = Lang.T("This setting could not be changed: ", "تعذّر تغيير هذا الإعداد: ") + ex.Message;
        }
    }
}

internal static class UiExtensions
{
    /// <summary>Configures an element inline (keeps the page code declarative).</summary>
    public static T With<T>(this T element, Action<T> configure)
    {
        configure(element);
        return element;
    }
}
