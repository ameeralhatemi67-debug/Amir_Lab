using NetVips;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Processing.Imaging;

namespace Toolbox.Processing.Ocr;

/// <summary>How to combine recognisers when a page may contain both Arabic and English.</summary>
public enum MixedStrategy
{
    /// <summary>Arabic recogniser only (it also reads Latin letters and digits).</summary>
    ArabicOnly,

    /// <summary>English recogniser only.</summary>
    EnglishOnly,

    /// <summary>Run both and keep, line by line, the recogniser that matches the line's script.</summary>
    PerLine,

    /// <summary>
    /// As <see cref="PerLine"/>, and inside Arabic lines replace Latin/number runs by the English
    /// reading of the same area when it covers the run completely.
    /// </summary>
    PerWord,
}

/// <summary>
/// Local text recognition for images and rendered PDF pages: prepares the picture (greyscale,
/// upscaling small text, size limits), runs the platform engine and maps results back.
/// </summary>
public static class OcrService
{
    /// <summary>Registered by the platform adapter (Windows: Windows.Media.Ocr).</summary>
    public static IOcrEngine? Engine { get; private set; }

    /// <summary>Strategy used for "ara+eng"; chosen from the evaluation in docs/OCR_EVALUATION.md.</summary>
    public static MixedStrategy Mixed { get; set; } = MixedStrategy.PerWord;

    /// <summary>
    /// Pictures whose long edge is shorter than this are enlarged (at most ×4) before recognition:
    /// small text recognises poorly. 0 disables it. Chosen in docs/OCR_EVALUATION.md.
    /// </summary>
    public static int UpscaleTarget { get; set; } = 1600;

    /// <summary>One recogniser's raw output, mapped to source pixels (for diagnostics and the evaluation tool).</summary>
    public static OcrPage RecognizeRaw(Image image, string languageTag)
    {
        var engine = Engine ?? throw new ProcessingException("Text recognition is not available on this computer.");
        using var prepared = Prepare(image, engine.MaxDimension, out double scale);
        return Rescale(engine.Recognize(ToBgra(prepared), prepared.Width, prepared.Height, languageTag), 1 / scale, image.Width, image.Height);
    }

    public static void Register(IOcrEngine engine)
    {
        Engine = engine;
        bool arabic = FindTag(engine, "ar") is not null, english = FindTag(engine, "en") is not null;
        var detail = (arabic, english) switch
        {
            (true, true) => "Arabic and English (Windows built-in OCR).",
            (false, true) => "English only. For Arabic, add Windows' Arabic language with \"Optical character recognition\" (Settings > Time & language > Language & region).",
            (true, false) => "Arabic only. For English, add English with \"Optical character recognition\" in Windows language settings.",
            _ => "No OCR languages are installed in Windows (Settings > Time & language > Language & region > a language with \"Optical character recognition\").",
        };
        RuntimeFeatures.Set(RuntimeFeatures.Ocr, arabic || english, detail);
    }

    /// <summary>Names of requested languages ("ara", "eng") that Windows cannot recognise here.</summary>
    public static IReadOnlyList<string> MissingLanguages(string languages)
    {
        if (Engine is null) return [];
        var missing = new List<string>();
        foreach (var code in languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (code == "ara" && FindTag(Engine, "ar") is null) missing.Add("Arabic");
            if (code == "eng" && FindTag(Engine, "en") is null) missing.Add("English");
        }
        return missing;
    }

    public static IReadOnlyList<string> AvailableLanguageCodes(IOcrEngine engine)
    {
        var codes = new List<string>();
        if (FindTag(engine, "ar") is not null) codes.Add("ara");
        if (FindTag(engine, "en") is not null) codes.Add("eng");
        return codes;
    }

    /// <summary>Recognises text. <paramref name="languages"/> is "ara+eng", "ara" or "eng".</summary>
    public static OcrPage Recognize(Image image, string languages, MixedStrategy? strategy = null)
    {
        var engine = Engine ?? throw new ProcessingException("Text recognition is not available on this computer.");
        var wanted = languages.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arTag = wanted.Contains("ara") ? FindTag(engine, "ar") : null;
        var enTag = wanted.Contains("eng") ? FindTag(engine, "en") : null;
        if (arTag is null && enTag is null)
            throw new ProcessingException($"The requested OCR language ({languages}) is not installed in Windows. {RuntimeFeatures.Describe(RuntimeFeatures.Ocr)}");

        using var prepared = Prepare(image, engine.MaxDimension, out double scale);
        var bgra = ToBgra(prepared);

        OcrPage Run(string tag) => Rescale(engine.Recognize(bgra, prepared.Width, prepared.Height, tag), 1 / scale, image.Width, image.Height);

        if (arTag is null) return OcrLayout.Normalize(Run(enTag!));
        if (enTag is null) return OcrLayout.Normalize(Run(arTag));
        return (strategy ?? Mixed) switch
        {
            MixedStrategy.ArabicOnly => OcrLayout.Normalize(Run(arTag)),
            MixedStrategy.EnglishOnly => OcrLayout.Normalize(Run(enTag)),
            MixedStrategy.PerWord => Merge(Run(arTag), Run(enTag), replaceWords: true),
            _ => Merge(Run(arTag), Run(enTag), replaceWords: false),
        };
    }

    /// <summary>
    /// Combines both recognisers: Latin-script lines come from the English result (the Arabic model
    /// misreads some Latin letters and punctuation). With <paramref name="replaceWords"/>, runs of
    /// Latin words and Western numbers inside Arabic lines are also taken from the English reading.
    /// </summary>
    internal static OcrPage Merge(OcrPage arabic, OcrPage english, bool replaceWords)
    {
        var arLines = OcrLayout.JoinFragments(arabic.Lines);
        var enLines = OcrLayout.JoinFragments(english.Lines);
        var merged = new List<(IReadOnlyList<OcrWord> Words, string Language)>();
        var usedEnglish = new HashSet<OcrLine>();
        foreach (var a in arLines)
        {
            var match = enLines.Where(e => !usedEnglish.Contains(e) && Overlap(a, e) > 0.5).OrderByDescending(e => Overlap(a, e)).FirstOrDefault();
            if (match is not null) usedEnglish.Add(match);
            if (match is null) merged.Add((a.Words, a.Language));
            else if (ArabicRatio(string.Concat(a.Words.Select(w => w.Text))) < 0.3) merged.Add((match.Words, match.Language));
            else merged.Add((replaceWords ? ReplaceLatinRuns(a.Words, match.Words) : a.Words, a.Language));
        }
        // Lines only the English recogniser found are kept too, inserted by vertical position;
        // otherwise the engine's reading order is preserved.
        foreach (var extra in enLines.Where(e => !usedEnglish.Contains(e) && !arLines.Any(a => Overlap(a, e) > 0.2)))
        {
            int at = merged.FindIndex(l => l.Words.Min(w => w.Y) > extra.Top);
            merged.Insert(at < 0 ? merged.Count : at, (extra.Words, extra.Language));
        }

        bool pageRtl = OcrLayout.IsRightToLeftPage(merged.SelectMany(l => l.Words));
        var lines = merged.Select(l => OcrLayout.MakeLine(l.Words, l.Language, pageRtl)).ToList();
        return new OcrPage(lines, arabic.Width, arabic.Height) { TextAngle = arabic.TextAngle };
    }

    private static IReadOnlyList<OcrWord> ReplaceLatinRuns(IReadOnlyList<OcrWord> arabicWords, IReadOnlyList<OcrWord> englishWords)
    {
        var visual = arabicWords.OrderBy(w => w.X).ToList();
        var result = new List<OcrWord>();
        int i = 0;
        while (i < visual.Count)
        {
            if (!IsLatinWord(visual[i].Text))
            {
                result.Add(visual[i++]);
                continue;
            }
            int start = i;
            while (i < visual.Count && IsLatinWord(visual[i].Text)) i++;
            var run = visual.GetRange(start, i - start);
            double height = run.Max(w => w.Height), left = run.Min(w => w.X) - height * 0.3, right = run.Max(w => w.X + w.Width) + height * 0.3;
            var replacement = englishWords.Where(e => IsLatinWord(e.Text) && e.X + e.Width / 2 >= left && e.X + e.Width / 2 <= right).ToList();
            result.AddRange(Covers(replacement, run, height) ? replacement : run);
        }
        return result;
    }

    /// <summary>
    /// The English words span the same extent as the Arabic model's run (nothing cut off or merged
    /// into a neighbour) without splitting it into more words: next to Arabic script the English
    /// model tends to break codes such as "AB1234" into "ABI 234".
    /// </summary>
    private static bool Covers(List<OcrWord> replacement, List<OcrWord> run, double height)
    {
        if (replacement.Count == 0 || replacement.Count > run.Count) return false;
        double tolerance = height * 0.5;
        return Math.Abs(replacement.Min(w => w.X) - run.Min(w => w.X)) <= tolerance
               && Math.Abs(replacement.Max(w => w.X + w.Width) - run.Max(w => w.X + w.Width)) <= tolerance
               && replacement.Sum(w => w.Text.Length) >= run.Sum(w => w.Text.Length) * 0.8;
    }

    /// <summary>Latin letters or Western digits and nothing from the Arabic blocks (Arabic-Indic digits stay with the Arabic model).</summary>
    private static bool IsLatinWord(string text) =>
        text.Any(ch => char.IsAsciiLetter(ch) || char.IsAsciiDigit(ch)) && !text.Any(ch => (int)ch is >= 0x0600 and <= 0x06FF);

    public static double ArabicRatio(string text)
    {
        int letters = 0, arabic = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            int c = ch;
            if (c is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF) arabic++;
        }
        return letters == 0 ? 0 : (double)arabic / letters;
    }

    private static double Overlap(OcrLine a, OcrLine b)
    {
        double vertical = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        double horizontal = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        if (vertical <= 0 || horizontal <= 0) return 0;
        double minHeight = Math.Max(1, Math.Min(a.Bottom - a.Top, b.Bottom - b.Top));
        return vertical / minHeight;
    }

    /// <summary>Greyscale, enlarged when the picture is small, within the engine's size limit.</summary>
    private static Image Prepare(Image image, int maxDimension, out double scale)
    {
        using var flat = image.HasAlpha() ? image.Flatten(background: [255, 255, 255]) : image.Copy();
        using var gray = flat.Colourspace(Enums.Interpretation.Bw);
        int longEdge = Math.Max(flat.Width, flat.Height);
        scale = UpscaleTarget > 0 && longEdge < UpscaleTarget ? Math.Min(4.0, (double)UpscaleTarget / longEdge) : 1.0;
        double limit = (double)maxDimension / Math.Max(flat.Width, flat.Height);
        scale = Math.Min(scale, limit);
        return Math.Abs(scale - 1) < 0.001 ? gray.Copy() : gray.Resize(scale, kernel: Enums.Kernel.Lanczos3);
    }

    private static byte[] ToBgra(Image gray)
    {
        using var u8 = gray.Cast(Enums.BandFormat.Uchar);
        var g = u8.WriteToMemory<byte>();
        var bgra = new byte[g.Length * 4];
        for (int i = 0, j = 0; i < g.Length; i++, j += 4)
        {
            bgra[j] = bgra[j + 1] = bgra[j + 2] = g[i];
            bgra[j + 3] = 255;
        }
        return bgra;
    }

    private static OcrPage Rescale(OcrPage page, double factor, int width, int height)
    {
        if (Math.Abs(factor - 1) < 0.001) return page with { Width = width, Height = height };
        var lines = page.Lines.Select(l => l with
        {
            Words = l.Words.Select(w => w with { X = w.X * factor, Y = w.Y * factor, Width = w.Width * factor, Height = w.Height * factor }).ToList(),
        }).ToList();
        return new OcrPage(lines, width, height) { TextAngle = page.TextAngle };
    }

    private static string? FindTag(IOcrEngine engine, string prefix) =>
        engine.Languages.FirstOrDefault(l => l.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase) || l.Equals(prefix, StringComparison.OrdinalIgnoreCase));
}
