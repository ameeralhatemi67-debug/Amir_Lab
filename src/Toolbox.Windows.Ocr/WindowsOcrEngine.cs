using System.Runtime.InteropServices.WindowsRuntime;
using Toolbox.Core;
using Toolbox.Core.Diagnostics;
using Toolbox.Processing.Ocr;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using WinOcr = Windows.Media.Ocr;

namespace Toolbox.Windows.Ocr;

/// <summary>
/// <see cref="IOcrEngine"/> backed by Windows.Media.Ocr, the recogniser that ships with Windows.
/// Recognition languages are the ones the user installed with Windows' "Optical character
/// recognition" language feature; nothing is downloaded by the app.
/// </summary>
public sealed class WindowsOcrEngine : IOcrEngine
{
    private WindowsOcrEngine(IReadOnlyList<string> languages, int maxDimension)
    {
        Languages = languages;
        MaxDimension = maxDimension;
    }

    public IReadOnlyList<string> Languages { get; }

    public int MaxDimension { get; }

    /// <summary>Probes the platform engine; null when Windows OCR is unavailable (e.g. Windows N without media features).</summary>
    public static WindowsOcrEngine? TryCreate()
    {
        try
        {
            var languages = WinOcr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();
            return new WindowsOcrEngine(languages, (int)WinOcr.OcrEngine.MaxImageDimension);
        }
        catch (Exception ex)
        {
            Log.Warn($"Windows OCR is unavailable: {ex.Message}");
            return null;
        }
    }

    /// <summary>Probes and registers with the processing layer (sets <see cref="Toolbox.Core.Actions.RuntimeFeatures.Ocr"/>).</summary>
    public static void Register()
    {
        var engine = TryCreate();
        if (engine is null)
        {
            Toolbox.Core.Actions.RuntimeFeatures.Set(Toolbox.Core.Actions.RuntimeFeatures.Ocr, false,
                "Windows' built-in text recognition is not available on this computer.");
            return;
        }
        OcrService.Register(engine);
        Log.Info($"OCR languages: {string.Join(", ", engine.Languages)} (max {engine.MaxDimension} px).");
    }

    public OcrPage Recognize(byte[] bgra, int width, int height, string languageTag)
    {
        var engine = WinOcr.OcrEngine.TryCreateFromLanguage(new Language(languageTag))
                     ?? throw new ProcessingException($"Windows could not start text recognition for {languageTag}.");
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();

        var lines = new List<OcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Select(w => new OcrWord(w.Text, w.BoundingRect.X, w.BoundingRect.Y, w.BoundingRect.Width, w.BoundingRect.Height))
                .ToList();
            if (words.Count > 0) lines.Add(new OcrLine(line.Text, words, languageTag));
        }
        return new OcrPage(lines, width, height) { TextAngle = result.TextAngle };
    }
}
