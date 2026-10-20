namespace OcrEval;

public enum Degradation
{
    None,
    Blur,
    Noise,
    Jpeg,
    Rotate,
    LowContrast,
    Lighting,
    PhoneLike,
}

/// <summary>One generated page: text lines rendered in a font at a size and resolution, then degraded.</summary>
public sealed record SampleSpec(string Id, string Category, string Script, IReadOnlyList<string> Lines, string Font,
    double PointSize, double Dpi, Degradation Degradation, bool RightToLeft)
{
    public string Truth => string.Join("\n", Lines);
}

/// <summary>
/// The generated corpus. All texts were written for this evaluation (no third-party content).
/// Mixed lines are chosen so that their visual order is unambiguous under the Unicode bidi algorithm.
/// </summary>
public static class Corpus
{
    public static readonly string[] English =
    [
        "The quarterly report shows a steady increase in revenue across all regions.",
        "Please review the attached invoice and confirm the payment details by Friday.",
        "The meeting has been moved to Tuesday at 10:30 in room 4B.",
        "Keep a copy of this document for your records and future reference.",
        "Delivery is expected within five to seven working days after approval.",
    ];

    public static readonly string[] Arabic =
    [
        "يوضح التقرير الربعي زيادة مستمرة في الإيرادات في جميع المناطق.",
        "يرجى مراجعة الفاتورة المرفقة وتأكيد تفاصيل الدفع قبل يوم الجمعة.",
        "تم نقل الاجتماع إلى يوم الثلاثاء في الساعة العاشرة صباحا.",
        "الرجاء الاحتفاظ بنسخة من هذا المستند للرجوع إليها عند الحاجة.",
        "نشكركم على تعاونكم ونتطلع إلى العمل معكم في المشروع القادم.",
    ];

    // LRE ... PDF forces a code to display left-to-right inside Arabic, as Word and browsers do by the bidi
    // algorithm (WPF's older bidi reorders hyphenated codes). Both marks are invisible and ignored when scoring.
    private const char Lre = (char)0x202A, Pdf = (char)0x202C;

    public static readonly string[] Mixed =
    [
        $"رقم الطلب {Lre}INV-2026-0042{Pdf} بتاريخ اليوم",
        "تم تحديث نظام Windows إلى الإصدار الجديد",
        "رمز المنتج AB1234 متوفر الآن",
        "يرجى إرسال الملف بصيغة PDF إلى قسم المالية",
        "البريد الإلكتروني info@example.com",
        "سيعقد المؤتمر في مدينة Dubai هذا العام",
    ];

    public static readonly string[] DigitsWestern =
    [
        "Invoice 2026-0042 dated 18/10/2026",
        "Total 1,250.75 due on 05/11/2026",
        "Phone +966 50 123 4567 ext. 309",
    ];

    public static readonly string[] DigitsArabic =
    [
        "رقم الفاتورة ٢٠٢٦ بتاريخ ١٨ أكتوبر",
        "المبلغ ١٢٥٠ ريال يستحق خلال ٣٠ يوما",
        "الهاتف ٠٥٠١٢٣٤٥٦٧ تحويلة ٣٠٩",
    ];

    public static IReadOnlyList<SampleSpec> Build()
    {
        var list = new List<SampleSpec>();

        foreach (var font in new[] { "Arial", "Times New Roman", "Calibri", "Georgia", "Courier New", "Segoe UI" })
            list.Add(new($"en-{Slug(font)}", "English · clean 300 DPI", "en", English, font, 11, 300, Degradation.None, false));

        foreach (var (font, size) in new[] { ("Arial", 12.0), ("Times New Roman", 12.0), ("Tahoma", 12.0), ("Segoe UI", 12.0),
                     ("Sakkal Majalla", 16.0), ("Traditional Arabic", 16.0), ("Simplified Arabic", 13.0) })
            list.Add(new($"ar-{Slug(font)}", "Arabic · clean 300 DPI", "ar", Arabic, font, size, 300, Degradation.None, true));

        foreach (var font in new[] { "Arial", "Times New Roman", "Tahoma", "Segoe UI" })
            list.Add(new($"mixed-{Slug(font)}", "Mixed Arabic+English · clean 300 DPI", "mixed", Mixed, font, 12, 300, Degradation.None, true));

        foreach (var font in new[] { "Arial", "Tahoma" })
        {
            list.Add(new($"digits-en-{Slug(font)}", "Numbers (Western digits)", "en", DigitsWestern, font, 12, 300, Degradation.None, false));
            list.Add(new($"digits-ar-{Slug(font)}", "Numbers (Arabic-Indic digits)", "ar", DigitsArabic, font, 12, 300, Degradation.None, true));
        }

        // Resolution and degradations, on two common fonts per script.
        var scripts = new (string Script, string[] Lines, bool Rtl, string[] Fonts)[]
        {
            ("en", English, false, ["Arial", "Times New Roman"]),
            ("ar", Arabic, true, ["Arial", "Times New Roman"]),
            ("mixed", Mixed, true, ["Arial", "Tahoma"]),
        };
        foreach (var (script, lines, rtl, fonts) in scripts)
        {
            foreach (var font in fonts)
            {
                string f = Slug(font);
                double pt = script == "en" ? 11 : 12;
                list.Add(new($"{script}-{f}-150dpi", "Low resolution · 150 DPI 10 pt", script, lines, font, 10, 150, Degradation.None, rtl));
                list.Add(new($"{script}-{f}-screen", "Screenshot size · 96 DPI 9 pt", script, lines, font, 9, 96, Degradation.None, rtl));
                foreach (var d in new[] { Degradation.Blur, Degradation.Noise, Degradation.Jpeg, Degradation.Rotate, Degradation.LowContrast, Degradation.Lighting, Degradation.PhoneLike })
                    list.Add(new($"{script}-{f}-{d.ToString().ToLowerInvariant()}", $"Degraded · {Describe(d)}", script, lines, font, pt, 200, d, rtl));
            }
        }
        return list;
    }

    public static string Describe(Degradation d) => d switch
    {
        Degradation.Blur => "blur σ1.3",
        Degradation.Noise => "noise σ28",
        Degradation.Jpeg => "JPEG q20",
        Degradation.Rotate => "rotated 3°",
        Degradation.LowContrast => "low contrast",
        Degradation.Lighting => "uneven lighting",
        Degradation.PhoneLike => "phone-like (tilt+light+noise+JPEG)",
        _ => "clean",
    };

    private static string Slug(string font) => font.ToLowerInvariant().Replace(' ', '-');
}
