using Toolbox.Core.Sound;

namespace Toolbox.Windows.Sound;

/// <summary>
/// Where Pulse's Sound tile and the windows find Imp Studio's engine while it is on, and the one
/// place its changes are brought to the UI thread (at most once per frame's worth of changes).
/// </summary>
internal static class SoundHub
{
    private static int _queued;

    /// <summary>The engine, while Imp Studio is on.</summary>
    public static ISoundControl? Control { get; private set; }

    /// <summary>Raised on the UI thread after the engine's snapshot changed, or it started or stopped.</summary>
    public static event Action? Changed;

    public static void Attach(ISoundControl? control)
    {
        if (Control is { } old) old.Changed -= OnChanged;
        Control = control;
        if (control is not null) control.Changed += OnChanged;
        Changed?.Invoke();
    }

    private static void OnChanged(SoundSnapshot _)
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        ToolboxHost.OnUi(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            Changed?.Invoke();
        });
    }

    /// <summary>The name to show for an app (Windows' own sounds have none of their own).</summary>
    public static string NameOf(SoundApp app) => app.IsSystem ? Theme.Lang.T("Windows sounds", "أصوات ويندوز") : app.Name;
}
