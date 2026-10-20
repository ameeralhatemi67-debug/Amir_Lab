using NetVips;
using Toolbox.Core;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Imaging;

namespace Toolbox.Processing.Studio;

public sealed record TargetSizeResult(byte[] Data, int Quality, int Width, int Height, bool Met, string Explanation);

/// <summary>
/// Tries to fit an image under a byte limit: first by lowering quality (binary search within
/// [minQuality, maxQuality]), then — only if allowed — by reducing dimensions in steps, never below
/// <paramref name="minEdge"/>. Reports honestly whether the target was met.
/// </summary>
public static class TargetSize
{
    public static TargetSizeResult Search(Image image, FileKind format, long targetBytes, EncodeSettings baseSettings,
        int minQuality = 40, int maxQuality = 92, bool allowDownscale = true, int minEdge = 480, CancellationToken cancellation = default)
    {
        if (format is not (FileKind.Jpeg or FileKind.WebP or FileKind.Avif))
            throw new ProcessingException($"A size target needs a lossy format (JPG, WebP or AVIF); {format.Label()} cannot be tuned by quality.");
        minQuality = Math.Clamp(minQuality, 1, 100);
        maxQuality = Math.Clamp(maxQuality, minQuality, 100);

        var current = image.Copy();
        try
        {
            byte[]? best = null;
            int bestQ = 0;
            for (int round = 0; round < 6; round++)
            {
                cancellation.ThrowIfCancellationRequested();
                var top = Encode(current, format, baseSettings, maxQuality);
                if (top.Length <= targetBytes)
                    return new TargetSizeResult(top, maxQuality, current.Width, current.Height, true,
                        $"Fits at quality {maxQuality}" + (round > 0 ? $" after reducing to {current.Width}×{current.Height}px." : "."));

                var low = Encode(current, format, baseSettings, minQuality);
                if (low.Length <= targetBytes)
                {
                    int lo = minQuality, hi = maxQuality;
                    best = low;
                    bestQ = minQuality;
                    while (hi - lo > 2)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int mid = (lo + hi) / 2;
                        var attempt = Encode(current, format, baseSettings, mid);
                        if (attempt.Length <= targetBytes) { lo = mid; best = attempt; bestQ = mid; }
                        else hi = mid;
                    }
                    return new TargetSizeResult(best, bestQ, current.Width, current.Height, true,
                        $"Fits at quality {bestQ}" + (round > 0 ? $" after reducing to {current.Width}×{current.Height}px." : "."));
                }

                if (!allowDownscale || Math.Max(current.Width, current.Height) <= minEdge)
                    return new TargetSizeResult(low, minQuality, current.Width, current.Height, false,
                        $"Could not reach {Bytes.Format(targetBytes)}: the smallest result within the limits is {Bytes.Format(low.Length)} (quality {minQuality}, {current.Width}×{current.Height}px).");

                // Shrink roughly in proportion to the overshoot, but at least 15 % per round.
                double factor = Math.Min(0.85, Math.Sqrt((double)targetBytes / low.Length) * 0.95);
                factor = Math.Max(factor, (double)minEdge / Math.Max(current.Width, current.Height));
                var next = current.Resize(factor);
                current.Dispose();
                current = next;
            }
            var final = Encode(current, format, baseSettings, minQuality);
            return new TargetSizeResult(final, minQuality, current.Width, current.Height, final.Length <= targetBytes,
                final.Length <= targetBytes ? "Fits." : $"Could not reach {Bytes.Format(targetBytes)}; best is {Bytes.Format(final.Length)}.");
        }
        finally
        {
            current.Dispose();
        }
    }

    private static byte[] Encode(Image image, FileKind format, EncodeSettings settings, int quality) =>
        ImageEncoder.SaveToBuffer(image, format, settings with { Quality = quality, Effort = format == FileKind.Avif ? 2 : settings.Effort });
}
