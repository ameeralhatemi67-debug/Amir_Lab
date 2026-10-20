using NetVips;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Processing.Imaging;

namespace Toolbox.Processing.Studio;

public sealed record StudioExportResult(int Width, int Height, IReadOnlyList<string> Notes);

/// <summary>Renders an edit document at full resolution and encodes it.</summary>
public static class StudioExporter
{
    /// <summary>The format "auto" means: PNG when framed or transparent, otherwise keep the source's format where possible.</summary>
    public static FileKind ResolveFormat(EditDocument doc, StudioSource source)
    {
        var requested = doc.Export.Format.ToLowerInvariant();
        return requested switch
        {
            "png" => FileKind.Png,
            "jpg" or "jpeg" => FileKind.Jpeg,
            "webp" => FileKind.WebP,
            "avif" => FileKind.Avif,
            "tiff" => FileKind.Tiff,
            "bmp" => FileKind.Bmp,
            _ when doc.Export.TargetBytes is not null => source.File.Kind is FileKind.WebP or FileKind.Avif ? source.File.Kind : FileKind.Jpeg,
            _ when doc.Background.Enabled => FileKind.Png,
            _ => source.File.Kind switch
            {
                FileKind.Jpeg or FileKind.Png or FileKind.WebP or FileKind.Avif or FileKind.Tiff or FileKind.Bmp => source.File.Kind,
                _ => FileKind.Png,
            },
        };
    }

    public static StudioExportResult Export(EditDocument doc, StudioSource source, FileKind format, string path, CancellationToken cancellation)
    {
        var notes = new List<string>(source.Notes);
        using var rendered = StudioRenderer.Render(doc, source, RenderStage.Final, proxyEdge: null, out _);
        cancellation.ThrowIfCancellationRequested();

        var image = rendered.Copy();
        try
        {
            if (doc.Export.MaxEdge is { } max && max > 0 && Math.Max(image.Width, image.Height) > max)
            {
                var resized = image.Resize((double)max / Math.Max(image.Width, image.Height), kernel: Enums.Kernel.Lanczos3);
                image.Dispose();
                image = resized;
                notes.Add($"Resized to {image.Width}×{image.Height}px.");
            }

            var settings = new EncodeSettings
            {
                Quality = doc.Export.Quality,
                KeepMetadata = false,
                Background = EncodeSettings.ParseColor(doc.Export.Matte),
            };
            notes.AddRange(ImageEncoder.DescribeChanges(image, format));

            if (doc.Export.TargetBytes is { } target)
            {
                var result = TargetSize.Search(image, format, target, settings, doc.Export.MinQuality, Math.Max(doc.Export.MinQuality, Math.Min(95, doc.Export.Quality)),
                    doc.Export.AllowDownscale, cancellation: cancellation);
                File.WriteAllBytes(path, result.Data);
                notes.Add((result.Met ? "Size target met: " : "Size target NOT met: ") + result.Explanation);
                return new StudioExportResult(result.Width, result.Height, notes);
            }

            ImageEncoder.Save(image, format, path, settings);
            return new StudioExportResult(image.Width, image.Height, notes);
        }
        finally
        {
            image.Dispose();
        }
    }
}

/// <summary>Job handler for <see cref="CapabilityMap.Ids.StudioExport"/>: option "document" holds the edit document JSON.</summary>
internal sealed class StudioExportHandler : IActionHandler
{
    public string ActionId => CapabilityMap.Ids.StudioExport;

    public Task RunAsync(ItemContext context)
    {
        var json = context.Options.GetString("document", "");
        if (json.Length == 0) throw new ProcessingException("No editing session was provided.");
        var doc = EditDocument.FromJson(json) with { SourcePath = context.Input.Path };

        using var source = StudioSource.Open(context.Input.Path, doc.Page, doc.Dpi);
        context.Report(0.2);
        var format = StudioExporter.ResolveFormat(doc, source);
        var suffix = context.Options.TryGetValue("suffix", out var s) && s.Length > 0 ? s : doc.Suffix();
        var slot = context.CreateOutput(new OutputSpec(context.Input.BaseName, suffix, format,
            source.PageCount > 1 ? source.Page : null, source.PageCount));
        var result = StudioExporter.Export(doc, source, format, slot.TempPath, context.Cancellation);
        foreach (var note in result.Notes) context.Note(note);
        context.Note("Exported from the original-quality source; metadata (camera, location) is not included.");
        slot.Expected = new ExpectedOutput(format, result.Width, result.Height);
        context.Report(1);
        return Task.CompletedTask;
    }
}
