using NetVips;
using PdfSharp.Drawing;
using Toolbox.Core;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Imaging;

namespace Toolbox.Processing.Pdf;

public sealed record PdfPageOptions(string PageSize = "a4", double MarginMm = 10);

/// <summary>Builds PDFs with PDFsharp (MIT, pinned 6.2.x).</summary>
public static class PdfComposer
{
    private const double PointsPerMm = 72.0 / 25.4;
    private const double PointsPerPixelAt96 = 72.0 / 96.0;

    /// <summary>
    /// Places each image on its own page, in the given order. JPEGs are embedded unchanged
    /// (no re-compression); other formats are embedded losslessly. Small images are not enlarged.
    /// Returns the number of pages written.
    /// </summary>
    public static int ImagesToPdf(IReadOnlyList<ProbedFile> images, PdfPageOptions options, string outputPath,
        Action<double>? progress, CancellationToken cancellation, ICollection<string>? notes = null)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        document.Info.Title = images.Count == 1 ? images[0].BaseName : $"{images[0].BaseName} and {images.Count - 1} more";
        document.Info.Creator = "File Toolbox";
        var streams = new List<MemoryStream>();
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                using var xImage = LoadForEmbedding(images[i], notes, streams, out int widthPx, out int heightPx, out double dpi);

                // Natural size at the image's DPI (96 if unknown), in points.
                double naturalW = widthPx * 72.0 / dpi, naturalH = heightPx * 72.0 / dpi;
                var page = document.AddPage();
                double margin = options.MarginMm * PointsPerMm;
                double pageW, pageH;
                switch (options.PageSize.ToLowerInvariant())
                {
                    case "fit":
                        pageW = naturalW;
                        pageH = naturalH;
                        margin = 0;
                        break;
                    case "letter":
                        (pageW, pageH) = (612, 792);
                        break;
                    default:
                        (pageW, pageH) = (595.28, 841.89);
                        break;
                }
                if (options.PageSize != "fit" && naturalW > naturalH) (pageW, pageH) = (pageH, pageW); // landscape for wide images
                page.Width = XUnit.FromPoint(pageW);
                page.Height = XUnit.FromPoint(pageH);

                double boxW = pageW - 2 * margin, boxH = pageH - 2 * margin;
                double scale = Math.Min(1.0, Math.Min(boxW / naturalW, boxH / naturalH));
                double drawW = naturalW * scale, drawH = naturalH * scale;
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(xImage, (pageW - drawW) / 2, (pageH - drawH) / 2, drawW, drawH);
                progress?.Invoke((i + 1.0) / images.Count);
            }

            cancellation.ThrowIfCancellationRequested();
            document.Save(outputPath);
            return images.Count;
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

    /// <summary>
    /// Loads an image for embedding: an unchanged JPEG when possible, otherwise lossless PNG data.
    /// The streams must stay open until the document is saved, so they are added to <paramref name="keepAlive"/>.
    /// </summary>
    internal static XImage LoadForEmbedding(ProbedFile file, ICollection<string>? notes, List<MemoryStream> keepAlive,
        out int widthPx, out int heightPx, out double dpi)
    {
        var (stream, w, h, d) = PrepareImage(file, notes);
        keepAlive.Add(stream);
        try
        {
            var image = XImage.FromStream(stream);
            (widthPx, heightPx, dpi) = (w, h, d);
            return image;
        }
        catch (Exception) when (file.Kind == FileKind.Jpeg)
        {
            // PDFsharp's JPEG reader refuses some valid variants (no JFIF/EXIF header, some progressive files):
            // embed losslessly instead of failing.
            (stream, w, h, d) = PrepareImage(file, null, forceLossless: true);
            keepAlive.Add(stream);
            var image = XImage.FromStream(stream);
            (widthPx, heightPx, dpi) = (w, h, d);
            return image;
        }
    }

    private static (MemoryStream Stream, int Width, int Height, double Dpi) PrepareImage(ProbedFile file, ICollection<string>? notes, bool forceLossless = false)
    {
        using var loaded = ImageLoader.Load(file);
        if (notes is not null) foreach (var n in loaded.Notes) notes.Add($"{file.Name}: {n}");
        var image = loaded.Image;
        double dpi = image.Xres > 0.5 ? image.Xres * 25.4 : 96;
        if (dpi < 30 || dpi > 2400) dpi = 96;

        // Unchanged JPEG pass-through when nothing needs fixing; a rotated (EXIF) or CMYK JPEG is re-encoded losslessly instead.
        bool rotated = file.Kind == FileKind.Jpeg && HasOrientation(file);
        if (!forceLossless && file.Kind == FileKind.Jpeg && !rotated && image.Interpretation is Enums.Interpretation.Srgb or Enums.Interpretation.Bw)
            return (new MemoryStream(File.ReadAllBytes(file.Path)), image.Width, image.Height, dpi);

        // Everything else: lossless 8-bit PNG (with alpha when present), which PDFsharp Core embeds directly.
        using var prepared = ImageEncoder.Prepare(image, FileKind.Png, new EncodeSettings());
        using var eight = prepared.Format == Enums.BandFormat.Uchar ? prepared.Copy() : prepared.Colourspace(Enums.Interpretation.Srgb);
        var png = eight.PngsaveBuffer(compression: 6, keep: Enums.ForeignKeep.None);
        return (new MemoryStream(png), eight.Width, eight.Height, dpi);
    }

    private static bool HasOrientation(ProbedFile file)
    {
        using var source = Source.NewFromFile(file.Path);
        using var header = Image.NewFromSource(source, "", Enums.Access.Sequential);
        return header.Contains("orientation") && header.Get("orientation") is int o && o > 1;
    }
}
