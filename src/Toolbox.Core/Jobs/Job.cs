using Toolbox.Core.Actions;

namespace Toolbox.Core.Jobs;

/// <summary>One user request (a drop, a palette command, a recipe step) and its per-file items.</summary>
public sealed class Job
{
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _changeVersion;

    internal Job(JobRequest request, ActionDefinition action, ActionOptions options, IReadOnlyList<JobItem> items)
    {
        Request = request;
        Action = action;
        Options = options;
        Items = items;
        Started = DateTimeOffset.Now;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public JobRequest Request { get; }
    public ActionDefinition Action { get; }
    public ActionOptions Options { get; }
    public IReadOnlyList<JobItem> Items { get; }
    public DateTimeOffset Started { get; }
    public DateTimeOffset? Finished { get; private set; }
    public string Title => Request.Title ?? Action.Title;

    public CancellationToken Cancellation => _cts.Token;
    public bool IsCancellationRequested => _cts.IsCancellationRequested;
    public Task Completion => _completion.Task;
    public bool IsFinished => Finished is not null;

    /// <summary>Monotonic counter, bumped on every change; lets UIs coalesce rapid progress events.</summary>
    public int ChangeVersion => Volatile.Read(ref _changeVersion);

    /// <summary>Raised from worker threads. UI code must marshal to its own thread.</summary>
    public event Action<Job>? Changed;

    /// <summary>Overall progress across items in [0,1].</summary>
    public double Progress
    {
        get
        {
            var countable = Items.Where(i => i.State != JobItemState.Skipped).ToList();
            if (countable.Count == 0) return 1;
            return countable.Sum(i => i.IsFinished ? 1 : i.Progress) / countable.Count;
        }
    }

    public JobSummary Summary => new(
        Items.Count,
        Items.Count(i => i.State == JobItemState.Completed),
        Items.Count(i => i.State == JobItemState.Failed),
        Items.Count(i => i.State == JobItemState.Cancelled),
        Items.Count(i => i.State == JobItemState.Skipped),
        Items.Count(i => i.State == JobItemState.Running),
        Items.Count(i => i.State == JobItemState.Queued),
        Items.Sum(i => i.Outputs.Count));

    /// <summary>Stops queued and running items. Outputs that were already saved stay in place.</summary>
    public void Cancel()
    {
        if (!IsFinished) _cts.Cancel();
    }

    internal void NotifyChanged()
    {
        Interlocked.Increment(ref _changeVersion);
        Changed?.Invoke(this);
    }

    internal void MarkFinished()
    {
        Finished = DateTimeOffset.Now;
        NotifyChanged();
        _completion.TrySetResult();
    }
}
