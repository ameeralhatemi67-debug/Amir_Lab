namespace Toolbox.Processing.Ocr;

/// <summary>A recognised word and its box in pixels of the image that was recognised.</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words, string Language)
{
    /// <summary>Reading direction of the line; <see cref="Words"/> are in reading order.</summary>
    public bool RightToLeft { get; init; }

    public double Top => Words.Count == 0 ? 0 : Words.Min(w => w.Y);
    public double Bottom => Words.Count == 0 ? 0 : Words.Max(w => w.Y + w.Height);
    public double Left => Words.Count == 0 ? 0 : Words.Min(w => w.X);
    public double Right => Words.Count == 0 ? 0 : Words.Max(w => w.X + w.Width);
}

public sealed record OcrPage(IReadOnlyList<OcrLine> Lines, int Width, int Height)
{
    /// <summary>Detected text rotation in degrees (clockwise), when the engine reports one.</summary>
    public double? TextAngle { get; init; }

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Plain text: one line per recognised line, a blank line between blocks that are far apart.</summary>
    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        OcrLine? previous = null;
        foreach (var line in Lines)
        {
            if (previous is not null)
            {
                double gap = line.Top - previous.Bottom;
                double height = Math.Max(1, previous.Bottom - previous.Top);
                sb.Append(gap > height * 1.2 ? "\r\n\r\n" : "\r\n");
            }
            sb.Append(line.Text);
            previous = line;
        }
        return sb.ToString();
    }
}

/// <summary>
/// A local recognition engine. The Windows build uses the OCR engine that ships with Windows
/// (Windows.Media.Ocr); a later macOS build can plug in its own.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Installed recognition languages as BCP-47 tags, e.g. "ar-SA", "en-US".</summary>
    IReadOnlyList<string> Languages { get; }

    /// <summary>Largest width or height the engine accepts.</summary>
    int MaxDimension { get; }

    /// <summary>Recognises text in tightly packed BGRA pixels with the given language.</summary>
    OcrPage Recognize(byte[] bgra, int width, int height, string languageTag);
}
