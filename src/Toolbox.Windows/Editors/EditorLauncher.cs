using Toolbox.Core.Actions;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Studio;

namespace Toolbox.Windows.Editors;

/// <summary>Routes editor actions to the right window: Metadata has its own; everything else is the Studio.</summary>
internal sealed class EditorLauncher : IEditorLauncher
{
    private readonly ActionDispatcher _dispatcher;
    private readonly StudioLauncher _studio;

    public EditorLauncher(ActionDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _studio = new StudioLauncher(dispatcher);
        dispatcher.RecipeEditor = _studio.OpenRecipe;
    }

    public bool CanOpen(ActionDefinition action) => action.Id == CapabilityMap.Ids.Metadata || PdfStart(action.Id) is not null || _studio.CanOpen(action);

    /// <summary>The PDF editor's entries (they open the viewer in edit mode).</summary>
    private static Viewer.ViewerStart? PdfStart(string id) => id switch
    {
        CapabilityMap.Ids.PdfEdit => Viewer.ViewerStart.Edit,
        CapabilityMap.Ids.PdfSign => Viewer.ViewerStart.Sign,
        CapabilityMap.Ids.PdfPages => Viewer.ViewerStart.Pages,
        _ => null,
    };

    public void OpenBlur(string path, bool findPersonalInfo, Native.POINT? anchor) => _studio.OpenBlur(path, findPersonalInfo, anchor);

    public void Open(ActionDefinition action, IReadOnlyList<string> paths, Native.POINT? anchor)
    {
        if (action.Id == CapabilityMap.Ids.Metadata)
        {
            var window = new MetadataWindow(paths, _dispatcher, anchor);
            window.Show();
            Foreground.Activate(window);
            return;
        }
        if (PdfStart(action.Id) is { } start)
        {
            Viewer.ViewerWindow.Open(paths, start, _dispatcher, anchor);
            return;
        }
        _studio.Open(action, paths, anchor);
    }
}
