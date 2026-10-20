using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;

namespace Toolbox.Core.Tests;

public class JobRunnerTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("ftb-jobs-");

    public void Dispose()
    {
        foreach (var f in _dir.EnumerateFiles("*", SearchOption.AllDirectories)) f.Attributes = FileAttributes.Normal;
        _dir.Delete(true);
    }

    private sealed class AcceptAll : IOutputVerifier
    {
        public OutputVerification Verify(string path, ExpectedOutput expected) => new(true, null, 10, 10);
    }

    private sealed class RejectAll : IOutputVerifier
    {
        public OutputVerification Verify(string path, ExpectedOutput expected) => OutputVerification.Fail("not a PNG");
    }

    /// <summary>Writes a fake PNG; optionally waits on a gate so tests can cancel mid-run.</summary>
    private sealed class FakePngHandler(TaskCompletionSource? gate = null) : IActionHandler
    {
        public string ActionId => CapabilityMap.Ids.ImageToPng;

        public async Task RunAsync(ItemContext context)
        {
            if (gate is not null) await gate.Task.WaitAsync(context.Cancellation);
            var slot = context.CreateOutput(new OutputSpec(context.Input.BaseName, "", FileKind.Png));
            await File.WriteAllTextAsync(slot.TempPath, "png", context.Cancellation);
        }
    }

    private string Jpeg(string name)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]);
        return path;
    }

    private JobRunner Runner(IActionHandler handler, IOutputVerifier? verifier = null, int concurrency = 2) =>
        new([handler], new OutputManager(() => new OutputPolicy(), new TempJournal(Path.Combine(_dir.FullName, "j.txt"))),
            verifier ?? new AcceptAll(), concurrency);

    [Fact]
    public async Task Converts_each_file_and_skips_unsupported_with_reason()
    {
        var a = Jpeg("a.jpg");
        var b = Jpeg("b.jpg");
        var bad = Path.Combine(_dir.FullName, "notes.xyz");
        File.WriteAllText(bad, "text");

        var job = Runner(new FakePngHandler()).Start(new JobRequest(CapabilityMap.Ids.ImageToPng, [a, b, bad]));
        await job.Completion;

        var summary = job.Summary;
        Assert.Equal(2, summary.Completed);
        Assert.Equal(1, summary.Skipped);
        Assert.Contains("Unsupported", job.Items.Single(i => i.State == JobItemState.Skipped).Error);
        Assert.True(File.Exists(Path.Combine(_dir.FullName, "a.png")));
        Assert.Equal("2 of 2 saved · 1 skipped", summary.Describe());
    }

    [Fact]
    public async Task Failed_verification_leaves_no_output()
    {
        var a = Jpeg("a.jpg");
        var job = Runner(new FakePngHandler(), new RejectAll()).Start(new JobRequest(CapabilityMap.Ids.ImageToPng, [a]));
        await job.Completion;

        Assert.Equal(JobItemState.Failed, job.Items[0].State);
        Assert.Contains("verification", job.Items[0].Error);
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*.png"));
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*" + TempJournal.TempExtension));
    }

    [Fact]
    public async Task Cancel_marks_unfinished_items_and_cleans_temp_files()
    {
        var gate = new TaskCompletionSource();
        var files = Enumerable.Range(1, 5).Select(i => Jpeg($"img{i}.jpg")).ToList();
        var job = Runner(new FakePngHandler(gate), concurrency: 1).Start(new JobRequest(CapabilityMap.Ids.ImageToPng, files));

        await Task.Delay(100);
        job.Cancel();
        await job.Completion;

        Assert.All(job.Items, i => Assert.Equal(JobItemState.Cancelled, i.State));
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*.png"));
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*" + TempJournal.TempExtension));
    }

    [Fact]
    public async Task CancelAll_stops_running_jobs_and_removes_their_temp_files()
    {
        var gate = new TaskCompletionSource();
        var files = Enumerable.Range(1, 3).Select(i => Jpeg($"exit{i}.jpg")).ToList();
        var runner = Runner(new FakePngHandler(gate), concurrency: 1);
        var job = runner.Start(new JobRequest(CapabilityMap.Ids.ImageToPng, files));
        await Task.Delay(100);
        Assert.Single(runner.Active);

        Assert.True(runner.CancelAll(TimeSpan.FromSeconds(5)));
        await job.Completion;
        Assert.Empty(runner.Active);
        Assert.All(job.Items, i => Assert.Equal(JobItemState.Cancelled, i.State));
        Assert.Empty(Directory.GetFiles(_dir.FullName, "*" + TempJournal.TempExtension));
    }

    [Fact]
    public async Task Nearly_full_drive_falls_back_and_says_why()
    {
        var fallback = Path.Combine(_dir.FullName, "fallback");
        var a = Jpeg("a.jpg");
        OutputManager.FreeSpaceOverride = dir => dir.StartsWith(fallback, StringComparison.OrdinalIgnoreCase) ? null
            : dir.StartsWith(_dir.FullName, StringComparison.OrdinalIgnoreCase) ? 1024 * 1024 : null;
        try
        {
            var outputs = new OutputManager(() => new OutputPolicy { FallbackFolder = fallback }, new TempJournal(Path.Combine(_dir.FullName, "j.txt")));
            var job = new JobRunner([new FakePngHandler()], outputs, new AcceptAll(), 1).Start(new JobRequest(CapabilityMap.Ids.ImageToPng, [a]));
            await job.Completion;

            Assert.Equal(JobItemState.Completed, job.Items[0].State);
            Assert.True(File.Exists(Path.Combine(fallback, "a.png")));
            Assert.Contains(job.Items[0].Notes, n => n.Contains("instead because only 1 MB is free on"));
        }
        finally
        {
            OutputManager.FreeSpaceOverride = null;
        }
    }

    [Fact]
    public async Task Full_drives_fail_early_naming_the_drive()
    {
        var a = Jpeg("b.jpg");
        OutputManager.FreeSpaceOverride = dir => dir.StartsWith(_dir.FullName, StringComparison.OrdinalIgnoreCase) ? 2 * 1024 * 1024 : null;
        try
        {
            var outputs = new OutputManager(() => new OutputPolicy { FallbackFolder = Path.Combine(_dir.FullName, "fb") }, new TempJournal(Path.Combine(_dir.FullName, "j.txt")));
            var job = new JobRunner([new FakePngHandler()], outputs, new AcceptAll(), 1).Start(new JobRequest(CapabilityMap.Ids.ImageToPng, [a]));
            await job.Completion;

            Assert.Equal(JobItemState.Failed, job.Items[0].State);
            Assert.StartsWith("Only 2 MB is free on", job.Items[0].Error);
            Assert.Contains("Free up some space", job.Items[0].Error);
            Assert.Empty(Directory.GetFiles(_dir.FullName, "*" + TempJournal.TempExtension, SearchOption.AllDirectories));
        }
        finally
        {
            OutputManager.FreeSpaceOverride = null;
        }
    }

    [Fact]
    public async Task Same_format_conversion_is_skipped_as_redundant()
    {
        var png = Path.Combine(_dir.FullName, "already.png");
        File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]);
        var job = Runner(new FakePngHandler()).Start(new JobRequest(CapabilityMap.Ids.ImageToPng, [png]));
        await job.Completion;
        Assert.Equal(JobItemState.Skipped, job.Items[0].State);
        Assert.Equal("Already PNG", job.Items[0].Error);
    }

    [Fact]
    public void Natural_order_sorts_numbers_and_arabic_digits()
    {
        var names = new[] { "Page 10", "Page 2", "Page 1", "صفحة ٣", "صفحة ١٢" };
        var sorted = names.OrderBy(n => n, NaturalComparer.Instance).ToList();
        Assert.True(sorted.IndexOf("Page 2") < sorted.IndexOf("Page 10"));
        Assert.True(sorted.IndexOf("صفحة ٣") < sorted.IndexOf("صفحة ١٢"));
    }
}

public class CapabilityMapTests
{
    [Fact]
    public void Every_wheel_layout_entry_exists_in_the_map()
    {
        var ids = WheelLayouts.DefaultConvert.PdfActions
            .Concat(WheelLayouts.DefaultConvert.ImageActions)
            .Concat(WheelLayouts.DefaultTools.ImageActions);
        foreach (var id in ids) Assert.True(CapabilityMap.TryGet(id, out _), id);
    }

    [Fact]
    public void Every_action_states_its_meaning()
    {
        Assert.All(CapabilityMap.All, a => Assert.False(string.IsNullOrWhiteSpace(a.Meaning), a.Id));
        Assert.Equal(CapabilityMap.All.Count, CapabilityMap.All.Select(a => a.Id).Distinct().Count());
    }

    [Fact]
    public void Feature_gated_actions_explain_why_they_are_unavailable()
    {
        var ocr = CapabilityMap.Get(CapabilityMap.Ids.OcrImageText);
        var file = new ProbedFile(@"C:\x.png", FileKind.Png, 10, null);
        var verdict = CapabilityMap.Evaluate(ocr, [file]);
        Assert.False(verdict.IsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(verdict.Reason));
    }

    [Fact]
    public void Partial_applicability_is_reported()
    {
        var toPdf = CapabilityMap.Get(CapabilityMap.Ids.ImagesToPdf);
        var files = new[]
        {
            new ProbedFile(@"C:\a.png", FileKind.Png, 10, null),
            new ProbedFile(@"C:\b.pdf", FileKind.Pdf, 10, null),
        };
        var verdict = CapabilityMap.Evaluate(toPdf, files);
        Assert.True(verdict.IsPartial);
        Assert.Equal(1, verdict.Applicable);
    }
}
