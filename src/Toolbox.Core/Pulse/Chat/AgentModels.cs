using System.Text.Json;
using System.Text.RegularExpressions;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>A model an agent can run with: what to pass (empty = its own default) and what to call it.</summary>
public sealed record AgentModel(string Id, string Name);

/// <summary>
/// The models a chat can pick for each agent. Claude Code takes an alias for its latest model of
/// a kind (<c>--model opus</c>); Codex lists the models it offers in its own cache
/// (<c>~/.codex/models_cache.json</c>), and its default is the one in its <c>config.toml</c>.
/// </summary>
public static partial class AgentModels
{
    [GeneratedRegex(@"^\s*model\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex ConfigModel();

    public static IReadOnlyList<AgentModel> Claude() =>
    [
        new("", "Default"),
        new("fable", "Fable"),
        new("opus", "Opus"),
        new("sonnet", "Sonnet"),
        new("haiku", "Haiku"),
    ];

    /// <summary>Codex's models as its cache lists them (the ones it shows, in its order), after its default.</summary>
    public static IReadOnlyList<AgentModel> Codex(string? home = null)
    {
        home ??= CodexUsageReader.DefaultHome;
        var list = new List<AgentModel>();
        string? configured = null;
        try
        {
            var config = Path.Combine(home, "config.toml");
            if (File.Exists(config) && ConfigModel().Match(File.ReadAllText(config)) is { Success: true } m) configured = m.Groups[1].Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            var cache = Path.Combine(home, "models_cache.json");
            if (File.Exists(cache))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cache));
                if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                {
                    var found = new List<(int Priority, AgentModel Model)>();
                    foreach (var model in models.EnumerateArray())
                    {
                        var slug = ClaudeStream.Str(model, "slug");
                        if (slug is not { Length: > 0 } || ClaudeStream.Str(model, "visibility") is { } v && v != "list") continue;
                        var name = ClaudeStream.Str(model, "display_name") is { Length: > 0 } d ? d : slug;
                        int priority = model.TryGetProperty("priority", out var p) && p.TryGetInt32(out var n) ? n : 999;
                        found.Add((priority, new AgentModel(slug, name)));
                    }
                    list.AddRange(found.OrderBy(f => f.Priority).Select(f => f.Model));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        var defaultName = configured is null ? "Default" : $"Default ({list.FirstOrDefault(m => m.Id == configured)?.Name ?? configured})";
        list.Insert(0, new AgentModel("", defaultName));
        return list;
    }

    public static IReadOnlyList<AgentModel> For(string agent, string? codexHome = null) => agent == Agent.Codex ? Codex(codexHome) : Claude();

    /// <summary>A model's name for showing ("Opus", "GPT-6.2-Sol"); the id itself when it isn't listed.</summary>
    public static string NameOf(string agent, string? id, string? codexHome = null)
    {
        if (string.IsNullOrWhiteSpace(id)) return "Default";
        return For(agent, codexHome).FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;
    }

    /// <summary>
    /// A model the caller asked for, matched to one the agent offers by id or name (any case), or
    /// passed through as it is when it looks like a model id. Empty for "default".
    /// </summary>
    public static string Pick(string agent, string? asked, string? codexHome = null)
    {
        var want = (asked ?? "").Trim();
        if (want.Length == 0 || want.Equals("default", StringComparison.OrdinalIgnoreCase)) return "";
        foreach (var m in For(agent, codexHome))
        {
            if (m.Id.Length == 0) continue;
            if (string.Equals(m.Id, want, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, want, StringComparison.OrdinalIgnoreCase)) return m.Id;
        }
        return Regex.IsMatch(want, @"^[A-Za-z0-9][A-Za-z0-9._:\-\[\]]{0,80}$") ? want : "";
    }
}
