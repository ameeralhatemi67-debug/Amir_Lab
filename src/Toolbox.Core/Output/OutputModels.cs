using Toolbox.Core.Inputs;

namespace Toolbox.Core.Output;

/// <summary>What a handler intends to produce; the final name is decided at commit time.</summary>
public sealed record OutputSpec(string SourceBaseName, string Suffix, FileKind Kind, int? Page = null, int? PageCount = null);

/// <summary>Properties the produced file must have to count as a successful result.</summary>
public sealed record ExpectedOutput(FileKind Kind, int? Width = null, int? Height = null, int? Pages = null);

public sealed record OutputVerification(bool Ok, string? Problem, int? Width = null, int? Height = null, int? Pages = null)
{
    public static OutputVerification Fail(string problem) => new(false, problem);
}

/// <summary>Re-opens a produced file and checks it really is what was promised.</summary>
public interface IOutputVerifier
{
    OutputVerification Verify(string path, ExpectedOutput expected);
}

/// <summary>A finished, verified output that now exists under its final name.</summary>
public sealed record OutputRecord(
    string Path,
    FileKind Kind,
    long SizeBytes,
    int? Width,
    int? Height,
    int? Pages,
    bool UsedFallbackFolder);

public enum OutputLocation
{
    /// <summary>Next to the source file (the default).</summary>
    BesideSource,

    /// <summary>Always into <see cref="OutputPolicy.Folder"/>.</summary>
    Folder,
}

public sealed record OutputPolicy
{
    public OutputLocation Location { get; init; } = OutputLocation.BesideSource;
    public string? Folder { get; init; }
    public string FallbackFolder { get; init; } = AppPaths.DefaultFallbackOutputDir;
    public string NamingPattern { get; init; } = OutputNamer.DefaultPattern;
}

/// <summary>A temporary file a handler writes into before verification and commit.</summary>
public sealed class OutputSlot
{
    internal OutputSlot(string tempPath, string directory, OutputSpec spec, bool usedFallback)
    {
        TempPath = tempPath;
        Directory = directory;
        Spec = spec;
        UsedFallback = usedFallback;
        Expected = new ExpectedOutput(spec.Kind);
    }

    /// <summary>Temporary file in the destination folder (same volume, so the final rename is atomic).</summary>
    public string TempPath { get; }

    public string Directory { get; }
    public OutputSpec Spec { get; }
    public bool UsedFallback { get; }

    /// <summary>Why the fallback folder was used, for the job's note.</summary>
    public string? FallbackReason { get; internal init; }

    /// <summary>Handlers refine this (dimensions, page count) once they know what they wrote.</summary>
    public ExpectedOutput Expected { get; set; }

    internal bool Finished { get; set; }
}
