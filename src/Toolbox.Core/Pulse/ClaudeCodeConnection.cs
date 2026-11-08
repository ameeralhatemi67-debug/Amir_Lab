using System.Text.Json;
using System.Text.Json.Nodes;

namespace Toolbox.Core.Pulse;

/// <summary>
/// Connects Pulse to Claude Code on this PC, only when the user asks: adds a status line and a few
/// hooks to Claude Code's own settings (~/.claude/settings.json) that run Amir Lap, which writes what
/// they report to a file in Amir Lap's data folder. Everything else in the settings is kept as it
/// was; an existing status line keeps working (Amir Lap runs it and shows its text) and comes back
/// when the connection is removed. A copy of the settings from before the first connection is kept.
/// </summary>
public sealed class ClaudeCodeConnection
{
    /// <summary>The hook events Pulse listens to (all long-standing ones).</summary>
    public static readonly IReadOnlyList<string> Events = ["UserPromptSubmit", "PostToolUse", "Notification", "Stop", "SessionEnd"];

    public const string StatusArg = "--pulse-status";
    public const string EventArg = "--pulse-event";

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public ClaudeCodeConnection(string settingsPath, string exePath, string pulseDataDir)
    {
        SettingsPath = settingsPath;
        ExePath = exePath;
        DataDir = pulseDataDir;
    }

    public string SettingsPath { get; }
    public string ExePath { get; }
    public string DataDir { get; }

    /// <summary>Claude Code's user settings: CLAUDE_CONFIG_DIR when set, otherwise .claude in the user's profile.</summary>
    public static string DefaultSettingsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "settings.json");

    public static string DefaultDataDir => Path.Combine(AppPaths.DataDir, "pulse");

    /// <summary>True when Claude Code has been used on this PC (its settings folder exists).</summary>
    public bool ClaudeCodeFound => Directory.Exists(Path.GetDirectoryName(SettingsPath));

    public string PreviousStatusLineFile => Path.Combine(DataDir, "previous-statusline.json");
    public string BackupFile => Path.Combine(DataDir, "claude-settings.before-pulse.json");

    /// <summary>Forward slashes and quotes work the same in Git Bash, cmd and PowerShell, which Claude Code may use.</summary>
    public string Command(string arg) => "\"" + ExePath.Replace('\\', '/') + "\" " + arg;

    /// <summary>True when the status line is Amir Lap's.</summary>
    public bool IsLinked => StatusCommandOf(Load()) is { } c && c.Contains(StatusArg, StringComparison.Ordinal);

    /// <summary>True when the connection points at another copy of Amir Lap (it was moved or reinstalled elsewhere).</summary>
    public bool NeedsRefresh => StatusCommandOf(Load()) is { } c && c.Contains(StatusArg, StringComparison.Ordinal) && c != Command(StatusArg);

    /// <summary>The status line that was there before Pulse, if any (Amir Lap runs it and shows its text).</summary>
    public string? PreviousStatusCommand
    {
        get
        {
            try
            {
                if (!File.Exists(PreviousStatusLineFile)) return null;
                return JsonNode.Parse(File.ReadAllText(PreviousStatusLineFile), documentOptions: ReadOptions)?["command"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Adds (or refreshes) the status line and hooks. Safe to call again.</summary>
    public void Link()
    {
        var root = Load() ?? new JsonObject();
        Directory.CreateDirectory(DataDir);
        if (File.Exists(SettingsPath) && !File.Exists(BackupFile)) File.Copy(SettingsPath, BackupFile);

        if (root["statusLine"] is JsonObject existing && !(StatusCommandOf(root) ?? "").Contains(StatusArg, StringComparison.Ordinal))
            File.WriteAllText(PreviousStatusLineFile, existing.ToJsonString(WriteOptions));
        root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = Command(StatusArg), ["padding"] = 0 };

        RemoveOurHooks(root);
        if (root["hooks"] is not JsonObject hooks) root["hooks"] = hooks = new JsonObject();
        foreach (var name in Events)
        {
            if (hooks[name] is not JsonArray groups) hooks[name] = groups = new JsonArray();
            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command(EventArg), ["timeout"] = 10 }),
            });
        }
        Save(root);
    }

    /// <summary>Removes Pulse's status line and hooks and puts back the status line that was there before.</summary>
    public void Unlink()
    {
        var root = Load();
        if (root is null) return;
        RemoveOurHooks(root);
        if ((StatusCommandOf(root) ?? "").Contains(StatusArg, StringComparison.Ordinal))
        {
            root.Remove("statusLine");
            try
            {
                if (File.Exists(PreviousStatusLineFile))
                {
                    if (JsonNode.Parse(File.ReadAllText(PreviousStatusLineFile), documentOptions: ReadOptions) is JsonObject previous) root["statusLine"] = previous;
                    File.Delete(PreviousStatusLineFile);
                }
            }
            catch (JsonException)
            {
            }
        }
        Save(root);
    }

    private static void RemoveOurHooks(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) return;
        foreach (var name in hooks.Select(p => p.Key).ToList())
        {
            if (hooks[name] is not JsonArray groups) continue;
            foreach (var group in groups.OfType<JsonObject>().ToList())
            {
                if (group["hooks"] is not JsonArray list) continue;
                foreach (var hook in list.OfType<JsonObject>().Where(IsOurs).ToList()) list.Remove(hook);
                if (list.Count == 0) groups.Remove(group);
            }
            if (groups.Count == 0) hooks.Remove(name);
        }
        if (hooks.Count == 0) root.Remove("hooks");
    }

    private static bool IsOurs(JsonObject hook) =>
        hook["command"] is JsonValue v && v.TryGetValue<string>(out var c) && c.Contains(EventArg, StringComparison.Ordinal);

    private static string? StatusCommandOf(JsonObject? root) =>
        root?["statusLine"] is JsonObject s && s["command"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : null;

    private JsonObject? Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var text = File.ReadAllText(SettingsPath);
            return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text, documentOptions: ReadOptions) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Claude Code's settings file could not be read ({ex.Message}). It was left unchanged.", ex);
        }
    }

    private void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var tmp = SettingsPath + ".amirlap.tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}

/// <summary>
/// What "AmirLap.exe --pulse-status" and "--pulse-event" do when Claude Code runs them: read the
/// JSON Claude Code sends, update the shared state, and (for the status line) print a short line.
/// They start without the app's windows and finish in a moment.
/// </summary>
public static class PulseBridge
{
    public static int StatusLine(TextReader input, TextWriter output, string statePath, string? previousCommand, Func<string, string, string?>? runPrevious, Action<Exception>? onError = null)
    {
        var json = input.ReadToEnd();
        ClaudeState? state = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            var now = DateTimeOffset.UtcNow;
            state = ClaudeStateStore.Update(statePath, s => s.ApplyStatusLine(root, now));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(ex);
        }
        string? text = previousCommand is not null && runPrevious is not null ? runPrevious(previousCommand, json) : null;
        output.Write(text ?? StatusText(state, DateTimeOffset.UtcNow));
        output.Flush();
        return 0;
    }

    public static int Event(TextReader input, string statePath, Func<long> findWindow, Action<Exception>? onError = null)
    {
        var json = input.ReadToEnd();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            long window = 0;
            try { window = findWindow(); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { onError?.Invoke(ex); }
            var now = DateTimeOffset.UtcNow;
            ClaudeStateStore.Update(statePath, s => s.ApplyHook(root, now, window));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            onError?.Invoke(ex);
        }
        return 0; // never block Claude Code
    }

    /// <summary>The status line's own text: "5h 64% · week 31%" (empty until the limits are known).</summary>
    public static string StatusText(ClaudeState? state, DateTimeOffset now)
    {
        if (state is null) return "";
        var parts = new List<string>();
        if (state.FiveHour is { } f) parts.Add($"5h {f.UsedAt(now):0}%");
        if (state.SevenDay is { } w) parts.Add($"week {w.UsedAt(now):0}%");
        return string.Join(" · ", parts);
    }
}
