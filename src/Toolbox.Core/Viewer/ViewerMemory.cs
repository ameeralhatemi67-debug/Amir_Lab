using System.Text.Json;

namespace Toolbox.Core.Viewer;

/// <summary>Where a file was left in the viewer: the page, how far down it (0–1), the zoom (0: fit width) and the theme.</summary>
public sealed record ViewerState(int Page, double Offset, double Zoom, string Theme = "light", double Level = 0.6, bool KeepPictures = true);

/// <summary>
/// Remembers, per file, where it was left in the viewer, so opening it again carries on there. A
/// file is known by its full path, size and last change; a changed file starts from the top. The
/// newest 300 files are kept, in one small JSON file in Amir Lap's data folder.
/// </summary>
public sealed class ViewerMemory
{
    public const int Capacity = 300;

    private readonly string _path;
    private readonly object _lock = new();
    private List<Entry>? _entries;

    public ViewerMemory(string? path = null) => _path = path ?? Path.Combine(AppPaths.DataDir, "viewer-memory.json");

    public ViewerState? Get(string file)
    {
        var key = Key(file);
        if (key is null) return null;
        lock (_lock) return Load().FirstOrDefault(e => e.Key == key)?.State;
    }

    public void Set(string file, ViewerState state)
    {
        var key = Key(file);
        if (key is null) return;
        lock (_lock)
        {
            var entries = Load();
            entries.RemoveAll(e => e.Key == key);
            entries.Insert(0, new Entry(key, state));
            if (entries.Count > Capacity) entries.RemoveRange(Capacity, entries.Count - Capacity);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(entries));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Remembering is a convenience; reading on stays possible.
            }
        }
    }

    private List<Entry> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            _entries = File.Exists(_path) ? JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _entries = [];
        }
        return _entries;
    }

    /// <summary>The file's identity: path (any case), size and last write; null when it cannot be read.</summary>
    internal static string? Key(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            return $"{Path.GetFullPath(file).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed record Entry(string Key, ViewerState State);
}
