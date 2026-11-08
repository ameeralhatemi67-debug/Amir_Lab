using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Output;
using Toolbox.Core.Settings;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Drag;
using Toolbox.Windows.Home;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Wheel;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Tool #1: the file wheel (drag a file, tap the activation key), the keyboard palette, recipes,
/// editors and history. While it is off, the keyboard watcher is removed and the wheel window closed.
/// </summary>
internal sealed class FileToolboxTool : ToolModule
{
    public const string ToolId = "fileToolbox";

    private readonly ToolboxHost _host;
    private readonly ActionDispatcher _dispatcher;
    private readonly DiagnosticsChannel _diagnostics;
    private WheelOverlayWindow? _overlay;
    private DragActivationService? _drag;

    public FileToolboxTool(ToolboxHost host, ActionDispatcher dispatcher, DiagnosticsChannel diagnostics)
    {
        _host = host;
        _dispatcher = dispatcher;
        _diagnostics = diagnostics;
        Commands = [new ToolCommand("palette", "Open the File Toolbox window", "Ctrl+Alt+F", () => App.ShowPalette())];
    }

    public override string Id => ToolId;
    public override string Name => "File Toolbox";
    public override string Summary => "Convert, edit and read files. Drag a file and tap a key to open the wheel.";
    public override string Icon => "toolbox";
    public override IReadOnlyList<ToolCommand> Commands { get; }

    public override IReadOnlyList<ToolGesture> Gestures(AppSettings settings) =>
        [new ToolGesture("Open the wheel while dragging a file", [settings.ActivationKey, "tap"])];

    public override string RestartKey(AppSettings settings) => settings.WheelSize;

    protected override void OnStart()
    {
        var settings = _host.Settings.Current;
        _overlay = new WheelOverlayWindow(App.WheelDiameter(settings.WheelSize));
        _overlay.Show();
        _overlay.MoveOffscreen();
        _drag = new DragActivationService(_overlay, () => _host.Settings.Current, () => _host.Recipes.All().Where(r => r.Pinned).ToList(), _diagnostics);
        _drag.Dropped += request => _dispatcher.Dispatch(request.ActionId, request.Paths, request.ScreenPoint);
        _drag.Start();
        _drag.WarmUp();
    }

    protected override void OnStop()
    {
        _drag?.Dispose();
        _drag = null;
        _overlay?.Close();
        _overlay = null;
        App.CloseFileToolboxWindows();
    }

    public override void Open() => App.ShowPalette();

    public override IEnumerable<TrayItem> TrayItems(Func<ToolCommand, string?> shortcutText)
    {
        var last = _host.LastAction is { } la && CapabilityMap.TryGet(la.ActionId, out var action) ? action : null;
        yield return new TrayItem("Open File Toolbox", () => App.ShowPalette(), Shortcut: shortcutText(Commands[0]));
        yield return last is null
            ? new TrayItem("Repeat last action")
            : new TrayItem($"Repeat: {last.Title}…", () => App.ShowPalette(preselect: last.Id));
        yield return new TrayItem("Recipes", App.ShowRecipes);
        yield return new TrayItem("History", App.ShowHistory);
    }

    public override void BuildSettings(SettingsPage page)
    {
        var s = page.Current;

        page.Section("Wheel");
        page.Card();
        page.Row("Open the wheel with", SettingsPage.Choice(["Shift", "Ctrl", "Alt", "CapsLock"], s.ActivationKey, v => page.Change(x => x.ActivationKey = v)),
            "Tap this key while dragging a file. The wheel always copies, so originals are never moved.");
        page.Row("Wheel size", SettingsPage.Choice(["S", "M", "L"], s.WheelSize, v => page.Change(x => x.WheelSize = v)));

        page.Section("Results");
        page.Card();
        var beside = new RadioButton { Content = "Next to the original file", IsChecked = s.OutputLocation == OutputLocation.BesideSource, Style = SettingsPage.Res("Toolbox.Radio"), GroupName = "out", Margin = new Thickness(0, 0, 0, 6) };
        var folder = new RadioButton { Content = "Always in this folder:", IsChecked = s.OutputLocation == OutputLocation.Folder, Style = SettingsPage.Res("Toolbox.Radio"), GroupName = "out" };
        var folderBox = new TextBox { Text = s.OutputFolder ?? "", Margin = new Thickness(26, 6, 0, 0), IsReadOnly = true };
        var pick = new Button { Content = "Choose folder…", Margin = new Thickness(26, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = page.Current.OutputFolder ?? AppPaths.DefaultFallbackOutputDir, UseDescriptionForTitle = true, Description = "Save results in" };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                folderBox.Text = dialog.SelectedPath;
                folder.IsChecked = true;
                page.Change(x => { x.OutputFolder = dialog.SelectedPath; x.OutputLocation = OutputLocation.Folder; });
            }
        };
        beside.Checked += (_, _) => page.Change(x => x.OutputLocation = OutputLocation.BesideSource);
        folder.Checked += (_, _) => { if (!string.IsNullOrWhiteSpace(page.Current.OutputFolder)) page.Change(x => x.OutputLocation = OutputLocation.Folder); };
        var where = new StackPanel();
        where.Children.Add(beside);
        where.Children.Add(folder);
        where.Children.Add(folderBox);
        where.Children.Add(pick);
        page.Stacked("Where results are saved", where,
            $"If that place cannot be written to (read-only, offline), results go to {s.ToOutputPolicy().FallbackFolder} and the result says so. Existing files are never overwritten: a number is added instead.");

        var preview = new TextBlock { Style = SettingsPage.Res("Text.Caption"), Margin = new Thickness(0, 6, 0, 0) };
        void UpdatePreview() => preview.Text = "Example: " + OutputNamer.Compose(page.Current.NamingPattern, new NameParts("Report", " Cropped", ".png", 3, 12, 1920, 1080));
        var pattern = new TextBox { Text = s.NamingPattern };
        pattern.TextChanged += (_, _) => { page.Change(x => x.NamingPattern = pattern.Text); UpdatePreview(); };
        UpdatePreview();
        var naming = new StackPanel();
        naming.Children.Add(pattern);
        naming.Children.Add(preview);
        page.Stacked("File names", naming, "Tokens: {name} {suffix} {page} {w} {h} {date} {time}. Default: {name}{suffix}{page}");

        page.Section("Default quality");
        page.Card();
        page.Row("JPG quality", SettingsPage.Slider(40, 100, s.JpegQuality, v => page.Change(x => x.JpegQuality = v)));
        page.Row("WebP quality", SettingsPage.Slider(40, 100, s.WebpQuality, v => page.Change(x => x.WebpQuality = v)));
        page.Row("AVIF quality", SettingsPage.Slider(30, 100, s.AvifQuality, v => page.Change(x => x.AvifQuality = v)));
        page.Row("PDF pages at", SettingsPage.Choice(["150", "200", "300", "600"], s.PdfDpi.ToString(CultureInfo.InvariantCulture), v => page.Change(x => x.PdfDpi = int.Parse(v, CultureInfo.InvariantCulture))),
            "DPI used when turning PDF pages into pictures.");
        var fill = new TextBox { Text = s.TransparencyBackground, Width = 100 };
        fill.LostKeyboardFocus += (_, _) => page.Change(x => x.TransparencyBackground = fill.Text.Trim());
        page.Row("Fill for transparency", fill, "Colour used where a transparent picture is saved as JPG or BMP (#RRGGBB).");

        page.Section("History");
        page.Card();
        page.Row("Keep for", SettingsPage.Choice(["7", "30", "90", "365"], s.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture), v => page.Change(x => x.HistoryRetentionDays = int.Parse(v, CultureInfo.InvariantCulture)), d => d + " days"),
            "History stores file paths, settings and outcomes, never file contents.");
        var historyButtons = new StackPanel { Orientation = Orientation.Horizontal };
        historyButtons.Children.Add(SettingsPage.Button("Open history", App.ShowHistory));
        var clear = SettingsPage.Button("Clear history", () => { });
        clear.Margin = new Thickness(6, 0, 0, 0);
        clear.Click += (_, _) => { _host.History.Clear(); clear.Content = "History cleared"; clear.IsEnabled = false; };
        historyButtons.Children.Add(clear);
        page.Row("Past jobs", historyButtons);

        page.Section("Recipes and brand styles");
        page.Card();
        page.Row("Saved steps you can run on any file from the wheel's third ring", SettingsPage.Button("Open recipes", App.ShowRecipes));

        page.Section("Windows");
        page.Card();
        page.Toggle("Add \"File Toolbox…\" to Explorer's right-click menu", ShellIntegration.IsContextMenuRegistered(),
            on => { ShellIntegration.SetContextMenu(on); page.Change(x => x.ExplorerContextMenu = on); },
            "On Windows 11 it appears under \"Show more options\". Only for supported file types.");
        page.Toggle("Add File Toolbox to \"Send to\"", ShellIntegration.IsSendToRegistered, ShellIntegration.SetSendTo);
    }
}
