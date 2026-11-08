namespace Toolbox.Core.Pulse;

/// <summary>What Claude's tile shows: the most urgent thing any session is doing.</summary>
/// <param name="State">waiting, working, done or idle.</param>
/// <param name="Waiting">Sessions waiting for an answer.</param>
/// <param name="Focus">The session to bring forward on a click (the one waiting, working or just done).</param>
public sealed record ClaudeStatus(string State, int Waiting, int Working, ClaudeSession? Focus)
{
    public static readonly ClaudeStatus Idle = new(ClaudeActivity.Idle, 0, 0, null);

    /// <summary>"Done" shows until the user opens Claude (<paramref name="seenAt"/>), for at most two hours.</summary>
    public static ClaudeStatus Of(ClaudeState? state, DateTimeOffset now, DateTimeOffset seenAt)
    {
        if (state is null || state.Sessions.Count == 0) return Idle;
        var sessions = state.Sessions.Values.Select(s => (Session: s, State: ClaudeState.StateAt(s, now))).ToList();
        var waiting = sessions.Where(s => s.State == ClaudeActivity.Waiting).OrderByDescending(s => s.Session.Updated).ToList();
        var working = sessions.Where(s => s.State == ClaudeActivity.Working).OrderByDescending(s => s.Session.Updated).ToList();
        if (waiting.Count > 0) return new ClaudeStatus(ClaudeActivity.Waiting, waiting.Count, working.Count, waiting[0].Session);
        if (working.Count > 0) return new ClaudeStatus(ClaudeActivity.Working, 0, working.Count, working[0].Session);
        var done = sessions
            .Where(s => s.State == ClaudeActivity.Done && s.Session.Finished is { } f && f > seenAt && now - f < TimeSpan.FromHours(2))
            .OrderByDescending(s => s.Session.Finished)
            .FirstOrDefault();
        return done.Session is not null ? new ClaudeStatus(ClaudeActivity.Done, 0, 0, done.Session) : Idle;
    }
}

/// <summary>A note Pulse shows: a limit getting close or reached, or a long Claude task finished.</summary>
public sealed record PulseAlert(string Kind, string Service, string Window, double Used, DateTimeOffset? ResetsAt, ClaudeSession? Session = null, TimeSpan? Took = null)
{
    public const string Warn = "warn";
    public const string Limit = "limit";
    public const string Finished = "finished";
}

/// <summary>
/// Decides when to show a note: each limit window warns once at the chosen level and once when it
/// is used up (again only after it resets), and a Claude task that took over a minute says when it
/// finished. Things already true when Pulse starts do not raise a note for a finished task.
/// </summary>
public sealed class PulseAlerts
{
    public static readonly TimeSpan LongTask = TimeSpan.FromMinutes(1);

    private readonly HashSet<string> _shown = [];
    private readonly Dictionary<string, DateTimeOffset> _finishedSeen = [];
    private bool _primed;

    public IReadOnlyList<PulseAlert> Check(ClaudeState? claude, UsageSnapshot? codex, int warnAt, bool notifyFinished, DateTimeOffset now)
    {
        var alerts = new List<PulseAlert>();
        if (warnAt > 0)
        {
            Limits(alerts, "claude", claude?.Limits, warnAt, now);
            Limits(alerts, "codex", codex, warnAt, now);
        }

        if (claude is not null)
        {
            foreach (var (id, s) in claude.Sessions)
            {
                if (s.State != ClaudeActivity.Done || s.Finished is not { } finished) continue;
                bool isNew = !_finishedSeen.TryGetValue(id, out var seen) || seen < finished;
                _finishedSeen[id] = finished;
                if (!isNew || !_primed || !notifyFinished || now - finished > TimeSpan.FromMinutes(5)) continue;
                var took = s.Started is { } started ? finished - started : (TimeSpan?)null;
                if (took is { } t && t >= LongTask)
                    alerts.Add(new PulseAlert(PulseAlert.Finished, "claude", "", 0, null, s, t));
            }
        }
        _primed = true;
        return alerts;
    }

    private void Limits(List<PulseAlert> alerts, string service, UsageSnapshot? snapshot, int warnAt, DateTimeOffset now)
    {
        if (snapshot is null) return;
        foreach (var (name, window) in new[] { ("session", snapshot.Session), ("week", snapshot.Week) })
        {
            if (window is null) continue;
            double used = window.UsedAt(now);
            string key = $"{service}|{name}|{window.ResetsAt?.ToUnixTimeSeconds()}";
            if (used >= 100)
            {
                if (_shown.Add(key + "|limit")) alerts.Add(new PulseAlert(PulseAlert.Limit, service, name, used, window.ResetsAt));
                _shown.Add(key + "|warn");
            }
            else if (used >= warnAt && _shown.Add(key + "|warn"))
            {
                alerts.Add(new PulseAlert(PulseAlert.Warn, service, name, used, window.ResetsAt));
            }
        }
    }
}
