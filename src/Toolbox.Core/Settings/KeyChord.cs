namespace Toolbox.Core.Settings;

[Flags]
public enum ChordModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A keyboard shortcut such as "Ctrl+Alt+F", stored in settings as text. Platform-neutral: the
/// Windows adapter maps <see cref="Key"/> names to virtual-key codes.
/// </summary>
public readonly record struct KeyChord(ChordModifiers Modifiers, string Key)
{
    /// <summary>Keys a shortcut may end with (besides A–Z, 0–9 and F1–F24).</summary>
    public static readonly IReadOnlySet<string> NamedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Up", "Down", "Left", "Right",
        "PrintScreen", "Pause", "Minus", "Plus", "Comma", "Period", "Slash", "Backquote", "Semicolon",
        "Quote", "LeftBracket", "RightBracket", "Backslash",
    };

    public static bool IsValidKeyName(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (key.Length == 1) return char.IsAsciiLetterOrDigit(key[0]);
        if ((key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out int f) && f is >= 1 and <= 24) return true;
        return NamedKeys.Contains(key);
    }

    /// <summary>
    /// A global shortcut needs Ctrl, Alt or Win, so ordinary typing (and Shift+letter) is never taken
    /// over. F13–F24 may stand alone: no keyboard types with them.
    /// </summary>
    public bool IsUsableGlobally =>
        IsValidKeyName(Key)
        && ((Modifiers & (ChordModifiers.Ctrl | ChordModifiers.Alt | ChordModifiers.Win)) != 0
            || (Key.Length > 1 && (Key[0] is 'F' or 'f') && int.TryParse(Key.AsSpan(1), out int f) && f >= 13));

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        // The plus key is written "Plus" ("Ctrl+Plus"), so '+' only ever separates parts.
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var modifiers = ChordModifiers.None;
        foreach (var part in parts[..^1])
        {
            var m = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => ChordModifiers.Ctrl,
                "alt" => ChordModifiers.Alt,
                "shift" => ChordModifiers.Shift,
                "win" or "windows" => ChordModifiers.Win,
                _ => ChordModifiers.None,
            };
            if (m == ChordModifiers.None || (modifiers & m) != 0) return false;
            modifiers |= m;
        }
        var key = Normalize(parts[^1]);
        if (!IsValidKeyName(key)) return false;
        chord = new KeyChord(modifiers, key);
        return true;
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"'{text}' is not a keyboard shortcut.");

    /// <summary>Canonical text: modifiers in the order Win, Ctrl, Alt, Shift (as Windows writes them), then the key.</summary>
    public override string ToString() => string.Join("+", Parts());

    /// <summary>The pieces to show as separate key caps.</summary>
    public IEnumerable<string> Parts()
    {
        if (Modifiers.HasFlag(ChordModifiers.Win)) yield return "Win";
        if (Modifiers.HasFlag(ChordModifiers.Ctrl)) yield return "Ctrl";
        if (Modifiers.HasFlag(ChordModifiers.Alt)) yield return "Alt";
        if (Modifiers.HasFlag(ChordModifiers.Shift)) yield return "Shift";
        yield return Key;
    }

    private static string Normalize(string key)
    {
        if (key.Length == 1) return key.ToUpperInvariant();
        if ((key[0] is 'F' or 'f') && int.TryParse(key.AsSpan(1), out int f)) return "F" + f;
        return NamedKeys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) ?? key;
    }
}
