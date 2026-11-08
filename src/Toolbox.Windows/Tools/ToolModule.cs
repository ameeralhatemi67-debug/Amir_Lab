using Toolbox.Core.Diagnostics;
using Toolbox.Core.Settings;
using Toolbox.Windows.Home;
using Toolbox.Windows.Shell;

namespace Toolbox.Windows.Tools;

/// <summary>Something a tool can do from a keyboard shortcut. <see cref="Title"/> is English (logs); <see cref="Label"/> is shown.</summary>
internal sealed record ToolCommand(string Id, string Title, string? DefaultShortcut, Action Run)
{
    /// <summary>The Arabic title, when there is one.</summary>
    public string? TitleAr { get; init; }

    /// <summary>Shortcuts to offer first when the current one is taken by another program.</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    /// <summary>The title in the app's language.</summary>
    public string Label => Theme.Lang.T(Title, TitleAr ?? Title);
}

/// <summary>A way to reach a tool that is not a registered shortcut (for the Dashboard's list).</summary>
internal sealed record ToolGesture(string Title, IReadOnlyList<string> Keys);

/// <summary>
/// One tool inside Amir Lap. A tool that is switched off is stopped: its windows, hooks and
/// shortcuts are released, so it costs nothing while it is off.
/// </summary>
internal abstract class ToolModule
{
    public abstract string Id { get; }
    public abstract string Name { get; }

    /// <summary>One line for the Dashboard and the page header.</summary>
    public abstract string Summary { get; }

    /// <summary>Key in <see cref="Theme.Icons"/>.</summary>
    public abstract string Icon { get; }

    public abstract IReadOnlyList<ToolCommand> Commands { get; }

    public virtual IReadOnlyList<ToolGesture> Gestures(AppSettings settings) => [];

    /// <summary>
    /// Settings that only take effect when the tool starts. When this text changes while the tool
    /// runs, the registry restarts it instead of asking the user to restart the app.
    /// </summary>
    public virtual string RestartKey(AppSettings settings) => "";

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning) return;
        OnStart();
        IsRunning = true;
        Log.Info($"{Name} started.");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        OnStop();
        IsRunning = false;
        Log.Info($"{Name} stopped.");
    }

    protected abstract void OnStart();
    protected abstract void OnStop();

    /// <summary>The tool's main window or action (Dashboard's "Open" button).</summary>
    public abstract void Open();

    /// <summary>Tool-specific settings, below the switch and shortcuts on the tool's page.</summary>
    public abstract void BuildSettings(SettingsPage page);

    /// <summary>Entries in the tray menu while the tool is on. <paramref name="shortcutText"/> gives a command's active shortcut.</summary>
    public abstract IEnumerable<TrayItem> TrayItems(Func<ToolCommand, string?> shortcutText);
}
