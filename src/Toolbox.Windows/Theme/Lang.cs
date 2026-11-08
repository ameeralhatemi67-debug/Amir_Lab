using System.Windows;

namespace Toolbox.Windows.Theme;

/// <summary>
/// English or Arabic for the Amir Lap window and the tray menu. Text sits next to the code that
/// shows it, as (English, Arabic) pairs. Arabic lays the window out right-to-left; shortcuts,
/// file names, folders and sizes stay left-to-right inside it.
/// </summary>
internal static class Lang
{
    public static bool IsArabic { get; private set; }

    public static string Code => IsArabic ? "ar" : "en";

    public static void Set(string? code) => IsArabic = string.Equals(code, "ar", StringComparison.OrdinalIgnoreCase);

    public static string T(string en, string ar) => IsArabic ? ar : en;

    public static FlowDirection Flow => IsArabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>"2 min ago", "Yesterday"… for the Recent list.</summary>
    public static string Ago(DateTimeOffset when, DateTimeOffset now)
    {
        var span = now - when;
        if (span < TimeSpan.FromMinutes(1)) return T("Just now", "الآن");
        if (span < TimeSpan.FromHours(1)) return Count((int)span.TotalMinutes, "min", "دقيقة", "دقيقتين", "دقائق");
        if (span < TimeSpan.FromHours(24) && when.Date == now.Date) return Count((int)span.TotalHours, "h", "ساعة", "ساعتين", "ساعات");
        if (when.Date == now.Date.AddDays(-1)) return T("Yesterday", "أمس");
        return when.ToString("d MMM", System.Globalization.CultureInfo.GetCultureInfo(IsArabic ? "ar-SA" : "en-GB"));
    }

    // Arabic counts: 1 and 2 have their own forms, 3–10 take the plural, 11 and up the singular.
    private static string Count(int n, string enUnit, string arOne, string arTwo, string arPlural)
    {
        if (!IsArabic) return $"{n} {enUnit} ago";
        return n switch
        {
            1 => $"قبل {arOne}",
            2 => $"قبل {arTwo}",
            <= 10 => $"قبل {n} {arPlural}",
            _ => $"قبل {n} {arOne}",
        };
    }
}
