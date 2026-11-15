using System.Text.Json;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Core.Sound;

/// <summary>
/// Each app's level and mute as the user last set them, per output ("Spotify: 60% on the speakers,
/// 35% on the headphones"), and when it was last heard. Kept in its own small file, written a moment
/// after the last change, so dragging a slider never rewrites the settings. Safe to use from any thread.
/// </summary>
public sealed class SoundMemory : IDisposable
{
    public sealed class Level
    {
        public float Volume { get; set; } = 1;
        public bool Muted { get; set; }
    }

    public sealed class App
    {
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public DateTimeOffset LastHeard { get; set; }

        /// <summary>By output id.</summary>
        public Dictionary<string, Level> Outputs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An app it knows, for the mixer's "Quiet right now".</summary>
    public sealed record Known(string Key, string Name, string? Path, Level? Level, DateTimeOffset LastHeard);

    private const int MaxApps = 200;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, App> _apps;
    private readonly Timer _save;
    private bool _dirty;

    public SoundMemory(string path, TimeSpan? saveDelay = null)
    {
        _path = path;
        _apps = Load(path);
        SaveDelay = saveDelay ?? TimeSpan.FromSeconds(1.5);
        _save = new Timer(_ => Flush());
    }

    public static string DefaultPath => System.IO.Path.Combine(AppPaths.DataDir, "sound", "apps.json");

    public TimeSpan SaveDelay { get; }

    /// <summary>The level the user last set for this app on this output, if any.</summary>
    public Level? Recall(string key, string deviceId)
    {
        lock (_gate)
            return _apps.TryGetValue(key, out var app) && app.Outputs.TryGetValue(deviceId, out var level)
                ? new Level { Volume = level.Volume, Muted = level.Muted }
                : null;
    }

    /// <summary>Notes the level the user just set (or Windows reported) for this app on this output.</summary>
    public void Remember(string key, string name, string? path, string deviceId, float volume, bool muted)
    {
        if (key.Length == 0 || deviceId.Length == 0) return;
        lock (_gate)
        {
            var app = Ensure(key, name, path);
            if (app.Outputs.TryGetValue(deviceId, out var old) && Math.Abs(old.Volume - volume) < 0.0005f && old.Muted == muted) return;
            app.Outputs[deviceId] = new Level { Volume = Math.Clamp(volume, 0f, 1f), Muted = muted };
            MarkDirty();
        }
    }

    /// <summary>Notes that the app made a sound now (kept to the minute, so it is cheap to call).</summary>
    public void Heard(string key, string name, string? path, DateTimeOffset now)
    {
        if (key.Length == 0) return;
        lock (_gate)
        {
            var app = Ensure(key, name, path);
            if (now - app.LastHeard < TimeSpan.FromMinutes(1)) return;
            app.LastHeard = now;
            MarkDirty();
        }
    }

    /// <summary>Apps it knows that aren't among <paramref name="except"/>, most recently heard first.</summary>
    public IReadOnlyList<Known> Recent(string? deviceId, IEnumerable<string> except, int max)
    {
        var skip = except.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
            return _apps.Where(p => !skip.Contains(p.Key) && p.Key != SoundApps.SystemKey && p.Value.LastHeard > DateTimeOffset.MinValue)
                .OrderByDescending(p => p.Value.LastHeard)
                .Take(max)
                .Select(p => new Known(p.Key, p.Value.Name.Length > 0 ? p.Value.Name : p.Key, p.Value.Path,
                    deviceId is not null && p.Value.Outputs.TryGetValue(deviceId, out var l) ? new Level { Volume = l.Volume, Muted = l.Muted } : null,
                    p.Value.LastHeard))
                .ToList();
    }

    public int Count
    {
        get { lock (_gate) return _apps.Count; }
    }

    /// <summary>Writes the file now if anything changed (also called on its own a moment after a change).</summary>
    public void Flush()
    {
        string json;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            json = JsonSerializer.Serialize(_apps, Options);
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Imp Studio could not save the app levels: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _save.Dispose();
        Flush();
    }

    private App Ensure(string key, string name, string? path)
    {
        if (!_apps.TryGetValue(key, out var app))
        {
            if (_apps.Count >= MaxApps)
            {
                var oldest = _apps.OrderBy(p => p.Value.LastHeard).First().Key;
                _apps.Remove(oldest);
            }
            _apps[key] = app = new App();
        }
        if (name.Length > 0 && app.Name != name) app.Name = name;
        if (path is not null && app.Path != path) app.Path = path;
        return app;
    }

    private void MarkDirty()
    {
        _dirty = true;
        _save.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private static Dictionary<string, App> Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, App>>(File.ReadAllText(path), Options) is { } apps)
                return new Dictionary<string, App>(apps.Where(p => p.Value is not null), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Imp Studio's app levels could not be read, starting fresh: {ex.Message}");
        }
        return new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
    }
}
