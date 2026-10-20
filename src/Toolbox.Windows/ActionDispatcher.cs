using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Processing.Pdf;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Shell;

namespace Toolbox.Windows;

/// <summary>Opens an editor for an action and a set of inputs; implemented by the Studio (M4).</summary>
internal interface IEditorLauncher
{
    bool CanOpen(ActionDefinition action);

    void Open(ActionDefinition action, IReadOnlyList<string> paths, Native.POINT? anchor);
}

/// <summary>
/// Decides what choosing an action does — run now, ask first (PDF pages), or open an editor — and
/// shows progress and results. Every entry point (wheel, palette, drop zone, context menu) goes through here.
/// </summary>
internal sealed class ActionDispatcher
{
    private readonly ToolboxHost _host;
    private readonly ToastManager _toasts;

    public ActionDispatcher(ToolboxHost host, ToastManager toasts)
    {
        _host = host;
        _toasts = toasts;
    }

    public IEditorLauncher? Editors { get; set; }

    public async void Dispatch(string actionId, IReadOnlyList<string> paths, Native.POINT? anchor, ActionOptions? overrides = null)
    {
        try
        {
            if (!CapabilityMap.TryGet(actionId, out var action))
            {
                Notice("Unknown action", actionId, anchor, warning: true);
                return;
            }
            var options = _host.DefaultOptions(action);
            if (overrides is not null) foreach (var (k, v) in overrides) options[k] = v;

            if (action.Mode == ActionMode.Editor || action.Id == CapabilityMap.Ids.StudioExport)
            {
                if (Editors?.CanOpen(action) == true) Editors.Open(action, paths, anchor);
                else Notice(action.Title, "This editor is not available in this build yet.", anchor, warning: true);
                return;
            }

            if (!_host.Runner.CanRun(action.Id))
            {
                Notice(action.Title, "This action is not implemented yet.", anchor, warning: true);
                return;
            }

            if (action.Mode == ActionMode.Options && action.Inputs.Contains(FileKind.Pdf) && !options.ContainsKey("pages"))
            {
                var pdfs = await Task.Run(() => CountPages(paths));
                if (pdfs.Any(p => p.Pages > 1))
                {
                    var picker = new PagePickerWindow(action, pdfs, options);
                    if (anchor is { } a) picker.PlaceNear(a, -80);
                    picker.Show();
                    Foreground.Activate(picker);
                    picker.Closed += (_, _) =>
                    {
                        if (picker.Result is { } chosen) Start(action, paths, chosen, anchor);
                    };
                    return;
                }
            }
            Start(action, paths, options, anchor);
        }
        catch (Exception ex)
        {
            Log.Error($"Dispatch of {actionId} failed", ex);
            Notice("Something went wrong", ex.Message, anchor, warning: true);
        }
    }

    public Job? Start(ActionDefinition action, IReadOnlyList<string> paths, ActionOptions options, Native.POINT? anchor, string? title = null)
    {
        try
        {
            var job = _host.Runner.Start(new JobRequest(action.Id, paths, options, Title: title));
            _host.LastAction = (action.Id, options);
            _toasts.Show(job, anchor);
            return job;
        }
        catch (ProcessingException ex)
        {
            Notice(action.Title, ex.Message, anchor, warning: true);
            return null;
        }
    }

    public void Retry(Job job)
    {
        var retry = _host.Runner.RetryFailed(job);
        if (retry is not null) _toasts.Show(retry, null);
    }

    public void Notice(string title, string message, Native.POINT? anchor, bool warning)
    {
        var toast = new NoticeToast(title, message, warning);
        toast.PlaceNear(anchor ?? ScreenGeometry.CursorPosition(), 28);
        toast.Show();
    }

    private static List<(ProbedFile File, int Pages)> CountPages(IReadOnlyList<string> paths)
    {
        var list = new List<(ProbedFile, int)>();
        foreach (var file in FileProbe.ProbeAll(paths).Where(f => f.Kind == FileKind.Pdf && f.IsUsable))
        {
            try
            {
                using var doc = PdfDocument.Open(file.Path);
                list.Add((file, doc.PageCount));
            }
            catch (ProcessingException)
            {
                list.Add((file, 1)); // the job itself will report the problem
            }
        }
        return list;
    }
}
