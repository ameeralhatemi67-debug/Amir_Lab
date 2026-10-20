using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Diagnostics;
using Toolbox.Processing.Studio;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Shell;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

namespace Toolbox.Windows.Studio;

/// <summary>Opens the Studio for the editing tools on the wheel.</summary>
internal sealed class StudioLauncher : IEditorLauncher
{
    private static readonly StudioStep[] AllSteps =
        [StudioStep.Crop, StudioStep.Adjust, StudioStep.Background, StudioStep.Annotate, StudioStep.Redact, StudioStep.Export];

    private readonly ActionDispatcher _dispatcher;

    public StudioLauncher(ActionDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public bool CanOpen(ActionDefinition action) => EntryFor(action.Id) is not null;

    public void Open(ActionDefinition action, IReadOnlyList<string> paths, Native.POINT? anchor)
    {
        var entry = EntryFor(action.Id)!;
        if (paths.Count == 0) return;
        var path = paths[0];
        if (paths.Count > 1)
            _dispatcher.Notice(entry.Title, $"The editor works on one file at a time; opened {IOPath.GetFileName(path)}.", anchor, warning: false);

        var window = new StudioWindow(entry, IOPath.GetFileName(path), anchor, (doc, suffix) => Save(entry, path, doc, suffix, anchor));
        window.Show();
        Foreground.Activate(window);
        _ = LoadAsync(window, entry, action, path);
    }

    private static async Task LoadAsync(StudioWindow window, StudioEntry entry, ActionDefinition action, string path)
    {
        try
        {
            var settings = App.Host.Settings.Current;
            var source = await Task.Run(() => StudioSource.Open(path, 1, settings.PdfDpi));
            var doc = new EditDocument
            {
                SourcePath = path,
                Page = source.Page,
                Dpi = source.Dpi > 0 ? source.Dpi : settings.PdfDpi,
                Background = new BackgroundSettings { Enabled = action.Id == Ids.Background },
                Export = action.Id == Ids.Compress ? new ExportSettings { Quality = 75 } : new ExportSettings { Quality = settings.JpegQuality },
            };
            window.Attach(new StudioSession(doc, source));
        }
        catch (ProcessingException ex)
        {
            window.ShowLoadError(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error("Studio could not open a file", ex);
            window.ShowLoadError("The file could not be opened: " + ex.Message);
        }
    }

    private void Save(StudioEntry entry, string path, EditDocument doc, string? suffix, Native.POINT? anchor)
    {
        var options = new ActionOptions { ["document"] = doc.ToJson() };
        if (suffix is not null) options["suffix"] = suffix;
        _dispatcher.Start(CapabilityMap.Get(Ids.StudioExport), [path], options, anchor ?? ScreenGeometry.CursorPosition(), entry.Title);
    }

    private static StudioEntry? EntryFor(string actionId) => actionId switch
    {
        Ids.Crop => new StudioEntry(StudioStep.Crop, "Crop Image", AllSteps),
        Ids.Edit => new StudioEntry(StudioStep.Adjust, "Edit Image", AllSteps),
        Ids.Background => new StudioEntry(StudioStep.Background, "Add Background", AllSteps),
        Ids.Annotate => new StudioEntry(StudioStep.Annotate, "Annotate", AllSteps),
        Ids.Redact => new StudioEntry(StudioStep.Redact, "Redact", AllSteps),
        Ids.Compress => new StudioEntry(StudioStep.Export, "Compress", [StudioStep.Export], " Compressed"),
        _ => null,
    };
}
