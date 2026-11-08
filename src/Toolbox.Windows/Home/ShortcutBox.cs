using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Toolbox.Core.Settings;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Home;

/// <summary>
/// Shows a command's shortcut as key caps. Click it (or press Enter) and type a new one: it is
/// checked against the app's other shortcuts and against other programs before it is saved.
/// Esc cancels, Backspace or Delete switches the shortcut off. When another program holds the
/// shortcut, a button offers a free one.
/// </summary>
internal sealed class ShortcutBox : StackPanel
{
    private readonly ToolRegistry _registry;
    private readonly ToolModule _tool;
    private readonly ToolCommand _command;
    private readonly Button _box = new() { Style = SettingsPage.Res("Lap.Button"), MinWidth = 150, Padding = new Thickness(8, 0, 8, 0) };
    private readonly Button _fix = new() { Style = SettingsPage.Res("Lap.Button.Accent"), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _reset = new() { Style = SettingsPage.Res("Lap.Button.Ghost"), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { Style = SettingsPage.Res("Lap.Help"), MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Right, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
    private IDisposable? _pause;
    private string? _message;
    private KeyChord? _suggestion;

    /// <summary>The box currently waiting for a shortcut (the window's Esc cancels it instead of closing).</summary>
    public static ShortcutBox? Recording { get; private set; }

    public void CancelRecording() => EndRecording(null);

    public ShortcutBox(ToolRegistry registry, ToolModule tool, ToolCommand command)
    {
        _registry = registry;
        _tool = tool;
        _command = command;
        HorizontalAlignment = HorizontalAlignment.Right;
        _reset.Content = Lang.T("Reset", "استعادة");
        _reset.ToolTip = Lang.T("Go back to the default shortcut", "العودة إلى الاختصار الافتراضي");

        var line = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        line.Children.Add(_box);
        line.Children.Add(_fix);
        line.Children.Add(_reset);
        Children.Add(line);
        Children.Add(_status);
        System.Windows.Automation.AutomationProperties.SetName(_box, Lang.T($"Shortcut for {command.Label}. Press Enter to change.", $"اختصار {command.Label}. اضغط Enter للتغيير."));

        _box.Click += (_, _) => { if (_pause is null) BeginRecording(); };
        _box.PreviewKeyDown += OnKeyDown;
        _box.LostKeyboardFocus += (_, _) => EndRecording(null);
        _reset.Click += (_, _) => { _message = null; _registry.SetShortcut(_tool, _command, null); };
        _fix.Click += (_, _) => { if (_suggestion is { } s) { _message = null; _registry.SetShortcut(_tool, _command, s.ToString()); } };

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
        _box.Content = new TextBlock { Text = Lang.T("Press the new shortcut…", "اضغط الاختصار الجديد…"), FontStyle = FontStyles.Italic };
        _box.SetResourceReference(Control.BorderBrushProperty, "Brush.Accent");
        _status.Text = Lang.T("Esc to cancel · Backspace to switch it off", "Esc للإلغاء · Backspace لإيقافه");
        _status.Visibility = Visibility.Visible;
        _fix.Visibility = Visibility.Collapsed;
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
            EndRecording(Lang.T("Shortcut switched off.", "تم إيقاف الاختصار."));
            return;
        }

        var name = VirtualKeys.NameOf(KeyInterop.VirtualKeyFromKey(key));
        if (name is null) { _status.Text = Lang.T($"{key} can't be used in a shortcut. Try a letter, number or F-key.", $"لا يمكن استخدام {key} في اختصار. جرّب حرفًا أو رقمًا أو مفتاح F."); return; }
        var chord = new KeyChord(
            (mods.HasFlag(ModifierKeys.Control) ? ChordModifiers.Ctrl : 0) | (mods.HasFlag(ModifierKeys.Alt) ? ChordModifiers.Alt : 0)
            | (mods.HasFlag(ModifierKeys.Shift) ? ChordModifiers.Shift : 0) | (mods.HasFlag(ModifierKeys.Windows) ? ChordModifiers.Win : 0),
            name);

        if (!chord.IsUsableGlobally) { _status.Text = Lang.T($"{chord} would get in the way of typing. Add Ctrl, Alt or Win.", $"{chord} سيعيق الكتابة. أضف Ctrl أو Alt أو Win."); return; }
        if (_registry.UsedByAnotherCommand(chord, _tool, _command) is { } other) { _status.Text = Lang.T($"{chord} is already used by \"{other}\".", $"{chord} مستخدم بالفعل في «{other}»."); return; }
        if (!_registry.IsAvailable(chord)) { _status.Text = Lang.T($"Another program already uses {chord}. Try another.", $"برنامج آخر يستخدم {chord}. جرّب غيره."); return; }

        _registry.SetShortcut(_tool, _command, chord.ToString());
        EndRecording(null);
    }

    private void Refresh()
    {
        if (_pause is not null) return;
        var chord = _registry.ChordOf(_tool, _command);
        var state = _registry.StateOf(_tool, _command);
        _box.Content = chord is { } c ? SettingsPage.Keys(c.Parts()) : new TextBlock { Text = Lang.T("Not set", "غير معيّن") };
        bool isDefault = (chord?.ToString() ?? "") == (_command.DefaultShortcut ?? "");
        _reset.Visibility = isDefault ? Visibility.Collapsed : Visibility.Visible;

        _suggestion = state == HotkeyState.Taken ? _registry.SuggestFree(_tool, _command) : null;
        _fix.Visibility = _suggestion is null ? Visibility.Collapsed : Visibility.Visible;
        if (_suggestion is { } s) _fix.Content = Lang.T("Use ", "استخدم ") + s;

        _status.Text = _message ?? (state, chord) switch
        {
            (HotkeyState.Taken, { } k) => Lang.T($"Not working: another program is using {k}.", $"لا يعمل: برنامج آخر يستخدم {k}."),
            (HotkeyState.Invalid, { } k) => Lang.T($"{k} can't be used as a shortcut. Choose another.", $"لا يمكن استخدام {k} كاختصار. اختر غيره."),
            (_, null) => Lang.T("No shortcut. Click to set one.", "لا يوجد اختصار. انقر لتعيين واحد."),
            (HotkeyState.Off, _) => Lang.T($"Works when {_tool.Name} is on.", $"يعمل عندما تكون {_tool.Name} قيد التشغيل."),
            _ => "",
        };
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (state == HotkeyState.Taken) _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        else _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        _message = null;
    }
}
