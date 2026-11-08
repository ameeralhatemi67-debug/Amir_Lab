using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.Core.Settings;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Home;

/// <summary>
/// Shows a command's shortcut as key caps. Click it (or press Enter) and type a new one: it is
/// checked against the app's other shortcuts and against other programs before it is saved.
/// Esc cancels, Backspace or Delete switches the shortcut off.
/// </summary>
internal sealed class ShortcutBox : StackPanel
{
    private readonly ToolRegistry _registry;
    private readonly ToolModule _tool;
    private readonly ToolCommand _command;
    private readonly Button _box = new() { MinWidth = 170, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4) };
    private readonly Button _reset = new() { Content = "Reset", Style = SettingsPage.Res("Toolbox.Button.Subtle"), Margin = new Thickness(6, 0, 0, 0), ToolTip = "Go back to the default shortcut" };
    private readonly TextBlock _status = new() { Style = SettingsPage.Res("Text.Caption"), MaxWidth = 300, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
    private IDisposable? _pause;
    private string? _message;

    /// <summary>The box currently waiting for a shortcut (the window's Esc cancels it instead of closing).</summary>
    public static ShortcutBox? Recording { get; private set; }

    public void CancelRecording() => EndRecording(null);

    public ShortcutBox(ToolRegistry registry, ToolModule tool, ToolCommand command)
    {
        _registry = registry;
        _tool = tool;
        _command = command;
        HorizontalAlignment = HorizontalAlignment.Right;

        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        line.Children.Add(_box);
        line.Children.Add(_reset);
        Children.Add(line);
        Children.Add(_status);
        System.Windows.Automation.AutomationProperties.SetName(_box, $"Shortcut for {command.Title}. Press Enter to change.");

        _box.Click += (_, _) => { if (_pause is null) BeginRecording(); };
        _box.PreviewKeyDown += OnKeyDown;
        _box.LostKeyboardFocus += (_, _) => EndRecording(null);
        _reset.Click += (_, _) => { _message = null; _registry.SetShortcut(_tool, _command, null); };

        _registry.Changed += Refresh;
        Unloaded += (_, _) => { _registry.Changed -= Refresh; EndRecording(null); };
        Loaded += (_, _) => { _registry.Changed -= Refresh; _registry.Changed += Refresh; Refresh(); };
        Refresh();
    }

    private void BeginRecording()
    {
        _pause = _registry.PauseShortcuts();
        Recording = this;
        _message = null;
        _box.Content = new TextBlock { Text = "Press the new shortcut…", FontStyle = FontStyles.Italic };
        _box.SetResourceReference(Control.BorderBrushProperty, "Brush.Accent");
        _status.Text = "Esc to cancel · Backspace to switch it off";
        _box.Focus();
    }

    private void EndRecording(string? message)
    {
        if (_pause is null) return;
        if (message is not null) _message = message;
        var pause = _pause;
        _pause = null;
        if (Recording == this) Recording = null;
        _box.ClearValue(Control.BorderBrushProperty);
        pause.Dispose(); // re-registers everything and raises Changed, which refreshes this box
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_pause is null)
        {
            if (e.Key is Key.Enter or Key.Space) { BeginRecording(); e.Handled = true; }
            return;
        }
        e.Handled = true; // while recording, keys belong to the recorder (no Alt menus, no Tab)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return; // wait for the key that completes the shortcut

        var mods = Keyboard.Modifiers;
        if (key == Key.Escape && mods == ModifierKeys.None) { EndRecording(null); return; }
        if (key is Key.Back or Key.Delete && mods == ModifierKeys.None)
        {
            _registry.SetShortcut(_tool, _command, "");
            EndRecording("Shortcut switched off.");
            return;
        }

        var name = VirtualKeys.NameOf(KeyInterop.VirtualKeyFromKey(key));
        if (name is null) { _status.Text = $"{key} can't be used in a shortcut. Try a letter, number or F-key."; return; }
        var chord = new KeyChord(
            (mods.HasFlag(ModifierKeys.Control) ? ChordModifiers.Ctrl : 0) | (mods.HasFlag(ModifierKeys.Alt) ? ChordModifiers.Alt : 0)
            | (mods.HasFlag(ModifierKeys.Shift) ? ChordModifiers.Shift : 0) | (mods.HasFlag(ModifierKeys.Windows) ? ChordModifiers.Win : 0),
            name);

        if (!chord.IsUsableGlobally) { _status.Text = $"{chord} would get in the way of typing. Add Ctrl, Alt or Win."; return; }
        if (_registry.UsedByAnotherCommand(chord, _tool, _command) is { } other) { _status.Text = $"{chord} is already used by \"{other}\"."; return; }
        if (!_registry.IsAvailable(chord)) { _status.Text = $"Another program already uses {chord}. Try another."; return; }

        _registry.SetShortcut(_tool, _command, chord.ToString());
        EndRecording(null);
    }

    private void Refresh()
    {
        if (_pause is not null) return;
        var chord = _registry.ChordOf(_tool, _command);
        _box.Content = chord is { } c ? SettingsPage.Keys(c.Parts()) : new TextBlock { Text = "Not set", Style = SettingsPage.Res("Text.Label") };
        bool isDefault = (chord?.ToString() ?? "") == (_command.DefaultShortcut ?? "");
        _reset.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;
        _status.Text = _message ?? (_registry.StateOf(_tool, _command), chord) switch
        {
            (HotkeyState.Taken, { } k) => $"Not working: another program or command is using {k}. Choose another.",
            (HotkeyState.Invalid, { } k) => $"{k} can't be used as a shortcut. Choose another.",
            (_, null) => "No shortcut. Click to set one.",
            (HotkeyState.Off, _) => $"Works when {_tool.Name} is on.",
            _ => "",
        };
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (_registry.StateOf(_tool, _command) == HotkeyState.Taken) _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        else _status.ClearValue(TextBlock.ForegroundProperty);
        _message = null;
    }
}
