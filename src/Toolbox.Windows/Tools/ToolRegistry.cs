using Toolbox.Core.Diagnostics;
using Toolbox.Core.Settings;
using Toolbox.Windows.Shell;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Keeps the running tools and the registered shortcuts in line with the settings: whenever a
/// switch or shortcut changes, it starts/stops tools and re-registers shortcuts.
/// </summary>
internal sealed class ToolRegistry : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly HotkeyManager _hotkeys = new();
    private readonly Dictionary<(string Tool, string Command), HotkeyState> _states = [];
    private readonly Dictionary<string, string> _restartKeys = [];
    private string? _signature;
    private bool _paused;

    public ToolRegistry(SettingsStore settings, IReadOnlyList<ToolModule> tools)
    {
        _settings = settings;
        Tools = tools;
        _settings.Changed += _ => ToolboxHost.OnUi(() => Apply());
    }

    public IReadOnlyList<ToolModule> Tools { get; }

    /// <summary>Raised after tools or shortcuts changed (the window and tray refresh on it).</summary>
    public event Action? Changed;

    public ToolModule? Find(string id) => Tools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled(ToolModule tool) => _settings.Current.Tool(tool.Id).Enabled;

    public KeyChord? ChordOf(ToolModule tool, ToolCommand command) =>
        _settings.Current.Tool(tool.Id).ShortcutFor(command.Id, command.DefaultShortcut);

    public HotkeyState StateOf(ToolModule tool, ToolCommand command) =>
        _states.TryGetValue((tool.Id, command.Id), out var s) ? s : HotkeyState.Off;

    /// <summary>The active shortcut as text, for menus ("Ctrl+Alt+F"), or null.</summary>
    public string? ActiveShortcut(ToolModule tool, ToolCommand command) =>
        StateOf(tool, command) == HotkeyState.Active ? ChordOf(tool, command)?.ToString() : null;

    /// <summary>Starts/stops tools and (re)registers shortcuts. Cheap when nothing relevant changed.</summary>
    public void Apply(bool force = false)
    {
        if (_paused) return;
        var s = _settings.Current;
        var signature = string.Join("|", Tools.Select(t =>
            $"{t.Id}:{s.Tool(t.Id).Enabled}:{t.RestartKey(s)}:" + string.Join(",", t.Commands.Select(c => $"{c.Id}={s.Tool(t.Id).ShortcutFor(c.Id, c.DefaultShortcut)}"))));
        if (!force && signature == _signature) return;
        _signature = signature;

        _hotkeys.UnregisterAll();
        _states.Clear();
        var used = new Dictionary<KeyChord, string>();
        foreach (var tool in Tools)
        {
            var state = s.Tool(tool.Id);
            try
            {
                if (state.Enabled)
                {
                    var restartKey = tool.RestartKey(s);
                    if (tool.IsRunning && _restartKeys.TryGetValue(tool.Id, out var previous) && previous != restartKey) tool.Stop();
                    _restartKeys[tool.Id] = restartKey;
                    tool.Start();
                }
                else
                {
                    tool.Stop();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"{tool.Name} could not be {(state.Enabled ? "started" : "stopped")}", ex);
            }

            foreach (var command in tool.Commands)
            {
                var chord = state.ShortcutFor(command.Id, command.DefaultShortcut);
                HotkeyState result;
                if (!state.Enabled || chord is null) result = HotkeyState.Off;
                else if (used.ContainsKey(chord.Value)) result = HotkeyState.Taken;
                else
                {
                    result = _hotkeys.Register(chord.Value, command.Run);
                    if (result == HotkeyState.Active) used[chord.Value] = command.Title;
                }
                _states[(tool.Id, command.Id)] = result;
                if (result is HotkeyState.Taken or HotkeyState.Invalid)
                    Log.Warn($"Shortcut {chord} for \"{command.Title}\" is not available ({result}).");
            }
        }
        Changed?.Invoke();
    }

    public void SetEnabled(ToolModule tool, bool enabled) => Edit(s => s.EditTool(tool.Id).Enabled = enabled);

    /// <summary>Sets a shortcut: a chord, "" to switch it off, or null to go back to the default.</summary>
    public void SetShortcut(ToolModule tool, ToolCommand command, string? text) => Edit(s =>
    {
        var shortcuts = s.EditTool(tool.Id).Shortcuts;
        if (text is null) shortcuts.Remove(command.Id);
        else shortcuts[command.Id] = text;
    });

    /// <summary>The title of another command (any tool, on or off) that already uses this chord.</summary>
    public string? UsedByAnotherCommand(KeyChord chord, ToolModule tool, ToolCommand command)
    {
        foreach (var t in Tools)
            foreach (var c in t.Commands)
                if (!(t == tool && c == command) && ChordOf(t, c) == chord)
                    return t == tool ? c.Title : $"{t.Name}: {c.Title}";
        return null;
    }

    /// <summary>
    /// True when Windows lets this app have the chord: it is free, or it is already this command's
    /// working shortcut. Call while shortcuts are paused for a correct answer about our own ones.
    /// </summary>
    public bool IsAvailable(KeyChord chord) => _hotkeys.IsFree(chord);

    /// <summary>Releases every shortcut while the user records a new one, so pressing it reaches the recorder.</summary>
    public IDisposable PauseShortcuts()
    {
        _paused = true;
        _hotkeys.UnregisterAll();
        return new Resume(this);
    }

    private sealed class Resume(ToolRegistry owner) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._paused = false;
            owner.Apply(force: true);
        }
    }

    private void Edit(Action<AppSettings> edit)
    {
        var next = _settings.Current.Clone();
        edit(next);
        _settings.Save(next); // Changed → Apply
    }

    public void Dispose()
    {
        foreach (var tool in Tools)
        {
            try { tool.Stop(); }
            catch (Exception ex) { Log.Warn($"{tool.Name} did not stop cleanly: {ex.Message}"); }
        }
        _hotkeys.Dispose();
    }
}
