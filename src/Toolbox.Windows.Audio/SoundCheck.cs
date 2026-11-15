using System.Globalization;
using System.Text;
using Toolbox.Core.Sound;

namespace Toolbox.Windows.Audio;

/// <summary>
/// A plain-text report of what Windows' sound system shows on this PC: the outputs, the main one
/// and its volume, every app's sound with its level, and which of Imp Studio's features this
/// Windows allows. For the CLI (<c>ftb sound</c>) and the settings page's "Copy a sound check".
/// Only reads; it changes nothing.
/// </summary>
public static class SoundCheck
{
    /// <summary>Process loopback (one app's sound, for Longshot later) needs Windows 10 build 20348 or newer.</summary>
    public const int ProcessLoopbackBuild = 20348;

    public static string Run(SoundEngine? running = null)
    {
        var engine = running ?? new SoundEngine();
        try
        {
            if (running is null && !engine.WaitReady(TimeSpan.FromSeconds(5))) return "Windows' sound system did not answer within 5 seconds.";
            var levels = engine.SampleLevels(TimeSpan.FromSeconds(2));
            return Format(engine.Current, levels, Environment.OSVersion.Version);
        }
        finally
        {
            if (running is null) engine.Dispose();
        }
    }

    public static string Format(SoundSnapshot snap, IReadOnlyDictionary<string, float> levels, Version windows)
    {
        var ci = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("Imp Studio sound check");
        sb.AppendLine($"Windows {windows.Major}.{windows.Minor} build {windows.Build}");
        sb.AppendLine();
        sb.AppendLine($"Outputs ({snap.Devices.Count}):");
        foreach (var d in snap.Devices)
            sb.AppendLine($"  {(d.Id == snap.DefaultId ? "*" : " ")} {d.Label} [{d.Kind}]");
        if (snap.Devices.Count == 0) sb.AppendLine("    none (no output is plugged in or switched on)");
        if (snap.Default is { } main)
            sb.AppendLine(string.Create(ci, $"Main output: {main.Label}, {SoundApps.Percent(snap.Master)}%{(snap.MasterMuted ? ", muted" : "")}"));
        sb.AppendLine();
        var apps = SoundApps.Group(snap.Sessions);
        sb.AppendLine($"Apps on the main output ({apps.Count}):");
        foreach (var a in apps)
        {
            var name = a.IsSystem ? "Windows sounds" : a.Name;
            var level = levels.TryGetValue(a.Key, out var peak) ? peak : 0;
            sb.AppendLine(string.Create(ci,
                $"  {name,-28} {SoundApps.Percent(a.Volume),3}%{(a.Muted ? " muted" : "")}  {(a.Playing ? "playing" : "quiet  ")}  level {level:0.00}  ({a.Key}{(a.Sessions > 1 ? $", {a.Sessions} sounds" : "")})"));
        }
        if (apps.Count == 0) sb.AppendLine("    none yet (an app shows here once it has made a sound)");
        sb.AppendLine();
        sb.AppendLine("What this Windows allows:");
        sb.AppendLine($"  Switching the main output: {(snap.CanSwitchOutput ? "yes" : "no (the output picker is hidden)")}");
        sb.AppendLine($"  Recording one app's sound (Longshot, later): {(windows.Build >= ProcessLoopbackBuild ? "yes" : $"no (needs build {ProcessLoopbackBuild})")}");
        return sb.ToString();
    }
}
