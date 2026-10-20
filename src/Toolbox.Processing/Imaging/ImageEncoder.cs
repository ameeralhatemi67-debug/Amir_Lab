using System.Globalization;
using NetVips;
using Toolbox.Core;
using Toolbox.Core.Inputs;

namespace Toolbox.Processing.Imaging;

/// <summary>How to encode one output.</summary>
public sealed record EncodeSettings
{
    /// <summary>1–100 for lossy formats.</summary>
    public int Quality { get; init; } = 90;

    public bool Lossless { get; init; }

    /// <summary>Copy EXIF/XMP/IPTC as well as the colour profile. When false only the ICC profile is kept.</summary>
    public bool KeepMetadata { get; init; } = true;

    /// <summary>RGB fill for formats without transparency.</summary>
    public double[] Background { get; init; } = [255, 255, 255];

    /// <summary>Encoder effort (speed/size trade-off) for WebP and AVIF.</summary>
    public int Effort { get; init; } = 4;

    public static double[] ParseColor(string? hex)
    {
        var h = (hex ?? "").Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
        if (h.Length == 6 && int.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return [(v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF];
        return [255, 255, 255];
    }
}

/// <summary>Writes images with explicit savers (never inferred from the file name).</summary>
public static class ImageEncoder
{
    public static readonly IReadOnlySet<FileKind> Writable = new HashSet<FileKind>
    {
        FileKind.Jpeg, FileKind.Png, FileKind.WebP, FileKind.Avif, FileKind.Tiff, FileKind.Bmp, FileKind.Gif,
    };

    /// <summary>Notes about irreversible changes the encoding makes (e.g. transparency flattened).</summary>
    public static IReadOnlyList<string> DescribeChanges(Image image, FileKind kind)
    {
        var notes = new List<string>();
        if (image.HasAlpha() && !kind.SupportsTransparency())
            notes.Add($"{kind.Label()} has no transparency; transparent areas were filled with the background colour.");
        return notes;
    }

    public static void Save(Image image, FileKind kind, string path, EncodeSettings settings)
    {
        using var prepared = Prepare(image, kind, settings);
        var keep = settings.KeepMetadata ? Enums.ForeignKeep.All : Enums.ForeignKeep.Icc;
        try
        {
            switch (kind)
            {
                case FileKind.Jpeg:
                    prepared.Jpegsave(path, q: Clamp(settings.Quality), optimizeCoding: true, keep: keep);
                    break;
                case FileKind.Png:
                    prepared.Pngsave(path, compression: 6, keep: keep);
                    break;
                case FileKind.WebP:
                    prepared.Webpsave(path, q: Clamp(settings.Quality), lossless: settings.Lossless, effort: Math.Clamp(settings.Effort, 0, 6),
                        smartSubsample: true, keep: keep);
                    break;
                case FileKind.Avif:
                    prepared.Heifsave(path, q: Clamp(settings.Quality), lossless: settings.Lossless, compression: Enums.ForeignHeifCompression.Av1,
                        effort: Math.Clamp(settings.Effort, 0, 9), keep: keep);
                    break;
                case FileKind.Tiff:
                    prepared.Tiffsave(path, compression: Enums.ForeignTiffCompression.Lzw, predictor: Enums.ForeignTiffPredictor.Horizontal, keep: keep);
                    break;
                case FileKind.Gif:
                    prepared.Gifsave(path, keep: Enums.ForeignKeep.None);
                    break;
                case FileKind.Bmp:
                    BmpWriter.Write(prepared, path);
                    break;
                default:
                    throw new ProcessingException($"Writing {kind.Label()} images is not supported.");
            }
        }
        catch (VipsException ex)
        {
            throw new ProcessingException($"Encoding {kind.Label()} failed: {ImageLoader.FirstLine(ex.Message)}", ex);
        }
    }

    /// <summary>Encodes to memory (used for size estimates and target-size search).</summary>
    public static byte[] SaveToBuffer(Image image, FileKind kind, EncodeSettings settings)
    {
        using var prepared = Prepare(image, kind, settings);
        var keep = settings.KeepMetadata ? Enums.ForeignKeep.All : Enums.ForeignKeep.Icc;
        return kind switch
        {
            FileKind.Jpeg => prepared.JpegsaveBuffer(q: Clamp(settings.Quality), optimizeCoding: true, keep: keep),
            FileKind.Png => prepared.PngsaveBuffer(compression: 6, keep: keep),
            FileKind.WebP => prepared.WebpsaveBuffer(q: Clamp(settings.Quality), lossless: settings.Lossless, effort: Math.Clamp(settings.Effort, 0, 6), smartSubsample: true, keep: keep),
            FileKind.Avif => prepared.HeifsaveBuffer(q: Clamp(settings.Quality), lossless: settings.Lossless, compression: Enums.ForeignHeifCompression.Av1, effort: Math.Clamp(settings.Effort, 0, 9), keep: keep),
            FileKind.Tiff => prepared.TiffsaveBuffer(compression: Enums.ForeignTiffCompression.Lzw, predictor: Enums.ForeignTiffPredictor.Horizontal, keep: keep),
            FileKind.Bmp => BmpWriter.Encode(prepared),
            _ => throw new ProcessingException($"Writing {kind.Label()} images is not supported."),
        };
    }

    /// <summary>Converts colour space, bit depth and alpha to what the target format can store.</summary>
    internal static Image Prepare(Image image, FileKind kind, EncodeSettings settings)
    {
        var current = image.Copy();
        void Swap(Image next)
        {
            current.Dispose();
            current = next;
        }

        bool gray = current.Interpretation is Enums.Interpretation.Bw or Enums.Interpretation.Grey16;
        bool supported = current.Interpretation is Enums.Interpretation.Srgb or Enums.Interpretation.Bw
            or Enums.Interpretation.Rgb16 or Enums.Interpretation.Grey16 or Enums.Interpretation.Rgb;
        if (!supported) Swap(current.Colourspace(Enums.Interpretation.Srgb));

        bool keepsHighDepth = kind is FileKind.Png or FileKind.Tiff;
        if (current.Format != Enums.BandFormat.Uchar && !keepsHighDepth)
            Swap(current.Colourspace(gray ? Enums.Interpretation.Bw : Enums.Interpretation.Srgb));
        if (current.Format != Enums.BandFormat.Uchar && current.Format != Enums.BandFormat.Ushort)
            Swap(current.Cast(Enums.BandFormat.Uchar));

        if (current.HasAlpha() && !kind.SupportsTransparency())
        {
            var bg = gray ? new[] { settings.Background.Average() } : settings.Background;
            Swap(current.Flatten(background: bg));
        }
        return current;
    }

    private static int Clamp(int q) => Math.Clamp(q, 1, 100);
}
