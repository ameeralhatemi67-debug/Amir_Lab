using System.Text.Json;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>Something an agent reported while working on a turn.</summary>
public abstract record AgentEvent;

/// <summary>The agent's own session id (Claude Code session, Codex thread).</summary>
public sealed record SessionEvent(string SessionId) : AgentEvent;

/// <summary>A piece of text the agent wrote (the last one is its answer).</summary>
public sealed record TextEvent(string Text) : AgentEvent;

/// <summary>Something the agent did: read, edit, run...</summary>
public sealed record StepEvent(ChatStep Step) : AgentEvent;

/// <summary>A warning that is not part of the answer (logged, not shown as the reply).</summary>
public sealed record NoticeEvent(string Text) : AgentEvent;

/// <summary>Claude's limits as its answers report them (0–100), when present.</summary>
public sealed record LimitsEvent(double? FiveHour, double? Week) : AgentEvent;

/// <summary>The turn is over: the final answer, what was not allowed, or what went wrong.</summary>
public sealed record DoneEvent(string? Answer, IReadOnlyList<DeniedAction> Denied, string? Error) : AgentEvent;

/// <summary>
/// Reads the lines Claude Code writes with <c>--output-format stream-json --verbose</c>: system/init
/// (the session), assistant messages (text and tool uses), rate_limit_event and result.
/// </summary>
public static class ClaudeStream
{
    public static IEnumerable<AgentEvent> Parse(string line)
    {
        if (!TryRead(line, out var root)) yield break;
        var type = Str(root, "type");
        switch (type)
        {
            case "system":
                if (Str(root, "subtype") == "init" && Str(root, "session_id") is { Length: > 0 } id) yield return new SessionEvent(id);
                break;
            case "assistant":
                if (root.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        var kind = Str(part, "type");
                        if (kind == "text" && Str(part, "text") is { Length: > 0 } text) yield return new TextEvent(text);
                        else if (kind == "tool_use") yield return new StepEvent(Step(Str(part, "name") ?? "", part.TryGetProperty("input", out var input) ? input : default));
                    }
                }
                break;
            case "rate_limit_event":
                if (root.TryGetProperty("rate_limit_info", out var info) && info.TryGetProperty("unifiedWindows", out var windows))
                    yield return new LimitsEvent(Utilisation(windows, "five_hour"), Utilisation(windows, "seven_day"));
                break;
            case "result":
            {
                var denied = new List<DeniedAction>();
                if (root.TryGetProperty("permission_denials", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in list.EnumerateArray())
                    {
                        var action = Denied(Str(d, "tool_name") ?? "", d.TryGetProperty("tool_input", out var ti) ? ti : default);
                        if (!denied.Any(x => x.Rule == action.Rule)) denied.Add(action);
                    }
                }
                bool error = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                var result = Str(root, "result");
                var subtype = Str(root, "subtype");
                if (error || (subtype is { Length: > 0 } s && s != "success"))
                    yield return new DoneEvent(null, denied, result is { Length: > 0 } ? result : subtype ?? "error");
                else yield return new DoneEvent(result, denied, null);
                break;
            }
        }
    }

    private static double? Utilisation(JsonElement windows, string name) =>
        windows.TryGetProperty(name, out var w) && w.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var v) ? Math.Round(v * 100, 1) : null;

    /// <summary>A tool use said plainly.</summary>
    public static ChatStep Step(string tool, JsonElement input)
    {
        string? path = Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path");
        return tool switch
        {
            "Edit" or "MultiEdit" or "NotebookEdit" => new ChatStep { Kind = "edit", Text = "Edited " + (path ?? "a file") },
            "Write" => new ChatStep { Kind = "edit", Text = "Wrote " + (path ?? "a file") },
            "Read" => new ChatStep { Kind = "read", Text = "Read " + (path ?? "a file") },
            "Glob" or "Grep" => new ChatStep { Kind = "read", Text = "Searched for " + (Str(input, "pattern") ?? "files") },
            "Bash" or "PowerShell" => new ChatStep { Kind = "run", Text = "Ran " + Short(Str(input, "command") ?? "a command", 120) },
            "WebSearch" => new ChatStep { Kind = "web", Text = "Searched the web for " + (Str(input, "query") ?? "something") },
            "WebFetch" => new ChatStep { Kind = "web", Text = "Read " + (Str(input, "url") ?? "a web page") },
            "Task" or "Agent" => new ChatStep { Kind = "other", Text = "Asked a helper: " + Short(Str(input, "description") ?? "a task", 80) },
            "TodoWrite" => new ChatStep { Kind = "other", Text = "Updated its task list" },
            _ => new ChatStep { Kind = "other", Text = "Used " + tool },
        };
    }

    /// <summary>A denied tool use, with the rules that allow it once or always.</summary>
    public static DeniedAction Denied(string tool, JsonElement input)
    {
        if (tool is "Bash" or "PowerShell")
        {
            var command = (Str(input, "command") ?? "").Trim();
            var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var prefix = string.Join(' ', words.Take(words.Length > 1 && !words[1].StartsWith('-') ? 2 : 1));
            return new DeniedAction
            {
                Tool = tool,
                Summary = "Run " + Short(command, 160),
                Rule = $"{tool}({command})",
                AlwaysRule = prefix.Length > 0 ? $"{tool}({prefix}:*)" : tool,
            };
        }
        var step = Step(tool, input);
        var verb = step.Kind == "edit" ? (tool == "Write" ? "Write " : "Edit ") : "Use " + tool + " on ";
        var path = Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path") ?? Str(input, "url");
        return new DeniedAction
        {
            Tool = tool,
            Summary = path is null ? "Use " + tool : verb + path,
            Rule = tool,
            AlwaysRule = tool,
        };
    }

    internal static string Short(string text, int max)
    {
        var one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= max ? one : one[..max] + "…";
    }

    internal static bool TryRead(string line, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(line);
            root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Reads the lines <c>codex exec --json</c> writes: thread.started, turn.started, item.* (agent
/// messages, commands, file changes, searches), turn.completed / turn.failed and error.
/// Errors before the turn starts are Codex warning about its own setup; they are notices.
/// </summary>
public sealed class CodexStream
{
    private bool _started;
    private string? _last;

    public IEnumerable<AgentEvent> Parse(string line)
    {
        if (!ClaudeStream.TryRead(line, out var root)) yield break;
        var type = ClaudeStream.Str(root, "type");
        switch (type)
        {
            case "thread.started":
                if (ClaudeStream.Str(root, "thread_id") is { Length: > 0 } id) yield return new SessionEvent(id);
                break;
            case "turn.started":
                _started = true;
                break;
            case "item.completed":
                if (!root.TryGetProperty("item", out var item)) break;
                foreach (var e in Item(item)) yield return e;
                break;
            case "turn.completed":
                yield return new DoneEvent(_last, [], null);
                break;
            case "turn.failed":
            {
                var message = root.TryGetProperty("error", out var err) ? ClaudeStream.Str(err, "message") : null;
                yield return new DoneEvent(_last, [], message ?? "Codex stopped.");
                break;
            }
            case "error":
                if (_started) yield return new DoneEvent(_last, [], ClaudeStream.Str(root, "message") ?? "Codex stopped.");
                else yield return new NoticeEvent(ClaudeStream.Str(root, "message") ?? "");
                break;
        }
    }

    private IEnumerable<AgentEvent> Item(JsonElement item)
    {
        var kind = ClaudeStream.Str(item, "type");
        switch (kind)
        {
            case "agent_message":
                if (ClaudeStream.Str(item, "text") is { Length: > 0 } text)
                {
                    _last = text;
                    yield return new TextEvent(text);
                }
                break;
            case "reasoning":
            case "todo_list":
                break;
            case "command_execution":
            {
                var command = ClaudeStream.Str(item, "command") ?? "a command";
                yield return new StepEvent(new ChatStep { Kind = "run", Text = "Ran " + ClaudeStream.Short(Unwrap(command), 120) });
                break;
            }
            case "file_change":
                if (item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in changes.EnumerateArray())
                    {
                        var path = ClaudeStream.Str(c, "path") ?? "a file";
                        var how = ClaudeStream.Str(c, "kind") switch { "add" => "Created ", "delete" => "Deleted ", _ => "Edited " };
                        yield return new StepEvent(new ChatStep { Kind = "edit", Text = how + path });
                    }
                }
                break;
            case "web_search":
                yield return new StepEvent(new ChatStep { Kind = "web", Text = "Searched the web for " + (ClaudeStream.Str(item, "query") ?? "something") });
                break;
            case "mcp_tool_call":
                yield return new StepEvent(new ChatStep { Kind = "other", Text = "Used " + (ClaudeStream.Str(item, "tool") ?? "a tool") });
                break;
            case "error":
                if (_started) yield return new StepEvent(new ChatStep { Kind = "other", Text = ClaudeStream.Short(ClaudeStream.Str(item, "message") ?? "A step failed", 160) });
                else yield return new NoticeEvent(ClaudeStream.Str(item, "message") ?? "");
                break;
            default:
                if (kind is { Length: > 0 })
                {
                    bool picture = kind.Contains("image", StringComparison.OrdinalIgnoreCase);
                    yield return new StepEvent(new ChatStep { Kind = picture ? "image" : "other", Text = picture ? "Made a picture" : "Did " + kind.Replace('_', ' ') });
                }
                break;
        }
    }

    /// <summary>Codex runs commands through a shell ("powershell -Command ..."); the command itself reads better.</summary>
    internal static string Unwrap(string command)
    {
        foreach (var marker in new[] { "-Command ", "-c " })
        {
            int i = command.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && i < 80) return command[(i + marker.Length)..].Trim().Trim('"', '\'');
        }
        return command;
    }
}
