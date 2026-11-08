using Toolbox.Core.Settings;
using Toolbox.Windows.Home;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Tests;

public class VirtualKeyTests
{
    public static IEnumerable<object[]> AllKeyNames()
    {
        foreach (var name in KeyChord.NamedKeys) yield return [name];
        for (char c = 'A'; c <= 'Z'; c++) yield return [c.ToString()];
        for (char c = '0'; c <= '9'; c++) yield return [c.ToString()];
        for (int f = 1; f <= 24; f++) yield return ["F" + f];
    }

    [Theory]
    [MemberData(nameof(AllKeyNames))]
    public void Every_key_a_shortcut_can_use_maps_to_a_virtual_key_and_back(string name)
    {
        Assert.True(VirtualKeys.TryGetCode(name, out uint vk), name);
        Assert.Equal(name, VirtualKeys.NameOf((int)vk));
    }

    [Fact]
    public void Modifier_and_typing_keys_cannot_end_a_shortcut()
    {
        Assert.Null(VirtualKeys.NameOf(0x10)); // Shift
        Assert.Null(VirtualKeys.NameOf(0x11)); // Ctrl
        Assert.Null(VirtualKeys.NameOf(0x0D)); // Enter
        Assert.Null(VirtualKeys.NameOf(0x1B)); // Esc
    }
}

public class ToolRegistryTests : IDisposable
{
    // Unusual chords, so the test never takes a shortcut a real program on this machine relies on.
    private const string ChordA = "Ctrl+Alt+Shift+F23";
    private const string ChordB = "Ctrl+Alt+Shift+F22";

    private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"amirlap-registry-{Guid.NewGuid():N}.json");

    public void Dispose() => System.IO.File.Delete(_path);

    private sealed class FakeTool(string id, params ToolCommand[] commands) : ToolModule
    {
        public int Starts, Stops;
        public string Restart = "1";
        public override string Id => id;
        public override string Name => "Fake " + id;
        public override string Summary => "";
        public override string Icon => "image";
        public override IReadOnlyList<ToolCommand> Commands => commands;
        public override string RestartKey(AppSettings settings) => Restart;
        protected override void OnStart() => Starts++;
        protected override void OnStop() => Stops++;
        public override void Open() { }
        public override void BuildSettings(SettingsPage page) { }
        public override IEnumerable<TrayItem> TrayItems(Func<ToolCommand, string?> shortcutText) => [];
    }

    private static void Sta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Edit(SettingsStore store, Action<AppSettings> edit)
    {
        var next = store.Current.Clone();
        edit(next);
        store.Save(next);
    }

    [Fact]
    public void Switching_a_tool_off_stops_it_and_releases_its_shortcuts() => Sta(() =>
    {
        var store = new SettingsStore(_path);
        var cmd = new ToolCommand("go", "Go", ChordA, () => { });
        var tool = new FakeTool("fake", cmd);
        using var registry = new ToolRegistry(store, [tool]);

        registry.Apply(force: true);
        Assert.True(tool.IsRunning);
        Assert.Equal(HotkeyState.Active, registry.StateOf(tool, cmd));

        Edit(store, s => s.EditTool("fake").Enabled = false);
        registry.Apply();
        Assert.False(tool.IsRunning);
        Assert.Equal(1, tool.Stops);
        Assert.Equal(HotkeyState.Off, registry.StateOf(tool, cmd));
        Assert.True(registry.IsAvailable(KeyChord.Parse(ChordA)), "the shortcut must be released while the tool is off");

        Edit(store, s => s.EditTool("fake").Enabled = true);
        registry.Apply();
        Assert.True(tool.IsRunning);
        Assert.Equal(2, tool.Starts);
    });

    [Fact]
    public void Unrelated_setting_changes_do_not_restart_tools() => Sta(() =>
    {
        var store = new SettingsStore(_path);
        var tool = new FakeTool("fake", new ToolCommand("go", "Go", ChordA, () => { }));
        using var registry = new ToolRegistry(store, [tool]);
        registry.Apply(force: true);

        Edit(store, s => s.JpegQuality = 70);
        registry.Apply();
        Assert.Equal((1, 0), (tool.Starts, tool.Stops));

        tool.Restart = "2"; // e.g. the wheel size changed
        registry.Apply();
        Assert.Equal((2, 1), (tool.Starts, tool.Stops));
    });

    [Fact]
    public void The_same_shortcut_on_two_commands_is_reported_not_registered_twice() => Sta(() =>
    {
        var store = new SettingsStore(_path);
        var first = new ToolCommand("one", "First", ChordA, () => { });
        var second = new ToolCommand("two", "Second", ChordA, () => { });
        var tool = new FakeTool("fake", first, second);
        using var registry = new ToolRegistry(store, [tool]);
        registry.Apply(force: true);

        Assert.Equal(HotkeyState.Active, registry.StateOf(tool, first));
        Assert.Equal(HotkeyState.Taken, registry.StateOf(tool, second));
        Assert.Equal("First", registry.UsedByAnotherCommand(KeyChord.Parse(ChordA), tool, second));
    });

    [Fact]
    public void Shortcuts_can_be_changed_switched_off_and_reset() => Sta(() =>
    {
        var store = new SettingsStore(_path);
        var cmd = new ToolCommand("go", "Go", ChordA, () => { });
        var tool = new FakeTool("fake", cmd);
        using var registry = new ToolRegistry(store, [tool]);
        registry.Apply(force: true);

        registry.SetShortcut(tool, cmd, ChordB);
        registry.Apply();
        Assert.Equal(ChordB, registry.ActiveShortcut(tool, cmd));

        registry.SetShortcut(tool, cmd, "");
        registry.Apply();
        Assert.Null(registry.ChordOf(tool, cmd));
        Assert.Equal(HotkeyState.Off, registry.StateOf(tool, cmd));

        registry.SetShortcut(tool, cmd, null);
        registry.Apply();
        Assert.Equal(ChordA, registry.ActiveShortcut(tool, cmd));
    });

    [Fact]
    public void A_shortcut_held_elsewhere_gets_a_free_alternative_offered() => Sta(() =>
    {
        var otherPath = _path + ".other.json";
        try
        {
            // Another "program" (a second registry with its own window) holds ChordA first.
            var holder = new FakeTool("holder", new ToolCommand("hold", "Hold", ChordA, () => { }));
            using var other = new ToolRegistry(new SettingsStore(otherPath), [holder]);
            other.Apply(force: true);

            var cmd = new ToolCommand("go", "Go", ChordA, () => { }) { Alternatives = [ChordB] };
            var tool = new FakeTool("fake", cmd);
            using var registry = new ToolRegistry(new SettingsStore(_path), [tool]);
            registry.Apply(force: true);

            Assert.Equal(HotkeyState.Taken, registry.StateOf(tool, cmd));
            Assert.Equal(ChordB, registry.SuggestFree(tool, cmd)?.ToString());

            registry.SetShortcut(tool, cmd, ChordB);
            registry.Apply();
            Assert.Equal(HotkeyState.Active, registry.StateOf(tool, cmd));
        }
        finally
        {
            System.IO.File.Delete(otherPath);
        }
    });

    [Fact]
    public void Pausing_releases_every_shortcut_until_resumed() => Sta(() =>
    {
        var store = new SettingsStore(_path);
        var tool = new FakeTool("fake", new ToolCommand("go", "Go", ChordA, () => { }));
        using var registry = new ToolRegistry(store, [tool]);
        registry.Apply(force: true);

        using (registry.PauseShortcuts())
            Assert.True(registry.IsAvailable(KeyChord.Parse(ChordA)));
        Assert.False(registry.IsAvailable(KeyChord.Parse(ChordA)), "taken back after the pause");
    });
}
