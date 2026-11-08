using System.Runtime.InteropServices;
using System.Windows.Interop;
using Toolbox.Core.Settings;

namespace Toolbox.Windows.Shell;

internal enum HotkeyState
{
    /// <summary>No shortcut set, or the tool is switched off.</summary>
    Off,

    /// <summary>Registered with Windows and working.</summary>
    Active,

    /// <summary>Another program (or another command here) already uses it.</summary>
    Taken,

    /// <summary>Not a usable global shortcut (for example a letter without Ctrl, Alt or Win).</summary>
    Invalid,
}

/// <summary>
/// The app's system-wide shortcuts, registered with RegisterHotKey on one message-only window.
/// Registration is cheap and costs nothing while idle (Windows only posts a message on use).
/// </summary>
internal sealed partial class HotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private const int ProbeId = 0xBFFF;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 0xA000;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("AmirLapHotkeys") { WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(Hook);
    }

    public HotkeyState Register(KeyChord chord, Action pressed)
    {
        if (!chord.IsUsableGlobally || !VirtualKeys.TryGetCode(chord.Key, out uint vk)) return HotkeyState.Invalid;
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, Native(chord.Modifiers) | MOD_NOREPEAT, vk)) return HotkeyState.Taken;
        _actions[id] = pressed;
        return HotkeyState.Active;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    /// <summary>True when Windows would accept the shortcut right now (nobody, this app included, holds it).</summary>
    public bool IsFree(KeyChord chord)
    {
        if (!chord.IsUsableGlobally || !VirtualKeys.TryGetCode(chord.Key, out uint vk)) return false;
        if (!RegisterHotKey(_source.Handle, ProbeId, Native(chord.Modifiers) | MOD_NOREPEAT, vk)) return false;
        UnregisterHotKey(_source.Handle, ProbeId);
        return true;
    }

    private static uint Native(ChordModifiers m) =>
        (m.HasFlag(ChordModifiers.Ctrl) ? MOD_CONTROL : 0)
        | (m.HasFlag(ChordModifiers.Alt) ? MOD_ALT : 0)
        | (m.HasFlag(ChordModifiers.Shift) ? MOD_SHIFT : 0)
        | (m.HasFlag(ChordModifiers.Win) ? MOD_WIN : 0);

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}

/// <summary>Key names used in <see cref="KeyChord"/> and their Windows virtual-key codes.</summary>
internal static class VirtualKeys
{
    private static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["PrintScreen"] = 0x2C,
        ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Pause"] = 0x13,
        ["Semicolon"] = 0xBA, ["Plus"] = 0xBB, ["Comma"] = 0xBC, ["Minus"] = 0xBD, ["Period"] = 0xBE,
        ["Slash"] = 0xBF, ["Backquote"] = 0xC0, ["LeftBracket"] = 0xDB, ["Backslash"] = 0xDC,
        ["RightBracket"] = 0xDD, ["Quote"] = 0xDE,
    };

    public static bool TryGetCode(string key, out uint vk)
    {
        vk = 0;
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            vk = char.ToUpperInvariant(key[0]);
            return true;
        }
        if (key.Length > 1 && key[0] is 'F' or 'f' && int.TryParse(key.AsSpan(1), out int f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            return true;
        }
        return Named.TryGetValue(key, out vk);
    }

    /// <summary>The key name for a virtual-key code, or null for keys a shortcut cannot end with.</summary>
    public static string? NameOf(int vk)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);
        foreach (var (name, code) in Named)
            if (code == vk) return name;
        return null;
    }
}
