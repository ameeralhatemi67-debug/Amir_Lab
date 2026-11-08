using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core.Settings;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Home;

/// <summary>
/// Builds one page of the Amir Lap window: a header, then headed groups of setting rows in cards.
/// Every change is saved immediately (there is no OK button), like the rest of the app.
/// </summary>
internal sealed class SettingsPage
{
    private readonly ToolboxHost _host;
    private StackPanel? _card;

    public SettingsPage(ToolboxHost host)
    {
        _host = host;
    }

    public StackPanel Root { get; } = new() { Margin = new Thickness(0, 0, 0, 24) };

    public AppSettings Current => _host.Settings.Current;

    public void Change(Action<AppSettings> edit)
    {
        var next = Current.Clone();
        edit(next);
        _host.Settings.Save(next);
    }

    // ---- structure ---------------------------------------------------------------------------

    public void Header(string icon, string title, string summary)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var badge = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 14, 0), Child = Icon(icon, 22, accent: true) };
        badge.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        DockPanel.SetDock(badge, Dock.Left);
        row.Children.Add(badge);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = Res("Text.Title"), FontSize = 20 });
        text.Children.Add(new TextBlock { Text = summary, Style = Res("Text.Caption"), FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) });
        row.Children.Add(text);
        Root.Children.Add(row);
        _card = null;
    }

    public void Section(string title)
    {
        Root.Children.Add(new TextBlock { Text = title, Style = Res("Text.Title"), FontSize = 13.5, Margin = new Thickness(2, 14, 0, 6) });
        _card = null;
    }

    /// <summary>Starts a new card; the following rows go into it.</summary>
    public void Card()
    {
        _card = new StackPanel();
        Root.Children.Add(new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 8), Child = _card });
    }

    private StackPanel Target
    {
        get
        {
            if (_card is null) Card();
            return _card!;
        }
    }

    /// <summary>Label (and help) on the left, the control on the right.</summary>
    public void Row(string label, FrameworkElement control, string? help = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
        if (help is not null) left.Children.Add(new TextBlock { Text = help, Style = Res("Text.Caption"), Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(left);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        AddRow(grid);
    }

    /// <summary>Label above, the control below at full width (text boxes, lists).</summary>
    public void Stacked(string label, UIElement control, string? help = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(control);
        if (help is not null) stack.Children.Add(new TextBlock { Text = help, Style = Res("Text.Caption"), Margin = new Thickness(0, 6, 0, 0) });
        AddRow(stack);
    }

    public void Toggle(string label, bool value, Action<bool> changed, string? help = null) =>
        Row(label, Switch(value, changed), help);

    public void Note(string text) => AddRow(new TextBlock { Text = text, Style = Res("Text.Caption") });

    public void Add(UIElement element)
    {
        Root.Children.Add(element);
        _card = null;
    }

    private void AddRow(UIElement content)
    {
        var target = Target;
        var row = new Border { Padding = new Thickness(16, 11, 16, 11), Child = content, BorderThickness = new Thickness(0, target.Children.Count == 0 ? 0 : 1, 0, 0) };
        row.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        target.Children.Add(row);
    }

    // ---- controls ----------------------------------------------------------------------------

    public static CheckBox Switch(bool value, Action<bool> changed)
    {
        var sw = new CheckBox { Style = Res("Toolbox.Switch"), IsChecked = value, Content = value ? "On" : "Off" };
        sw.Checked += (_, _) => { sw.Content = "On"; Safe(() => changed(true), sw); };
        sw.Unchecked += (_, _) => { sw.Content = "Off"; Safe(() => changed(false), sw); };
        return sw;
    }

    public static FrameworkElement Choice(string[] options, string current, Action<string> changed, Func<string, string>? label = null)
    {
        var bar = new Border { Style = Res("Toolbox.SegmentBar"), HorizontalAlignment = HorizontalAlignment.Left };
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var group = Guid.NewGuid().ToString("N");
        foreach (var option in options)
        {
            var rb = new RadioButton { Content = label?.Invoke(option) ?? option, Style = Res("Toolbox.Segment"), GroupName = group, IsChecked = string.Equals(option, current, StringComparison.OrdinalIgnoreCase) };
            rb.Checked += (_, _) => changed(option);
            stack.Children.Add(rb);
        }
        bar.Child = stack;
        return bar;
    }

    public static FrameworkElement Slider(int min, int max, int value, Action<int> changed)
    {
        var grid = new Grid { Width = 220 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        var slider = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = value.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { text.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture); changed((int)e.NewValue); };
        grid.Children.Add(slider);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    public static Button Button(string text, Action click, bool accent = false)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        if (accent) b.Style = Res("Toolbox.Button.Accent");
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Shortcut shown as key caps ("Ctrl" "Alt" "F").</summary>
    public static FrameworkElement Keys(IEnumerable<string> parts, double fontSize = 12)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in parts)
            stack.Children.Add(new Border { Style = (Style)Application.Current.Resources["Toolbox.KeyCap"], Child = new TextBlock { Text = part, FontSize = fontSize, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center } });
        return stack;
    }

    public static Path Icon(string key, double size, bool accent = false)
    {
        var p = new Path { Data = Icons.Get(key), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        p.SetResourceReference(Shape.StrokeProperty, accent ? "Brush.Accent" : "Brush.Text");
        return p;
    }

    public static Style Res(string key) => (Style)Application.Current.Resources[key];

    private static void Safe(Action action, FrameworkElement control)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            control.ToolTip = "This setting could not be changed: " + ex.Message;
        }
    }
}
