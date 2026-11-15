namespace Toolbox.Core.Sound;

/// <summary>An output Windows can play on (headphones, speakers, a monitor's speakers...).</summary>
/// <param name="Id">Windows' endpoint id ("{0.0.0.00000000}.{guid}").</param>
/// <param name="Name">Its name as Windows shows it ("Speakers (Realtek(R) Audio)").</param>
/// <param name="Kind">headphones, speakers, monitor, line, remote or other (from its form factor).</param>
public sealed record SoundDevice(string Id, string Name, string Kind)
{
    /// <summary>The name for people: "Speakers · Realtek(R) Audio".</summary>
    public string Label => SoundNames.TidyDevice(Name);

    /// <summary>The short first part: "Speakers", "Headphones".</summary>
    public string Short => SoundNames.ShortDevice(Name);
}

/// <summary>
/// One app's sound on the main output as Windows reports it (an audio session). An app can have
/// several (Discord's voice and its pings); <see cref="AppKey"/> groups them.
/// </summary>
public sealed record SoundSession(
    string InstanceId,
    string AppKey,
    int ProcessId,
    string Name,
    string? ExePath,
    bool IsSystem,
    bool Active,
    float Volume,
    bool Muted);

/// <summary>What the sound engine sees: the outputs, the main one, its volume and every app on it.</summary>
public sealed record SoundSnapshot(
    IReadOnlyList<SoundDevice> Devices,
    string? DefaultId,
    float Master,
    bool MasterMuted,
    IReadOnlyList<SoundSession> Sessions,
    bool CanSwitchOutput,
    bool Ready = true)
{
    public static readonly SoundSnapshot Empty = new([], null, 0, false, [], false, Ready: false);

    /// <summary>The main output, when there is one.</summary>
    public SoundDevice? Default => Devices.FirstOrDefault(d => d.Id == DefaultId);
}

/// <summary>
/// The sound engine as the windows see it: a snapshot that changes, live levels while someone
/// shows them, and the few things that can be set. Implemented by the Windows engine and by
/// stand-ins in the tests.
/// </summary>
public interface ISoundControl
{
    SoundSnapshot Current { get; }

    /// <summary>Raised after anything in <see cref="Current"/> changed. Not on the UI thread.</summary>
    event Action<SoundSnapshot>? Changed;

    /// <summary>Each app's peak level (0..1, by app key) about 30 times a second, while watched. Not on the UI thread.</summary>
    event Action<IReadOnlyDictionary<string, float>>? Levels;

    /// <summary>Starts the level meters until the returned handle is disposed (they cost nothing otherwise).</summary>
    IDisposable WatchLevels();

    void SetAppVolume(string appKey, float volume);
    void SetAppMute(string appKey, bool muted);
    void SetMaster(float volume);
    void SetMasterMute(bool muted);

    /// <summary>Makes an output the main one (Windows' default for apps and media).</summary>
    void SetOutput(string deviceId);
}

/// <summary>Imp Studio's preferences (the per-app levels it remembers live in their own file).</summary>
public sealed class SoundSettings
{
    /// <summary>How much one press of Louder or Quieter changes the app in front, in percent.</summary>
    public int Step { get; set; } = 5;

    /// <summary>Shows the small level bar near the top of the screen after a hotkey.</summary>
    public bool LevelBar { get; set; } = true;

    /// <summary>Sets an app back to the level it had last time on that output, when it starts a sound.</summary>
    public bool Remember { get; set; } = true;

    /// <summary>The steps offered on the settings page.</summary>
    public static readonly int[] Steps = [2, 5, 10];
}
