using NetVips;
using Toolbox.Core.Actions;
using Toolbox.Core.Jobs;
using Toolbox.Processing.Ocr;
using Toolbox.Processing.Pdf;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

namespace Toolbox.Processing.Tests;

/// <summary>
/// A stand-in recogniser that answers like Windows OCR does: words with boxes, listed left to
/// right as they appear on the page (so Arabic lines come out in visual, not reading, order).
/// Boxes are given as fractions of the page and scaled to whatever size is recognised.
/// </summary>
internal sealed class FakeOcrEngine : IOcrEngine
{
    private readonly Dictionary<string, List<List<(string Text, double X, double Y, double W, double H)>>> _byLanguage = new();

    public IReadOnlyList<string> Languages => ["ar-SA", "en-US"];

    public int MaxDimension => 10000;

    public int Calls { get; private set; }

    /// <summary>Adds a line for one recogniser; words in left-to-right visual order with fractional boxes.</summary>
    public FakeOcrEngine Line(string language, params (string Text, double X, double Y, double W, double H)[] words)
    {
        if (!_byLanguage.TryGetValue(language, out var lines)) _byLanguage[language] = lines = [];
        lines.Add([.. words]);
        return this;
    }

    public OcrPage Recognize(byte[] bgra, int width, int height, string languageTag)
    {
        Calls++;
        var lines = _byLanguage.GetValueOrDefault(languageTag) ?? [];
        return new OcrPage(lines.Select(l => new OcrLine(string.Join(" ", l.Select(w => w.Text)),
            l.Select(w => new OcrWord(w.Text, w.X * width, w.Y * height, w.W * width, w.H * height)).ToList(), languageTag)).ToList(), width, height);
    }
}

[Collection(OcrCollection.Name)]
public sealed class OcrTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    // "يرجى مراجعة الفاتورة" read by the Arabic model: visual order is the reverse of reading order.
    private static FakeOcrEngine ArabicAndEnglishPage() => new FakeOcrEngine()
        .Line("ar-SA", ("الفاتورة", 0.20, 0.10, 0.20, 0.05), ("مراجعة", 0.45, 0.10, 0.18, 0.05), ("يرجى", 0.68, 0.10, 0.12, 0.05))
        .Line("ar-SA", ("lnvoice", 0.10, 0.30, 0.20, 0.05), ("total", 0.32, 0.30, 0.14, 0.05))
        .Line("en-US", ("Invoice", 0.10, 0.30, 0.20, 0.05), ("total", 0.32, 0.30, 0.14, 0.05))
        .Line("en-US", ("Jl-3", 0.20, 0.10, 0.20, 0.05), ("ab", 0.68, 0.10, 0.12, 0.05));

    [Fact]
    public void Arabic_words_are_put_in_reading_order()
    {
        var words = new[]
        {
            new OcrWord("الفاتورة", 10, 0, 50, 20), new OcrWord("مراجعة", 70, 0, 40, 20), new OcrWord("يرجى", 120, 0, 30, 20),
        };
        var line = OcrLayout.MakeLine(words, "ar-SA", pageRtl: true);
        Assert.Equal("يرجى مراجعة الفاتورة", line.Text);
        Assert.True(line.RightToLeft);
    }

    [Fact]
    public void Latin_runs_inside_Arabic_keep_their_own_order()
    {
        // Visual: [العام] [2026] [Windows 11] [نظام]  →  reading: نظام Windows 11 2026 العام
        var words = new[]
        {
            new OcrWord("العام", 0, 0, 40, 20), new OcrWord("Windows", 50, 0, 60, 20), new OcrWord("11", 115, 0, 15, 20),
            new OcrWord("نظام", 140, 0, 40, 20),
        };
        var line = OcrLayout.MakeLine(words, "ar-SA", pageRtl: true);
        Assert.Equal("نظام Windows 11 العام", line.Text);
    }

    [Fact]
    public void English_lines_are_unchanged_and_fragments_on_one_baseline_are_joined()
    {
        var a = new OcrLine("The meeting", [new OcrWord("The", 0, 100, 30, 20), new OcrWord("meeting", 35, 100, 70, 20)], "en-US");
        var column = new OcrLine("Other column", [new OcrWord("Other", 400, 100, 50, 20), new OcrWord("column", 455, 100, 60, 20)], "en-US");
        var b = new OcrLine("moved to Tuesday", [new OcrWord("moved", 115, 101, 55, 20), new OcrWord("to", 175, 101, 20, 20), new OcrWord("Tuesday", 200, 101, 70, 20)], "en-US");
        var page = OcrLayout.Normalize(new OcrPage([a, column, b], 600, 300));
        Assert.Equal(["The meeting moved to Tuesday", "Other column"], page.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Mixed_merge_takes_latin_lines_and_runs_from_the_english_model()
    {
        var fake = ArabicAndEnglishPage();
        OcrService.Register(fake);
        using var image = Image.Black(1000, 1400).Linear([1.0], [255.0]).Cast(Enums.BandFormat.Uchar);
        var page = OcrService.Recognize(image, "ara+eng");
        Assert.Equal(["يرجى مراجعة الفاتورة", "Invoice total"], page.Lines.Select(l => l.Text));
        Assert.True(page.Lines[0].RightToLeft);
        Assert.False(page.Lines[1].RightToLeft);
        // Boxes are mapped back to the source picture even though it was enlarged for recognition.
        Assert.InRange(page.Lines[1].Words[0].X, 99, 101);
    }

    [Fact]
    public void Visual_text_reverses_arabic_but_keeps_numbers()
    {
        Assert.Equal("abc", SearchablePdf.VisualText("abc"));
        Assert.Equal("ىجري", SearchablePdf.VisualText("يرجى"));
        Assert.Equal("2026مقر", SearchablePdf.VisualText("رقم2026"));
    }

    [Fact]
    public async Task Searchable_pdf_from_an_image_has_a_text_layer_in_reading_order()
    {
        OcrService.Register(ArabicAndEnglishPage());
        var png = _ws.Png("scan.png", 1000, 1400);
        var job = await _ws.Run(Ids.OcrSearchablePdf, png);
        Assert.Equal(JobItemState.Completed, job.Items[0].State);
        var output = Assert.Single(job.Items[0].Outputs);
        Assert.EndsWith("scan Searchable.pdf", output.Path);

        using var pdf = PdfDocument.Open(output.Path);
        Assert.Equal(1, pdf.PageCount);
        var text = pdf.GetText(1);
        Assert.Contains("يرجى مراجعة الفاتورة", text);
        Assert.Contains("Invoice total", text);
        Assert.True(text.IndexOf("يرجى", StringComparison.Ordinal) < text.IndexOf("Invoice", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Searchable_pdf_keeps_original_pages_and_skips_pages_that_already_have_text()
    {
        var fake = new FakeOcrEngine().Line("en-US", ("Scanned", 0.1, 0.1, 0.3, 0.05), ("words", 0.45, 0.1, 0.2, 0.05));
        OcrService.Register(fake);
        // Page 1: a picture only (like a scan). Page 2: real text.
        var picture = _ws.Png("page.png", 850, 1100);
        var scanJob = await _ws.Run(Ids.ImagesToPdf, picture);
        var scanPdf = scanJob.Items[0].Outputs[0].Path;
        var textPdf = _ws.Pdf("text.pdf", 1);
        var merged = _ws.PathOf("mixed.pdf");
        using (var a = PdfSharp.Pdf.IO.PdfReader.Open(scanPdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        using (var b = PdfSharp.Pdf.IO.PdfReader.Open(textPdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            doc.AddPage(a.Pages[0]);
            doc.AddPage(b.Pages[0]);
            doc.Save(merged);
        }
        var before = Workspace.Sha256(merged);

        var job = await _ws.Run(Ids.OcrSearchablePdf, merged, ("languages", "eng"));
        Assert.Equal(JobItemState.Completed, job.Items[0].State);
        Assert.Equal(before, Workspace.Sha256(merged));
        Assert.Contains(job.Items[0].Notes, n => n.Contains("Page 2") && n.Contains("copied unchanged"));

        using var result = PdfDocument.Open(job.Items[0].Outputs[0].Path);
        using var original = PdfDocument.Open(merged);
        Assert.Equal(2, result.PageCount);
        Assert.Contains("Scanned words", result.GetText(1));
        Assert.Contains("Marker for page 1", result.GetText(2));
        Assert.Equal(1, fake.Calls); // page 2 was not recognised again
        // The page still looks the same: the original image is kept, not re-rendered.
        using var was = original.RenderPage(1, 50, out _);
        using var now = result.RenderPage(1, 50, out _);
        Assert.InRange(Math.Abs(was.Avg() - now.Avg()), 0, 0.5);
    }

    [Fact]
    public async Task Text_layer_lines_up_on_a_rotated_page()
    {
        var fake = new FakeOcrEngine().Line("en-US", ("Rotated", 0.1, 0.1, 0.3, 0.05));
        OcrService.Register(fake);
        var path = _ws.PathOf("rotated.pdf");
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            var page = doc.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(612);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(792);
            using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
                gfx.DrawRectangle(PdfSharp.Drawing.XBrushes.LightGray, 50, 50, 300, 300);
            page.Rotate = 90;
            doc.Save(path);
        }

        var job = await _ws.Run(Ids.OcrSearchablePdf, path, ("languages", "eng"));
        Assert.Equal(JobItemState.Completed, job.Items[0].State);
        using var result = PdfDocument.Open(job.Items[0].Outputs[0].Path);
        Assert.Contains("Rotated", result.GetText(1));
        // The word was recognised near the top-left of the displayed (rotated) page; PDFium must find it there.
        var box = result.GetTextBox(1, 0);
        var size = result.GetPageSize(1);
        Assert.Equal(792, size.Width, 1);
        int w = 1000, h = (int)(1000 * size.Height / size.Width);
        var corners = new[] { (box.Left, box.Top), (box.Left, box.Bottom), (box.Right, box.Top), (box.Right, box.Bottom) }
            .Select(c => result.PageToDisplay(1, c.Item1, c.Item2, w, h)).ToList();
        // 0.1 of the displayed size; PDFium measures character boxes from a substitute font, so allow 2%.
        Assert.InRange(corners.Min(c => c.X), 80, 120);
        Assert.InRange(corners.Min(c => c.Y), 0.1 * h - 20, 0.1 * h + 20);
    }

    [Fact]
    public async Task Text_from_image_is_written_in_reading_order()
    {
        OcrService.Register(ArabicAndEnglishPage());
        var png = _ws.Png("note.png", 1000, 1400);
        var job = await _ws.Run(Ids.OcrImageText, png);
        Assert.Equal(JobItemState.Completed, job.Items[0].State);
        var text = File.ReadAllText(job.Items[0].Outputs[0].Path);
        Assert.Equal("يرجى مراجعة الفاتورة\r\n\r\nInvoice total", text);
    }

    [Fact]
    public async Task Text_from_a_pdf_recognises_scans_and_reuses_existing_text()
    {
        var fake = new FakeOcrEngine().Line("en-US", ("Scanned", 0.1, 0.1, 0.3, 0.05), ("page", 0.45, 0.1, 0.2, 0.05));
        OcrService.Register(fake);
        var picture = _ws.Png("page.png", 850, 1100);
        var scanPdf = (await _ws.Run(Ids.ImagesToPdf, picture)).Items[0].Outputs[0].Path;
        var textPdf = _ws.Pdf("text.pdf", 1);
        var both = _ws.PathOf("both.pdf");
        using (var a = PdfSharp.Pdf.IO.PdfReader.Open(scanPdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        using (var b = PdfSharp.Pdf.IO.PdfReader.Open(textPdf, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        using (var doc = new PdfSharp.Pdf.PdfDocument())
        {
            doc.AddPage(a.Pages[0]);
            doc.AddPage(b.Pages[0]);
            doc.Save(both);
        }

        var job = await _ws.Run(Ids.OcrImageText, both, ("languages", "eng"));
        Assert.Equal(JobItemState.Completed, job.Items[0].State);
        var pages = File.ReadAllText(job.Items[0].Outputs[0].Path).Split('\f');
        Assert.Equal("Scanned page", pages[0]);
        Assert.Contains("Marker for page 1", pages[1]);
        Assert.Equal(1, fake.Calls);
        Assert.Contains(job.Items[0].Notes, n => n.StartsWith("Page 2 already had selectable text"));
    }

    [Fact]
    public async Task Blank_picture_reports_that_no_text_was_found()
    {
        OcrService.Register(new FakeOcrEngine());
        var png = _ws.Png("blank.png");
        var job = await _ws.Run(Ids.OcrImageText, png);
        Assert.Equal(JobItemState.Failed, job.Items[0].State);
        Assert.Contains("No text was recognised", job.Items[0].Error);
        Assert.Empty(_ws.TempLeftovers());
    }

    [Fact]
    public void Registering_reports_language_availability()
    {
        OcrService.Register(new FakeOcrEngine());
        Assert.True(RuntimeFeatures.IsAvailable(RuntimeFeatures.Ocr));
        Assert.Contains("Arabic and English", RuntimeFeatures.Describe(RuntimeFeatures.Ocr));
    }
}

/// <summary>OCR tests swap the process-wide engine, so they never run in parallel with each other.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OcrCollection
{
    public const string Name = "ocr";
}
