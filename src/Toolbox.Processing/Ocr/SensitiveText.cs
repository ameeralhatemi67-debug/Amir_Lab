using System.Text;
using System.Text.RegularExpressions;

namespace Toolbox.Processing.Ocr;

public enum SensitiveKind
{
    Email,
    Phone,
    Card,
    Iban,
    IpAddress,
    Secret,
}

/// <summary>Personal information found in recognised text, and the box to hide (in the recognised image's pixels).</summary>
public sealed record SensitiveHit(SensitiveKind Kind, string Text, double X, double Y, double Width, double Height)
{
    /// <summary>The text with most of it dotted out, for listing what was found without showing it again.</summary>
    public string Masked
    {
        get
        {
            var t = Text.Trim();
            if (Kind == SensitiveKind.Email && t.IndexOf('@') is > 0 and var at)
                return t[..Math.Min(2, at)] + "•••" + t[at..];
            int keep = Kind is SensitiveKind.Secret ? 3 : 4;
            if (t.Length <= keep + 2) return new string('•', t.Length);
            return Kind is SensitiveKind.Card or SensitiveKind.Phone or SensitiveKind.Iban
                ? "•••• " + t[^keep..]
                : t[..keep] + "•••";
        }
    }
}

/// <summary>
/// Finds personal information in recognised text, so it can be hidden before a screenshot is
/// shared: e-mail addresses, phone numbers, card numbers (Luhn-checked), IBANs (mod-97-checked), IP
/// addresses, and secrets (API keys and tokens with known shapes, long random-looking strings, and
/// what follows "password:" or "token:"). Arabic-Indic digits count as digits. Each hit's box is
/// the union of the words it covers, trimmed to the characters on the line's first and last word
/// when the line reads left to right, with a little padding. Suggestions only: the user decides.
/// </summary>
public static partial class SensitiveText
{
    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+ ?@ ?[A-Za-z0-9\-]+(?: ?\. ?[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<![\d])(?:\d[ \-]?){12,18}\d(?![\d])")]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,4})?\b")]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"(?<![\w+])(?:\+|00)?\(?\d{1,4}\)?(?:[ \-.]{0,2}\(?\d{2,5}\)?){1,5}(?![\w])")]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b")]
    private static partial Regex IpPattern();

    [GeneratedRegex(@"\b(?:sk-(?:ant-|proj-)?[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}|eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{4,})")]
    private static partial Regex KnownSecretPattern();

    [GeneratedRegex(@"(?i)(?:password|passwd|pwd|passcode|pin|token|secret|api[ _-]?key|كلمة المرور|كلمة السر|الرمز السري)\s*[:=]\s*(\S{3,})")]
    private static partial Regex LabelledSecretPattern();

    [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{24,}")]
    private static partial Regex LongTokenPattern();

    [GeneratedRegex(@"^\d{1,4}[-./]\d{1,2}[-./]\d{1,4}$")]
    private static partial Regex DatePattern();

    public static IReadOnlyList<SensitiveHit> Find(OcrPage page)
    {
        var hits = new List<SensitiveHit>();
        foreach (var line in page.Lines) hits.AddRange(Find(line));
        return hits;
    }

    public static IReadOnlyList<SensitiveHit> Find(OcrLine line)
    {
        var hits = new List<SensitiveHit>();
        if (line.Words.Count == 0) return hits;
        var sb = new StringBuilder();
        var spans = new List<(int Start, int End, OcrWord Word)>();
        foreach (var w in line.Words)
        {
            if (sb.Length > 0) sb.Append(' ');
            int start = sb.Length;
            sb.Append(Normalize(w.Text));
            spans.Add((start, sb.Length, w));
        }
        var text = sb.ToString();
        var claimed = new bool[text.Length];

        void Add(SensitiveKind kind, int start, int length)
        {
            if (length <= 0) return;
            for (int i = start; i < start + length; i++) if (claimed[i]) return;
            for (int i = start; i < start + length; i++) claimed[i] = true;
            var box = Box(spans, start, start + length, line.RightToLeft);
            if (box is { } b) hits.Add(new SensitiveHit(kind, text.Substring(start, length), b.X, b.Y, b.W, b.H));
        }

        foreach (Match m in KnownSecretPattern().Matches(text)) Add(SensitiveKind.Secret, m.Index, m.Length);
        foreach (Match m in LabelledSecretPattern().Matches(text)) Add(SensitiveKind.Secret, m.Groups[1].Index, m.Groups[1].Length);
        foreach (Match m in EmailPattern().Matches(text)) Add(SensitiveKind.Email, m.Index, m.Length);
        foreach (Match m in IbanPattern().Matches(text))
            if (IsIban(m.Value)) Add(SensitiveKind.Iban, m.Index, m.Length);
        foreach (Match m in CardPattern().Matches(text))
            if (IsCard(m.Value)) Add(SensitiveKind.Card, m.Index, m.Length);
        foreach (Match m in IpPattern().Matches(text)) Add(SensitiveKind.IpAddress, m.Index, m.Length);
        foreach (Match m in PhonePattern().Matches(text))
            if (IsPhone(m.Value)) Add(SensitiveKind.Phone, m.Index, m.Length);
        foreach (Match m in LongTokenPattern().Matches(text))
            if (LooksRandom(m.Value)) Add(SensitiveKind.Secret, m.Index, m.Length);
        return hits;
    }

    /// <summary>Arabic-Indic and Persian digits as ASCII digits (same length, so positions hold).</summary>
    internal static string Normalize(string s)
    {
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= '٠' and <= '٩') chars[i] = (char)('0' + chars[i] - '٠');
            else if (chars[i] is >= '۰' and <= '۹') chars[i] = (char)('0' + chars[i] - '۰');
        }
        return new string(chars);
    }

    private static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());

    internal static bool IsCard(string s)
    {
        var d = Digits(s);
        if (d.Length is < 13 or > 19 || d.Distinct().Count() < 2) return false;
        int sum = 0;
        bool twice = false;
        for (int i = d.Length - 1; i >= 0; i--)
        {
            int n = d[i] - '0';
            if (twice) { n *= 2; if (n > 9) n -= 9; }
            sum += n;
            twice = !twice;
        }
        return sum % 10 == 0;
    }

    internal static bool IsIban(string s)
    {
        var compact = s.Replace(" ", "");
        if (compact.Length is < 15 or > 34) return false;
        var rearranged = compact[4..] + compact[..4];
        int remainder = 0;
        foreach (char c in rearranged)
        {
            string digits = char.IsAsciiDigit(c) ? c.ToString() : char.IsAsciiLetterUpper(c) ? (c - 'A' + 10).ToString() : "";
            if (digits.Length == 0) return false;
            foreach (char d in digits) remainder = (remainder * 10 + (d - '0')) % 97;
        }
        return remainder == 1;
    }

    internal static bool IsPhone(string s)
    {
        var t = s.Trim();
        var d = Digits(t);
        if (d.Length is < 8 or > 15 || d.Distinct().Count() < 3) return false;
        if (DatePattern().IsMatch(t)) return false;
        bool international = t.StartsWith('+') || t.StartsWith("00", StringComparison.Ordinal);
        bool trunk = t.StartsWith('0') || t.StartsWith('(');
        // A bare run of digits is more often an order number or an amount than a phone number.
        bool grouped = t.Any(c => c is ' ' or '-' or '.' or '(' or ')');
        if (!international && !trunk && d.Length < 10) return false;
        if (!international && !grouped && !t.StartsWith('0')) return false;
        if (t.Count(c => c == '.') >= 2 && !international) return false; // 1.250.000 is an amount
        return true;
    }

    /// <summary>A long run that mixes letters and digits like a key or a token (not a word or a path).</summary>
    internal static bool LooksRandom(string s)
    {
        int digits = s.Count(char.IsAsciiDigit), upper = s.Count(char.IsAsciiLetterUpper), lower = s.Count(char.IsAsciiLetterLower);
        if (digits < 3 || upper + lower < 6) return false;
        if (upper == 0 && digits < 6) return false;
        // Entropy per character: real words and paths stay well below random keys.
        var counts = s.GroupBy(c => c).Select(g => (double)g.Count() / s.Length);
        double entropy = -counts.Sum(p => p * Math.Log2(p));
        return entropy >= 3.6;
    }

    /// <summary>The box of characters [start, end) of the line: whole words in the middle, the first and last word trimmed when reading left to right.</summary>
    private static (double X, double Y, double W, double H)? Box(List<(int Start, int End, OcrWord Word)> spans, int start, int end, bool rtl)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var (s, e, w) in spans)
        {
            if (e <= start || s >= end) continue;
            double x0 = w.X, x1 = w.X + w.Width;
            int len = Math.Max(1, e - s);
            if (!rtl)
            {
                if (start > s) x0 = w.X + w.Width * (start - s) / len;
                if (end < e) x1 = w.X + w.Width * (end - s) / len;
            }
            left = Math.Min(left, x0);
            right = Math.Max(right, x1);
            top = Math.Min(top, w.Y);
            bottom = Math.Max(bottom, w.Y + w.Height);
        }
        if (left > right) return null;
        double h = bottom - top, pad = Math.Max(2, h * 0.18);
        return (left - pad, top - pad, right - left + 2 * pad, h + 2 * pad);
    }
}
