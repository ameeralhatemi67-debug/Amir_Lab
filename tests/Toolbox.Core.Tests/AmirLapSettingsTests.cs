using Toolbox.Core.Settings;

namespace Toolbox.Core.Tests;

public class KeyChordTests
{
    [Theory]
    [InlineData("Ctrl+Alt+F", "Ctrl+Alt+F")]
    [InlineData("alt+ctrl+f", "Ctrl+Alt+F")]
    [InlineData("Control+Shift+s", "Ctrl+Shift+S")]
    [InlineData("Shift+Win+L", "Win+Shift+L")]
    [InlineData("Ctrl + Alt + f12", "Ctrl+Alt+F12")]
    [InlineData("ctrl+alt+pageup", "Ctrl+Alt+PageUp")]
    [InlineData("F13", "F13")]
    public void Parses_and_writes_canonical_text(string text, string canonical)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(canonical, chord.ToString());
        Assert.Equal(chord, KeyChord.Parse(chord.ToString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+F")]
    [InlineData("Hyper+F")]
    [InlineData("Ctrl+Alt+Enter")]
    [InlineData("Ctrl+Alt+F25")]
    [InlineData("Ctrl+Alt")]
    public void Rejects_what_is_not_a_shortcut(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+F", true)]
    [InlineData("Win+S", true)]
    [InlineData("F13", true)]
    [InlineData("Shift+A", false)]   // would take over typing capital A
    [InlineData("A", false)]
    [InlineData("F5", false)]        // apps use it on its own
    public void Global_shortcuts_need_ctrl_alt_or_win(string text, bool usable) =>
        Assert.Equal(usable, KeyChord.Parse(text).IsUsableGlobally);
}

public class ToolSettingsTests
{
    [Fact]
    public void Missing_shortcut_means_default_and_empty_means_off()
    {
        var tool = new ToolSettings();
        Assert.Equal("Ctrl+Alt+F", tool.ShortcutFor("palette", "Ctrl+Alt+F")?.ToString());

        tool.Shortcuts["palette"] = "Ctrl+Shift+F";
        Assert.Equal("Ctrl+Shift+F", tool.ShortcutFor("palette", "Ctrl+Alt+F")?.ToString());

        tool.Shortcuts["palette"] = "";
        Assert.Null(tool.ShortcutFor("palette", "Ctrl+Alt+F"));
    }

    [Fact]
    public void Tool_switches_and_shortcuts_survive_saving()
    {
        var path = Path.Combine(Path.GetTempPath(), $"amirlap-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            var next = store.Current.Clone();
            next.EditTool("fileToolbox").Enabled = false;
            next.EditTool("fileToolbox").Shortcuts["palette"] = "Ctrl+Alt+G";
            next.TrayClick = "palette";
            store.Save(next);

            var reloaded = new SettingsStore(path).Current;
            Assert.False(reloaded.Tool("fileToolbox").Enabled);
            Assert.Equal("Ctrl+Alt+G", reloaded.Tool("fileToolbox").Shortcuts["palette"]);
            Assert.Equal("palette", reloaded.TrayClick);
            Assert.True(reloaded.Tool("longshot").Enabled); // never stored: default
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Longshot_settings_survive_saving_and_default_sensibly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"amirlap-longshot-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            Assert.Equal(("wait", "auto", "png", true, 0.55), (store.Current.Longshot.AfterSelect, store.Current.Longshot.ScrollStep,
                store.Current.Longshot.Format, store.Current.Longshot.CopyToClipboard, store.Current.Longshot.StepFraction));
            var next = store.Current.Clone();
            next.Longshot.AfterSelect = "auto";
            next.Longshot.ScrollStep = "small";
            next.Longshot.SaveFolder = @"D:\Captures";
            store.Save(next);
            Assert.DoesNotContain("stepFraction", File.ReadAllText(path)); // derived, not stored

            var reloaded = new SettingsStore(path).Current.Longshot;
            Assert.Equal(("auto", "small", @"D:\Captures", 0.35), (reloaded.AfterSelect, reloaded.ScrollStep, reloaded.SaveFolder, reloaded.StepFraction));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Settings_from_before_the_rename_load_with_tool_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"amirlap-old-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "activationKey": "Ctrl", "jpegQuality": 77, "theme": "dark" }""");
            var s = new SettingsStore(path).Current;
            Assert.Equal("Ctrl", s.ActivationKey);
            Assert.Equal(77, s.JpegQuality);
            Assert.Equal("home", s.TrayClick);
            Assert.True(s.Tool("fileToolbox").Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class LegacyDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"amirlap-migration-{Guid.NewGuid():N}");
    private string Old => Path.Combine(_root, "FileToolbox");
    private string New => Path.Combine(_root, "AmirLap");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(Old, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void Copies_user_data_once_and_leaves_the_old_folder_alone()
    {
        Write("settings.json", "{}");
        Write("history.jsonl", "line");
        Write(@"recipes\mine.json", "r");
        Write(@"presets\brand-blue.json", "b");
        Write(@"logs\toolbox.log", "not carried over");
        Write("pending-temp-files.txt", "not carried over");

        Assert.Equal(4, LegacyDataMigration.Run(Old, New));
        Assert.True(File.Exists(Path.Combine(New, "settings.json")));
        Assert.True(File.Exists(Path.Combine(New, "recipes", "mine.json")));
        Assert.True(File.Exists(Path.Combine(New, "presets", "brand-blue.json")));
        Assert.False(Directory.Exists(Path.Combine(New, "logs")));
        Assert.False(File.Exists(Path.Combine(New, "pending-temp-files.txt")));
        Assert.True(File.Exists(Path.Combine(Old, "settings.json")), "the old folder must stay intact");

        // Second start: nothing more to do, even if the old folder changed.
        Write(@"recipes\later.json", "r2");
        Assert.Equal(0, LegacyDataMigration.Run(Old, New));
        Assert.False(File.Exists(Path.Combine(New, "recipes", "later.json")));
    }

    [Fact]
    public void Does_nothing_when_Amir_Lap_already_has_settings()
    {
        Write("settings.json", """{ "jpegQuality": 50 }""");
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "settings.json"), """{ "jpegQuality": 95 }""");

        Assert.Equal(0, LegacyDataMigration.Run(Old, New));
        Assert.Contains("95", File.ReadAllText(Path.Combine(New, "settings.json")));
    }

    [Fact]
    public void Does_nothing_when_there_was_no_File_Toolbox_data()
    {
        Write(@"logs\toolbox.log", "only logs");
        Assert.Equal(0, LegacyDataMigration.Run(Old, New));
        Assert.False(Directory.Exists(New));
        Assert.Equal(0, LegacyDataMigration.Run(Path.Combine(_root, "missing"), New));
    }
}
