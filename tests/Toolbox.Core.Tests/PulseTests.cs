using System.Text.Json;
using System.Text.Json.Nodes;
using Toolbox.Core.Pulse;
using Toolbox.Core.Settings;

namespace Toolbox.Core.Tests;

public sealed class PulseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amirlap-pulse-" + Guid.NewGuid().ToString("N"));

    public PulseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 8, 18, 0, 0, TimeSpan.Zero);

    // A line in the format Codex writes into its session logs.
    private static string CodexLine(double primary, double secondary, long primaryReset = 1794163899) =>
        "{\"timestamp\":\"2026-11-08T17:50:00.000Z\",\"ordinal\":12,\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":{\"limit_id\":\"codex\",\"limit_name\":null," +
        $"\"primary\":{{\"used_percent\":{primary},\"window_minutes\":300,\"resets_at\":{primaryReset}}}," +
        $"\"secondary\":{{\"used_percent\":{secondary},\"window_minutes\":10080,\"resets_at\":1794448794}}," +
        "\"credits\":{\"has_credits\":false,\"unlimited\":false,\"balance\":\"0\"},\"plan_type\":\"plus\",\"rate_limit_reached_type\":null}}}";

    // ---- Codex ---------------------------------------------------------------------------------

    [Fact]
    public void Codex_limits_are_read_from_a_session_log_line()
    {
        var snap = CodexUsageReader.ParseLine(CodexLine(24, 4), Now);
        Assert.NotNull(snap);
        Assert.Equal(24, snap!.Session!.UsedPercent);
        Assert.Equal(300, snap.Session.WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794163899), snap.Session.ResetsAt);
        Assert.Equal(4, snap.Week!.UsedPercent);
        Assert.Equal("plus", snap.Plan);
        Assert.Equal(new DateTimeOffset(2026, 11, 8, 17, 50, 0, TimeSpan.Zero), snap.ReadAt);
    }

    [Fact]
    public void Lines_without_limits_are_ignored()
    {
        Assert.Null(CodexUsageReader.ParseLine("{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\"}}", Now));
        Assert.Null(CodexUsageReader.ParseLine("not json", Now));
    }

    [Fact]
    public void The_newest_session_log_gives_the_latest_limits()
    {
        var day1 = Directory.CreateDirectory(Path.Combine(_dir, "sessions", "2026", "11", "07")).FullName;
        var day2 = Directory.CreateDirectory(Path.Combine(_dir, "sessions", "2026", "11", "08")).FullName;
        var old = Path.Combine(day1, "rollout-a.jsonl");
        File.WriteAllLines(old, [CodexLine(90, 50)]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-20));
        var current = Path.Combine(day2, "rollout-b.jsonl");
        File.WriteAllLines(current, [CodexLine(10, 2), "{\"type\":\"response_item\"}", CodexLine(30, 5), "{\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\"}}"]);

        Assert.Equal(current, CodexUsageReader.LatestSessionFile(_dir));
        var snap = CodexUsageReader.ReadLatest(_dir);
        Assert.Equal(30, snap!.Session!.UsedPercent);
        Assert.Equal(5, snap.Week!.UsedPercent);
    }

    [Fact]
    public void No_codex_folder_means_no_numbers() =>
        Assert.Null(CodexUsageReader.ReadLatest(Path.Combine(_dir, "missing")));

    [Fact]
    public void A_window_that_has_reset_counts_as_unused()
    {
        var w = new UsageWindow(80, Now.AddMinutes(-1));
        Assert.Equal(0, w.UsedAt(Now));
        Assert.Equal(80, new UsageWindow(80, Now.AddMinutes(5)).UsedAt(Now));
        Assert.Equal(UsageLevel.Ok, Usage.LevelOf(69));
        Assert.Equal(UsageLevel.Warn, Usage.LevelOf(70));
        Assert.Equal(UsageLevel.Hot, Usage.LevelOf(85));
    }

    // ---- Claude Code state -----------------------------------------------------------------------

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void The_status_line_reports_claudes_limits()
    {
        var state = new ClaudeState();
        state.ApplyStatusLine(Json("{\"session_id\":\"s1\",\"workspace\":{\"current_dir\":\"D:\\\\work\\\\Amir_Lab\"},\"rate_limits\":{\"five_hour\":{\"used_percentage\":64,\"resets_at\":1794163899},\"seven_day\":{\"used_percentage\":31.5,\"resets_at\":1794448794}}}"), Now);
        Assert.Equal(64, state.FiveHour!.UsedPercent);
        Assert.Equal(31.5, state.SevenDay!.UsedPercent);
        Assert.Equal(Now, state.LimitsUpdated);
        Assert.Equal("Amir_Lab", state.Sessions["s1"].Project);
        Assert.Equal("5h 64% · week 32%", PulseBridge.StatusText(state, Now));

        // A later report without limits (an API key, or before the first answer) keeps the last ones.
        state.ApplyStatusLine(Json("{\"session_id\":\"s1\"}"), Now.AddMinutes(1));
        Assert.Equal(64, state.FiveHour!.UsedPercent);
    }

    [Fact]
    public void Hooks_follow_what_a_session_is_doing()
    {
        var state = new ClaudeState();
        state.ApplyHook(Json("{\"session_id\":\"s1\",\"cwd\":\"D:\\\\work\\\\app\",\"hook_event_name\":\"UserPromptSubmit\"}"), Now, window: 42);
        Assert.Equal(ClaudeActivity.Working, state.Sessions["s1"].State);
        Assert.Equal(Now, state.Sessions["s1"].Started);
        Assert.Equal(42, state.Sessions["s1"].Window);

        state.ApplyHook(Json("{\"session_id\":\"s1\",\"hook_event_name\":\"Notification\",\"notification_type\":\"permission_prompt\",\"message\":\"Claude needs your permission to use Bash\"}"), Now.AddSeconds(30));
        Assert.Equal(ClaudeActivity.Waiting, state.Sessions["s1"].State);

        state.ApplyHook(Json("{\"session_id\":\"s1\",\"hook_event_name\":\"PostToolUse\",\"tool_name\":\"Bash\"}"), Now.AddSeconds(40));
        Assert.Equal(ClaudeActivity.Working, state.Sessions["s1"].State);

        // An idle reminder is not a question.
        state.ApplyHook(Json("{\"session_id\":\"s1\",\"hook_event_name\":\"Notification\",\"notification_type\":\"idle_prompt\"}"), Now.AddSeconds(45));
        Assert.Equal(ClaudeActivity.Working, state.Sessions["s1"].State);

        state.ApplyHook(Json("{\"session_id\":\"s1\",\"hook_event_name\":\"Stop\"}"), Now.AddMinutes(3));
        Assert.Equal(ClaudeActivity.Done, state.Sessions["s1"].State);
        Assert.Equal(Now.AddMinutes(3), state.Sessions["s1"].Finished);

        state.ApplyHook(Json("{\"session_id\":\"s1\",\"hook_event_name\":\"SessionEnd\"}"), Now.AddMinutes(4));
        Assert.Empty(state.Sessions);
    }

    [Fact]
    public void Claudes_tile_shows_the_most_urgent_session()
    {
        var state = new ClaudeState();
        state.Sessions["a"] = new ClaudeSession { State = ClaudeActivity.Working, Updated = Now };
        state.Sessions["b"] = new ClaudeSession { State = ClaudeActivity.Waiting, Updated = Now, Cwd = "D:\\b" };
        state.Sessions["c"] = new ClaudeSession { State = ClaudeActivity.Waiting, Updated = Now.AddSeconds(-5) };
        var status = ClaudeStatus.Of(state, Now, Now.AddHours(-1));
        Assert.Equal(ClaudeActivity.Waiting, status.State);
        Assert.Equal(2, status.Waiting);
        Assert.Equal("b", status.Focus!.Project);

        // Work with no news for a long time most likely ended without saying so.
        var stale = new ClaudeState();
        stale.Sessions["a"] = new ClaudeSession { State = ClaudeActivity.Working, Updated = Now.AddMinutes(-30) };
        Assert.Equal(ClaudeActivity.Idle, ClaudeStatus.Of(stale, Now, Now.AddHours(-1)).State);

        // "Done" shows until Claude is opened from the dock.
        var done = new ClaudeState();
        done.Sessions["a"] = new ClaudeSession { State = ClaudeActivity.Done, Updated = Now, Finished = Now.AddMinutes(-2) };
        Assert.Equal(ClaudeActivity.Done, ClaudeStatus.Of(done, Now, Now.AddHours(-1)).State);
        Assert.Equal(ClaudeActivity.Idle, ClaudeStatus.Of(done, Now, Now.AddMinutes(-1)).State);
    }

    [Fact]
    public void The_state_file_is_shared_safely()
    {
        var path = Path.Combine(_dir, "pulse", "claude-state.json");
        Parallel.For(0, 12, i =>
        {
            var json = Json($"{{\"session_id\":\"s{i}\",\"hook_event_name\":\"UserPromptSubmit\"}}");
            ClaudeStateStore.Update(path, s => s.ApplyHook(json, DateTimeOffset.UtcNow));
        });
        Assert.Equal(12, ClaudeStateStore.Load(path).Sessions.Count);
    }

    [Fact]
    public void The_bridge_updates_the_state_and_prints_a_status_line()
    {
        var path = Path.Combine(_dir, "pulse", "claude-state.json");
        var output = new StringWriter();
        var input = "{\"session_id\":\"s1\",\"rate_limits\":{\"five_hour\":{\"used_percentage\":12,\"resets_at\":4000000000}}}";
        PulseBridge.StatusLine(new StringReader(input), output, path, previousCommand: null, runPrevious: null);
        Assert.Equal("5h 12%", output.ToString());
        Assert.Equal(12, ClaudeStateStore.Load(path).FiveHour!.UsedPercent);

        // With a status line from before Pulse, its text is shown instead (with the same input).
        string? seen = null;
        var output2 = new StringWriter();
        PulseBridge.StatusLine(new StringReader(input), output2, path, "my-line", (cmd, json) => { seen = cmd + "|" + json; return "mine"; });
        Assert.Equal("mine", output2.ToString());
        Assert.Equal("my-line|" + input, seen);

        PulseBridge.Event(new StringReader("{\"session_id\":\"s2\",\"hook_event_name\":\"UserPromptSubmit\"}"), path, () => 7);
        Assert.Equal(7, ClaudeStateStore.Load(path).Sessions["s2"].Window);

        // Rubbish in never fails Claude Code.
        Assert.Equal(0, PulseBridge.Event(new StringReader("nope"), path, () => 0));
    }

    // ---- alerts ----------------------------------------------------------------------------------

    [Fact]
    public void Each_limit_warns_once_and_again_after_it_resets()
    {
        var alerts = new PulseAlerts();
        var reset = Now.AddHours(1);
        UsageSnapshot Codex(double used, DateTimeOffset r) => new(new UsageWindow(used, r, 300), null, "plus", Now);

        Assert.Empty(alerts.Check(null, Codex(50, reset), 80, true, Now));
        var warn = Assert.Single(alerts.Check(null, Codex(82, reset), 80, true, Now));
        Assert.Equal(PulseAlert.Warn, warn.Kind);
        Assert.Equal("codex", warn.Service);
        Assert.Empty(alerts.Check(null, Codex(90, reset), 80, true, Now));
        Assert.Equal(PulseAlert.Limit, Assert.Single(alerts.Check(null, Codex(100, reset), 80, true, Now)).Kind);
        Assert.Empty(alerts.Check(null, Codex(100, reset), 80, true, Now));

        // A new window (new reset time) can warn again; 0 turns warnings off.
        Assert.Single(alerts.Check(null, Codex(85, reset.AddHours(5)), 80, true, Now));
        Assert.Empty(new PulseAlerts().Check(null, Codex(99, reset), 0, true, Now));
    }

    [Fact]
    public void A_long_task_says_when_it_finished_but_not_one_from_before_pulse_started()
    {
        var alerts = new PulseAlerts();
        var state = new ClaudeState();
        state.Sessions["old"] = new ClaudeSession { State = ClaudeActivity.Done, Started = Now.AddMinutes(-10), Finished = Now.AddMinutes(-1), Updated = Now };
        Assert.Empty(alerts.Check(state, null, 80, true, Now)); // already done when Pulse started

        state.Sessions["new"] = new ClaudeSession { State = ClaudeActivity.Done, Started = Now.AddMinutes(-3), Finished = Now, Updated = Now, Cwd = "D:\\app" };
        state.Sessions["quick"] = new ClaudeSession { State = ClaudeActivity.Done, Started = Now.AddSeconds(-20), Finished = Now, Updated = Now };
        var finished = Assert.Single(alerts.Check(state, null, 80, true, Now));
        Assert.Equal(PulseAlert.Finished, finished.Kind);
        Assert.Equal("app", finished.Session!.Project);
        Assert.Equal(TimeSpan.FromMinutes(3), finished.Took);
        Assert.Empty(alerts.Check(state, null, 80, true, Now));
    }

    // ---- connecting Claude Code --------------------------------------------------------------------

    [Fact]
    public void Connecting_keeps_everything_else_and_disconnecting_puts_it_back()
    {
        var settings = Path.Combine(_dir, ".claude", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, """
            {
              "model": "opus",
              "statusLine": { "type": "command", "command": "~/.claude/my-status.sh" },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "say done" } ] } ] }
            }
            """);
        var pulseData = Path.Combine(_dir, "data", "pulse");
        var connection = new ClaudeCodeConnection(settings, @"C:\Users\You\AppData\Local\Programs\AmirLap\AmirLap.exe", pulseData);
        Assert.False(connection.IsLinked);

        connection.Link();
        connection.Link(); // twice: still one of each
        Assert.True(connection.IsLinked);
        Assert.False(connection.NeedsRefresh);
        var root = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        Assert.Equal("opus", (string?)root["model"]);
        Assert.Equal("\"C:/Users/You/AppData/Local/Programs/AmirLap/AmirLap.exe\" --pulse-status", (string?)root["statusLine"]!["command"]);
        foreach (var name in ClaudeCodeConnection.Events)
            Assert.Single(root["hooks"]![name]!.AsArray().SelectMany(g => g!["hooks"]!.AsArray()), h => ((string?)h!["command"])!.Contains("--pulse-event"));
        Assert.Contains(root["hooks"]!["Stop"]!.AsArray().SelectMany(g => g!["hooks"]!.AsArray()), h => (string?)h!["command"] == "say done");
        Assert.Equal("~/.claude/my-status.sh", connection.PreviousStatusCommand);
        Assert.True(File.Exists(connection.BackupFile));

        // Another copy of Amir Lap sees the connection points elsewhere.
        Assert.True(new ClaudeCodeConnection(settings, @"D:\Other\AmirLap.exe", pulseData).NeedsRefresh);

        connection.Unlink();
        Assert.False(connection.IsLinked);
        root = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        Assert.Equal("~/.claude/my-status.sh", (string?)root["statusLine"]!["command"]);
        Assert.Equal("say done", (string?)root["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"]);
        Assert.Single(root["hooks"]!.AsObject()); // only the user's own hook is left
        Assert.Equal("opus", (string?)root["model"]);
    }

    [Fact]
    public void Connecting_creates_the_settings_when_there_are_none()
    {
        var settings = Path.Combine(_dir, "fresh", "settings.json");
        var connection = new ClaudeCodeConnection(settings, @"C:\AmirLap\AmirLap.exe", Path.Combine(_dir, "data2"));
        connection.Link();
        Assert.True(connection.IsLinked);
        Assert.Null(connection.PreviousStatusCommand);
        connection.Unlink();
        var root = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        Assert.Null(root["statusLine"]);
        Assert.Null(root["hooks"]);
    }

    [Fact]
    public void A_broken_settings_file_is_left_alone()
    {
        var settings = Path.Combine(_dir, "broken", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, "{ \"model\": ");
        var connection = new ClaudeCodeConnection(settings, @"C:\AmirLap\AmirLap.exe", Path.Combine(_dir, "data3"));
        Assert.Throws<InvalidOperationException>(connection.Link);
        Assert.Equal("{ \"model\": ", File.ReadAllText(settings));
    }

    // ---- settings ----------------------------------------------------------------------------------

    [Fact]
    public void Pulse_settings_have_sensible_defaults_and_survive_a_round_trip()
    {
        var s = new AppSettings();
        Assert.Equal("right", s.Pulse.Edge);
        Assert.Equal(["claude", "codex", "chatgpt", "longshot", "toolbox"], s.Pulse.ActiveTiles);
        s.Pulse.CurrentSet = "study";
        s.Pulse.Active.Tiles = ["note", "nonsense", "note", "claude"];
        Assert.Equal(["note", "claude"], s.Pulse.ActiveTiles);

        var path = Path.Combine(_dir, "settings.json");
        new SettingsStore(path).Save(s);
        var back = new SettingsStore(path).Current;
        Assert.Equal("study", back.Pulse.CurrentSet);
        Assert.Equal(2, back.Pulse.Sets.Count);
        Assert.Equal(["note", "claude"], back.Pulse.ActiveTiles);

        Assert.Equal(["b", "a", "c"], PulseTiles.Move(["a", "b", "c"], "b", -1));
        Assert.Equal(["a", "b", "c"], PulseTiles.Move(["a", "b", "c"], "a", -1));
        Assert.Equal("S", PulseSettings.SizeNearest(0.75));
        Assert.Equal("M", PulseSettings.SizeNearest(1.05));
        Assert.Equal("L", PulseSettings.SizeNearest(1.3));
    }
}
