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
    }

    public bool CanOpen(ActionDefinition action) => action.Id == CapabilityMap.Ids.Metadata || _studio.CanOpen(action);

    public void Open(ActionDefinition action, IReadOnlyList<string> paths, Native.POINT? anchor)
    {
        if (action.Id == CapabilityMap.Ids.Metadata)
        {
            var window = new MetadataWindow(paths, _dispatcher, anchor);
            window.Show();
            Foreground.Activate(window);
            return;
        }
        _studio.Open(action, paths, anchor);
    }
}
