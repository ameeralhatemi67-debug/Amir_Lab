using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;

namespace OcrEval;

/// <summary>
/// Renders sample pages with WPF text (full Unicode bidi and Arabic shaping) and applies
/// degradations with libvips. Must run on an STA thread.
/// </summary>
public static class PageRenderer
{
    public static Image Render(SampleSpec spec)
    {
        double scale = spec.Dpi / 96.0;
        double emDip = spec.PointSize * 96.0 / 72.0;
        var typeface = new Typeface(new FontFamily(spec.Font), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var culture = CultureInfo.GetCultureInfo(spec.RightToLeft ? "ar-SA" : "en-US");
        var flow = spec.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        // Each line is a list of segments; text between LRE (U+202A) and PDF (U+202C) is laid out
        // left-to-right on its own, the way the Unicode bidi algorithm places codes inside Arabic.
        var ltrCulture = CultureInfo.GetCultureInfo("en-US");
        var lines = spec.Lines.Select(l => Segments(l).Select(s => (s.Ltr && spec.RightToLeft,
            Text: new FormattedText(s.Text, s.Ltr ? ltrCulture : culture, s.Ltr ? FlowDirection.LeftToRight : flow, typeface, emDip, Brushes.Black, 1.0))).ToList()).ToList();
        double margin = 48;
        double width = Math.Ceiling(lines.Max(l => l.Sum(s => s.Text.WidthIncludingTrailingWhitespace)) + 2 * margin);
        double lineStep = emDip * 1.6;
        double height = Math.Ceiling(margin * 2 + lineStep * lines.Count);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            for (int i = 0; i < lines.Count; i++)
            {
                double y = margin + i * lineStep;
                if (!spec.RightToLeft)
                {
                    double x = margin;
                    foreach (var (_, text) in lines[i])
                    {
                        dc.DrawText(text, new Point(x, y));
                        x += text.WidthIncludingTrailingWhitespace;
                    }
                    continue;
                }
                // Right-to-left: segments run from the right margin leftwards. An RTL FormattedText
                // is drawn from its top-right corner, an embedded LTR one from its top-left.
                double right = width - margin;
                foreach (var (ltr, text) in lines[i])
                {
                    double w = text.WidthIncludingTrailingWhitespace;
                    dc.DrawText(text, ltr ? new Point(right - w, y) : new Point(right, y));
                    right -= w;
                }
            }
        }
        int pw = (int)Math.Ceiling(width * scale), ph = (int)Math.Ceiling(height * scale);
        var bitmap = new RenderTargetBitmap(pw, ph, spec.Dpi, spec.Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[pw * ph * 4];
        bitmap.CopyPixels(pixels, pw * 4, 0);

        using var bgra = Image.NewFromMemory(pixels, pw, ph, 4, Enums.BandFormat.Uchar);
        using var rgb = bgra[2].Bandjoin(bgra[1], bgra[0]);
        double ppm = spec.Dpi / 25.4;
        using var tagged = rgb.Copy(interpretation: Enums.Interpretation.Srgb, xres: ppm, yres: ppm);
        return Degrade(tagged, spec.Degradation, spec.Id.GetHashCode());
    }

    private static IEnumerable<(string Text, bool Ltr)> Segments(string line)
    {
        const char lre = (char)0x202A, pdf = (char)0x202C;
        int at = 0;
        while (at < line.Length)
        {
            int open = line.IndexOf(lre, at);
            if (open < 0)
            {
                yield return (line[at..], false);
                yield break;
            }
            if (open > at) yield return (line[at..open], false);
            int close = line.IndexOf(pdf, open);
            if (close < 0) close = line.Length;
            yield return (line[(open + 1)..close], true);
            at = close + 1;
        }
    }

    public static Image Degrade(Image image, Degradation degradation, int seed)
    {
        switch (degradation)
        {
            case Degradation.Blur:
                return image.Gaussblur(1.3);
            case Degradation.Noise:
                return AddNoise(image, 28);
            case Degradation.Jpeg:
                return Recompress(image, 20);
            case Degradation.Rotate:
                return image.Similarity(angle: 3, background: [255, 255, 255]);
            case Degradation.LowContrast:
                return (image * 0.30 + 160).Cast(Enums.BandFormat.Uchar);
            case Degradation.Lighting:
                return Light(image);
            case Degradation.PhoneLike:
            {
                using var tilted = image.Similarity(angle: -1.5, background: [255, 255, 255]);
                using var lit = Light(tilted);
                using var noisy = AddNoise(lit, 12);
                using var soft = noisy.Gaussblur(0.7);
                return Recompress(soft, 45);
            }
            default:
                return image.Copy();
        }
    }

    private static Image AddNoise(Image image, double sigma)
    {
        using var noise = Image.Gaussnoise(image.Width, image.Height, sigma: sigma, mean: 0);
        using var sum = image.Cast(Enums.BandFormat.Float) + noise;
        return sum.Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private static Image Recompress(Image image, int quality)
    {
        var buffer = image.JpegsaveBuffer(q: quality, keep: Enums.ForeignKeep.None);
        return Image.NewFromBuffer(buffer, access: Enums.Access.Random).Copy();
    }

    /// <summary>A shadow falling from one corner: brightness drops to about 55% at the far corner.</summary>
    private static Image Light(Image image)
    {
        using var xy = Image.Xyz(image.Width, image.Height);
        using var x = xy[0].Cast(Enums.BandFormat.Float) / image.Width;
        using var y = xy[1].Cast(Enums.BandFormat.Float) / image.Height;
        using var gain = 1.0 - (x * 0.25 + y * 0.20);
        using var lit = image.Cast(Enums.BandFormat.Float) * gain;
        using var tinted = lit * new double[] { 1.0, 0.97, 0.90 };
        return tinted.Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
    }
}
