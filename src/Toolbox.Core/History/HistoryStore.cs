using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toolbox.Core.Jobs;

namespace Toolbox.Core.History;

public sealed record HistoryOutput(string Path, long SizeBytes, int? Width, int? Height, int? Pages);

public sealed record HistoryItem(IReadOnlyList<string> Inputs, string State, string? Error, IReadOnlyList<HistoryOutput> Outputs, IReadOnlyList<string> Notes);

/// <summary>
/// One finished job. Stores paths, settings, sizes and outcomes — never file contents.
/// </summary>
public sealed record HistoryEntry(
    Guid JobId,
    DateTimeOffset Started,
    DateTimeOffset? Finished,
    string ActionId,
    string Title,
    string? RecipeId,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyList<HistoryItem> Items)
{
    public static HistoryEntry From(Job job) => new(
        job.Id,
        job.Started,
        job.Finished,
        job.Action.Id,
        job.Title,
        job.Request.RecipeId,
        new Dictionary<string, string>(job.Options),
        job.Items.Select(i => new HistoryItem(
            i.Inputs.Select(f => f.Path).ToList(),
            i.State.ToString(),
            i.Error,
            i.Outputs.Select(o => new HistoryOutput(o.Path, o.SizeBytes, o.Width, o.Height, o.Pages)).ToList(),
            i.Notes)).ToList());
}

/// <summary>Append-only JSON-lines history with retention by age and count.</summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly Func<(int Days, int MaxEntries)> _retention;
    private readonly object _gate = new();

    public HistoryStore(string? path = null, Func<(int Days, int MaxEntries)>? retention = null)
    {
        _path = path ?? AppPaths.HistoryFile;
        _retention = retention ?? (() => (30, 1000));
    }

    public event Action? Changed;

    public void Append(HistoryEntry entry)
    {
        lock (_gate)
        {
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonOptions) + "\n", Encoding.UTF8);
            PruneIfNeeded();
        }
        Changed?.Invoke();
    }

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<HistoryEntry> ReadRecent(int max = 200)
    {
        lock (_gate)
        {
            return ReadAll().AsEnumerable().Reverse().Take(max).ToList();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        Changed?.Invoke();
    }

    private List<HistoryEntry> ReadAll()
    {
        var list = new List<HistoryEntry>();
        if (!File.Exists(_path)) return list;
        foreach (var line in File.ReadLines(_path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions) is { } entry) list.Add(entry);
            }
            catch (JsonException)
            {
                // A damaged line (for example from a power cut) is skipped, not fatal.
            }
        }
        return list;
    }

    private void PruneIfNeeded()
    {
        var (days, max) = _retention();
        var all = ReadAll();
        var cutoff = DateTimeOffset.Now.AddDays(-Math.Max(1, days));
        var kept = all.Where(e => e.Started >= cutoff).TakeLast(Math.Max(1, max)).ToList();
        if (kept.Count == all.Count) return;

        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, kept.Select(e => JsonSerializer.Serialize(e, JsonOptions)), Encoding.UTF8);
        File.Move(tmp, _path, overwrite: true);
    }
}
