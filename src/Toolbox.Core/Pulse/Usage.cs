namespace Toolbox.Core.Pulse;

/// <summary>One limit window: the share used and when it starts over.</summary>
public sealed record UsageWindow(double UsedPercent, DateTimeOffset? ResetsAt, int? WindowMinutes = null)
{
    /// <summary>The share used now: once the window has reset, nothing of it is used.</summary>
    public double UsedAt(DateTimeOffset now) => ResetsAt is { } r && r <= now ? 0 : Math.Clamp(UsedPercent, 0, 100);
}

/// <summary>The limits a service reported last: the short window (a session or five hours) and the week.</summary>
public sealed record UsageSnapshot(UsageWindow? Session, UsageWindow? Week, string? Plan, DateTimeOffset ReadAt);

public enum UsageLevel { Ok, Warn, Hot }

public static class Usage
{
    public static UsageLevel LevelOf(double usedPercent) =>
        usedPercent >= 85 ? UsageLevel.Hot : usedPercent >= 70 ? UsageLevel.Warn : UsageLevel.Ok;

    /// <summary>Seconds since the Unix epoch, as a time (null for missing or nonsense values).</summary>
    public static DateTimeOffset? FromUnix(double? seconds) =>
        seconds is > 946684800 and < 4102444800 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds.Value * 1000)) : null;
}
