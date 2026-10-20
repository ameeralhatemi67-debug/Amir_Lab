using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Toolbox.Processing.Ocr;

/// <summary>Affine map from pixels of a recognised page image (origin top-left) to PDF user space.</summary>
public readonly record struct PageTransform(double A, double B, double C, double D, double E, double F)
{
    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>An image drawn over a whole page of <paramref name="pageHeight"/> points at <paramref name="pointsPerPixel"/>.</summary>
    public static PageTransform ForImagePage(double pointsPerPixel, double pageHeight) =>
        new(pointsPerPixel, 0, 0, -pointsPerPixel, 0, pageHeight);

    /// <summary>From the user-space positions of the image's top-left, top-right and bottom-left corners.</summary>
    public static PageTransform FromCorners((double X, double Y) topLeft, (double X, double Y) topRight, (double X, double Y) bottomLeft, int width, int height) =>
        new((topRight.X - topLeft.X) / width, (topRight.Y - topLeft.Y) / width,
            (bottomLeft.X - topLeft.X) / height, (bottomLeft.Y - topLeft.Y) / height,
            topLeft.X, topLeft.Y);
}

/// <summary>
/// Writes an invisible, searchable text layer onto PDF pages. Like Tesseract's PDF output, the
/// text uses a "glyphless" font: Identity-H encoding with each UTF-16 code unit as its own
/// character id and an identity ToUnicode map, so any Arabic or Latin text can be searched and
/// copied without embedding a real font. Text render mode 3 keeps it invisible; each word is
/// stretched over the box where it was recognised so selections line up with the picture.
/// </summary>
public static class SearchablePdf
{
    public const string FontName = "GlyphLessFont";
    private const string ResourceName = "/FtbOcr";
    private const double Advance = 0.5; // every character is half an em wide (DW 500)

    /// <summary>Creates the font objects once per document.</summary>
    public static PdfDictionary CreateFont(PdfSharp.Pdf.PdfDocument document)
    {
        var toUnicode = new PdfDictionary(document);
        toUnicode.CreateStream(Encoding.ASCII.GetBytes(IdentityToUnicode()));
        document.Internals.AddObject(toUnicode);

        var descriptor = new PdfDictionary(document);
        descriptor.Elements.SetName("/Type", "/FontDescriptor");
        descriptor.Elements.SetName("/FontName", "/" + FontName);
        descriptor.Elements.SetInteger("/Flags", 5);
        descriptor.Elements["/FontBBox"] = new PdfArray(document, new PdfInteger(0), new PdfInteger(0), new PdfInteger(500), new PdfInteger(1000));
        descriptor.Elements.SetInteger("/ItalicAngle", 0);
        descriptor.Elements.SetInteger("/Ascent", 1000);
        descriptor.Elements.SetInteger("/Descent", 0);
        descriptor.Elements.SetInteger("/CapHeight", 1000);
        descriptor.Elements.SetInteger("/StemV", 80);
        document.Internals.AddObject(descriptor);

        var systemInfo = new PdfDictionary(document);
        systemInfo.Elements.SetString("/Registry", "Adobe");
        systemInfo.Elements.SetString("/Ordering", "Identity");
        systemInfo.Elements.SetInteger("/Supplement", 0);

        var cidFont = new PdfDictionary(document);
        cidFont.Elements.SetName("/Type", "/Font");
        cidFont.Elements.SetName("/Subtype", "/CIDFontType2");
        cidFont.Elements.SetName("/BaseFont", "/" + FontName);
        cidFont.Elements["/CIDSystemInfo"] = systemInfo;
        cidFont.Elements.SetReference("/FontDescriptor", descriptor);
        cidFont.Elements.SetInteger("/DW", (int)(Advance * 1000));
        cidFont.Elements.SetName("/CIDToGIDMap", "/Identity");
        document.Internals.AddObject(cidFont);

        var font = new PdfDictionary(document);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type0");
        font.Elements.SetName("/BaseFont", "/" + FontName);
        font.Elements.SetName("/Encoding", "/Identity-H");
        font.Elements["/DescendantFonts"] = new PdfArray(document, cidFont.Reference!);
        font.Elements.SetReference("/ToUnicode", toUnicode);
        document.Internals.AddObject(font);
        return font;
    }

    /// <summary>Adds the recognised words of <paramref name="ocr"/> to <paramref name="page"/>. Returns the number of words written.</summary>
    public static int AddTextLayer(PdfPage page, PdfDictionary font, OcrPage ocr, PageTransform map)
    {
        var resources = page.Resources;
        var fonts = resources.Elements.GetDictionary("/Font");
        if (fonts is null)
        {
            fonts = new PdfDictionary(page.Owner);
            resources.Elements["/Font"] = fonts;
        }
        fonts.Elements.SetReference(ResourceName, font);

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("Q\n");
        int written = 0;
        foreach (var line in ocr.Lines)
        {
            // Visual order, left to right, as PDF producers write text and as PDF readers
            // expect it: they turn right-to-left runs back into reading order when extracting.
            foreach (var word in line.Words.OrderBy(w => w.X))
            {
                var text = VisualText(word.Text);
                if (text.Length == 0 || word.Width <= 0 || word.Height <= 0) continue;
                var p0 = map.Apply(word.X, word.Y + word.Height);           // bottom-left: text origin
                var p1 = map.Apply(word.X + word.Width, word.Y + word.Height); // bottom-right
                var pt = map.Apply(word.X, word.Y);                          // top-left: one em up
                double units = text.Length * Advance;
                sb.Append("BT\n").Append(ResourceName).Append(" 1 Tf 3 Tr\n");
                sb.Append(string.Format(inv, "{0:0.####} {1:0.####} {2:0.####} {3:0.####} {4:0.###} {5:0.###} Tm\n",
                    (p1.X - p0.X) / units, (p1.Y - p0.Y) / units, pt.X - p0.X, pt.Y - p0.Y, p0.X, p0.Y));
                sb.Append('<');
                foreach (var ch in text) sb.Append(((int)ch).ToString("X4", inv));
                sb.Append("> Tj\nET\n");
                written++;
            }
        }

        // Wrap the existing content in q ... Q so its graphics state cannot leak into the text layer.
        SetStream(page.Contents.PrependContent(), "q\n");
        SetStream(page.Contents.AppendContent(), sb.ToString());
        return written;
    }

    /// <summary>
    /// Characters in left-to-right visual order. A word with Arabic letters is reversed; digits and
    /// Latin letters inside it keep their order (they read left to right inside right-to-left text).
    /// </summary>
    internal static string VisualText(string word)
    {
        if (!word.Any(OcrLayout.IsArabicLetter)) return word;
        var runs = new List<string>();
        var current = new StringBuilder();
        bool? ltr = null;
        foreach (var ch in word)
        {
            bool isLtr = char.IsAsciiLetterOrDigit(ch) || char.IsDigit(ch);
            if (ltr is not null && isLtr != ltr && current.Length > 0)
            {
                runs.Add(ltr.Value ? current.ToString() : Reverse(current.ToString()));
                current.Clear();
            }
            ltr = isLtr;
            current.Append(ch);
        }
        if (current.Length > 0) runs.Add(ltr == true ? current.ToString() : Reverse(current.ToString()));
        runs.Reverse();
        return string.Concat(runs);
    }

    private static string Reverse(string s)
    {
        var chars = s.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    private static void SetStream(PdfContent content, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        if (content.Stream is null) content.CreateStream(bytes);
        else content.Stream.Value = bytes;
    }

    private static string IdentityToUnicode()
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
        sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        // 256 ranges, each varying only the last byte (as the CMap format requires), at most 100 per block.
        for (int block = 0; block < 256; block += 100)
        {
            int count = Math.Min(100, 256 - block);
            sb.Append(count).Append(" beginbfrange\n");
            for (int hi = block; hi < block + count; hi++)
                sb.Append(CultureInfo.InvariantCulture, $"<{hi:X2}00> <{hi:X2}FF> <{hi:X2}00>\n");
            sb.Append("endbfrange\n");
        }
        sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        return sb.ToString();
    }
}
