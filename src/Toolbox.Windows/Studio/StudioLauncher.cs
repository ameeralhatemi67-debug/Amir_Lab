using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Recipes;
using Toolbox.Processing.Recipes;
using Toolbox.Processing.Studio;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Shell;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

namespace Toolbox.Windows.Studio;

/// <summary>Opens the Studio for the editing tools on the wheel and for interactive recipes.</summary>
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
        var path = First(paths, entry.Title, anchor);
        var settings = App.Host.Settings.Current;
        Show(entry, path, anchor, [], source => new EditDocument
        {
            SourcePath = path,
            Page = source.Page,
            Dpi = source.Dpi > 0 ? source.Dpi : settings.PdfDpi,
            Background = new BackgroundSettings { Enabled = action.Id == Ids.Background },
            Export = action.Id == Ids.Compress ? new ExportSettings { Quality = 75 } : new ExportSettings { Quality = settings.JpegQuality },
        });
    }

    /// <summary>The Blur step on one picture (Longshot's result, Preview); optionally finding personal information at once.</summary>
    public void OpenBlur(string path, bool findPersonalInfo, Native.POINT? anchor)
    {
        var entry = new StudioEntry(StudioStep.Redact, findPersonalInfo ? "Hide personal info" : "Blur & hide", AllSteps) { FindPersonalInfo = findPersonalInfo };
        var settings = App.Host.Settings.Current;
        Show(entry, path, anchor, [], source => new EditDocument
        {
            SourcePath = path,
            Page = source.Page,
            Dpi = source.Dpi > 0 ? source.Dpi : settings.PdfDpi,
            Export = new ExportSettings { Quality = settings.JpegQuality },
        });
    }

    /// <summary>Interactive recipe: the editor opens on the crop step with the recipe's framing and export applied.</summary>
    public void OpenRecipe(Recipe recipe, IReadOnlyList<string> paths, Native.POINT? anchor)
    {
        if (paths.Count == 0) return;
        var path = First(paths, recipe.Name, anchor);
        var entry = new StudioEntry(StudioStep.Crop, recipe.Name, AllSteps, recipe.Suffix.Length > 0 ? recipe.Suffix : null);
        var variants = recipe.Steps.Where(s => s.Op == RecipeOp.Frame).Select(s => s.Get("ratios", ""))
            .SelectMany(r => r.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        var settings = App.Host.Settings.Current;
        Show(entry, path, anchor, variants, source =>
        {
            var template = RecipeCompiler.InteractiveTemplate(recipe, path, id => App.Host.Brands.Find(id));
            return template with { Page = source.Page, Dpi = source.Dpi > 0 ? source.Dpi : settings.PdfDpi };
        });
    }

    private string First(IReadOnlyList<string> paths, string title, Native.POINT? anchor)
    {
        if (paths.Count > 1)
            _dispatcher.Notice(title, $"The editor works on one file at a time; opened {IOPath.GetFileName(paths[0])}.", anchor, warning: false);
        return paths[0];
    }

    private void Show(StudioEntry entry, string path, Native.POINT? anchor, IReadOnlyList<string> variants, Func<StudioSource, EditDocument> makeDocument)
    {
        var window = new StudioWindow(entry, IOPath.GetFileName(path), anchor,
            (doc, suffix, chosenVariants) => Save(entry, path, doc, suffix, chosenVariants, anchor), variants);
        window.Show();
        Foreground.Activate(window);
        _ = LoadAsync(window, path, makeDocument);
    }

    private static async Task LoadAsync(StudioWindow window, string path, Func<StudioSource, EditDocument> makeDocument)
    {
        try
        {
            var settings = App.Host.Settings.Current;
            var source = await Task.Run(() => StudioSource.Open(path, 1, settings.PdfDpi));
            window.Attach(new StudioSession(makeDocument(source), source));
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

    private void Save(StudioEntry entry, string path, EditDocument doc, string? suffix, IReadOnlyList<string> variants, Native.POINT? anchor)
    {
        var options = new ActionOptions { ["document"] = doc.ToJson() };
        if (suffix is not null) options["suffix"] = suffix;
        if (variants.Count > 0) options["variants"] = string.Join(",", variants);
        _dispatcher.Start(CapabilityMap.Get(Ids.StudioExport), [path], options, anchor ?? ScreenGeometry.CursorPosition(), entry.Title);
    }

    private static StudioEntry? EntryFor(string actionId) => actionId switch
    {
        Ids.Crop => new StudioEntry(StudioStep.Crop, "Crop Image", AllSteps),
        Ids.Edit => new StudioEntry(StudioStep.Adjust, "Edit Image", AllSteps),
        Ids.Background => new StudioEntry(StudioStep.Background, "Add Background", AllSteps),
        Ids.Annotate => new StudioEntry(StudioStep.Annotate, "Annotate", AllSteps),
        Ids.Redact => new StudioEntry(StudioStep.Redact, "Blur & hide", AllSteps),
        Ids.Compress => new StudioEntry(StudioStep.Export, "Compress", [StudioStep.Export], " Compressed"),
        _ => null,
    };
}
