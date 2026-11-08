using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Toolbox.Core.Settings;
using Toolbox.Windows.Home;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Shell;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Tool #2: scrolling screenshots. Draw a box, then Extend / Auto / Follow to grow it downward.
/// While on, it only holds its shortcuts; nothing runs until a capture starts.
/// </summary>
internal sealed class LongshotTool : ToolModule
{
    public const string ToolId = "longshot";

    private readonly ToolboxHost _host;

    public LongshotTool(ToolboxHost host)
    {
        _host = host;
        Commands =
        [
            new ToolCommand("capture", "Start a scrolling capture", "Ctrl+Alt+S", () => Capture()),
            new ToolCommand("captureDelayed", "Scrolling capture after a delay", "Ctrl+Alt+Shift+S", () => Capture(delayed: true)),
            new ToolCommand("repeat", "Capture the same area again", null, CaptureSameArea),
        ];
    }

    public override string Id => ToolId;
    public override string Name => "Longshot";
    public override string Summary => "Scrolling screenshots. Draw a box, then extend it downward to capture a whole page.";
    public override string Icon => "longshot";
    public override IReadOnlyList<ToolCommand> Commands { get; }

    /// <summary>The last area captured (for "Capture the same area again").</summary>
    public PixelRect? LastArea { get; set; }

    protected override void OnStart() { } // nothing to load: a capture starts from scratch each time

    protected override void OnStop() => LongshotCapture.CancelActive();

    public void Capture(bool delayed = false) =>
        LongshotCapture.Start(_host, this, delaySeconds: delayed ? Math.Clamp(_host.Settings.Current.Longshot.DelaySeconds, 1, 30) : 0);

    private void CaptureSameArea()
    {
        if (LastArea is { } area) LongshotCapture.Start(_host, this, area);
        else Capture();
    }

    /// <summary>From the Dashboard: get the Amir Lap window out of the way first, then capture.</summary>
    public override void Open()
    {
        App.MinimizeHome();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); Capture(); };
        timer.Start();
    }

    public override IEnumerable<TrayItem> TrayItems(Func<ToolCommand, string?> shortcutText)
    {
        yield return new TrayItem("Start a scrolling capture", () => Capture(), Shortcut: shortcutText(Commands[0]));
        yield return new TrayItem($"Capture after {_host.Settings.Current.Longshot.DelaySeconds} seconds", () => Capture(delayed: true), Shortcut: shortcutText(Commands[1]));
        yield return new TrayItem("Capture the same area again", LastArea is null ? null : CaptureSameArea, Shortcut: shortcutText(Commands[2]));
        yield return new TrayItem("Open screenshots folder", OpenFolder);
    }

    private string Folder => _host.Settings.Current.Longshot.SaveFolder is { Length: > 0 } f ? f : LongshotOutput.DefaultFolder;

    private void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
    }

    public override void BuildSettings(SettingsPage page)
    {
        var s = page.Current.Longshot;
        void Edit(Action<LongshotSettings> edit) => page.Change(x => edit(x.Longshot));

        page.Section("Capturing");
        page.Card();
        page.Row("After drawing the box", SettingsPage.Choice(["wait", "auto", "follow"], s.AfterSelect, v => Edit(x => x.AfterSelect = v),
            v => v switch { "wait" => "Wait for me", "auto" => "Auto", _ => "Follow" }),
            "Wait for me: press Extend for each step. Auto: scrolls to the bottom by itself. Follow: you scroll, Longshot adds.");
        page.Row("Scroll step", SettingsPage.Choice(["small", "auto", "large"], s.ScrollStep, v => Edit(x => x.ScrollStep = v),
            v => v switch { "small" => "Small", "auto" => "Automatic", _ => "Large" }),
            "Smaller steps line up more reliably on busy pages; larger ones are faster.");
        page.Row("Delay for the delayed shortcut", SettingsPage.Choice(["3", "5", "10"], s.DelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            v => Edit(x => x.DelaySeconds = int.Parse(v, System.Globalization.CultureInfo.InvariantCulture)), v => v + " s"),
            "Time to open a menu or hover something before the capture starts.");

        page.Section("Saving");
        page.Card();
        page.Toggle("Copy to the clipboard", s.CopyToClipboard, on => Edit(x => x.CopyToClipboard = on), "Paste it straight into a chat or document with Ctrl+V.");
        page.Toggle("Show the result window", s.ShowResult, on => Edit(x => x.ShowResult = on), "When it's off, a short notice says where the capture was saved.");
        page.Row("Format", SettingsPage.Choice(["png", "jpg"], s.Format, v => Edit(x => x.Format = v), v => v.ToUpperInvariant()),
            "PNG keeps text sharp. JPG files are smaller.");
        var folderBox = new TextBox { Text = Folder, IsReadOnly = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        buttons.Children.Add(SettingsPage.Button("Choose folder…", () =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = Folder, UseDescriptionForTitle = true, Description = "Save long screenshots in" };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            Edit(x => x.SaveFolder = dialog.SelectedPath);
            folderBox.Text = dialog.SelectedPath;
        }));
        var reset = SettingsPage.Button("Use Pictures\\Screenshots", () => { Edit(x => x.SaveFolder = null); folderBox.Text = LongshotOutput.DefaultFolder; });
        reset.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(reset);
        var open = SettingsPage.Button("Open folder", OpenFolder);
        open.Margin = new Thickness(6, 0, 0, 0);
        buttons.Children.Add(open);
        var where = new StackPanel();
        where.Children.Add(folderBox);
        where.Children.Add(buttons);
        page.Stacked("Save to", where, "Files are named like \"Longshot 2026-11-08 143012.png\". Existing files are never overwritten.");

        page.Section("Good to know");
        page.Card();
        page.Note("Sticky headers and footers (menus, cookie bars) are found automatically and kept once, at the top and bottom.");
        page.Note("Leave the scrollbar out of the box: its moving thumb would show up in every strip.");
        page.Note("Videos, animations and carousels inside the box can stop it. Draw the box around them.");
        page.Note("Longshot's panel and frame never appear in your screenshots, and nothing leaves this computer.");
        page.Note("Apps running as administrator (Task Manager, for example) can't be scrolled by Longshot: Windows blocks it.");
    }
}
