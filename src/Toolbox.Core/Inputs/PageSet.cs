using System.Globalization;

namespace Toolbox.Core.Inputs;

/// <summary>Parses page selections such as "all", "3" or "1,3,5-7" (1-based) against a page count.</summary>
public static class PageSet
{
    public static IReadOnlyList<int> Parse(string? text, int pageCount)
    {
        if (pageCount <= 0) throw new ProcessingException("The document has no pages.");
        var spec = (text ?? "all").Trim();
        if (spec.Length == 0 || spec.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Enumerable.Range(1, pageCount).ToList();

        var pages = new SortedSet<int>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = raw.IndexOf('-');
            if (dash > 0)
            {
                int from = ParseNumber(raw[..dash]);
                var toText = raw[(dash + 1)..].Trim();
                int to = toText.Length == 0 || toText.Equals("end", StringComparison.OrdinalIgnoreCase) ? pageCount : ParseNumber(toText);
                if (from > to) (from, to) = (to, from);
                Check(from, pageCount);
                Check(to, pageCount);
                for (int p = from; p <= to; p++) pages.Add(p);
            }
            else
            {
                int p = ParseNumber(raw);
                Check(p, pageCount);
                pages.Add(p);
            }
        }
        if (pages.Count == 0) throw new ProcessingException("No pages were selected.");
        return pages.ToList();
    }

    /// <summary>Compact description such as "pages 1–3, 7" for messages.</summary>
    public static string Describe(IReadOnlyList<int> pages, int pageCount)
    {
        if (pages.Count == pageCount) return pageCount == 1 ? "the page" : $"all {pageCount} pages";
        var parts = new List<string>();
        for (int i = 0; i < pages.Count; i++)
        {
            int start = pages[i];
            while (i + 1 < pages.Count && pages[i + 1] == pages[i] + 1) i++;
            parts.Add(start == pages[i] ? $"{start}" : $"{start}–{pages[i]}");
        }
        return (pages.Count == 1 ? "page " : "pages ") + string.Join(", ", parts);
    }

    private static int ParseNumber(string s)
    {
        if (!int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            throw new ProcessingException($"\"{s.Trim()}\" is not a page number.");
        return n;
    }

    private static void Check(int page, int pageCount)
    {
        if (page < 1 || page > pageCount)
            throw new ProcessingException(pageCount == 1
                ? $"Page {page} does not exist; the document has 1 page."
                : $"Page {page} does not exist; the document has {pageCount} pages.");
    }
}
