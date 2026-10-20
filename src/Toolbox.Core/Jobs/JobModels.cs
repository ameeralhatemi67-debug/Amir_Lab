using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Output;

namespace Toolbox.Core.Jobs;

public sealed record JobRequest(
    string ActionId,
    IReadOnlyList<string> InputPaths,
    ActionOptions? Options = null,
    string? RecipeId = null,
    string? Title = null);

public enum JobItemState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,

    /// <summary>The input does not apply to the action; reported, never silently dropped.</summary>
    Skipped,
}

public sealed class JobItem
{
    private readonly object _gate = new();
    private List<OutputRecord> _outputs = [];
    private List<string> _notes = [];

    internal JobItem(IReadOnlyList<ProbedFile> inputs, JobItemState initialState = JobItemState.Queued, string? error = null)
    {
        Inputs = inputs;
        State = initialState;
        Error = error;
    }

    public IReadOnlyList<ProbedFile> Inputs { get; }
    public string Label => Inputs.Count == 1 ? Inputs[0].Name : $"{Inputs.Count} files";
    public JobItemState State { get; internal set; }
    public double Progress { get; internal set; }
    public string? Error { get; internal set; }

    public IReadOnlyList<OutputRecord> Outputs { get { lock (_gate) return _outputs.ToList(); } }
    public IReadOnlyList<string> Notes { get { lock (_gate) return _notes.ToList(); } }

    public bool IsFinished => State is JobItemState.Completed or JobItemState.Failed or JobItemState.Cancelled or JobItemState.Skipped;

    internal void SetOutputs(IEnumerable<OutputRecord> outputs) { lock (_gate) _outputs = outputs.ToList(); }
    internal void AddNote(string note) { lock (_gate) if (!_notes.Contains(note)) _notes.Add(note); }
}

public sealed record JobSummary(int Total, int Completed, int Failed, int Cancelled, int Skipped, int Running, int Queued, int OutputCount)
{
    public bool IsFinished => Running == 0 && Queued == 0;

    /// <summary>Human-readable status such as "1 of 1 saved" or "7 of 8 saved · 1 failed".</summary>
    public string Describe()
    {
        int processable = Total - Skipped;
        var parts = new List<string>();
        if (OutputCount > Completed && Completed > 0)
            parts.Add($"{OutputCount} files saved");
        else
            parts.Add($"{Completed} of {processable} saved");
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Cancelled > 0) parts.Add($"{Cancelled} cancelled");
        if (Skipped > 0) parts.Add($"{Skipped} skipped");
        return string.Join(" · ", parts);
    }
}
