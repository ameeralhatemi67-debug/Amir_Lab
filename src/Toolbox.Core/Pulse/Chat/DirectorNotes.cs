using System.Text;
using System.Text.Json;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>
/// A readable copy of a director's conversation with the owner, made when it opens a chat, so the
/// guest can read it when the brief isn't enough. It keeps what the owner and the agent said and a
/// line for each thing the agent did; tool output is left out. Claude Code keeps a session as
/// <c>~/.claude/projects/&lt;folder&gt;/&lt;id&gt;.jsonl</c>; Codex keeps a thread as
/// <c>~/.codex/sessions/…/rollout-…-&lt;id&gt;.jsonl</c>.
/// </summary>
public static class DirectorNotes
{
    /// <summary>Longest copy kept; a longer conversation keeps its end (the newest part).</summary>
    public const int MaxChars = 300_000;

    private const int MaxLine = 4000;

    /// <summary>The session's own file, or null when it can't be found.</summary>
    public static string? FindSession(string agent, string session, string? folder, string? claudeHome = null, string? codexHome = null)
    {
        if (string.IsNullOrWhiteSpace(session) || session.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || session.Contains("..", StringComparison.Ordinal)) return null;
        try
        {
            if (agent == Agent.Codex)
            {
                var root = Path.Combine(codexHome ?? CodexUsageReader.DefaultHome, "sessions");
                if (!Directory.Exists(root)) return null;
                return Directory.EnumerateFiles(root, $"rollout-*{session}.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            var projects = Path.Combine(claudeHome ?? AgentSessions.ClaudeHome, "projects");
            if (!Directory.Exists(projects)) return null;
            if (folder is { Length: > 0 })
            {
                var slug = AgentSessions.ClaudeSlug(folder);
                foreach (var dir in Directory.EnumerateDirectories(projects).Where(d => string.Equals(Path.GetFileName(d), slug, StringComparison.OrdinalIgnoreCase)))
                {
                    var file = Path.Combine(dir, session + ".jsonl");
                    if (File.Exists(file)) return file;
                }
            }
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var file = Path.Combine(dir, session + ".jsonl");
                if (File.Exists(file)) return file;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    /// <summary>Writes the readable copy to <paramref name="target"/>. Returns false when there was nothing to copy.</summary>
    public static bool Write(string agent, string sessionFile, string target)
    {
        var text = Read(agent, sessionFile);
        if (text.Length == 0) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var name = Agent.Name(agent);
        var head = new StringBuilder()
            .Append("# ").Append(name).Append("'s conversation with the owner\n\n")
            .Append("Copied by Pulse when ").Append(name).Append(" opened the chat (").Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)).Append("). ")
            .Append("It keeps what was said and one line for each thing ").Append(name).Append(" did; tool output is left out.\n");
        if (text.Length > MaxChars)
        {
            var cut = text[^MaxChars..];
            int start = cut.IndexOf("\n## ", StringComparison.Ordinal);
            text = start > 0 ? cut[(start + 1)..] : cut;
            head.Append("The conversation was long, so only its newest part is here.\n");
        }
        File.WriteAllText(target, head.Append('\n').Append(text).ToString(), new UTF8Encoding(false));
        return true;
    }

    /// <summary>The conversation as Markdown: "## Owner" and "## Claude" sections, with the agent's actions as bullets.</summary>
    public static string Read(string agent, string sessionFile)
    {
        var sb = new StringBuilder();
        string? last = null;
        void Say(string who, string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > MaxLine) text = text[..MaxLine] + " […]";
            if (last != who) sb.Append("\n## ").Append(who).Append("\n\n");
            else sb.Append('\n');
            sb.Append(text).Append('\n');
            last = who;
        }
        void Did(string who, string what)
        {
            if (last != who) sb.Append("\n## ").Append(who).Append("\n\n");
            sb.Append("- ").Append(what).Append('\n');
            last = who;
        }
        var me = Agent.Name(agent);
        try
        {
            using var stream = new FileStream(sessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (!ClaudeStream.TryRead(line, out var root)) continue;
                if (agent == Agent.Codex) ReadCodexLine(root, me, Say, Did);
                else ReadClaudeLine(root, me, Say, Did);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return sb.ToString().Trim();
    }

    private static void ReadClaudeLine(JsonElement root, string me, Action<string, string> say, Action<string, string> did)
    {
        var type = ClaudeStream.Str(root, "type");
        if (type is not ("user" or "assistant")) return;
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) return;
        if (type == "user")
        {
            if (content.ValueKind == JsonValueKind.String) { if (Typed(content.GetString())) say("Owner", content.GetString()!); return; }
            if (content.ValueKind != JsonValueKind.Array) return;
            foreach (var part in content.EnumerateArray())
                if (ClaudeStream.Str(part, "type") == "text" && ClaudeStream.Str(part, "text") is { } t && Typed(t)) say("Owner", t);
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;
        foreach (var part in content.EnumerateArray())
        {
            var kind = ClaudeStream.Str(part, "type");
            if (kind == "text" && ClaudeStream.Str(part, "text") is { Length: > 0 } text) say(me, text);
            else if (kind == "tool_use")
            {
                var step = ClaudeStream.Step(ClaudeStream.Str(part, "name") ?? "", part.TryGetProperty("input", out var input) ? input : default);
                if (step.Text.Length > 0) did(me, Short(step.Text));
            }
        }
    }

    private static void ReadCodexLine(JsonElement root, string me, Action<string, string> say, Action<string, string> did)
    {
        if (!root.TryGetProperty("payload", out var payload)) return;
        var type = ClaudeStream.Str(root, "type");
        var kind = ClaudeStream.Str(payload, "type");
        if (type == "event_msg" && kind == "user_message" && ClaudeStream.Str(payload, "message") is { } asked && Typed(asked)) say("Owner", asked);
        else if (type == "event_msg" && kind == "agent_message" && ClaudeStream.Str(payload, "message") is { Length: > 0 } answer) say(me, answer);
        else if (type == "response_item" && kind is "function_call" or "custom_tool_call" or "local_shell_call")
        {
            var name = ClaudeStream.Str(payload, "name") ?? kind;
            var args = ClaudeStream.Str(payload, "arguments") ?? ClaudeStream.Str(payload, "input") ?? "";
            did(me, Short($"{name} {args}".Trim()));
        }
    }

    /// <summary>Text the owner typed (not a reminder, a command echo or a block of context the app added).</summary>
    private static bool Typed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.TrimStart();
        return !t.StartsWith('<') && !t.StartsWith("Caveat:", StringComparison.Ordinal) && !t.StartsWith("# AGENTS.md", StringComparison.Ordinal);
    }

    private static string Short(string text)
    {
        var one = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return one.Length <= 200 ? one : one[..200] + "…";
    }
}
