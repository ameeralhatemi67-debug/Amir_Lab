using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Output;

namespace Toolbox.Core.Jobs;

/// <summary>
/// Runs jobs off the UI thread with bounded concurrency shared across all jobs, so large batches
/// cannot overwhelm the computer. Each item is verified before its outputs are committed.
/// </summary>
public sealed class JobRunner
{
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int HandleDiskFullHResult = unchecked((int)0x80070027);

    private readonly IReadOnlyDictionary<string, IActionHandler> _handlers;
    private readonly OutputManager _outputs;
    private readonly IOutputVerifier _verifier;
    private readonly SemaphoreSlim _slots;

    public JobRunner(IEnumerable<IActionHandler> handlers, OutputManager outputs, IOutputVerifier verifier, int? maxConcurrency = null)
    {
        _handlers = handlers.ToDictionary(h => h.ActionId, StringComparer.OrdinalIgnoreCase);
        _outputs = outputs;
        _verifier = verifier;
        MaxConcurrency = maxConcurrency ?? Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
        _slots = new SemaphoreSlim(MaxConcurrency, MaxConcurrency);
    }

    public int MaxConcurrency { get; }

    /// <summary>Raised once per job when every item has finished (history, notifications).</summary>
    public event Action<Job>? JobFinished;

    public bool CanRun(string actionId) => _handlers.ContainsKey(actionId);

    public Job Start(JobRequest request)
    {
        var action = CapabilityMap.Get(request.ActionId);
        if (!_handlers.TryGetValue(action.Id, out var handler))
            throw new ProcessingException($"\"{action.Title}\" is not implemented yet.");

        var options = (request.Options ?? new ActionOptions()).WithDefaults(action);
        var probed = FileProbe.ProbeAll(request.InputPaths);
        var items = BuildItems(action, options, probed);
        var job = new Job(request, action, options, items);

        _ = RunAsync(job, handler);
        return job;
    }

    /// <summary>Starts a new job containing only the failed items of a finished job.</summary>
    public Job? RetryFailed(Job job)
    {
        var failed = job.Items.Where(i => i.State == JobItemState.Failed).SelectMany(i => i.Inputs).Select(f => f.Path).ToList();
        if (failed.Count == 0) return null;
        return Start(job.Request with { InputPaths = failed });
    }

    private static List<JobItem> BuildItems(ActionDefinition action, ActionOptions options, IReadOnlyList<ProbedFile> probed)
    {
        var items = new List<JobItem>();
        var accepted = new List<ProbedFile>();

        foreach (var file in probed)
        {
            var verdict = CapabilityMap.Evaluate(action, [file]);
            if (verdict.IsEnabled) accepted.Add(file);
            else items.Add(new JobItem([file], JobItemState.Skipped, verdict.Reason));
        }

        if (action.Arity == InputArity.Combined)
        {
            if (accepted.Count > 0)
            {
                var ordered = options.GetString("order", "name") == "given"
                    ? accepted
                    : accepted.OrderBy(f => f.Name, NaturalComparer.Instance).ToList();
                items.Insert(0, new JobItem(ordered));
            }
        }
        else
        {
            items.InsertRange(0, accepted.Select(f => new JobItem([f])));
        }
        return items;
    }

    private async Task RunAsync(Job job, IActionHandler handler)
    {
        await Task.Yield();
        var runnable = job.Items.Where(i => i.State == JobItemState.Queued).ToList();
        try
        {
            await Task.WhenAll(runnable.Select(item => RunItemAsync(job, handler, item)));
        }
        finally
        {
            job.MarkFinished();
            JobFinished?.Invoke(job);
        }
    }

    private async Task RunItemAsync(Job job, IActionHandler handler, JobItem item)
    {
        var ct = job.Cancellation;
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            item.State = JobItemState.Cancelled;
            job.NotifyChanged();
            return;
        }

        var context = new ItemContext(item, job.Action, job.Options, _outputs, ct, job.NotifyChanged);
        try
        {
            item.State = JobItemState.Running;
            job.NotifyChanged();

            await Task.Run(() => handler.RunAsync(context), ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            var committed = new List<OutputRecord>();
            var slots = context.Slots.ToList();
            if (slots.Count == 0 && job.Action.Output is not null)
                throw new ProcessingException("The action finished without producing a file.");

            // Verify everything first so a partly broken result never appears as a success.
            var verifications = slots.Select(s => (Slot: s, Result: _verifier.Verify(s.TempPath, s.Expected))).ToList();
            var bad = verifications.FirstOrDefault(v => !v.Result.Ok);
            if (bad.Slot is not null)
                throw new ProcessingException($"The produced file did not pass verification: {bad.Result.Problem}");

            foreach (var (slot, result) in verifications)
                committed.Add(_outputs.Commit(slot, result));

            item.SetOutputs(committed);
            if (committed.Any(o => o.UsedFallbackFolder))
                item.AddNote($"The source folder could not be written to; saved in \"{committed.First(o => o.UsedFallbackFolder).Path}\".");
            item.Progress = 1;
            item.State = JobItemState.Completed;
        }
        catch (OperationCanceledException)
        {
            item.State = JobItemState.Cancelled;
        }
        catch (Exception ex)
        {
            item.Error = Describe(ex);
            item.State = JobItemState.Failed;
            Diagnostics.Log.Error($"Job {job.Id} item '{item.Label}' failed", ex);
        }
        finally
        {
            foreach (var slot in context.Slots) _outputs.Discard(slot);
            _slots.Release();
            job.NotifyChanged();
        }
    }

    internal static string Describe(Exception ex) => ex switch
    {
        ProcessingException p => p.Message,
        UnauthorizedAccessException => "Access was denied. Check that the file is not open elsewhere and that you can write to the destination.",
        IOException io when io.HResult is DiskFullHResult or HandleDiskFullHResult => "There is not enough free disk space to save the result.",
        FileNotFoundException => "The file could not be found. It may have been moved or deleted.",
        IOException io => $"A file error occurred: {io.Message}",
        OutOfMemoryException => "The file is too large to process with the available memory.",
        _ => $"Unexpected error: {ex.Message}",
    };
}
