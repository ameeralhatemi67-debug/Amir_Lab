namespace Toolbox.Core;

/// <summary>
/// One-time copy of File Toolbox's user data into Amir Lap's data folder, on the first start after
/// the rename. Only what the user made is carried over (settings, history, recipes, brand styles);
/// logs, diagnostics and temporary bookkeeping are not. The old folder is left untouched, so going
/// back to an older build loses nothing.
/// </summary>
public static class LegacyDataMigration
{
    public const string MarkerFile = "migrated-from-FileToolbox.txt";

    private static readonly string[] Files = ["settings.json", "history.jsonl"];
    private static readonly string[] Folders = ["recipes", "presets"];

    /// <summary>
    /// Copies the legacy data when the new folder has none yet. Returns the number of files copied
    /// (0 when there was nothing to do). Never overwrites a file that already exists.
    /// </summary>
    public static int Run(string legacyDir, string newDir)
    {
        if (!Directory.Exists(legacyDir)) return 0;
        if (File.Exists(Path.Combine(newDir, "settings.json")) || File.Exists(Path.Combine(newDir, MarkerFile))) return 0;
        bool hasUserData = Files.Any(f => File.Exists(Path.Combine(legacyDir, f)))
                           || Folders.Any(d => Directory.Exists(Path.Combine(legacyDir, d)) && Directory.EnumerateFileSystemEntries(Path.Combine(legacyDir, d)).Any());
        if (!hasUserData) return 0;

        Directory.CreateDirectory(newDir);
        int copied = 0;
        foreach (var name in Files)
            copied += CopyIfMissing(Path.Combine(legacyDir, name), Path.Combine(newDir, name));
        foreach (var folder in Folders)
        {
            var from = Path.Combine(legacyDir, folder);
            if (!Directory.Exists(from)) continue;
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                copied += CopyIfMissing(file, Path.Combine(newDir, folder, Path.GetRelativePath(from, file)));
        }
        File.WriteAllText(Path.Combine(newDir, MarkerFile),
            $"Copied {copied} file(s) from {legacyDir} on {DateTime.Now:yyyy-MM-dd HH:mm}. The original folder was left as it was.{Environment.NewLine}");
        return copied;
    }

    private static int CopyIfMissing(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return 0;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Copy(from, to);
        return 1;
    }
}
