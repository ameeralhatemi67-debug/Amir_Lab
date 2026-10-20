using NetVips;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Toolbox.Processing;
using Toolbox.Processing.Ocr;
using Toolbox.Windows.Ocr;

namespace Toolbox.Windows.Tests;

/// <summary>Runs the real Windows recogniser on rendered text (needs the Windows OCR languages installed).</summary>
public sealed class WindowsOcrTests
{
    private static Image Render(string text, string family)
    {
        ProcessingEngine.Initialize();
        using var surface = SKSurface.Create(new SKImageInfo(1400, 220, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var typeface = SKTypeface.FromFamilyName(family);
        using var font = new SKFont(typeface, 64);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        using var shaper = new SKShaper(typeface);
        canvas.DrawShapedText(shaper, text, 60, 130, SKTextAlign.Left, font, paint);
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return Image.NewFromBuffer(data.ToArray());
    }

    private static bool Ready(string prefix)
    {
        var engine = WindowsOcrEngine.TryCreate();
        if (engine is null || !engine.Languages.Any(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        OcrService.Register(engine);
        return true;
    }

    [Fact]
    public void Recognises_english()
    {
        if (!Ready("en")) return; // English OCR not installed on this machine
        using var image = Render("Invoice total 1250 due Friday", "Arial");
        var text = OcrService.Recognize(image, "eng").ToText();
        Assert.Equal("Invoice total 1250 due Friday", text);
    }

    [Fact]
    public void Recognises_arabic_in_reading_order()
    {
        if (!Ready("ar")) return; // Arabic OCR not installed on this machine
        using var image = Render("يرجى مراجعة الفاتورة المرفقة", "Arial");
        var page = OcrService.Recognize(image, "ara+eng");
        Assert.Equal("يرجى مراجعة الفاتورة المرفقة", page.ToText());
        Assert.True(page.Lines[0].RightToLeft);
    }
}
