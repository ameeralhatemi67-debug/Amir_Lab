namespace Toolbox.Processing.Ocr;

internal enum WordDirection
{
    Neutral,
    LeftToRight,
    RightToLeft,
}

/// <summary>
/// Turns raw recogniser output (word boxes, possibly split into fragments, in visual order) into
/// lines in reading order. Windows OCR reports Arabic words left-to-right as they appear on the
/// page, so an Arabic line would otherwise read backwards.
/// </summary>
internal static class OcrLayout
{
    /// <summary>Joins fragments of one visual line, decides the page direction and orders every line's words for reading.</summary>
    public static OcrPage Normalize(OcrPage raw)
    {
        var lines = JoinFragments(raw.Lines);
        bool pageRtl = IsRightToLeftPage(lines.SelectMany(l => l.Words));
        var ordered = lines.Select(l => MakeLine(l.Words, l.Language, pageRtl)).ToList();
        return new OcrPage(ordered, raw.Width, raw.Height) { TextAngle = raw.TextAngle };
    }

    public static OcrLine MakeLine(IEnumerable<OcrWord> words, string language, bool pageRtl)
    {
        var logical = LogicalOrder(words, pageRtl, out bool rtl);
        return new OcrLine(string.Join(" ", logical.Select(w => w.Text)), logical, language) { RightToLeft = rtl };
    }

    public static bool IsRightToLeftPage(IEnumerable<OcrWord> words)
    {
        int arabic = 0, latin = 0;
        foreach (var w in words)
        {
            foreach (var ch in w.Text)
            {
                if (IsArabicLetter(ch)) arabic++;
                else if (char.IsAsciiLetter(ch)) latin++;
            }
        }
        return arabic > latin;
    }

    /// <summary>
    /// Word-level bidi: words are placed in visual order (by x), then an RTL line is reversed while
    /// runs of left-to-right words (Latin, numbers) keep their internal order; an LTR line keeps
    /// visual order and reverses runs of Arabic words.
    /// </summary>
    public static IReadOnlyList<OcrWord> LogicalOrder(IEnumerable<OcrWord> words, bool pageRtl, out bool lineRtl)
    {
        var visual = words.OrderBy(w => w.X + w.Width / 2).ToList();
        var dirs = visual.Select(w => Classify(w.Text)).ToList();
        bool anyRtl = dirs.Contains(WordDirection.RightToLeft), anyLtr = dirs.Contains(WordDirection.LeftToRight);
        lineRtl = anyRtl && (pageRtl || !anyLtr);
        if (!anyRtl) return visual;

        // Resolve neutral words: between two words of the same direction they take it, otherwise the line's.
        var line = lineRtl ? WordDirection.RightToLeft : WordDirection.LeftToRight;
        var resolved = dirs.ToArray();
        for (int i = 0; i < resolved.Length; i++)
        {
            if (resolved[i] != WordDirection.Neutral) continue;
            var before = Nearest(dirs, i, -1);
            var after = Nearest(dirs, i, +1);
            resolved[i] = before == after && before != WordDirection.Neutral ? before : line;
        }

        // Runs of the opposite direction keep their own order; the sequence of runs follows the line.
        var runs = new List<List<OcrWord>>();
        for (int i = 0; i < visual.Count; i++)
        {
            bool embedded = resolved[i] != line;
            if (embedded && i > 0 && resolved[i - 1] == resolved[i]) runs[^1].Add(visual[i]);
            else runs.Add([visual[i]]);
        }
        if (lineRtl) runs.Reverse();
        var result = new List<OcrWord>(visual.Count);
        foreach (var run in runs)
        {
            bool reverseRun = !lineRtl && run.Count > 1 && Classify(run[0].Text) == WordDirection.RightToLeft;
            if (reverseRun) run.Reverse();
            result.AddRange(run);
        }
        return result;
    }

    public static WordDirection Classify(string text)
    {
        bool ltr = false;
        foreach (var ch in text)
        {
            if (IsArabicLetter(ch)) return WordDirection.RightToLeft;
            if (char.IsAsciiLetter(ch) || char.IsDigit(ch) || (int)ch is >= 0x00C0 and <= 0x024F) ltr = true; // Latin, Latin-1, Latin Extended
        }
        return ltr ? WordDirection.LeftToRight : WordDirection.Neutral;
    }

    public static bool IsArabicLetter(char ch)
    {
        int c = ch;
        return (c is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F or >= 0x08A0 and <= 0x08FF or >= 0xFB50 and <= 0xFDFF or >= 0xFE70 and <= 0xFEFF)
               && char.IsLetter(ch);
    }

    /// <summary>
    /// Recognisers split a visual line at wide gaps (monospaced text, tabs) and list the pieces as
    /// separate lines, which scrambles reading order. Pieces on the same baseline whose gap is
    /// smaller than 1.5 line heights are joined; wider gaps are treated as columns and kept apart.
    /// </summary>
    public static List<OcrLine> JoinFragments(IReadOnlyList<OcrLine> lines)
    {
        var groups = new List<List<OcrLine>>();
        foreach (var line in lines)
        {
            if (line.Words.Count == 0) continue;
            var target = groups.FirstOrDefault(g => g.Any(other => SameVisualLine(other, line)));
            if (target is null) groups.Add([line]);
            else target.Add(line);
        }
        return groups.Select(g => g.Count == 1 ? g[0] : new OcrLine("", g.SelectMany(l => l.Words).ToList(), g[0].Language)).ToList();
    }

    private static bool SameVisualLine(OcrLine a, OcrLine b)
    {
        double ha = a.Bottom - a.Top, hb = b.Bottom - b.Top;
        double minH = Math.Max(1, Math.Min(ha, hb)), maxH = Math.Max(ha, hb);
        if (maxH > minH * 1.6) return false;
        double vertical = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (vertical < minH * 0.6) return false;
        double gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
        return gap < maxH * 1.5;
    }

    private static WordDirection Nearest(List<WordDirection> dirs, int from, int step)
    {
        for (int i = from + step; i >= 0 && i < dirs.Count; i += step)
            if (dirs[i] != WordDirection.Neutral) return dirs[i];
        return WordDirection.Neutral;
    }
}
