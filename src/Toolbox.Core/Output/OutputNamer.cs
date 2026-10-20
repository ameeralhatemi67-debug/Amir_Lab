using System.Globalization;
using System.Text;

namespace Toolbox.Core.Output;

/// <summary>Everything a naming pattern can refer to for one output file.</summary>
public sealed record NameParts(
    string SourceBaseName,
    string Suffix,
    string Extension,
    int? Page = null,
    int? PageCount = null,
    int? Width = null,
    int? Height = null,
    DateTime? Date = null);

/// <summary>
/// Builds output file names from a pattern. Tokens: {name} {suffix} {page} {w} {h} {date} {time}.
/// {suffix} and {page} expand to an empty string when not relevant, so the default pattern
/// "{name}{suffix}{page}" gives "Report.jpg", "Report Cropped.jpg" or "Report - Page 03.jpg".
/// </summary>
public static class OutputNamer
{
    public const string DefaultPattern = "{name}{suffix}{page}";
    public const int MaxFileNameLength = 180;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string Compose(string pattern, NameParts parts)
    {
        if (string.IsNullOrWhiteSpace(pattern)) pattern = DefaultPattern;
        var date = parts.Date ?? DateTime.Now;

        var pageToken = parts.Page is { } page
            ? " - Page " + page.ToString(CultureInfo.InvariantCulture).PadLeft(PageDigits(parts.PageCount), '0')
            : "";

        var name = new StringBuilder(pattern)
            .Replace("{name}", parts.SourceBaseName)
            .Replace("{suffix}", parts.Suffix)
            .Replace("{page}", pageToken)
            .Replace("{w}", parts.Width?.ToString(CultureInfo.InvariantCulture) ?? "")
            .Replace("{h}", parts.Height?.ToString(CultureInfo.InvariantCulture) ?? "")
            .Replace("{date}", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Replace("{time}", date.ToString("HHmmss", CultureInfo.InvariantCulture))
            .ToString();

        return Sanitize(name) + parts.Extension;
    }

    /// <summary>"Name.ext" → "Name (2).ext", "Name (3).ext", ...</summary>
    public static string WithCollisionIndex(string fileName, int index)
    {
        if (index <= 1) return fileName;
        var ext = Path.GetExtension(fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        return $"{stem} ({index}){ext}";
    }

    /// <summary>
    /// Makes a stem safe for Windows file names while keeping every Unicode letter (Arabic included).
    /// </summary>
    public static string Sanitize(string stem)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(stem.Length);
        foreach (var ch in stem)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 || char.IsControl(ch) ? '_' : ch);

        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0) cleaned = "Output";
        if (ReservedDeviceNames.Contains(cleaned.Split('.')[0])) cleaned = "_" + cleaned;
        if (cleaned.Length > MaxFileNameLength)
        {
            cleaned = cleaned[..MaxFileNameLength];
            // Do not cut a surrogate pair in half.
            if (char.IsHighSurrogate(cleaned[^1])) cleaned = cleaned[..^1];
            cleaned = cleaned.TrimEnd('.', ' ');
        }
        return cleaned;
    }

    private static int PageDigits(int? pageCount) =>
        Math.Max(2, (pageCount ?? 1).ToString(CultureInfo.InvariantCulture).Length);
}
