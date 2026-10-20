using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Core;
using Toolbox.Core.Output;
using Toolbox.Core.Settings;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Studio;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Settings;

/// <summary>Preferences. Every change is saved immediately to the local settings file.</summary>
internal sealed class SettingsWindow : GlassWindow
{
    private readonly ToolboxHost _host;
    private readonly bool _hotkeyRegistered;
    private readonly StackPanel _body = new();
    private readonly TextBlock _namePreview = new();

    public SettingsWindow(ToolboxHost host, bool hotkeyRegistered)
    {
        _host = host;
        _hotkeyRegistered = hotkeyRegistered;
        Title = "File Toolbox settings";
        Width = 620;
        Height = 720;
        MinHeight = 420;
        Resizable = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build();
    }

    private AppSettings S => _host.Settings.Current;

    private void Change(Action<AppSettings> edit)
    {
        var next = S.Clone();
        edit(next);
        _host.Settings.Save(next);
        UpdateNamePreview();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(18, 14, 8, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 10, 8) };
        var close = new Button { Style = StudioWindow.Res("Toolbox.Button.Icon"), Content = StudioWindow.IconPath("close", 12), ToolTip = "Close" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Settings", Style = StudioWindow.Res("Text.Title"), FontSize = 16 });
        MakeDragHandle(header);
        root.Children.Add(header);

        BuildSections();
        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 10, 0) };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private void BuildSections()
    {
        // Wheel
        Section("Wheel");
        Row("Open the wheel with", Choice(["Shift", "Ctrl", "Alt", "CapsLock"], S.ActivationKey, v => Change(s => s.ActivationKey = v)),
            "Tap this key while dragging a file. Explorer also uses Shift/Ctrl/Alt to show move/copy/link while dragging; the wheel always copies, so originals are never moved.");
        Row("Wheel size", Choice(["S", "M", "L"], S.WheelSize, v => Change(s => s.WheelSize = v)), "Takes effect after restarting File Toolbox.");
        Row("Theme", Choice(["system", "light", "dark"], S.Theme, v => { Change(s => s.Theme = v); ThemeResources.Apply(Application.Current, v); }));
        Row("Reduce motion", Choice(["system", "on", "off"], S.ReduceMotion, v => Change(s => s.ReduceMotion = v)), "\"system\" follows Windows' animation setting.");

        // Output
        Section("Results");
        var beside = new RadioButton { Content = "Next to the original file", IsChecked = S.OutputLocation == OutputLocation.BesideSource, Style = StudioWindow.Res("Toolbox.Radio"), GroupName = "out", Margin = new Thickness(0, 0, 0, 6) };
        var folder = new RadioButton { Content = "Always in this folder:", IsChecked = S.OutputLocation == OutputLocation.Folder, Style = StudioWindow.Res("Toolbox.Radio"), GroupName = "out" };
        var folderBox = new TextBox { Text = S.OutputFolder ?? "", Margin = new Thickness(26, 6, 0, 0), IsReadOnly = true };
        var pick = new Button { Content = "Choose folder…", Margin = new Thickness(26, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = S.OutputFolder ?? AppPaths.DefaultFallbackOutputDir, UseDescriptionForTitle = true, Description = "Save results in" };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                folderBox.Text = dialog.SelectedPath;
                folder.IsChecked = true;
                Change(s => { s.OutputFolder = dialog.SelectedPath; s.OutputLocation = OutputLocation.Folder; });
            }
        };
        beside.Checked += (_, _) => Change(s => s.OutputLocation = OutputLocation.BesideSource);
        folder.Checked += (_, _) => { if (!string.IsNullOrWhiteSpace(S.OutputFolder)) Change(s => s.OutputLocation = OutputLocation.Folder); };
        _body.Children.Add(beside);
        _body.Children.Add(folder);
        _body.Children.Add(folderBox);
        _body.Children.Add(pick);
        _body.Children.Add(Caption($"If the chosen place cannot be written to (read-only, offline), results go to {S.ToOutputPolicy().FallbackFolder} and the result says so. Existing files are never overwritten: a number is added instead."));

        var pattern = new TextBox { Text = S.NamingPattern, Margin = new Thickness(0, 4, 0, 0) };
        pattern.TextChanged += (_, _) => Change(s => s.NamingPattern = pattern.Text);
        Row("File names", pattern, "Tokens: {name} {suffix} {page} {w} {h} {date} {time}. Default: {name}{suffix}{page}");
        _namePreview.Style = StudioWindow.Res("Text.Caption");
        _body.Children.Add(_namePreview);
        UpdateNamePreview();

        // Quality
        Section("Default quality");
        Row("JPG quality", Slider(40, 100, S.JpegQuality, v => Change(s => s.JpegQuality = v)));
        Row("WebP quality", Slider(40, 100, S.WebpQuality, v => Change(s => s.WebpQuality = v)));
        Row("AVIF quality", Slider(30, 100, S.AvifQuality, v => Change(s => s.AvifQuality = v)));
        Row("PDF pages at", Choice(["150", "200", "300", "600"], S.PdfDpi.ToString(CultureInfo.InvariantCulture), v => Change(s => s.PdfDpi = int.Parse(v, CultureInfo.InvariantCulture))), "DPI used when turning PDF pages into pictures.");
        var fill = new TextBox { Text = S.TransparencyBackground, Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        fill.LostKeyboardFocus += (_, _) => Change(s => s.TransparencyBackground = fill.Text.Trim());
        Row("Fill for transparency", fill, "Colour used where a transparent picture is saved as JPG or BMP (#RRGGBB).");

        // History
        Section("History");
        Row("Keep for", Choice(["7", "30", "90", "365"], S.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture), v => Change(s => s.HistoryRetentionDays = int.Parse(v, CultureInfo.InvariantCulture))), "Days. History stores file paths, settings and outcomes — never file contents.");
        var clear = new Button { Content = "Clear history", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0) };
        clear.Click += (_, _) => { _host.History.Clear(); clear.Content = "History cleared"; clear.IsEnabled = false; };
        _body.Children.Add(clear);

        // Windows integration
        Section("Windows");
        Toggle("Add \"File Toolbox…\" to Explorer's right-click menu", ShellIntegration.IsContextMenuRegistered(), on => { ShellIntegration.SetContextMenu(on); Change(s => s.ExplorerContextMenu = on); },
            "On Windows 11 it appears under \"Show more options\". Only for supported file types.");
        Toggle("Add File Toolbox to \"Send to\"", ShellIntegration.IsSendToRegistered, on => ShellIntegration.SetSendTo(on));
        Toggle("Start with Windows", ShellIntegration.IsStartWithWindows, on => { ShellIntegration.SetStartWithWindows(on); Change(s => s.StartWithWindows = on); },
            "Starts in the notification area so the wheel is always ready.");
        _body.Children.Add(Caption(_hotkeyRegistered
            ? "Ctrl+Alt+F opens the File Toolbox window from anywhere."
            : "Ctrl+Alt+F is used by another program, so the shortcut is unavailable; use the tray icon instead."));

        // Privacy
        Section("Privacy");
        _body.Children.Add(Caption("Everything runs on this computer. File Toolbox does not upload files, extracted text, thumbnails or usage data, and needs no account."));
        var openData = new Button { Content = "Open data folder", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        openData.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
        _body.Children.Add(openData);
    }

    private void UpdateNamePreview()
    {
        var sample = OutputNamer.Compose(S.NamingPattern, new NameParts("Report", " Cropped", ".png", 3, 12, 1920, 1080));
        _namePreview.Text = $"Example: {sample}";
    }

    // ---- building blocks ------------------------------------------------------------------------

    private void Section(string title) => _body.Children.Add(new TextBlock { Text = title, Style = StudioWindow.Res("Text.Title"), FontSize = 14, Margin = new Thickness(0, 18, 0, 8) });

    private void Row(string label, FrameworkElement control, string? help = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        _body.Children.Add(grid);
        if (help is not null) _body.Children.Add(new TextBlock { Text = help, Style = StudioWindow.Res("Text.Caption"), Margin = new Thickness(170, 2, 0, 4) });
    }

    private void Toggle(string label, bool value, Action<bool> changed, string? help = null)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 6, 0, 2) };
        check.Checked += (_, _) => Safe(() => changed(true), check);
        check.Unchecked += (_, _) => Safe(() => changed(false), check);
        _body.Children.Add(check);
        if (help is not null) _body.Children.Add(new TextBlock { Text = help, Style = StudioWindow.Res("Text.Caption"), Margin = new Thickness(26, 0, 0, 4) });
    }

    private static void Safe(Action action, CheckBox check)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            check.ToolTip = "Could not change this setting: " + ex.Message;
        }
    }

    private static FrameworkElement Choice(string[] options, string current, Action<string> changed)
    {
        var bar = new Border { Style = (Style)Application.Current.Resources["Toolbox.SegmentBar"], HorizontalAlignment = HorizontalAlignment.Left };
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var group = Guid.NewGuid().ToString("N");
        foreach (var option in options)
        {
            var rb = new RadioButton { Content = option, Style = StudioWindow.Res("Toolbox.Segment"), GroupName = group, IsChecked = string.Equals(option, current, StringComparison.OrdinalIgnoreCase) };
            rb.Checked += (_, _) => changed(option);
            stack.Children.Add(rb);
        }
        bar.Child = stack;
        return bar;
    }

    private static FrameworkElement Slider(int min, int max, int value, Action<int> changed)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var slider = new System.Windows.Controls.Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock { Text = value.ToString(CultureInfo.InvariantCulture), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { text.Text = ((int)e.NewValue).ToString(CultureInfo.InvariantCulture); changed((int)e.NewValue); };
        grid.Children.Add(slider);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private static TextBlock Caption(string text) => new() { Text = text, Style = StudioWindow.Res("Text.Caption"), Margin = new Thickness(0, 6, 0, 2) };
}
