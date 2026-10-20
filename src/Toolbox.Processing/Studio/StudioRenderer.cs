using NetVips;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Toolbox.Processing.Imaging;

namespace Toolbox.Processing.Studio;

/// <summary>How far through the pipeline to render.</summary>
public enum RenderStage
{
    /// <summary>Rotation/flip and adjustments, before cropping (what the crop editor shows).</summary>
    Uncropped,

    /// <summary>Everything except the background framing.</summary>
    Content,

    /// <summary>The complete result.</summary>
    Final,
}

/// <summary>
/// The single renderer behind both the live preview (from a downscaled proxy) and the export
/// (from the full-resolution source). Every distance in the document is in full-resolution pixels
/// and is multiplied by the proxy scale, so the preview is a faithful miniature of the export.
/// Pipeline: orient → crop → adjust → redact → annotate → background.
/// </summary>
public static class StudioRenderer
{
    public const int DefaultProxyEdge = 1600;

    /// <summary>Renders to a libvips image (8-bit sRGB, 3 or 4 bands). <paramref name="scale"/> is output px per full-res px.</summary>
    public static Image Render(EditDocument doc, StudioSource source, RenderStage stage, int? proxyEdge, out double scale)
    {
        Image baseImage;
        if (proxyEdge is { } edge)
        {
            baseImage = source.Proxy(edge, out scale).Copy();
        }
        else
        {
            baseImage = source.Full.Copy();
            scale = 1;
        }

        var img = Orient(baseImage, doc.Crop);
        if (stage == RenderStage.Uncropped)
            return Adjust(img, doc.Adjust, scale);

        if (doc.Crop.Rect is { } rect)
        {
            var (ow, oh) = OrientedSize(source.Width, source.Height, doc.Crop.Rotation);
            var r = rect.ClampTo(ow, oh);
            int x = (int)Math.Round(r.X * scale), y = (int)Math.Round(r.Y * scale);
            int w = Math.Max(1, (int)Math.Round(r.Width * scale)), h = Math.Max(1, (int)Math.Round(r.Height * scale));
            x = Math.Clamp(x, 0, img.Width - 1);
            y = Math.Clamp(y, 0, img.Height - 1);
            w = Math.Min(w, img.Width - x);
            h = Math.Min(h, img.Height - y);
            img = Swap(img, img.ExtractArea(x, y, w, h));
        }
        img = Adjust(img, doc.Adjust, scale);
        img = ApplyRedactions(img, doc.Redactions, scale);

        if (doc.Annotations.Count == 0 && (stage == RenderStage.Content || !doc.Background.Enabled))
            return img;

        using var content = ToSkBitmap(img);
        img.Dispose();
        using var annotated = doc.Annotations.Count > 0 ? DrawAnnotations(content, doc.Annotations, scale) : null;
        var current = annotated ?? content;
        if (stage == RenderStage.Content || !doc.Background.Enabled) return FromSkBitmap(current);
        using var framed = Frame(current, doc.Background, scale);
        return FromSkBitmap(framed);
    }

    /// <summary>Preview pixels for WPF: premultiplied BGRA, plus the size.</summary>
    public static (byte[] Bgra, int Width, int Height, double Scale) RenderPreview(EditDocument doc, StudioSource source, RenderStage stage, int proxyEdge = DefaultProxyEdge)
    {
        using var image = Render(doc, source, stage, proxyEdge, out var scale);
        using var bitmap = ToSkBitmap(image);
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bytes = new byte[info.BytesSize];
        unsafe
        {
            fixed (byte* p = bytes) { using var pixmap = bitmap.PeekPixels(); pixmap.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0); }
        }
        return (bytes, bitmap.Width, bitmap.Height, scale);
    }

    /// <summary>Pixel size of the source after rotation.</summary>
    public static (int Width, int Height) OrientedSize(int width, int height, int rotation) =>
        ((rotation % 180 + 180) % 180) == 90 ? (height, width) : (width, height);

    /// <summary>Output size of the final render at full resolution, without rendering pixels.</summary>
    public static (int Width, int Height) FinalSize(EditDocument doc, StudioSource source)
    {
        var (w, h) = OrientedSize(source.Width, source.Height, doc.Crop.Rotation);
        if (doc.Crop.Rect is { } r)
        {
            var c = r.ClampTo(w, h);
            (w, h) = (c.Width, c.Height);
        }
        if (doc.Background.Enabled)
        {
            var (cw, ch) = CanvasSize(w, h, doc.Background, 1);
            (w, h) = (cw, ch);
        }
        if (doc.Export.MaxEdge is { } max && Math.Max(w, h) > max)
        {
            double s = (double)max / Math.Max(w, h);
            (w, h) = (Math.Max(1, (int)Math.Round(w * s)), Math.Max(1, (int)Math.Round(h * s)));
        }
        return (w, h);
    }

    /// <summary>Canvas size around content of w×h for the background settings, at <paramref name="scale"/>.</summary>
    public static (int Width, int Height) CanvasSize(int w, int h, BackgroundSettings bg, double scale)
    {
        double p = Math.Max(0, bg.Padding) * scale;
        double width = w + 2 * p, height = h + 2 * p;
        if (BackgroundPresets.Ratio(bg.Ratio) is { } ratio)
        {
            if (width / height > ratio) height = width / ratio;
            else width = height * ratio;
        }
        return (Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)));
    }

    // ---- pipeline steps --------------------------------------------------------------------

    private static Image Orient(Image img, CropSettings crop)
    {
        int rotation = (crop.Rotation % 360 + 360) % 360;
        if (rotation == 90) img = Swap(img, img.Rot(Enums.Angle.D90));
        else if (rotation == 180) img = Swap(img, img.Rot(Enums.Angle.D180));
        else if (rotation == 270) img = Swap(img, img.Rot(Enums.Angle.D270));
        if (crop.FlipHorizontal) img = Swap(img, img.Flip(Enums.Direction.Horizontal));
        if (crop.FlipVertical) img = Swap(img, img.Flip(Enums.Direction.Vertical));
        return img;
    }

    internal static Image Adjust(Image img, AdjustSettings a, double scale)
    {
        if (a.IsIdentity) return img;
        bool alpha = img.HasAlpha();
        var rgb = alpha ? img.ExtractBand(0, n: 3) : img.Copy();
        var alphaBand = alpha ? img.ExtractBand(3) : null;
        img.Dispose();

        if (a.Brightness != 0 || a.Contrast != 0)
        {
            double f = Math.Pow(2, a.Contrast / 100.0);
            double offset = a.Brightness * 1.1;
            double b = 128 - 128 * f + offset;
            rgb = Swap(rgb, rgb.Linear([f, f, f], [b, b, b]));
        }
        if (a.Saturation != 0)
        {
            double k = 1 + a.Saturation / 100.0;
            using var gray1 = rgb.Colourspace(Enums.Interpretation.Bw);
            using var gray = gray1.Bandjoin(gray1, gray1);
            using var scaled = rgb * k;
            using var grayPart = gray * (1 - k);
            rgb = Swap(rgb, scaled + grayPart);
        }
        rgb = Swap(rgb, rgb.Cast(Enums.BandFormat.Uchar));
        if (a.Sharpen > 0)
        {
            double sigma = Math.Max(0.5, 1.2 * Math.Max(scale, 0.35));
            rgb = Swap(rgb, rgb.Copy(interpretation: Enums.Interpretation.Srgb).Sharpen(sigma: sigma, m2: a.Sharpen / 100.0 * 4));
            rgb = Swap(rgb, rgb.Colourspace(Enums.Interpretation.Srgb).Cast(Enums.BandFormat.Uchar));
        }
        var result = alphaBand is null ? rgb.Copy(interpretation: Enums.Interpretation.Srgb) : rgb.Bandjoin(alphaBand).Copy(interpretation: Enums.Interpretation.Srgb);
        rgb.Dispose();
        alphaBand?.Dispose();
        return result;
    }

    private static Image ApplyRedactions(Image img, IReadOnlyList<Redaction> redactions, double scale)
    {
        foreach (var r in redactions)
        {
            int x = Math.Clamp((int)Math.Floor(r.Rect.X * scale), 0, img.Width - 1);
            int y = Math.Clamp((int)Math.Floor(r.Rect.Y * scale), 0, img.Height - 1);
            int w = Math.Clamp((int)Math.Ceiling(r.Rect.Width * scale), 1, img.Width - x);
            int h = Math.Clamp((int)Math.Ceiling(r.Rect.Height * scale), 1, img.Height - y);
            switch (r.Mode)
            {
                case RedactionMode.Solid:
                {
                    var c = EncodeSettings.ParseColor(r.Color);
                    double[] ink = img.Bands == 4 ? [c[0], c[1], c[2], 255] : c;
                    img = Swap(img, img.Mutate(m => m.DrawRect(ink, x, y, w, h, fill: true)));
                    break;
                }
                case RedactionMode.Pixelate:
                {
                    int block = Math.Max(4, Math.Min(w, h) / 10);
                    using var region = img.ExtractArea(x, y, w, h);
                    using var small = region.Resize(1.0 / block, kernel: Enums.Kernel.Nearest);
                    using var big = small.Resize((double)w / small.Width, kernel: Enums.Kernel.Nearest, vscale: (double)h / small.Height);
                    using var fitted = big.Width >= w && big.Height >= h ? big.ExtractArea(0, 0, w, h) : big.Embed(0, 0, w, h, extend: Enums.Extend.Copy);
                    img = Swap(img, img.Insert(fitted, x, y));
                    break;
                }
                case RedactionMode.Blur:
                {
                    using var region = img.ExtractArea(x, y, w, h);
                    using var blurred = region.Gaussblur(Math.Max(3, Math.Min(w, h) / 8.0)).Cast(Enums.BandFormat.Uchar);
                    img = Swap(img, img.Insert(blurred, x, y));
                    break;
                }
            }
        }
        return img;
    }

    private static SKBitmap DrawAnnotations(SKBitmap content, IReadOnlyList<Annotation> annotations, double scale)
    {
        var result = content.Copy();
        using var canvas = new SKCanvas(result);
        int step = 0;
        foreach (var a in annotations)
        {
            var color = SKColor.Parse(a.Color);
            float stroke = (float)Math.Max(1, a.Stroke * scale);
            var pts = new List<SKPoint>();
            for (int i = 0; i + 1 < a.Points.Count; i += 2) pts.Add(new SKPoint((float)(a.Points[i] * scale), (float)(a.Points[i + 1] * scale)));
            if (pts.Count == 0) continue;
            using var paint = new SKPaint { IsAntialias = true, Color = color, StrokeWidth = stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            switch (a.Kind)
            {
                case AnnotationKind.Rectangle or AnnotationKind.Ellipse when pts.Count >= 2:
                {
                    var rect = SKRect.Create(Math.Min(pts[0].X, pts[1].X), Math.Min(pts[0].Y, pts[1].Y), Math.Abs(pts[1].X - pts[0].X), Math.Abs(pts[1].Y - pts[0].Y));
                    paint.Style = a.Filled ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
                    if (a.Kind == AnnotationKind.Rectangle) canvas.DrawRoundRect(rect, stroke, stroke, paint);
                    else canvas.DrawOval(rect, paint);
                    break;
                }
                case AnnotationKind.Highlight when pts.Count >= 2:
                {
                    var rect = SKRect.Create(Math.Min(pts[0].X, pts[1].X), Math.Min(pts[0].Y, pts[1].Y), Math.Abs(pts[1].X - pts[0].X), Math.Abs(pts[1].Y - pts[0].Y));
                    paint.Style = SKPaintStyle.Fill;
                    paint.Color = color.WithAlpha(0x66);
                    paint.BlendMode = SKBlendMode.Multiply;
                    canvas.DrawRect(rect, paint);
                    break;
                }
                case AnnotationKind.Line or AnnotationKind.Arrow when pts.Count >= 2:
                {
                    paint.Style = SKPaintStyle.Stroke;
                    var from = pts[0];
                    var to = pts[^1];
                    canvas.DrawLine(from, to, paint);
                    if (a.Kind == AnnotationKind.Arrow)
                    {
                        float len = Math.Max(stroke * 4, 14 * (float)scale);
                        double angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
                        using var builder = new SKPathBuilder();
                        builder.MoveTo(to);
                        builder.LineTo(to.X - len * (float)Math.Cos(angle - 0.45), to.Y - len * (float)Math.Sin(angle - 0.45));
                        builder.LineTo(to.X - len * (float)Math.Cos(angle + 0.45), to.Y - len * (float)Math.Sin(angle + 0.45));
                        builder.Close();
                        using var head = builder.Detach();
                        paint.Style = SKPaintStyle.StrokeAndFill;
                        canvas.DrawPath(head, paint);
                    }
                    break;
                }
                case AnnotationKind.Pen when pts.Count >= 2:
                {
                    paint.Style = SKPaintStyle.Stroke;
                    using var builder = new SKPathBuilder();
                    builder.MoveTo(pts[0]);
                    for (int i = 1; i < pts.Count; i++) builder.LineTo(pts[i]);
                    using var path = builder.Detach();
                    canvas.DrawPath(path, paint);
                    break;
                }
                case AnnotationKind.Text when !string.IsNullOrWhiteSpace(a.Text):
                {
                    using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
                    using var font = new SKFont(typeface, (float)(a.FontSize * scale));
                    using var shaper = new SKShaper(typeface);
                    paint.Style = SKPaintStyle.Fill;
                    float y = pts[0].Y;
                    foreach (var line in a.Text.Replace("\r\n", "\n").Split('\n'))
                    {
                        canvas.DrawShapedText(shaper, line, pts[0].X, y + font.Size, SKTextAlign.Left, font, paint);
                        y += font.Size * 1.25f;
                    }
                    break;
                }
                case AnnotationKind.Step:
                {
                    step++;
                    float radius = (float)(Math.Max(a.FontSize, 24) * scale * 0.75);
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawCircle(pts[0], radius, paint);
                    using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
                    using var font = new SKFont(typeface, radius * 1.15f);
                    using var textPaint = new SKPaint { IsAntialias = true, Color = Luma(color) > 0.6 ? SKColors.Black : SKColors.White };
                    var label = step.ToString();
                    float width = font.MeasureText(label);
                    font.GetFontMetrics(out var metrics);
                    canvas.DrawText(label, pts[0].X - width / 2, pts[0].Y - (metrics.Ascent + metrics.Descent) / 2, SKTextAlign.Left, font, textPaint);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>Places the content on a styled canvas with padding, rounded corners and a soft shadow.</summary>
    private static SKBitmap Frame(SKBitmap content, BackgroundSettings bg, double scale)
    {
        var (width, height) = CanvasSize(content.Width, content.Height, bg, scale);
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new Toolbox.Core.ProcessingException("Not enough memory for the background canvas.");
        var canvas = surface.Canvas;
        DrawFill(canvas, width, height, bg, scale);

        float x = (width - content.Width) / 2f, y = (height - content.Height) / 2f;
        var rect = SKRect.Create(x, y, content.Width, content.Height);
        float radius = (float)Math.Min(Math.Max(0, bg.Corner) * scale, Math.Min(content.Width, content.Height) / 2.0);
        var rounded = new SKRoundRect(rect, radius, radius);

        if (bg.Shadow > 0)
        {
            float blur = (float)(bg.Shadow * scale);
            using var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, 92),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, Math.Max(0.5f, blur / 2f)),
            };
            var shadowRect = new SKRoundRect(SKRect.Create(x, y + blur * 0.35f, content.Width, content.Height), radius, radius);
            canvas.DrawRoundRect(shadowRect, shadow);
        }

        using (var image = SKImage.FromBitmap(content))
        {
            canvas.Save();
            canvas.ClipRoundRect(rounded, SKClipOperation.Intersect, antialias: true);
            canvas.DrawImage(image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            canvas.Restore();
        }

        var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.ReadPixels(result.Info, result.GetPixels(), result.RowBytes, 0, 0);
        return result;
    }

    private static void DrawFill(SKCanvas canvas, int width, int height, BackgroundSettings bg, double scale)
    {
        var fill = bg.Fill ?? "";
        if (fill.StartsWith('#'))
        {
            canvas.Clear(SKColor.Parse(fill));
            return;
        }
        if (fill.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
        {
            if (TryDrawImageFill(canvas, width, height, fill[6..], bg.ImageBlur * scale)) return;
            fill = "preset:slate";
        }

        var preset = BackgroundPresets.Find(fill.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) ? fill[7..] : fill)
                     ?? BackgroundPresets.Gradients[0];
        double radians = preset.Angle * Math.PI / 180;
        float cx = width / 2f, cy = height / 2f;
        float half = (float)(Math.Abs(width / 2.0 * Math.Cos(radians)) + Math.Abs(height / 2.0 * Math.Sin(radians)));
        var start = new SKPoint(cx - half * (float)Math.Cos(radians), cy - half * (float)Math.Sin(radians));
        var end = new SKPoint(cx + half * (float)Math.Cos(radians), cy + half * (float)Math.Sin(radians));
        using var shader = SKShader.CreateLinearGradient(start, end,
            preset.Stops.Select(s => SKColor.Parse(s.Color)).ToArray(),
            preset.Stops.Select(s => (float)s.Offset).ToArray(),
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawRect(0, 0, width, height, paint);

        if (preset.GlowColor is { } glow)
        {
            var glowColor = SKColor.Parse(glow);
            float r = Math.Max(width, height) * 0.7f;
            using var radial = SKShader.CreateRadialGradient(new SKPoint(width * 0.82f, height * 0.12f), r,
                [glowColor.WithAlpha(150), glowColor.WithAlpha(0)], [0f, 1f], SKShaderTileMode.Clamp);
            using var glowPaint = new SKPaint { Shader = radial, IsAntialias = true, BlendMode = SKBlendMode.Screen };
            canvas.DrawRect(0, 0, width, height, glowPaint);
        }
    }

    private static bool TryDrawImageFill(SKCanvas canvas, int width, int height, string path, double blur)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var loaded = ImageLoader.Load(Toolbox.Core.Inputs.FileProbe.Probe(path));
            using var normal = StudioSource.Normalize(loaded.Image);
            double cover = Math.Max((double)width / normal.Width, (double)height / normal.Height);
            using var resized = normal.Resize(cover);
            int x = Math.Max(0, (resized.Width - width) / 2), y = Math.Max(0, (resized.Height - height) / 2);
            using var cropped = resized.ExtractArea(x, y, Math.Min(width, resized.Width - x), Math.Min(height, resized.Height - y));
            using var blurred = blur >= 0.5 ? cropped.Gaussblur(blur).Cast(Enums.BandFormat.Uchar) : cropped.Copy();
            using var bitmap = ToSkBitmap(blurred);
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, SKRect.Create(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            return true;
        }
        catch (Exception ex) when (ex is Toolbox.Core.ProcessingException or VipsException or IOException)
        {
            return false;
        }
    }

    // ---- conversions -----------------------------------------------------------------------

    internal static SKBitmap ToSkBitmap(Image image)
    {
        using var rgba = image.Bands == 4 ? image.Copy() : image.Bandjoin(255);
        using var uchar = rgba.Format == Enums.BandFormat.Uchar ? rgba.Copy() : rgba.Cast(Enums.BandFormat.Uchar);
        var bytes = uchar.WriteToMemory<byte>();
        var bitmap = new SKBitmap(new SKImageInfo(uchar.Width, uchar.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, bitmap.GetPixels(), bytes.Length);
        return bitmap;
    }

    internal static Image FromSkBitmap(SKBitmap bitmap)
    {
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bytes = new byte[info.BytesSize];
        unsafe
        {
            fixed (byte* p = bytes) { using var pixmap = bitmap.PeekPixels(); pixmap.ReadPixels(info, (IntPtr)p, info.RowBytes, 0, 0); }
        }
        bool opaque = true;
        for (int i = 3; i < bytes.Length; i += 4)
        {
            if (bytes[i] != 255) { opaque = false; break; }
        }
        using var rgba = Image.NewFromMemoryCopy<byte>(bytes, bitmap.Width, bitmap.Height, 4, Enums.BandFormat.Uchar);
        return opaque
            ? rgba.ExtractBand(0, n: 3).Copy(interpretation: Enums.Interpretation.Srgb)
            : rgba.Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private static double Luma(SKColor c) => (0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue) / 255.0;

    private static Image Swap(Image old, Image next)
    {
        if (!ReferenceEquals(old, next)) old.Dispose();
        return next;
    }
}
