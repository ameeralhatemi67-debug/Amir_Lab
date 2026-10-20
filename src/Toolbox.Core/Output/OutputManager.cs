using Toolbox.Core.Inputs;

namespace Toolbox.Core.Output;

/// <summary>
/// Owns the file-integrity rules for every result:
/// write to a hidden temp file in the destination → verify → rename to a collision-safe final name.
/// Sources are never opened for writing, and existing files are never overwritten.
/// </summary>
public sealed class OutputManager
{
    private const int MaxCollisionIndex = 9999;

    /// <summary>Below this much free space a result is not started: encoders would fail half-way.</summary>
    public const long MinimumFreeBytes = 32L * 1024 * 1024;

    private readonly Func<OutputPolicy> _policy;
    private readonly TempJournal _journal;

    public OutputManager(Func<OutputPolicy> policy, TempJournal journal)
    {
        _policy = policy;
        _journal = journal;
    }

    public OutputPolicy Policy => _policy();

    /// <summary>
    /// Reserves a temp file for one output. Falls back to the fallback folder when the preferred
    /// folder cannot be written (read-only media, permissions, folder deleted).
    /// </summary>
    public OutputSlot CreateSlot(string sourceDirectory, OutputSpec spec)
    {
        var policy = _policy();
        var preferred = policy.Location == OutputLocation.Folder && !string.IsNullOrWhiteSpace(policy.Folder)
            ? policy.Folder!
            : sourceDirectory;
        // A nearly full drive is treated like an unwritable folder: try the fallback folder, which is
        // usually on another drive, and say why.
        var spaceProblem = FreeSpaceProblem(preferred);
        string? writeError = null;
        if (spaceProblem is null && TryCreateTemp(preferred, out var temp, out writeError))
            return Track(new OutputSlot(temp, preferred, spec, usedFallback: false));

        var reason = spaceProblem ?? "the destination folder could not be written to (read-only, missing or not permitted)";
        if (FreeSpaceProblem(policy.FallbackFolder) is { } fallbackSpace)
            throw new ProcessingException(Sentence(spaceProblem ?? fallbackSpace) + " Free up some space and try again.");
        if (TryCreateTemp(policy.FallbackFolder, out temp, out var fallbackError))
            return Track(new OutputSlot(temp, policy.FallbackFolder, spec, usedFallback: true) { FallbackReason = reason });

        throw new ProcessingException(
            $"Could not save into \"{preferred}\" ({spaceProblem ?? writeError}) or the fallback folder \"{policy.FallbackFolder}\": {fallbackError}");
    }

    /// <summary>Moves a verified temp file to its final, collision-safe name and returns the record.</summary>
    public OutputRecord Commit(OutputSlot slot, OutputVerification verification)
    {
        if (slot.Finished) throw new InvalidOperationException("Output slot already finished.");
        if (!verification.Ok)
            throw new InvalidOperationException("Refusing to commit an output that failed verification.");

        var policy = _policy();
        var parts = new NameParts(
            slot.Spec.SourceBaseName,
            slot.Spec.Suffix,
            slot.Spec.Kind.Extension(),
            slot.Spec.Page,
            slot.Spec.PageCount,
            verification.Width,
            verification.Height);
        var baseName = OutputNamer.Compose(policy.NamingPattern, parts);

        for (int index = 1; index <= MaxCollisionIndex; index++)
        {
            var candidate = Path.Combine(slot.Directory, OutputNamer.WithCollisionIndex(baseName, index));
            if (File.Exists(candidate) || Directory.Exists(candidate)) continue;
            try
            {
                File.SetAttributes(slot.TempPath, FileAttributes.Normal);
                // overwrite:false maps to MoveFileEx without REPLACE_EXISTING: atomic and never clobbers.
                File.Move(slot.TempPath, candidate, overwrite: false);
            }
            catch (IOException) when (File.Exists(candidate))
            {
                continue; // Another process created the same name in the meantime.
            }

            slot.Finished = true;
            _journal.Remove(slot.TempPath);
            var size = new FileInfo(candidate).Length;
            return new OutputRecord(candidate, slot.Spec.Kind, size, verification.Width, verification.Height,
                verification.Pages, slot.UsedFallback);
        }

        throw new ProcessingException($"Could not find a free file name for \"{baseName}\".");
    }

    /// <summary>Deletes the temp file of an output that failed or was cancelled.</summary>
    public void Discard(OutputSlot slot)
    {
        if (slot.Finished) return;
        slot.Finished = true;
        try
        {
            if (File.Exists(slot.TempPath))
            {
                File.SetAttributes(slot.TempPath, FileAttributes.Normal);
                File.Delete(slot.TempPath);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _journal.Remove(slot.TempPath);
    }

    /// <summary>Free-space figure used instead of the real one (tests only).</summary>
    internal static Func<string, long?>? FreeSpaceOverride { get; set; }

    private static string Sentence(string text) => char.ToUpperInvariant(text[0]) + text[1..] + ".";

    /// <summary>
    /// A description such as "only 3 MB is free on D:" when the folder's drive is nearly full, else null.
    /// Network paths and drives that cannot be queried are not checked.
    /// </summary>
    internal static string? FreeSpaceProblem(string directory)
    {
        long available;
        string drive;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            drive = root.TrimEnd('\\');
            if (FreeSpaceOverride?.Invoke(directory) is { } fake) available = fake;
            else
            {
                var info = new DriveInfo(root);
                if (!info.IsReady) return null;
                available = info.AvailableFreeSpace;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        return available < MinimumFreeBytes ? $"only {Bytes.Format(available)} is free on {drive}" : null;
    }

    private OutputSlot Track(OutputSlot slot)
    {
        _journal.Add(slot.TempPath);
        return slot;
    }

    private static bool TryCreateTemp(string directory, out string tempPath, out string? error)
    {
        tempPath = "";
        error = null;
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $".ftb-{Guid.NewGuid():N}"[..13] + TempJournal.TempExtension);
            // Not marked hidden: Windows rejects create/truncate opens of hidden files, which every
            // encoder uses. The name makes the file's purpose obvious and it only lives during processing.
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            tempPath = path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }
}
