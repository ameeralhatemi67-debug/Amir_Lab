using System.Text;
using System.Text.Json;

namespace Toolbox.Core.Pulse;

/// <summary>
/// Codex's limits, from Codex's own session logs on this PC: every answer it gets carries the
/// five-hour and weekly limits ("rate_limits"), which Codex writes into the session's .jsonl file.
/// Only the end of the newest log is read; nothing is sent anywhere.
/// </summary>
public static class CodexUsageReader
{
    private const int TailBytes = 512 * 1024;

    /// <summary>Codex's folder: CODEX_HOME when set, otherwise .codex in the user's profile.</summary>
    public static string DefaultHome =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static string SessionsDir(string home) => Path.Combine(home, "sessions");

    /// <summary>The newest Codex limits on this PC, or null when Codex left none.</summary>
    public static UsageSnapshot? ReadLatest(string? home = null)
    {
        var file = LatestSessionFile(home ?? DefaultHome);
        return file is null ? null : ReadTail(file);
    }

    /// <summary>
    /// The session log written most recently. Logs sit in sessions\yyyy\MM\dd; a session that started
    /// yesterday may still be written today, so the three newest days are compared.
    /// </summary>
    public static string? LatestSessionFile(string home)
    {
        var root = SessionsDir(home);
        if (!Directory.Exists(root)) return null;
        try
        {
            var days = Directory.EnumerateDirectories(root)
                .SelectMany(y => Directory.EnumerateDirectories(y))
                .SelectMany(m => Directory.EnumerateDirectories(m))
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Take(3)
                .ToList();
            var files = days.SelectMany(d => Directory.EnumerateFiles(d, "*.jsonl")).ToList();
            if (files.Count == 0) files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList();
            return files.Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The last limits written in a session log (only its end is read).</summary>
    public static UsageSnapshot? ReadTail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var readAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var lines = text.Split('\n');
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
                if (ParseLine(lines[i], readAt) is { } snapshot) return snapshot;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    /// <summary>Reads the limits from one log line (an event whose payload has "rate_limits").</summary>
    public static UsageSnapshot? ParseLine(string line, DateTimeOffset readAt)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!Find(doc.RootElement, "rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return null;
            var primary = Window(limits, "primary");
            var secondary = Window(limits, "secondary");
            if (primary is null && secondary is null) return null;
            string? plan = limits.TryGetProperty("plan_type", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            if (doc.RootElement.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ts.GetString(), out var when))
                readAt = when;
            // The shorter window is the "session" one (five hours), the longer the week.
            if (primary is not null && secondary is not null && (primary.WindowMinutes ?? 0) > (secondary.WindowMinutes ?? 0))
                (primary, secondary) = (secondary, primary);
            return new UsageSnapshot(primary, secondary, plan, readAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UsageWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percent", out var used) || used.ValueKind != JsonValueKind.Number) return null;
        int? minutes = w.TryGetProperty("window_minutes", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : null;
        DateTimeOffset? resets = null;
        if (w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number) resets = Usage.FromUnix(r.GetDouble());
        else if (w.TryGetProperty("resets_in_seconds", out var rin) && rin.ValueKind == JsonValueKind.Number) resets = DateTimeOffset.UtcNow.AddSeconds(rin.GetDouble());
        return new UsageWindow(used.GetDouble(), resets, minutes);
    }

    private static bool Find(JsonElement element, string name, out JsonElement found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out found)) return true;
            foreach (var p in element.EnumerateObject())
                if (Find(p.Value, name, out found)) return true;
        }
        found = default;
        return false;
    }
}
