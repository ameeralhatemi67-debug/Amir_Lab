using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Home;

/// <summary>
/// The Amir Lap window: a sidebar with the Dashboard, one page per tool (switch, shortcuts,
/// settings) and General. Opened from the tray; the tools themselves never need it.
/// </summary>
internal sealed class AmirLapWindow : GlassWindow
{
    public const string DashboardPage = "dashboard";
    public const string GeneralPage = "general";

    private readonly ToolboxHost _host;
    private readonly ToolRegistry _tools;
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 4, 24, 0) };
    private readonly Dictionary<string, RadioButton> _nav = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ellipse> _dots = new(StringComparer.OrdinalIgnoreCase);
    private string _page = DashboardPage;

    public AmirLapWindow(ToolboxHost host, ToolRegistry tools)
    {
        _host = host;
        _tools = tools;
        Title = "Amir Lap";
        Width = 960;
        Height = 700;
        MinWidth = 760;
        MinHeight = 480;
        Resizable = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build();
        _tools.Changed += OnToolsChanged;
        Closed += (_, _) => _tools.Changed -= OnToolsChanged;
        ShowPage(DashboardPage);
    }

    public string CurrentPage => _page;

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

    // ---- frame -------------------------------------------------------------------------------

    private UIElement Build()
    {
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition());

        // Sidebar
        var side = new DockPanel { Margin = new Thickness(12, 12, 10, 12) };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 0, 16), Background = Brushes.Transparent };
        brand.Children.Add(new Image { Source = AppIcon.Render(32, ThemeResources.Current.Accent), Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) });
        brand.Children.Add(new TextBlock { Text = "Amir Lap", Style = SettingsPage.Res("Text.Title"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        MakeDragHandle(brand);
        DockPanel.SetDock(brand, Dock.Top);
        side.Children.Add(brand);

        var general = NavItem(GeneralPage, "General", "sliders", dot: false);
        DockPanel.SetDock(general, Dock.Bottom);
        side.Children.Add(general);

        var list = new StackPanel();
        list.Children.Add(NavItem(DashboardPage, "Dashboard", "dashboard", dot: false));
        list.Children.Add(new TextBlock { Text = "Tools", Style = SettingsPage.Res("Text.Label"), FontSize = 11.5, Margin = new Thickness(12, 14, 0, 4) });
        foreach (var tool in _tools.Tools) list.Children.Add(NavItem(tool.Id, tool.Name, tool.Icon, dot: true));
        side.Children.Add(list);
        root.Children.Add(side);

        // Content
        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
        right.RowDefinitions.Add(new RowDefinition());
        var top = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 6, 8, 0) };
        var close = new Button { Style = SettingsPage.Res("Toolbox.Button.Icon"), Content = SettingsPage.Icon("close", 12), ToolTip = "Close (Esc)" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(new Border { Background = Brushes.Transparent });
        MakeDragHandle(top);
        right.Children.Add(top);
        Grid.SetRow(_scroll, 1);
        right.Children.Add(_scroll);

        var content = new Border { Child = right, CornerRadius = new CornerRadius(8, 0, 0, 0), BorderThickness = new Thickness(1, 1, 0, 0) };
        content.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        content.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        Grid.SetColumn(content, 1);
        root.Children.Add(content);

        UpdateDots();
        return root;
    }

    private RadioButton NavItem(string id, string text, string icon, bool dot)
    {
        var row = new DockPanel();
        var glyph = SettingsPage.Icon(icon, 16);
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
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var item = new RadioButton { Style = SettingsPage.Res("Toolbox.NavItem"), GroupName = "amirlap-nav", Content = row };
        System.Windows.Automation.AutomationProperties.SetName(item, text);
        item.Checked += (_, _) => Render(id);
        _nav[id] = item;
        return item;
    }

    private void UpdateDots()
    {
        foreach (var tool in _tools.Tools)
        {
            if (!_dots.TryGetValue(tool.Id, out var dot)) continue;
            bool on = _tools.IsEnabled(tool);
            dot.SetResourceReference(Shape.FillProperty, on ? "Brush.Success" : "Brush.TextDisabled");
            dot.ToolTip = on ? $"{tool.Name} is on" : $"{tool.Name} is off";
        }
    }

    private void OnToolsChanged()
    {
        UpdateDots();
        if (_page == DashboardPage) Render(DashboardPage, keepScroll: true);
    }

    private void Render(string id, bool keepScroll = false)
    {
        _page = id;
        double offset = _scroll.VerticalOffset;
        var page = new SettingsPage(_host);
        if (id == DashboardPage) BuildDashboard(page);
        else if (id == GeneralPage) BuildGeneral(page);
        else if (_tools.Find(id) is { } tool) BuildTool(page, tool);
        _scroll.Content = page.Root;
        if (keepScroll) _scroll.ScrollToVerticalOffset(offset);
        else _scroll.ScrollToTop();
    }

    // ---- pages -------------------------------------------------------------------------------

    private void BuildDashboard(SettingsPage page)
    {
        page.Header("dashboard", "Dashboard", "Your tools at a glance: switch them on or off, open them, and see every shortcut.");

        var cards = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var tool in _tools.Tools) cards.Children.Add(ToolCard(tool));
        page.Add(cards);

        page.Section("All shortcuts");
        page.Card();
        var settings = page.Current;
        foreach (var tool in _tools.Tools)
        {
            bool on = _tools.IsEnabled(tool);
            foreach (var command in tool.Commands)
            {
                var chord = _tools.ChordOf(tool, command);
                FrameworkElement right = (on, chord, _tools.StateOf(tool, command)) switch
                {
                    (_, null, _) => Muted("Not set"),
                    (false, { } c, _) => Faded(SettingsPage.Keys(c.Parts())),
                    (true, { } c, HotkeyState.Taken) => Warning($"{c} is used by another program"),
                    (true, { } c, HotkeyState.Invalid) => Warning($"{c} can't be used as a shortcut"),
                    (true, { } c, _) => SettingsPage.Keys(c.Parts()),
                };
                page.Row(command.Title, right, on ? tool.Name : $"{tool.Name} (off)");
            }
            foreach (var gesture in tool.Gestures(settings))
            {
                FrameworkElement keys = SettingsPage.Keys(gesture.Keys.Take(gesture.Keys.Count - 1));
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(keys);
                line.Children.Add(new TextBlock { Text = gesture.Keys[^1], Style = SettingsPage.Res("Text.Label"), Margin = new Thickness(2, 0, 0, 0) });
                page.Row(gesture.Title, on ? line : Faded(line), on ? tool.Name : $"{tool.Name} (off)");
            }
        }
        page.Note("Change a shortcut on its tool's page. Shortcuts work from anywhere in Windows while the tool is on.");
    }

    private Border ToolCard(ToolModule tool)
    {
        bool on = _tools.IsEnabled(tool);
        var stack = new StackPanel();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var sw = SettingsPage.Switch(on, v => _tools.SetEnabled(tool, v));
        sw.Content = null;
        System.Windows.Automation.AutomationProperties.SetName(sw, $"{tool.Name} on or off");
        DockPanel.SetDock(sw, Dock.Right);
        head.Children.Add(sw);
        var icon = SettingsPage.Icon(tool.Icon, 20, accent: true);
        icon.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = tool.Name, Style = SettingsPage.Res("Text.Title"), FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        stack.Children.Add(head);

        stack.Children.Add(new TextBlock { Text = tool.Summary, Style = SettingsPage.Res("Text.Caption"), FontSize = 12, Height = 34, Margin = new Thickness(0, 0, 0, 10) });

        var foot = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (on) buttons.Children.Add(SettingsPage.Button("Open", tool.Open, accent: true));
        var settingsButton = SettingsPage.Button("Settings", () => ShowPage(tool.Id));
        settingsButton.Margin = new Thickness(on ? 6 : 0, 0, 0, 0);
        buttons.Children.Add(settingsButton);
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        if (tool.Commands.Count > 0 && _tools.ChordOf(tool, tool.Commands[0]) is { } chord)
            foot.Children.Add(on ? SettingsPage.Keys(chord.Parts(), 11.5) : Faded(SettingsPage.Keys(chord.Parts(), 11.5)));
        else
            foot.Children.Add(new Border());
        stack.Children.Add(foot);

        return new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Width = 330, Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 10, 10), Child = stack };
    }

    private void BuildTool(SettingsPage page, ToolModule tool)
    {
        bool on = _tools.IsEnabled(tool);
        page.Header(tool.Icon, tool.Name, tool.Summary);
        page.Card();
        page.Toggle($"Turn on {tool.Name}", on, v => _tools.SetEnabled(tool, v),
            "When it's off, its shortcuts are released and it uses no memory or keyboard watching.");

        if (tool.Commands.Count > 0 || tool.Gestures(page.Current).Count > 0)
        {
            page.Section("Shortcuts");
            page.Card();
            foreach (var command in tool.Commands)
                page.Row(command.Title, new ShortcutBox(_tools, tool, command), command.DefaultShortcut is { } d ? $"Default: {d}" : null);
            foreach (var gesture in tool.Gestures(page.Current))
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal };
                line.Children.Add(SettingsPage.Keys(gesture.Keys.Take(gesture.Keys.Count - 1)));
                line.Children.Add(new TextBlock { Text = gesture.Keys[^1], Style = SettingsPage.Res("Text.Label"), Margin = new Thickness(2, 0, 0, 0) });
                page.Row(gesture.Title, line, "Change the key below.");
            }
        }

        tool.BuildSettings(page);
    }

    private void BuildGeneral(SettingsPage page)
    {
        page.Header("sliders", "General", "Settings for Amir Lap as a whole.");

        page.Section("Appearance");
        page.Card();
        page.Row("Theme", SettingsPage.Choice(["system", "light", "dark"], page.Current.Theme, v =>
        {
            page.Change(s => s.Theme = v);
            ThemeResources.Apply(Application.Current, v);
        }, v => v switch { "system" => "Windows setting", "light" => "Light", _ => "Dark" }));
        page.Row("Reduce motion", SettingsPage.Choice(["system", "on", "off"], page.Current.ReduceMotion, v => page.Change(s => s.ReduceMotion = v),
            v => v switch { "system" => "Windows setting", "on" => "On", _ => "Off" }));

        page.Section("Start-up and tray");
        page.Card();
        page.Toggle("Start with Windows", ShellIntegration.IsStartWithWindows, on =>
        {
            ShellIntegration.SetStartWithWindows(on);
            page.Change(s => s.StartWithWindows = on);
        }, "Starts quietly in the notification area, so your tools and shortcuts are always ready.");
        page.Row("Clicking the tray icon opens", SettingsPage.Choice(["home", "palette"], page.Current.TrayClick, v => page.Change(s => s.TrayClick = v),
            v => v == "home" ? "This window" : "File Toolbox"), "Right-click the tray icon for every tool's menu.");

        page.Section("Privacy");
        page.Card();
        page.Note("Everything runs on this computer. Amir Lap does not upload files, screenshots, extracted text or usage data, and needs no account.");
        page.Row("Settings, history, recipes and logs", SettingsPage.Button("Open data folder",
            () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true })));

        page.Section("About");
        page.Card();
        var version = typeof(AmirLapWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
        page.Row("Amir Lap", new TextBlock { Text = "Version " + version, Style = SettingsPage.Res("Text.Label") }, "Your desktop tools in one place: " + string.Join(", ", _tools.Tools.Select(t => t.Name)) + ".");
    }

    // ---- small helpers -----------------------------------------------------------------------

    private static FrameworkElement Faded(FrameworkElement element)
    {
        element.Opacity = 0.45;
        return element;
    }

    private static TextBlock Muted(string text) => new() { Text = text, Style = SettingsPage.Res("Text.Label") };

    private static TextBlock Warning(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12 };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        return t;
    }
}
