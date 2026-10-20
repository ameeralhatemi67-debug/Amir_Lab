using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Output;

namespace Toolbox.Core.Jobs;

/// <summary>Executes one action for one job item. Implementations live in Toolbox.Processing.</summary>
public interface IActionHandler
{
    string ActionId { get; }

    /// <summary>
    /// Produce outputs by calling <see cref="ItemContext.CreateOutput"/> and writing to the slot's temp path.
    /// Throw <see cref="ProcessingException"/> with a user-facing message on failure.
    /// </summary>
    Task RunAsync(ItemContext context);
}

/// <summary>Everything a handler may use while processing one item.</summary>
public sealed class ItemContext
{
    private readonly OutputManager _outputs;
    private readonly Action _changed;
    private readonly List<OutputSlot> _slots = [];

    internal ItemContext(JobItem item, ActionDefinition action, ActionOptions options, OutputManager outputs,
        CancellationToken cancellation, Action changed)
    {
        Item = item;
        Action = action;
        Options = options;
        _outputs = outputs;
        Cancellation = cancellation;
        _changed = changed;
    }

    public JobItem Item { get; }
    public ActionDefinition Action { get; }
    public ActionOptions Options { get; }
    public CancellationToken Cancellation { get; }
    public IReadOnlyList<ProbedFile> Inputs => Item.Inputs;
    public ProbedFile Input => Item.Inputs[0];

    internal IReadOnlyList<OutputSlot> Slots => _slots;

    /// <summary>Reserves a temp file for one output in the destination folder for this item.</summary>
    public OutputSlot CreateOutput(OutputSpec spec)
    {
        Cancellation.ThrowIfCancellationRequested();
        var slot = _outputs.CreateSlot(Input.Directory, spec);
        lock (_slots) _slots.Add(slot);
        return slot;
    }

    /// <summary>Reports progress of this item in [0,1].</summary>
    public void Report(double fraction)
    {
        Item.Progress = Math.Clamp(fraction, 0, 1);
        _changed();
    }

    /// <summary>Adds a user-visible note, e.g. "Animated GIF: only the first frame was exported."</summary>
    public void Note(string message)
    {
        Item.AddNote(message);
        _changed();
    }
}
