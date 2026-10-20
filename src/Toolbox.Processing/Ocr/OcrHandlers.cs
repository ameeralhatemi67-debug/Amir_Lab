using System.Text;
using NetVips;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Processing.Imaging;
using Toolbox.Processing.Pdf;

namespace Toolbox.Processing.Ocr;

internal static class OcrPages
{
    /// <summary>A page counts as already having text when its text layer holds a few real characters.</summary>
    public static bool HasTextLayer(string text) => text.Count(char.IsLetterOrDigit) >= 3;

    public static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public static string Languages(ActionOptions options) => options.GetString("languages", "ara+eng");

    /// <summary>Tells the user when part of the request could not be honoured because a language is not installed.</summary>
    public static void NoteMissing(ItemContext context, string languages)
    {
        foreach (var name in OcrService.MissingLanguages(languages))
            context.Note($"{name} text recognition is not installed in Windows, so {name} text was not recognised. Add it in Settings > Time & language > Language & region ({name} > Language options > Optical character recognition).");
    }

    public static string Describe(string languages) => languages switch
    {
        "ara" => "Arabic",
        "eng" => "English",
        _ => "Arabic and English",
    };
}

/// <summary>Image or scanned PDF → UTF-8 text recognised locally. PDF pages that already have a text layer use it as is.</summary>
internal sealed class OcrTextHandler : IActionHandler
{
    public string ActionId => CapabilityMap.Ids.OcrImageText;

    public Task RunAsync(ItemContext context)
    {
        var input = context.Input;
        var languages = OcrPages.Languages(context.Options);
        OcrPages.NoteMissing(context, languages);
        string text;
        if (input.Kind == FileKind.Pdf)
        {
            using var pdf = PdfDocument.Open(input.Path);
            var pages = PageSet.Parse(context.Options.GetString("pages", "all"), pdf.PageCount);
            double dpi = context.Options.GetDouble("dpi", 300, 150, 600);
            var texts = new List<string>();
            var reused = new List<int>();
            for (int i = 0; i < pages.Count; i++)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                var existing = pdf.GetText(pages[i]);
                if (OcrPages.HasTextLayer(existing))
                {
                    texts.Add(existing);
                    reused.Add(pages[i]);
                }
                else
                {
                    using var image = pdf.RenderPage(pages[i], dpi, out _);
                    texts.Add(OcrService.Recognize(image, languages).ToText());
                }
                context.Report((i + 1.0) / (pages.Count + 1));
            }
            if (reused.Count > 0)
                context.Note(OcrPages.Sentence($"{PageSet.Describe(reused, pdf.PageCount)} already had selectable text, which was used instead of recognising the picture."));
            // Pages are separated by a form feed, as in PDF → TXT.
            text = string.Join("\f", texts);
        }
        else
        {
            using var loaded = ImageLoader.Load(input);
            foreach (var note in loaded.Notes) context.Note(note);
            text = OcrService.Recognize(loaded.Image, languages).ToText();
        }

        if (text.Count(char.IsLetterOrDigit) == 0)
            throw new ProcessingException("No text was recognised. The picture may be too small, blurred or not contain text in the chosen languages.");

        var slot = context.CreateOutput(new OutputSpec(input.BaseName, "", FileKind.Text));
        File.WriteAllText(slot.TempPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        context.Note($"Recognised locally ({OcrPages.Describe(languages)}). Check names, numbers and codes against the original.");
        context.Report(1);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Scan (image or PDF) → PDF with an invisible recognised-text layer. PDF pages are copied
/// unchanged (vector content, images and existing text kept); pages that already have text are
/// not recognised again.
/// </summary>
internal sealed class OcrSearchablePdfHandler : IActionHandler
{
    public string ActionId => CapabilityMap.Ids.OcrSearchablePdf;

    public Task RunAsync(ItemContext context)
    {
        var input = context.Input;
        var languages = OcrPages.Languages(context.Options);
        OcrPages.NoteMissing(context, languages);
        double dpi = context.Options.GetDouble("dpi", 300, 150, 600);
        var slot = context.CreateOutput(new OutputSpec(input.BaseName, CapabilityMap.Get(ActionId).OutputSuffix, FileKind.Pdf));
        var recognisedPages = new List<int>();
        int pageCount;

        using (var output = new PdfSharp.Pdf.PdfDocument())
        {
            output.Options.CompressContentStreams = true;
            output.Info.Creator = "File Toolbox";
            var font = SearchablePdf.CreateFont(output);
            var keepAlive = new List<MemoryStream>();
            try
            {
                pageCount = input.Kind == FileKind.Pdf
                    ? AddPdfPages(context, output, font, languages, dpi, keepAlive, recognisedPages)
                    : AddImagePage(context, output, font, languages, keepAlive, recognisedPages);
                context.Cancellation.ThrowIfCancellationRequested();
                output.Save(slot.TempPath);
            }
            finally
            {
                foreach (var s in keepAlive) s.Dispose();
            }
        }

        VerifyTextLayer(slot.TempPath, recognisedPages);
        slot.Expected = new ExpectedOutput(FileKind.Pdf, Pages: pageCount);
        if (recognisedPages.Count == 0)
            context.Note("No text was recognised, so the PDF has no text layer. The scan may be too small or blurred.");
        context.Note($"Recognised locally ({OcrPages.Describe(languages)}). The text layer is invisible; search and copy it to check accuracy.");
        context.Report(1);
        return Task.CompletedTask;
    }

    private static int AddPdfPages(ItemContext context, PdfSharp.Pdf.PdfDocument output, PdfSharp.Pdf.PdfDictionary font, string languages,
        double dpi, List<MemoryStream> keepAlive, List<int> recognised)
    {
        var bytes = File.ReadAllBytes(context.Input.Path);
        using var pdf = PdfDocument.Open(bytes);
        var title = pdf.GetMetaText("Title");
        if (title.Length > 0) output.Info.Title = title;

        PdfSharp.Pdf.PdfDocument? source = null;
        try
        {
            var stream = new MemoryStream(bytes, writable: false);
            keepAlive.Add(stream);
            source = PdfReader.Open(stream, PdfDocumentOpenMode.Import);
        }
        catch (Exception ex)
        {
            Toolbox.Core.Diagnostics.Log.Warn($"PDFsharp could not import {context.Input.Name}: {ex.Message}");
            context.Note($"The PDF's pages could not be copied as they are, so they were recreated from {dpi:0} DPI images. Vector content and existing text were not kept.");
        }

        var skipped = new List<int>();
        for (int n = 1; n <= pdf.PageCount; n++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            bool hasText = OcrPages.HasTextLayer(pdf.GetText(n));
            if (source is not null && hasText)
            {
                output.AddPage(source.Pages[n - 1]);
                skipped.Add(n);
            }
            else
            {
                using var image = pdf.RenderPage(n, dpi, out double effectiveDpi);
                PdfSharp.Pdf.PdfPage page;
                PageTransform map;
                if (source is not null)
                {
                    page = output.AddPage(source.Pages[n - 1]);
                    map = pdf.GetRenderTransform(n, image.Width, image.Height);
                }
                else
                {
                    page = AddPicturePage(output, image, 72.0 / effectiveDpi, keepAlive);
                    map = PageTransform.ForImagePage(72.0 / effectiveDpi, page.Height.Point);
                }
                var ocr = OcrService.Recognize(image, languages);
                if (SearchablePdf.AddTextLayer(page, font, ocr, map) > 0) recognised.Add(n);
            }
            context.Report(n / (pdf.PageCount + 1.0));
        }
        if (skipped.Count > 0)
            context.Note(OcrPages.Sentence($"{PageSet.Describe(skipped, pdf.PageCount)} already had selectable text and {(skipped.Count == 1 ? "was" : "were")} copied unchanged."));
        source?.Dispose();
        return pdf.PageCount;
    }

    private static int AddImagePage(ItemContext context, PdfSharp.Pdf.PdfDocument output, PdfSharp.Pdf.PdfDictionary font, string languages,
        List<MemoryStream> keepAlive, List<int> recognised)
    {
        var input = context.Input;
        using var loaded = ImageLoader.Load(input);
        foreach (var note in loaded.Notes) context.Note(note);
        var image = loaded.Image;
        output.Info.Title = input.BaseName;

        // Scans carry their resolution; photos and screenshots often say 72 DPI or nothing, so
        // those are sized to fit an A4 page instead of becoming metres wide.
        double dpi = image.Xres * 25.4;
        if (dpi < 100 || dpi > 2400) dpi = Math.Max(72, Math.Max(image.Width, image.Height) / 11.69);
        double pointsPerPixel = 72.0 / dpi;

        using var xImage = PdfComposer.LoadForEmbedding(input, null, keepAlive, out _, out _, out _);
        var page = output.AddPage();
        page.Width = XUnit.FromPoint(image.Width * pointsPerPixel);
        page.Height = XUnit.FromPoint(image.Height * pointsPerPixel);
        using (var gfx = XGraphics.FromPdfPage(page))
            gfx.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
        context.Report(0.3);

        var ocr = OcrService.Recognize(image, languages);
        if (SearchablePdf.AddTextLayer(page, font, ocr, PageTransform.ForImagePage(pointsPerPixel, page.Height.Point)) > 0) recognised.Add(1);
        return 1;
    }

    private static PdfSharp.Pdf.PdfPage AddPicturePage(PdfSharp.Pdf.PdfDocument output, Image image, double pointsPerPixel, List<MemoryStream> keepAlive)
    {
        var jpeg = new MemoryStream(image.JpegsaveBuffer(q: 85, keep: Enums.ForeignKeep.None));
        keepAlive.Add(jpeg);
        using var xImage = XImage.FromStream(jpeg);
        var page = output.AddPage();
        page.Width = XUnit.FromPoint(image.Width * pointsPerPixel);
        page.Height = XUnit.FromPoint(image.Height * pointsPerPixel);
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(xImage, 0, 0, page.Width.Point, page.Height.Point);
        return page;
    }

    /// <summary>Re-opens the result with PDFium and checks every recognised page now has extractable text.</summary>
    private static void VerifyTextLayer(string path, List<int> recognisedPages)
    {
        using var check = PdfDocument.Open(path);
        foreach (var n in recognisedPages)
        {
            if (!check.GetText(n).Any(char.IsLetterOrDigit))
                throw new ProcessingException($"The text layer on page {n} could not be read back, so the PDF was not saved.");
        }
    }
}
