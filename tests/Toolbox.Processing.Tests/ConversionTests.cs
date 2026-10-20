using DocumentFormat.OpenXml.Packaging;
using NetVips;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Processing.Docs;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

namespace Toolbox.Processing.Tests;

public sealed class ImageConversionTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public async Task Transparent_png_to_jpg_fills_transparency_and_keeps_source()
    {
        var src = _ws.Png("logo.png", 400, 200, transparentCorner: true);
        var hash = Workspace.Sha256(src);

        var job = await _ws.Run(Ids.ImageToJpg, src);

        var item = Assert.Single(job.Items);
        Assert.Equal(JobItemState.Completed, item.State);
        var output = Assert.Single(item.Outputs);
        Assert.Equal(_ws.PathOf("logo.jpg"), output.Path);
        Assert.Equal((400, 200), (output.Width!.Value, output.Height!.Value));
        Assert.Contains(item.Notes, n => n.Contains("transparent"));
        using var jpg = Image.NewFromFile(output.Path);
        Assert.Equal(3, jpg.Bands);
        var corner = jpg.Getpoint(5, 5);
        Assert.All(corner, c => Assert.True(c > 245, $"corner should be white, was {c}"));
        Assert.Equal(hash, Workspace.Sha256(src));
        Assert.Empty(_ws.TempLeftovers());
    }

    [Fact]
    public async Task Exif_rotated_photo_is_output_upright()
    {
        var src = _ws.RotatedJpeg("phone photo.jpg", 300, 200);
        var job = await _ws.Run(Ids.ImageToPng, src);
        var output = Assert.Single(job.Items[0].Outputs);
        Assert.Equal((200, 300), (output.Width!.Value, output.Height!.Value));
    }

    [Theory]
    [InlineData(Ids.ImageToWebp, ".webp")]
    [InlineData(Ids.ImageToAvif, ".avif")]
    [InlineData(Ids.ImageToTiff, ".tiff")]
    [InlineData(Ids.ImageToBmp, ".bmp")]
    [InlineData(Ids.ImageToPng, ".png")]
    public async Task Converts_to_every_supported_format_with_verified_output(string action, string extension)
    {
        var src = _ws.Png("source image.png", 320, 240);
        if (action == Ids.ImageToPng) src = _ws.RotatedJpeg("source image.jpg", 320, 240);
        var job = await _ws.Run(action, src);
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error);
        var output = Assert.Single(item.Outputs);
        Assert.EndsWith(extension, output.Path);
        Assert.True(output.SizeBytes > 0);
    }

    [Fact]
    public async Task Large_image_converts_without_issue()
    {
        var src = _ws.Png("large.png", 8000, 6000);
        var job = await _ws.Run(Ids.ImageToWebp, src, ("quality", "70"));
        var output = Assert.Single(job.Items[0].Outputs);
        Assert.Equal((8000, 6000), (output.Width!.Value, output.Height!.Value));
    }

    [Fact]
    public async Task Repeated_conversion_never_overwrites()
    {
        var src = _ws.Png("chart.png");
        await _ws.Run(Ids.ImageToWebp, src);
        var second = await _ws.Run(Ids.ImageToWebp, src);
        Assert.Equal(_ws.PathOf("chart (2).webp"), second.Items[0].Outputs[0].Path);
        Assert.True(File.Exists(_ws.PathOf("chart.webp")));
    }

    [Fact]
    public async Task Arabic_and_bracketed_names_work()
    {
        var arabic = _ws.Png("صورة المنتج [نسخة 2].png");
        var job = await _ws.Run(Ids.ImageToJpg, arabic);
        Assert.Equal(JobItemState.Completed, job.Items[0].State);
        Assert.Equal(_ws.PathOf("صورة المنتج [نسخة 2].jpg"), job.Items[0].Outputs[0].Path);
    }

    [Fact]
    public async Task Corrupt_input_fails_clearly_and_leaves_nothing_behind()
    {
        var bad = _ws.PathOf("broken.png");
        File.WriteAllBytes(bad, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var job = await _ws.Run(Ids.ImageToJpg, bad);
        var item = Assert.Single(job.Items);
        Assert.Equal(JobItemState.Failed, item.State);
        Assert.Contains("could not be read", item.Error);
        Assert.False(File.Exists(_ws.PathOf("broken.jpg")));
        Assert.Empty(_ws.TempLeftovers());
    }

    [Fact]
    public async Task Mixed_batch_reports_skipped_files()
    {
        var png = _ws.Png("a.png");
        var pdf = _ws.Pdf("doc.pdf", 1);
        var job = await _ws.Run(Ids.ImageToWebp, [png, pdf]);
        Assert.Equal(1, job.Summary.Completed);
        var skipped = Assert.Single(job.Items, i => i.State == JobItemState.Skipped);
        Assert.Equal("Not available for PDF", skipped.Error);
    }

    [Fact]
    public async Task Cancelled_batch_keeps_finished_outputs_and_cleans_up()
    {
        var files = Enumerable.Range(1, 8).Select(i => _ws.Png($"big {i}.png", 3000, 2000)).ToList();
        var job = _ws.Runner.Start(new JobRequest(Ids.ImageToAvif, files));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (job.Summary.Completed == 0 && sw.Elapsed < TimeSpan.FromSeconds(60)) await Task.Delay(20);
        job.Cancel();
        await job.Completion;

        var summary = job.Summary;
        Assert.True(summary.Completed >= 1);
        Assert.True(summary.Cancelled >= 1, "some items should have been cancelled");
        foreach (var item in job.Items.Where(i => i.State == JobItemState.Completed))
            Assert.True(File.Exists(item.Outputs[0].Path));
        Assert.Equal(summary.Completed, Directory.GetFiles(_ws.Root, "*.avif").Length);
        Assert.Empty(_ws.TempLeftovers());
    }

    [Fact]
    public async Task Read_only_destination_falls_back_and_says_so()
    {
        var lockedDir = Path.Combine(_ws.Root, "locked");
        Directory.CreateDirectory(lockedDir);
        var src = Path.Combine(lockedDir, "photo.png");
        File.Copy(_ws.Png("seed.png"), src);
        var rule = DenyCreateFiles(lockedDir);
        try
        {
            var job = await _ws.Run(Ids.ImageToJpg, src);
            var item = Assert.Single(job.Items);
            Assert.True(item.State == JobItemState.Completed, item.Error);
            Assert.StartsWith(_ws.Fallback, item.Outputs[0].Path);
            Assert.True(item.Outputs[0].UsedFallbackFolder);
            Assert.Contains(item.Notes, n => n.Contains("could not be written"));
        }
        finally
        {
            RemoveRule(lockedDir, rule);
        }
    }

    private static System.Security.AccessControl.FileSystemAccessRule DenyCreateFiles(string dir)
    {
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.WriteData,
            System.Security.AccessControl.AccessControlType.Deny);
        acl.AddAccessRule(rule);
        info.SetAccessControl(acl);
        return rule;
    }

    private static void RemoveRule(string dir, System.Security.AccessControl.FileSystemAccessRule rule)
    {
        var info = new DirectoryInfo(dir);
        var acl = info.GetAccessControl();
        acl.RemoveAccessRule(rule);
        info.SetAccessControl(acl);
    }
}

public sealed class PdfTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public async Task Single_page_pdf_renders_to_one_jpg_at_300_dpi()
    {
        var src = _ws.Pdf("Page 001.pdf", 1);
        var job = await _ws.Run(Ids.PdfToJpg, src);
        var output = Assert.Single(job.Items[0].Outputs);
        Assert.Equal(_ws.PathOf("Page 001.jpg"), output.Path); // the reference naming
        Assert.Equal((2550, 3300), (output.Width!.Value, output.Height!.Value));
    }

    [Fact]
    public async Task Selected_pages_render_in_order_with_page_numbers()
    {
        var src = _ws.Pdf("Handbook.pdf", 12);
        var job = await _ws.Run(Ids.PdfToPng, src, ("pages", "11,3,4-5"), ("dpi", "72"));
        var names = job.Items[0].Outputs.Select(o => Path.GetFileName(o.Path)).ToList();
        Assert.Equal(["Handbook - Page 03.png", "Handbook - Page 04.png", "Handbook - Page 05.png", "Handbook - Page 11.png"], names);
        Assert.All(job.Items[0].Outputs, o => Assert.Equal((612, 792), (o.Width!.Value, o.Height!.Value)));
    }

    [Fact]
    public async Task All_pages_are_exported_by_default_never_just_the_first()
    {
        var src = _ws.Pdf("three.pdf", 3);
        var job = await _ws.Run(Ids.PdfToJpg, src, ("dpi", "50"));
        Assert.Equal(3, job.Items[0].Outputs.Count);
    }

    [Fact]
    public async Task Invalid_page_selection_is_rejected_with_a_reason()
    {
        var src = _ws.Pdf("short.pdf", 2);
        var job = await _ws.Run(Ids.PdfToJpg, src, ("pages", "5"));
        Assert.Equal(JobItemState.Failed, job.Items[0].State);
        Assert.Equal("Page 5 does not exist; the document has 2 pages.", job.Items[0].Error);
    }

    [Fact]
    public async Task Digital_pdf_text_is_extracted_with_page_separators()
    {
        var src = _ws.Pdf("notes.pdf", 3);
        var job = await _ws.Run(Ids.PdfToTxt, src);
        var output = Assert.Single(job.Items[0].Outputs);
        var text = File.ReadAllText(output.Path);
        Assert.Contains("Marker for page 1", text);
        Assert.Contains("Marker for page 3", text);
        Assert.Equal(2, text.Count(c => c == '\f'));
    }

    [Fact]
    public async Task Scanned_pdf_without_text_layer_is_explained_not_faked()
    {
        var src = _ws.Pdf("scan.pdf", 2, withText: false);
        var job = await _ws.Run(Ids.PdfToTxt, src);
        Assert.Equal(JobItemState.Failed, job.Items[0].State);
        Assert.Contains("no text layer", job.Items[0].Error);
        Assert.Contains("OCR", job.Items[0].Error);
        Assert.False(File.Exists(_ws.PathOf("scan.txt")));
    }

    [Fact]
    public async Task Pdf_text_to_docx_contains_text_and_is_labelled_text_only()
    {
        var src = _ws.Pdf("report.pdf", 2);
        var job = await _ws.Run(Ids.PdfToDocxText, src);
        var item = job.Items[0];
        Assert.True(item.State == JobItemState.Completed, item.Error);
        Assert.Contains(item.Notes, n => n.Contains("Text only"));
        using var doc = WordprocessingDocument.Open(item.Outputs[0].Path, false);
        var body = doc.MainDocumentPart!.Document.Body!.InnerText;
        Assert.Contains("Marker for page 2", body);
    }

    [Fact]
    public async Task Images_become_one_pdf_in_natural_name_order()
    {
        var a = _ws.Png("Scan 10.png", 300, 600);   // portrait
        var b = _ws.Png("Scan 2.png", 900, 300);    // landscape
        var c = _ws.Png("Scan 1.png", 400, 400);    // square
        var job = await _ws.Run(Ids.ImagesToPdf, [a, b, c]);
        var output = Assert.Single(job.Items[0].Outputs);
        Assert.Equal(3, output.Pages);
        Assert.Equal(_ws.PathOf("Scan 1 + 2 more.pdf"), output.Path);

        using var pdf = Pdf.PdfDocument.Open(output.Path);
        var sizes = Enumerable.Range(1, 3).Select(pdf.GetPageSize).ToList();
        Assert.True(sizes[0].Height > sizes[0].Width, "Scan 1 (square) on a portrait page");
        Assert.True(sizes[1].Width > sizes[1].Height, "Scan 2 (landscape) on a landscape page");
        Assert.True(sizes[2].Height > sizes[2].Width, "Scan 10 (portrait) last");
    }

    [Fact]
    public async Task Images_to_docx_embeds_pictures()
    {
        var a = _ws.Png("Slide.png", 800, 450);
        var job = await _ws.Run(Ids.ImagesToDocx, a);
        using var doc = WordprocessingDocument.Open(job.Items[0].Outputs[0].Path, false);
        Assert.Single(doc.MainDocumentPart!.ImageParts);
    }

    [Fact]
    public void Rtl_detection_for_arabic_paragraphs()
    {
        Assert.True(DocxComposer.IsRightToLeft("تقرير الربع الأول 2026"));
        Assert.False(DocxComposer.IsRightToLeft("Quarterly report للشركة"));
    }

    [Fact]
    public void Page_sets_parse_and_describe()
    {
        Assert.Equal([1, 2, 3], PageSet.Parse("all", 3));
        Assert.Equal([2, 4, 5, 6], PageSet.Parse("6,2, 4-5", 8));
        Assert.Equal([7, 8], PageSet.Parse("7-", 8));
        Assert.Equal("pages 2, 4–6", PageSet.Describe([2, 4, 5, 6], 8));
    }
}
