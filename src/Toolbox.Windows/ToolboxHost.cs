using System.Windows;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.History;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Core.Presets;
using Toolbox.Core.Recipes;
using Toolbox.Core.Settings;
using Toolbox.Processing;
using Toolbox.Windows.Diagnostics;

namespace Toolbox.Windows;

/// <summary>Composition root for the desktop app: one instance of each long-lived service.</summary>
internal sealed class ToolboxHost
{
    public ToolboxHost(DiagnosticsChannel diagnostics)
    {
        Diagnostics = diagnostics;
        Settings = new SettingsStore();
        History = new HistoryStore(retention: () => (Settings.Current.HistoryRetentionDays, Settings.Current.HistoryMaxEntries));
        Journal = new TempJournal(AppPaths.TempJournalFile);
        int orphans = Journal.CleanupOrphans();
        if (orphans > 0) Log.Info($"Removed {orphans} temporary file(s) left by an earlier session.");

        Recipes = new RecipeStore();
        Brands = new BrandStore();
        ProcessingEngine.Brands = id => Brands.Find(id);

        Outputs = new OutputManager(() => Settings.Current.ToOutputPolicy(), Journal);
        Runner = ProcessingEngine.CreateRunner(Outputs);
        Runner.JobFinished += OnJobFinished;
        CleanupStaging();
    }

    public RecipeStore Recipes { get; }
    public BrandStore Brands { get; }

    /// <summary>A runner that saves into a specific folder (recipes with their own destination). Shares history.</summary>
    public JobRunner RunnerFor(string folder)
    {
        var outputs = new OutputManager(() => Settings.Current.ToOutputPolicy() with { Location = OutputLocation.Folder, Folder = folder }, Journal);
        var runner = ProcessingEngine.CreateRunner(outputs);
        runner.JobFinished += OnJobFinished;
        return runner;
    }

    /// <summary>Removes recipe staging folders left by a crash (they only ever hold intermediates).</summary>
    private static void CleanupStaging()
    {
        var staging = System.IO.Path.Combine(AppPaths.DataDir, "staging");
        if (!System.IO.Directory.Exists(staging)) return;
        foreach (var dir in System.IO.Directory.GetDirectories(staging))
        {
            try { System.IO.Directory.Delete(dir, recursive: true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public DiagnosticsChannel Diagnostics { get; }
    public SettingsStore Settings { get; }
    public HistoryStore History { get; }
    public TempJournal Journal { get; }
    public OutputManager Outputs { get; }
    public JobRunner Runner { get; }

    /// <summary>The last action started by the user, for "Repeat last action".</summary>
    public (string ActionId, ActionOptions Options)? LastAction { get; set; }

    /// <summary>Default options for an action, taken from the user's quality preferences.</summary>
    public ActionOptions DefaultOptions(ActionDefinition action)
    {
        var s = Settings.Current;
        var options = new ActionOptions();
        switch (action.Output)
        {
            case Core.Inputs.FileKind.Jpeg: options["quality"] = s.JpegQuality.ToString(); break;
            case Core.Inputs.FileKind.WebP: options["quality"] = s.WebpQuality.ToString(); break;
            case Core.Inputs.FileKind.Avif: options["quality"] = s.AvifQuality.ToString(); break;
        }
        if (action.Options.Any(o => o.Key == "dpi")) options["dpi"] = s.PdfDpi.ToString();
        if (action.Options.Any(o => o.Key == "background")) options["background"] = s.TransparencyBackground;
        return options;
    }

    private void OnJobFinished(Job job)
    {
        try
        {
            History.Append(HistoryEntry.From(job));
        }
        catch (Exception ex)
        {
            Log.Warn($"History could not be written: {ex.Message}");
        }

        foreach (var item in job.Items)
        {
            Diagnostics.AppendJob(new
            {
                actionId = job.Action.Id,
                state = item.State.ToString(),
                outputs = item.Outputs.Select(o => o.Path).ToList(),
                error = item.Error,
                inputs = item.Inputs.Select(i => i.Path).ToList(),
            });
        }
        Log.Info($"Job {job.Action.Id} finished: {job.Summary.Describe()}.");
    }

    public static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
