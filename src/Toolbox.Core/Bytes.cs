using System.Globalization;

namespace Toolbox.Core;

public static class Bytes
{
    /// <summary>Formats a size the way Explorer does (1024-based units with KB/MB/GB labels).</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        var format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.0#";
        return value.ToString(format, CultureInfo.InvariantCulture) + " " + units[unit];
    }

    /// <summary>Parses "2MB", "500 KB", "1.5 mb" or a plain byte count.</summary>
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToUpperInvariant().Replace(" ", "");
        long multiplier = 1;
        foreach (var (suffix, mult) in new (string, long)[] { ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1) })
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal))
            {
                multiplier = mult;
                t = t[..^suffix.Length];
                break;
            }
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value < 0) return false;
        bytes = (long)Math.Round(value * multiplier);
        return true;
    }
}
