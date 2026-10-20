using System.Text;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Processing.Docs;
using Toolbox.Processing.Imaging;
using Toolbox.Processing.Pdf;

namespace Toolbox.Processing.Handlers;

internal static class HandlerSettings
{
    public static EncodeSettings Encode(ActionOptions options, int defaultQuality) => new()
    {
        Quality = options.GetInt("quality", defaultQuality, 1, 100),
        Lossless = options.GetBool("lossless", false),
        KeepMetadata = !options.GetString("metadata", "keep").Equals("strip", StringComparison.OrdinalIgnoreCase),
        Background = EncodeSettings.ParseColor(options.GetString("background", "#FFFFFF")),
    };

    public static string CombinedName(IReadOnlyList<ProbedFile> inputs) =>
        inputs.Count == 1 ? inputs[0].BaseName : $"{inputs[0].BaseName} + {inputs.Count - 1} more";
}

/// <summary>Image → image format conversion. Dimensions are unchanged; orientation is applied.</summary>
internal sealed class ImageConvertHandler(string actionId, FileKind target, int defaultQuality) : IActionHandler
{
    public string ActionId => actionId;

    public Task RunAsync(ItemContext context)
    {
        var input = context.Input;
        using var loaded = ImageLoader.Load(input);
        foreach (var note in loaded.Notes) context.Note(note);
        foreach (var note in ImageEncoder.DescribeChanges(loaded.Image, target)) context.Note(note);
        context.Cancellation.ThrowIfCancellationRequested();

        var settings = HandlerSettings.Encode(context.Options, defaultQuality);
        var slot = context.CreateOutput(new OutputSpec(input.BaseName, "", target));
        ImageEncoder.Save(loaded.Image, target, slot.TempPath, settings);
        slot.Expected = new ExpectedOutput(target, loaded.Width, loaded.Height);
        context.Report(1);
        return Task.CompletedTask;
    }
}

/// <summary>PDF pages → one image per selected page, in page order.</summary>
internal sealed class PdfRenderHandler(string actionId, FileKind target) : IActionHandler
{
    public string ActionId => actionId;

    public Task RunAsync(ItemContext context)
    {
        var input = context.Input;
        using var pdf = PdfDocument.Open(input.Path);
        var pages = PageSet.Parse(context.Options.GetString("pages", "all"), pdf.PageCount);
        double dpi = context.Options.GetDouble("dpi", 300, 36, 1200);
        var settings = HandlerSettings.Encode(context.Options, 90) with { KeepMetadata = false };

        for (int i = 0; i < pages.Count; i++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            int page = pages[i];
            using var image = pdf.RenderPage(page, dpi, out var effectiveDpi);
            if (effectiveDpi < dpi - 0.5)
                context.Note($"Page {page} is very large; it was rendered at {effectiveDpi:0} DPI instead of {dpi:0} DPI.");

            var slot = context.CreateOutput(new OutputSpec(input.BaseName, "", target,
                pdf.PageCount > 1 ? page : null, pdf.PageCount));
            ImageEncoder.Save(image, target, slot.TempPath, settings);
            slot.Expected = new ExpectedOutput(target, image.Width, image.Height);
            context.Report((i + 1.0) / pages.Count);
        }
        if (pdf.PageCount > 1)
            context.Note($"Exported {PageSet.Describe(pages, pdf.PageCount)} of {pdf.PageCount}.");
        return Task.CompletedTask;
    }
}

/// <summary>Existing PDF text layer → TXT or text-only DOCX. Never runs OCR.</summary>
internal sealed class PdfTextHandler(string actionId, FileKind target) : IActionHandler
{
    public string ActionId => actionId;

    public Task RunAsync(ItemContext context)
    {
        var input = context.Input;
        using var pdf = PdfDocument.Open(input.Path);
        var pages = PageSet.Parse(context.Options.GetString("pages", "all"), pdf.PageCount);

        var texts = new List<(int Page, string Text)>();
        for (int i = 0; i < pages.Count; i++)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            texts.Add((pages[i], pdf.GetText(pages[i])));
            context.Report((i + 1.0) / (pages.Count + 1));
        }

        var empty = texts.Where(t => string.IsNullOrWhiteSpace(t.Text)).Select(t => t.Page).ToList();
        if (empty.Count == texts.Count)
            throw new ProcessingException(
                "This PDF has no text layer (it is probably a scan), so there is no text to extract. Use Text (OCR) in the Tools ring to recognise it.");
        if (empty.Count > 0)
            context.Note($"{PageSet.Describe(empty, pdf.PageCount)} {(empty.Count == 1 ? "has" : "have")} no text layer (probably scanned) and came out empty.");

        var slot = context.CreateOutput(new OutputSpec(input.BaseName, "", target));
        if (target == FileKind.Text)
        {
            // Pages are separated by a form feed, the convention of pdftotext and print spoolers.
            var joined = string.Join("\f", texts.Select(t => t.Text));
            File.WriteAllText(slot.TempPath, joined, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        else
        {
            DocxComposer.TextToDocx(texts, input.BaseName, slot.TempPath);
            context.Note("Text only: layout, images and tables from the PDF are not reproduced.");
        }
        context.Report(1);
        return Task.CompletedTask;
    }
}

/// <summary>Images → one PDF, one image per page.</summary>
internal sealed class ImagesToPdfHandler : IActionHandler
{
    public string ActionId => CapabilityMap.Ids.ImagesToPdf;

    public Task RunAsync(ItemContext context)
    {
        var options = new PdfPageOptions(context.Options.GetString("page", "a4"), context.Options.GetDouble("margin", 10, 0, 50));
        var slot = context.CreateOutput(new OutputSpec(HandlerSettings.CombinedName(context.Inputs), "", FileKind.Pdf));
        var notes = new List<string>();
        int pages = PdfComposer.ImagesToPdf(context.Inputs, options, slot.TempPath, context.Report, context.Cancellation, notes);
        foreach (var n in notes) context.Note(n);
        slot.Expected = new ExpectedOutput(FileKind.Pdf, Pages: pages);
        return Task.CompletedTask;
    }
}

/// <summary>Images → one Word document, one image per page. No OCR.</summary>
internal sealed class ImagesToDocxHandler : IActionHandler
{
    public string ActionId => CapabilityMap.Ids.ImagesToDocx;

    public Task RunAsync(ItemContext context)
    {
        var name = HandlerSettings.CombinedName(context.Inputs);
        var slot = context.CreateOutput(new OutputSpec(name, "", FileKind.Docx));
        var notes = new List<string>();
        DocxComposer.ImagesToDocx(context.Inputs, name, slot.TempPath, context.Report, context.Cancellation, notes);
        foreach (var n in notes) context.Note(n);
        context.Note("Images are embedded as pictures; text in them is not editable.");
        return Task.CompletedTask;
    }
}
