using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toolbox.Core.Pulse;

/// <summary>What Claude Code is doing in one session.</summary>
public static class ClaudeActivity
{
    public const string Idle = "idle";
    public const string Working = "working";
    public const string Waiting = "waiting";
    public const string Done = "done";
}

/// <summary>One Claude Code session, as its hooks last reported it.</summary>
public sealed class ClaudeSession
{
    public string State { get; set; } = ClaudeActivity.Idle;
    public string? Cwd { get; set; }
    public DateTimeOffset Updated { get; set; }

    /// <summary>When the current task started (the prompt was sent).</summary>
    public DateTimeOffset? Started { get; set; }

    /// <summary>When the last task finished.</summary>
    public DateTimeOffset? Finished { get; set; }

    /// <summary>The window the session runs in (its terminal or app), to bring it forward.</summary>
    public long Window { get; set; }

    /// <summary>The project folder's name, for display.</summary>
    [JsonIgnore]
    public string Project => string.IsNullOrEmpty(Cwd) ? "" : Path.GetFileName(Cwd.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Cwd;
}

/// <summary>
/// Claude's limits and sessions as Claude Code's status line and hooks last reported them. They are
/// written by "AmirLap.exe --pulse-status / --pulse-event" (run by Claude Code itself) into a file in
/// Amir Lap's data folder, and read by the dock. Nothing leaves this PC.
/// </summary>
public sealed class ClaudeState
{
    public UsageWindow? FiveHour { get; set; }
    public UsageWindow? SevenDay { get; set; }

    /// <summary>When the limits were last reported.</summary>
    public DateTimeOffset? LimitsUpdated { get; set; }

    public Dictionary<string, ClaudeSession> Sessions { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Sessions not heard from for this long are forgotten.</summary>
    public static readonly TimeSpan Forget = TimeSpan.FromHours(12);

    /// <summary>A session still "working" after this long without a word most likely ended without saying so.</summary>
    public static readonly TimeSpan StaleWork = TimeSpan.FromMinutes(20);

    /// <summary>The limits as a snapshot (null until the status line has reported them).</summary>
    [JsonIgnore]
    public UsageSnapshot? Limits => FiveHour is null && SevenDay is null ? null : new UsageSnapshot(FiveHour, SevenDay, null, LimitsUpdated ?? DateTimeOffset.MinValue);

    /// <summary>Takes the limits from the status line's input (claude.ai Pro and Max report them).</summary>
    public void ApplyStatusLine(JsonElement input, DateTimeOffset now)
    {
        if (input.ValueKind != JsonValueKind.Object) return;
        if (input.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            var five = Window(limits, "five_hour", 300);
            var week = Window(limits, "seven_day", 10080);
            if (five is not null || week is not null)
            {
                FiveHour = five ?? (FiveHour?.ResetsAt > now ? FiveHour : null);
                SevenDay = week ?? (SevenDay?.ResetsAt > now ? SevenDay : null);
                LimitsUpdated = now;
            }
        }
        if (Id(input) is { } id)
        {
            var session = Session(id, now);
            if (input.TryGetProperty("workspace", out var ws) && ws.ValueKind == JsonValueKind.Object && Str(ws, "current_dir") is { } dir) session.Cwd = dir;
            else if (Str(input, "cwd") is { } cwd) session.Cwd = cwd;
        }
        Prune(now);
    }

    /// <summary>Follows a hook event: a prompt or tool use means working, a permission prompt means waiting, Stop means done.</summary>
    public void ApplyHook(JsonElement input, DateTimeOffset now, long window = 0)
    {
        if (input.ValueKind != JsonValueKind.Object || Id(input) is not { } id) return;
        var name = Str(input, "hook_event_name") ?? "";
        if (name == "SessionEnd")
        {
            Sessions.Remove(id);
            return;
        }
        var s = Session(id, now);
        if (Str(input, "cwd") is { } cwd) s.Cwd = cwd;
        if (window != 0) s.Window = window;
        switch (name)
        {
            case "UserPromptSubmit":
                s.State = ClaudeActivity.Working;
                s.Started = now;
                break;
            case "PreToolUse" or "PostToolUse" or "PostToolUseFailure" or "SubagentStart":
                if (s.State != ClaudeActivity.Working) s.Started ??= now;
                s.State = ClaudeActivity.Working;
                break;
            case "PermissionRequest":
                s.State = ClaudeActivity.Waiting;
                break;
            case "Notification":
                var type = Str(input, "notification_type");
                if (type == "permission_prompt" || (type is null && (Str(input, "message") ?? "").Contains("permission", StringComparison.OrdinalIgnoreCase)))
                    s.State = ClaudeActivity.Waiting;
                break;
            case "Stop" or "StopFailure":
                s.State = ClaudeActivity.Done;
                s.Finished = now;
                break;
            case "SessionStart":
                if (s.State != ClaudeActivity.Working) s.State = ClaudeActivity.Idle;
                break;
        }
        Prune(now);
    }

    /// <summary>Forgets sessions not heard from in <see cref="Forget"/>.</summary>
    public void Prune(DateTimeOffset now)
    {
        foreach (var key in Sessions.Where(p => now - p.Value.Updated > Forget).Select(p => p.Key).ToList())
            Sessions.Remove(key);
    }

    /// <summary>A session's state now: work with no news for <see cref="StaleWork"/> counts as idle.</summary>
    public static string StateAt(ClaudeSession s, DateTimeOffset now) =>
        s.State == ClaudeActivity.Working && now - s.Updated > StaleWork ? ClaudeActivity.Idle : s.State;

    private ClaudeSession Session(string id, DateTimeOffset now)
    {
        if (!Sessions.TryGetValue(id, out var s)) Sessions[id] = s = new ClaudeSession();
        s.Updated = now;
        return s;
    }

    private static UsageWindow? Window(JsonElement limits, string name, int minutes)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number) return null;
        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number ? Usage.FromUnix(r.GetDouble())
            : w.TryGetProperty("resets_at", out var rs) && rs.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(rs.GetString(), out var parsed) ? parsed
            : null;
        return new UsageWindow(used.GetDouble(), resets, minutes);
    }

    private static string? Id(JsonElement input) => Str(input, "session_id") is { Length: > 0 } id ? id : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Reads and updates the shared state file. Claude Code may run several hooks at once, so updates take a lock.</summary>
public static class ClaudeStateStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public static string DefaultPath => Path.Combine(AppPaths.DataDir, "pulse", "claude-state.json");

    public static ClaudeState Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<ClaudeState>(stream, Json) ?? new ClaudeState();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return new ClaudeState();
    }

    /// <summary>Loads, changes and saves the state under a lock shared by every Amir Lap process.</summary>
    public static ClaudeState Update(string path, Action<ClaudeState> change)
    {
        using var mutex = new Mutex(false, "Local\\AmirLap.PulseState." + StableHash(Path.GetFullPath(path).ToUpperInvariant()));
        bool owned = false;
        try
        {
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
            catch (AbandonedMutexException) { owned = true; }
            var state = Load(path);
            change(state);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
            File.Move(tmp, path, overwrite: true);
            return state;
        }
        finally
        {
            if (owned) mutex.ReleaseMutex();
        }
    }

    /// <summary>The same number in every process (string hash codes differ from process to process).</summary>
    private static string StableHash(string text)
    {
        uint h = 2166136261;
        foreach (char c in text) { h ^= c; h *= 16777619; }
        return h.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }
}
