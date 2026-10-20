using System.Globalization;
using System.Text;

namespace OcrEval;

public static class Metrics
{
    /// <summary>Strict comparison form: NFC, whitespace collapsed to single spaces.</summary>
    public static string Strict(string text) => CollapseSpaces(text.Normalize(NormalizationForm.FormC));

    /// <summary>
    /// Lenient form for Arabic: NFKC (presentation forms → letters), no diacritics or tatweel,
    /// alef variants unified. Digits and Latin text are compared exactly.
    /// </summary>
    public static string Lenient(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Normalize(NormalizationForm.FormKC))
        {
            int c = ch;
            if (c is >= 0x064B and <= 0x0652 or 0x0640 or 0x0670) continue; // harakat, tatweel, superscript alef
            sb.Append(c is 0x0623 or 0x0625 or 0x0622 or 0x0671 ? (char)0x0627 : ch); // hamza/madda/wasla alef -> alef
        }
        return CollapseSpaces(sb.ToString());
    }

    public static double Cer(string truth, string hypothesis)
    {
        if (truth.Length == 0) return hypothesis.Length == 0 ? 0 : 1;
        return (double)Levenshtein(truth.AsSpan(), hypothesis.AsSpan()) / truth.Length;
    }

    public static double Wer(string truth, string hypothesis)
    {
        var t = truth.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var h = hypothesis.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length == 0) return h.Length == 0 ? 0 : 1;
        return (double)Levenshtein<string>(t, h) / t.Length;
    }

    private static string CollapseSpaces(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (var ch in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format) continue; // bidi marks, ZWJ/ZWNJ
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static int Levenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    private static int Levenshtein<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        var cmp = EqualityComparer<T>.Default;
        var previous = new int[b.Count + 1];
        var current = new int[b.Count + 1];
        for (int j = 0; j <= b.Count; j++) previous[j] = j;
        for (int i = 1; i <= a.Count; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Count; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (cmp.Equals(a[i - 1], b[j - 1]) ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Count];
    }
}
