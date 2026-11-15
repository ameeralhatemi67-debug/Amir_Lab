namespace Toolbox.Core.Sound;

/// <summary>An app in the mixer: its sessions on the main output taken together.</summary>
/// <param name="Key">The app's key: its program's file name in lower case ("spotify.exe"), or <see cref="SoundApps.SystemKey"/>.</param>
/// <param name="Playing">At least one of its sessions is playing (an open stream, even a silent one).</param>
/// <param name="Volume">Its level (0..1): the loudest of its playing sessions, or of all of them.</param>
/// <param name="Muted">Every one of its sessions is muted.</param>
public sealed record SoundApp(string Key, string Name, string? ExePath, bool IsSystem, bool Playing, float Volume, bool Muted, int Sessions, int ProcessId);

/// <summary>Who an audio session belongs to, how sessions group into apps, and volume steps.</summary>
public static class SoundApps
{
    /// <summary>The key of Windows' own sounds (notifications, errors), which have no program of their own.</summary>
    public const string SystemKey = "#system";

    /// <summary>
    /// The app behind a session, from Windows' session identifier, which holds the program's path:
    /// "{0.0.0.00000000}.{guid}|\Device\HarddiskVolume3\Program Files\Spotify\Spotify.exe%b{guid}".
    /// Windows' own sounds have "#" in place of a path. Nothing about the process is opened, so this
    /// also works for games whose process is protected.
    /// </summary>
    public static (string Key, string? Path, bool IsSystem) FromSessionIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return ("", null, false);
        int bar = identifier.IndexOf('|');
        var rest = bar >= 0 ? identifier[(bar + 1)..] : identifier;
        int end = rest.IndexOf("%b", StringComparison.Ordinal);
        if (end >= 0) rest = rest[..end];
        if (rest.StartsWith('#')) return (SystemKey, null, true);
        if (rest.Length == 0) return ("", null, false);
        var key = KeyOf(rest);
        return (key, key.Length == 0 ? null : rest, false);
    }

    /// <summary>
    /// The key for a program: its file name in lower case. Paths with a version in them
    /// ("Discord\app-1.0.9180\Discord.exe") keep the same key across updates, which is where
    /// Windows' own memory of app volumes forgets them.
    /// </summary>
    public static string KeyOf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        int slash = trimmed.LastIndexOfAny(['\\', '/']);
        return (slash >= 0 ? trimmed[(slash + 1)..] : trimmed).ToLowerInvariant();
    }

    /// <summary>A name for people: the program's description, else its product name, else its file name.</summary>
    public static string NameFrom(string? description, string? product, string? path)
    {
        if (Clean(description) is { } d) return d;
        if (Clean(product) is { } p) return p;
        if (path is null) return "";
        var file = KeyOf(path);
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
        return file.Length == 0 ? "" : char.ToUpperInvariant(file[0]) + file[1..];

        static string? Clean(string? s)
        {
            s = s?.Trim();
            return string.IsNullOrEmpty(s) || s.Length > 40 || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? null : s;
        }
    }

    /// <summary>
    /// The sessions grouped into apps. Apps keep a steady order (by name, Windows' sounds last), so
    /// rows don't jump while the user drags a slider.
    /// </summary>
    public static IReadOnlyList<SoundApp> Group(IEnumerable<SoundSession> sessions)
    {
        var apps = new List<SoundApp>();
        foreach (var group in sessions.GroupBy(s => s.AppKey.Length > 0 ? s.AppKey : s.InstanceId, StringComparer.Ordinal))
        {
            var all = group.ToList();
            var playing = all.Where(s => s.Active).ToList();
            var lead = playing.Count > 0 ? playing : all;
            var named = lead.FirstOrDefault(s => s.Name.Length > 0) ?? all.FirstOrDefault(s => s.Name.Length > 0) ?? lead[0];
            apps.Add(new SoundApp(
                group.Key,
                named.Name.Length > 0 ? named.Name : group.Key,
                named.ExePath,
                all.Any(s => s.IsSystem),
                playing.Count > 0,
                lead.Max(s => s.Volume),
                all.All(s => s.Muted),
                all.Count,
                named.ProcessId));
        }
        return apps.OrderBy(a => a.IsSystem).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>A level as a whole percent.</summary>
    public static int Percent(float volume) => (int)Math.Round(Math.Clamp(volume, 0f, 1f) * 100);

    /// <summary>
    /// One press of Louder or Quieter: to the next multiple of <paramref name="step"/> percent
    /// (37% → 40% → 45%, or 37% → 35% → 30%), between 0 and 100.
    /// </summary>
    public static float Step(float volume, int step, bool up)
    {
        step = Math.Clamp(step, 1, 50);
        int p = Percent(volume);
        int next = up ? (p / step + 1) * step : ((p + step - 1) / step - 1) * step;
        return Math.Clamp(next, 0, 100) / 100f;
    }
}

/// <summary>Names of outputs as people say them.</summary>
public static class SoundNames
{
    /// <summary>"Speakers (Realtek(R) Audio)" → "Speakers · Realtek(R) Audio".</summary>
    public static string TidyDevice(string name)
    {
        int open = name.IndexOf(" (", StringComparison.Ordinal);
        if (open <= 0 || !name.EndsWith(')')) return name;
        var inner = name[(open + 2)..^1].Trim();
        return inner.Length == 0 ? name[..open] : $"{name[..open]} · {inner}";
    }

    /// <summary>"Speakers (Realtek(R) Audio)" → "Speakers".</summary>
    public static string ShortDevice(string name)
    {
        int open = name.IndexOf(" (", StringComparison.Ordinal);
        return open > 0 ? name[..open] : name;
    }

    /// <summary>What kind of output, from Windows' form factor for the endpoint.</summary>
    public static string KindOf(int formFactor) => formFactor switch
    {
        3 or 5 => "headphones", // headphones, headset
        1 => "speakers",
        9 => "monitor",         // HDMI or DisplayPort audio
        2 or 8 => "line",       // line out, S/PDIF
        0 => "remote",
        _ => "other",
    };
}
