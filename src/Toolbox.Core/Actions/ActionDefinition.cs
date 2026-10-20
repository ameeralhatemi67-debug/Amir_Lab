using Toolbox.Core.Inputs;

namespace Toolbox.Core.Actions;

/// <summary>Which wheel ring an action belongs to.</summary>
public enum ActionGroup
{
    Convert,
    Tools,
    Recipes,
}

/// <summary>What happens when the action is chosen.</summary>
public enum ActionMode
{
    /// <summary>Runs straight away with the current defaults.</summary>
    Immediate,

    /// <summary>Asks a short question first when the input needs it (for example which PDF pages).</summary>
    Options,

    /// <summary>Opens a focused editor.</summary>
    Editor,
}

/// <summary>How the selected files map to job items.</summary>
public enum InputArity
{
    /// <summary>Each input is processed on its own and produces its own outputs.</summary>
    PerFile,

    /// <summary>All inputs are combined into one output (for example images into one PDF).</summary>
    Combined,
}

public enum OptionType
{
    Integer,
    Number,
    Text,
    Choice,
    Boolean,
    Color,
    PageSet,
    ByteSize,
}

public sealed record OptionDefinition(
    string Key,
    string Label,
    OptionType Type,
    string Default,
    string? Help = null,
    double? Min = null,
    double? Max = null,
    IReadOnlyList<string>? Choices = null);

/// <summary>
/// One entry of the capability map: a defined operation, not just a target extension.
/// <see cref="Meaning"/> is shown to the user and documents exactly what the output contains.
/// </summary>
public sealed record ActionDefinition
{
    public required string Id { get; init; }

    /// <summary>Short wheel label, e.g. "JPG" or "Crop".</summary>
    public required string Label { get; init; }

    /// <summary>Full name, e.g. "Render pages as JPG".</summary>
    public required string Title { get; init; }

    /// <summary>Plain-language description of what the result is.</summary>
    public required string Meaning { get; init; }

    public required ActionGroup Group { get; init; }

    public required IReadOnlySet<FileKind> Inputs { get; init; }

    /// <summary>Format of the produced file(s); null when the output keeps the source's format (most editing tools).</summary>
    public FileKind? Output { get; init; }

    public ActionMode Mode { get; init; } = ActionMode.Immediate;

    public InputArity Arity { get; init; } = InputArity.PerFile;

    /// <summary>Icon key resolved by the UI layer.</summary>
    public string Icon { get; init; } = "";

    /// <summary>Text appended to the source name, e.g. " Cropped". Empty for plain conversions.</summary>
    public string OutputSuffix { get; init; } = "";

    public IReadOnlyList<OptionDefinition> Options { get; init; } = [];

    /// <summary>Known limitations, stated honestly in the UI and in docs/CAPABILITIES.md.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];

    /// <summary>True when the result may lose information (lossy encoding, dropped pages, flattened layers...).</summary>
    public bool Lossy { get; init; }

    /// <summary>Runtime feature this action depends on (for example "heic-decode"); null when always available.</summary>
    public string? RequiresFeature { get; init; }

    /// <summary>Input kinds for which the action is pointless (e.g. JPG → JPG) and shown disabled.</summary>
    public IReadOnlySet<FileKind> RedundantFor { get; init; } = new HashSet<FileKind>();

    public bool Accepts(FileKind kind) => Inputs.Contains(kind) && !RedundantFor.Contains(kind);
}
