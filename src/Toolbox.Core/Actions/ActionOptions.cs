using System.Globalization;

namespace Toolbox.Core.Actions;

/// <summary>
/// String-keyed action settings. Kept as plain strings so they serialise identically in
/// history, recipes and the command line (<c>--set key=value</c>).
/// </summary>
public sealed class ActionOptions : Dictionary<string, string>
{
    public ActionOptions() : base(StringComparer.OrdinalIgnoreCase) { }

    public ActionOptions(IDictionary<string, string> source) : base(source, StringComparer.OrdinalIgnoreCase) { }

    public ActionOptions With(string key, string value)
    {
        var copy = new ActionOptions(this) { [key] = value };
        return copy;
    }

    public string GetString(string key, string fallback) =>
        TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    public int GetInt(string key, int fallback, int? min = null, int? max = null)
    {
        var value = TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
        if (min is { } lo) value = Math.Max(lo, value);
        if (max is { } hi) value = Math.Min(hi, value);
        return value;
    }

    public double GetDouble(string key, double fallback, double? min = null, double? max = null)
    {
        var value = TryGetValue(key, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
        if (min is { } lo) value = Math.Max(lo, value);
        if (max is { } hi) value = Math.Min(hi, value);
        return value;
    }

    public bool GetBool(string key, bool fallback) =>
        TryGetValue(key, out var raw) && bool.TryParse(raw, out var parsed) ? parsed : fallback;

    /// <summary>Applies an action's declared defaults for any key the caller did not set.</summary>
    public ActionOptions WithDefaults(ActionDefinition action)
    {
        var merged = new ActionOptions(this);
        foreach (var option in action.Options)
            if (!merged.ContainsKey(option.Key))
                merged[option.Key] = option.Default;
        return merged;
    }

    public static ActionOptions Parse(IEnumerable<string> assignments)
    {
        var options = new ActionOptions();
        foreach (var assignment in assignments)
        {
            var eq = assignment.IndexOf('=');
            if (eq <= 0) throw new FormatException($"Expected key=value, got '{assignment}'.");
            options[assignment[..eq].Trim()] = assignment[(eq + 1)..].Trim();
        }
        return options;
    }
}
