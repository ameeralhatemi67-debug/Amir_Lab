using System.Text.Json;
using System.Text.RegularExpressions;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>An agent's own earlier session in a folder, which a chat can continue (as a copy).</summary>
public sealed record PastSession(string Agent, string Id, string Title, DateTimeOffset Updated, int Messages);

/// <summary>
/// The sessions Claude Code and Codex already keep for a folder, read from their own files on this
/// PC. Claude Code keeps one file per session in <c>~/.claude/projects/&lt;folder with every other
/// character as -&gt;/</c>; Codex keeps its sessions by date in <c>~/.codex/sessions</c>, each
/// starting with the folder it ran in, and their names in <c>session_index.jsonl</c>.
/// </summary>
public static partial class AgentSessions
{
    private const long MaxRead = 8L * 1024 * 1024;

    [GeneratedRegex(@"""(?:customTitle|aiTitle)""\s*:\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"[^A-Za-z0-9]")]
    private static partial Regex NotAlphanumeric();

    public static string ClaudeHome =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    /// <summary>The name Claude Code gives a folder's session folder: every character but letters and digits becomes "-".</summary>
    public static string ClaudeSlug(string folder) => NotAlphanumeric().Replace(Path.TrimEndingDirectorySeparator(folder), "-");

    /// <summary>Claude Code's sessions for a folder, newest first.</summary>
    public static List<PastSession> Claude(string folder, string? home = null, int max = 12)
    {
        var projects = Path.Combine(home ?? ClaudeHome, "projects");
        var list = new List<PastSession>();
        if (!Directory.Exists(projects)) return list;
        var slug = ClaudeSlug(folder);
        try
        {
            var dirs = Directory.EnumerateDirectories(projects).Where(d => string.Equals(Path.GetFileName(d), slug, StringComparison.OrdinalIgnoreCase));
            var files = dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl")).Select(f => new FileInfo(f)).Where(f => f.Length > 0)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(max);
            foreach (var f in files)
            {
                var (title, messages) = ReadClaude(f.FullName);
                if (messages == 0) continue;
                list.Add(new PastSession(Agent.Claude, Path.GetFileNameWithoutExtension(f.Name), title, f.LastWriteTimeUtc, messages));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>A Claude Code session's title (its custom or generated title, else its first message) and how many messages the user wrote.</summary>
    internal static (string Title, int Messages) ReadClaude(string file)
    {
        string? title = null, first = null;
        int messages = 0;
        long read = 0;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                read += line.Length;
                if (read > MaxRead) break;
                if (line.Contains("Title\"", StringComparison.Ordinal) && TitlePattern().Match(line) is { Success: true } t)
                    title = Unescape(t.Groups[1].Value);
                if (!line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) continue;
                var text = UserText(line);
                if (text is null) continue;
                messages++;
                first ??= text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (Tidy(title ?? first ?? "Claude Code session"), messages);
    }

    /// <summary>The text of a user line the user typed (not a tool result, a reminder or a command echo).</summary>
    private static string? UserText(string line)
    {
        if (!ClaudeStream.TryRead(line, out var root) || ClaudeStream.Str(root, "type") != "user") return null;
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)) return null;
        string? text = content.ValueKind switch
        {
            JsonValueKind.String => content.GetString(),
            JsonValueKind.Array => content.EnumerateArray().Where(p => ClaudeStream.Str(p, "type") == "text").Select(p => ClaudeStream.Str(p, "text")).FirstOrDefault(),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        return text.StartsWith('<') || text.StartsWith("Caveat:", StringComparison.Ordinal) ? null : text;
    }

    /// <summary>Codex's sessions for a folder, newest first.</summary>
    public static List<PastSession> Codex(string folder, string? home = null, int max = 12)
    {
        var codexHome = home ?? CodexUsageReader.DefaultHome;
        var root = Path.Combine(codexHome, "sessions");
        var list = new List<PastSession>();
        if (!Directory.Exists(root)) return list;
        var names = CodexNames(codexHome);
        var want = Normal(folder);
        try
        {
            var files = Directory.EnumerateFiles(root, "rollout-*.jsonl", SearchOption.AllDirectories).Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(600);
            foreach (var f in files)
            {
                if (list.Count >= max) break;
                var (id, cwd, first) = ReadCodexHead(f.FullName);
                if (id is null || cwd is null || Normal(cwd) != want) continue;
                var title = names.TryGetValue(id, out var name) ? name : first ?? "Codex session";
                list.Add(new PastSession(Agent.Codex, id, Tidy(title), f.LastWriteTimeUtc, 0));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }

    /// <summary>Codex's session names from session_index.jsonl (a later line renames).</summary>
    internal static Dictionary<string, string> CodexNames(string home)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var file = Path.Combine(home, "session_index.jsonl");
        try
        {
            if (!File.Exists(file)) return names;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (ClaudeStream.TryRead(line, out var r) && ClaudeStream.Str(r, "id") is { Length: > 0 } id && ClaudeStream.Str(r, "thread_name") is { Length: > 0 } name)
                    names[id] = name;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return names;
    }

    /// <summary>A Codex session's id and folder (its first line) and the first thing the user asked.</summary>
    internal static (string? Id, string? Cwd, string? First) ReadCodexHead(string file)
    {
        string? id = null, cwd = null, first = null;
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            int lines = 0;
            while (reader.ReadLine() is { } line && lines++ < 40)
            {
                if (!ClaudeStream.TryRead(line, out var r)) continue;
                if (!r.TryGetProperty("payload", out var payload)) continue;
                var type = ClaudeStream.Str(r, "type");
                if (type == "session_meta")
                {
                    id = ClaudeStream.Str(payload, "id");
                    cwd = ClaudeStream.Str(payload, "cwd");
                }
                else if (type == "event_msg" && ClaudeStream.Str(payload, "type") == "user_message" && ClaudeStream.Str(payload, "message") is { Length: > 0 } m)
                {
                    first = m;
                    break;
                }
                if (id is not null && cwd is not null && lines > 12) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (id, cwd, first);
    }

    private static string Normal(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path.ToUpperInvariant(); }
    }

    private static string Unescape(string json)
    {
        try { return JsonSerializer.Deserialize<string>("\"" + json + "\"") ?? json; }
        catch (JsonException) { return json; }
    }

    private static string Tidy(string text)
    {
        var one = Regex.Replace(text, @"\s+", " ").Trim();
        return one.Length <= 70 ? one : one[..70] + "…";
    }
}
