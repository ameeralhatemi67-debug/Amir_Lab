using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;
using Toolbox.Processing.Pdf;
using PdfiumDocument = Toolbox.Processing.Pdf.PdfDocument;

namespace Toolbox.Processing.Tests;

/// <summary>
/// The viewer's and editor's PDF engine on generated files: the dark copy, the PDF editor's Save
/// (order, turns, merging, added text, ink, rectangles and pictures, on plain and rotated pages),
/// picture positions, search, outline and links. Results are rendered with PDFium and checked pixel
/// by pixel at 72 DPI, where one pixel is one point of the displayed page.
/// </summary>
public sealed class PdfEditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amirlap-pdfedit-" + Guid.NewGuid().ToString("N"));

    public PdfEditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static byte[] Png(SKColor colour, int w = 40, int h = 40)
    {
        using var bitmap = new SKBitmap(w, h);
        bitmap.Erase(colour);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Page 1 (A4, 595×842): a black square at (50, 50, 100×100) from the top-left and a red picture at
    /// (300, 400, 120×120). Page 2: the same size but /Rotate 90, a blue square at the top-left of its
    /// unrotated page. Page 3: a green picture drawn inside a form XObject at (100, 600, 80×80).
    /// </summary>
    private string MakeSample()
    {
        var path = Path.Combine(_dir, "sample.pdf");
        using var pdf = new PdfSharp.Pdf.PdfDocument();
        var p1 = pdf.AddPage();
        p1.Width = XUnit.FromPoint(595);
        p1.Height = XUnit.FromPoint(842);
        using (var gfx = XGraphics.FromPdfPage(p1))
        {
            gfx.DrawRectangle(XBrushes.Black, 50, 50, 100, 100);
            using var red = XImage.FromStream(new MemoryStream(Png(SKColors.Red)));
            gfx.DrawImage(red, 300, 400, 120, 120);
        }
        var p2 = pdf.AddPage();
        p2.Width = XUnit.FromPoint(595);
        p2.Height = XUnit.FromPoint(842);
        using (var gfx = XGraphics.FromPdfPage(p2)) gfx.DrawRectangle(XBrushes.Blue, 0, 0, 100, 100);
        p2.Rotate = 90;
        var p3 = pdf.AddPage();
        p3.Width = XUnit.FromPoint(595);
        p3.Height = XUnit.FromPoint(842);
        var form = new XForm(pdf, XUnit.FromPoint(80), XUnit.FromPoint(80));
        using (var fg = XGraphics.FromForm(form))
        using (var green = XImage.FromStream(new MemoryStream(Png(SKColors.Lime))))
            fg.DrawImage(green, 0, 0, 80, 80);
        using (var gfx = XGraphics.FromPdfPage(p3)) gfx.DrawImage(form, 100, 600, 80, 80);
        pdf.Save(path);
        return path;
    }

    /// <summary>A text page made with Skia (real, embedded text), with an outline and links made with PDFsharp.</summary>
    private string MakeText()
    {
        var skia = Path.Combine(_dir, "text-skia.pdf");
        using (var stream = new SKFileWStream(skia))
        using (var doc = SKDocument.CreatePdf(stream))
        {
            using var typeface = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
            using var font = new SKFont(typeface, 20);
            using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
            for (int page = 1; page <= 2; page++)
            {
                var canvas = doc.BeginPage(595, 842);
                canvas.DrawText($"Hello Amir, page {page}.", 72, 100, SKTextAlign.Left, font, paint);
                canvas.DrawText("Second line: amir in lower case.", 72, 140, SKTextAlign.Left, font, paint);
                doc.EndPage();
            }
            doc.Close();
        }
        var path = Path.Combine(_dir, "text.pdf");
        using var pdf = PdfSharp.Pdf.IO.PdfReader.Open(skia, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify);
        pdf.Outlines.Add("Introduction", pdf.Pages[0]);
        pdf.Outlines.Add("Results", pdf.Pages[1]);
        // A link from the top-left 100×50 of page 1 to page 2 (PDFsharp rectangles are in user space here).
        pdf.Pages[0].AddDocumentLink(new PdfRectangle(new XRect(0, 792, 100, 50)), 2);
        pdf.Pages[0].AddWebLink(new PdfRectangle(new XRect(200, 792, 100, 50)), "https://example.com/amir");
        pdf.Save(path);
        return path;
    }

    private static (byte B, byte G, byte R) Pixel(byte[] bgra, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    private static byte[] Render(string path, int page, out int width, out int height)
    {
        using var pdf = PdfiumDocument.Open(path);
        var size = pdf.GetPageSize(page);
        width = (int)Math.Round(size.Width);
        height = (int)Math.Round(size.Height);
        return pdf.RenderBgra(page, width, height);
    }

    private static void AssertColour((byte B, byte G, byte R) p, byte r, byte g, byte b, string what, int tolerance = 40)
    {
        Assert.True(Math.Abs(p.R - r) <= tolerance && Math.Abs(p.G - g) <= tolerance && Math.Abs(p.B - b) <= tolerance,
            $"{what}: expected about ({r}, {g}, {b}), got ({p.R}, {p.G}, {p.B})");
    }

    [Fact]
    public void The_sample_renders_as_drawn()
    {
        var path = MakeSample();
        var px = Render(path, 1, out int w, out _);
        AssertColour(Pixel(px, w, 100, 100), 0, 0, 0, "black square");
        AssertColour(Pixel(px, w, 360, 460), 255, 0, 0, "red picture");
        AssertColour(Pixel(px, w, 500, 800), 255, 255, 255, "paper");
    }

    [Fact]
    public void Picture_boxes_are_found_also_inside_forms()
    {
        var path = MakeSample();
        using var pdf = PdfiumDocument.Open(path);
        var map1 = pdf.GetPageMap(1);
        var pictures = pdf.GetImageBoxes(1);
        Assert.Single(pictures);
        var (x, y, bw, bh) = map1.ToDisplay(pictures[0]);
        Assert.InRange(x, 298, 302);
        Assert.InRange(y, 398, 402);
        Assert.InRange(bw, 118, 122);
        Assert.InRange(bh, 118, 122);
        Assert.Empty(pdf.GetImageBoxes(2));
        var inForm = pdf.GetImageBoxes(3);
        Assert.Single(inForm);
        var (fx, fy, fw, fh) = pdf.GetPageMap(3).ToDisplay(inForm[0]);
        Assert.InRange(fx, 98, 102);
        Assert.InRange(fy, 598, 602);
        Assert.InRange(fw, 78, 82);
        Assert.InRange(fh, 78, 82);
    }

    [Fact]
    public void Page_maps_follow_rotation()
    {
        var path = MakeSample();
        using var pdf = PdfiumDocument.Open(path);
        Assert.Equal(1, pdf.GetRotation(2));
        var size = pdf.GetPageSize(2);
        Assert.Equal(842, size.Width, 0.5);  // turned: shown landscape
        Assert.Equal(595, size.Height, 0.5);
        var map = pdf.GetPageMap(2);
        // Round trip display → user → display, anywhere on the page.
        foreach (var (dx, dy) in new[] { (0.0, 0.0), (100.0, 50.0), (800.0, 590.0) })
        {
            var (ux, uy) = map.ToUser(dx, dy);
            var (bx, by) = map.ToDisplay(ux, uy);
            Assert.Equal(dx, bx, 3);
            Assert.Equal(dy, by, 3);
        }
        // The blue square (top-left of the unrotated page) shows at the top-right once turned clockwise.
        var px = Render(path, 2, out int w, out _);
        AssertColour(Pixel(px, w, 842 - 50, 50), 0, 0, 255, "blue square on the turned page");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Dark_copy_inverts_the_page_and_keeps_pictures_when_asked(bool keepPictures)
    {
        var path = MakeSample();
        var dark = Path.Combine(_dir, $"dark-{keepPictures}.pdf");
        PdfDarkCopy.Make(path, dark, keepPictures);
        var px = Render(dark, 1, out int w, out _);
        AssertColour(Pixel(px, w, 500, 800), 0, 0, 0, "paper turns black");
        AssertColour(Pixel(px, w, 100, 100), 255, 255, 255, "black square turns white");
        if (keepPictures) AssertColour(Pixel(px, w, 360, 460), 255, 0, 0, "picture kept");
        else AssertColour(Pixel(px, w, 360, 460), 0, 255, 255, "picture inverted");
        // The picture inside a form (page 3) is found and kept too.
        var px3 = Render(dark, 3, out int w3, out _);
        if (keepPictures) AssertColour(Pixel(px3, w3, 140, 640), 0, 255, 0, "picture in a form kept");
        AssertColour(Pixel(px3, w3, 400, 200), 0, 0, 0, "paper of page 3");
        // The text layer survives: a dark copy of a text PDF can still be searched.
        var text = MakeText();
        var darkText = Path.Combine(_dir, $"dark-text-{keepPictures}.pdf");
        PdfDarkCopy.Make(text, darkText, keepPictures);
        using var reread = PdfiumDocument.Open(darkText);
        Assert.Contains("Hello Amir", reread.GetText(1));
    }

    [Fact]
    public void Editor_reorders_turns_merges_and_draws_where_it_was_drawn()
    {
        var path = MakeSample();
        var other = MakeText();
        var stamp = Png(SKColors.Magenta, 20, 20);
        var plan = new PdfEditPlan([path, other],
        [
            // 1: the rotated page, with a green square at its displayed (100, 100).
            new PdfPagePlan(0, 2, 0, [new PdfRectMark(100, 100, 60, 60, 0xFF00C000)]),
            // 2: page 1, turned once more, with ink, white cover over the black square, a picture and text.
            new PdfPagePlan(0, 1, 1,
            [
                new PdfRectMark(40, 40, 120, 120, 0xFFFFFFFF),
                new PdfInkMark([[new PdfPoint(200, 700), new PdfPoint(260, 700), new PdfPoint(320, 700)]], 0xFF0000FF, 12),
                new PdfImageMark(450, 100, 80, 80, stamp),
                new PdfTextMark(72, 300, 400, "Signed by Amir", 24, 0xFF000000),
            ]),
            // 3: a page from the second file.
            new PdfPagePlan(1, 2, 0, []),
        ]);
        var output = Path.Combine(_dir, "edited.pdf");
        PdfEditor.Save(plan, output);

        using var result = PdfiumDocument.Open(output);
        Assert.Equal(3, result.PageCount);
        Assert.Equal(1, result.GetRotation(1));
        Assert.Equal(1, result.GetRotation(2));
        Assert.Contains("page 2", result.GetText(3));
        Assert.Contains("Signed by Amir", result.GetText(2));

        // Page 1 is shown as it was edited: the green square at display (130, 130).
        var px1 = Render(output, 1, out int w1, out _);
        AssertColour(Pixel(px1, w1, 130, 130), 0, 192, 0, "green square on the rotated page");

        // Page 2 was turned after editing: display (x, y) of the edit is now at (H − y, x).
        var px2 = Render(output, 2, out int w2, out _);
        (int X, int Y) Turned(int x, int y) => (842 - y, x);
        var cover = Turned(100, 100);
        AssertColour(Pixel(px2, w2, cover.X, cover.Y), 255, 255, 255, "white cover over the black square");
        var ink = Turned(260, 700);
        AssertColour(Pixel(px2, w2, ink.X, ink.Y), 0, 0, 255, "ink line");
        var pic = Turned(490, 140);
        AssertColour(Pixel(px2, w2, pic.X, pic.Y), 255, 0, 255, "stamp picture");
        var redPicture = Turned(360, 460);
        AssertColour(Pixel(px2, w2, redPicture.X, redPicture.Y), 255, 0, 0, "original picture still there");
    }

    [Fact]
    public void Editor_never_overwrites_and_needs_a_page()
    {
        var path = MakeSample();
        Assert.Throws<Toolbox.Core.ProcessingException>(() => PdfEditor.Save(new PdfEditPlan([path], []), Path.Combine(_dir, "none.pdf")));
        Assert.Throws<IOException>(() => PdfEditor.Save(new PdfEditPlan([path], [new PdfPagePlan(0, 1, 0, [])]), path));
    }

    [Fact]
    public void Arabic_text_is_written_as_text()
    {
        var path = MakeSample();
        var output = Path.Combine(_dir, "arabic.pdf");
        PdfEditor.Save(new PdfEditPlan([path], [new PdfPagePlan(0, 1, 0, [new PdfTextMark(72, 600, 450, "توقيع أمير", 28, 0xFF000000)])]), output);
        using var result = PdfiumDocument.Open(output);
        var text = result.GetText(1);
        Assert.Contains("أمير", text);
        Assert.True(PdfEditor.IsRightToLeft("توقيع"));
        Assert.False(PdfEditor.IsRightToLeft("Amir توقيع"));
    }

    [Fact]
    public void Search_finds_text_with_and_without_case()
    {
        using var pdf = PdfiumDocument.Open(MakeText());
        var any = pdf.Find("amir", matchCase: false);
        Assert.Equal(4, any.Count); // "Amir" and "amir" on each of two pages
        var exact = pdf.Find("amir", matchCase: true);
        Assert.Equal(2, exact.Count);
        Assert.All(exact, h => Assert.NotEmpty(h.Boxes));
        var first = pdf.Find("Hello", matchCase: true, onlyPage: 2);
        Assert.Single(first);
        var (x, y, _, _) = pdf.GetPageMap(2).ToDisplay(first[0].Boxes[0]);
        Assert.InRange(x, 68, 80);   // drawn at x 72
        Assert.InRange(y, 75, 102);  // baseline at 100, 20 pt type
        Assert.Empty(pdf.Find("nowhere", false));
    }

    [Fact]
    public void Outline_and_links_lead_to_their_pages()
    {
        using var pdf = PdfiumDocument.Open(MakeText());
        var outline = pdf.GetOutline();
        Assert.Equal(["Introduction", "Results"], outline.Select(o => o.Title));
        Assert.Equal([1, 2], outline.Select(o => o.Page ?? 0));
        var map = pdf.GetPageMap(1);
        var (ux, uy) = map.ToUser(50, 25);
        var link = pdf.GetLinkAt(1, ux, uy);
        Assert.NotNull(link);
        Assert.Equal(2, link!.Page);
        var (wx, wy) = map.ToUser(250, 25);
        Assert.Equal("https://example.com/amir", pdf.GetLinkAt(1, wx, wy)?.Uri);
        var (nx, ny) = map.ToUser(400, 400);
        Assert.Null(pdf.GetLinkAt(1, nx, ny));
    }
}
