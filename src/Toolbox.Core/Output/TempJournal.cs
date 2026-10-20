namespace Toolbox.Core.Output;

/// <summary>
/// Remembers temporary output files that are being written so that, if the app is closed or
/// crashes mid-job, the next start can remove them instead of leaving clutter next to the user's files.
/// </summary>
public sealed class TempJournal
{
    public const string TempExtension = ".ftb-tmp";

    private readonly string _journalPath;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);

    public TempJournal(string journalPath)
    {
        _journalPath = journalPath;
    }

    public void Add(string tempPath)
    {
        lock (_gate)
        {
            _pending.Add(tempPath);
            Persist();
        }
    }

    public void Remove(string tempPath)
    {
        lock (_gate)
        {
            if (_pending.Remove(tempPath)) Persist();
        }
    }

    /// <summary>Deletes temp files left behind by an earlier run. Only touches files with our temp extension.</summary>
    public int CleanupOrphans()
    {
        lock (_gate)
        {
            if (!File.Exists(_journalPath)) return 0;
            int removed = 0;
            foreach (var line in File.ReadAllLines(_journalPath))
            {
                var path = line.Trim();
                if (path.Length == 0 || !path.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (File.Exists(path))
                    {
                        File.SetAttributes(path, FileAttributes.Normal);
                        File.Delete(path);
                        removed++;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _pending.Clear();
            Persist();
            return removed;
        }
    }

    private void Persist()
    {
        try
        {
            File.WriteAllLines(_journalPath, _pending);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
