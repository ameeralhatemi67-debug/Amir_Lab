using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toolbox.Processing.Studio;

/// <summary>Integer rectangle in pixels of the oriented (rotated/flipped) full-resolution source.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public PixelRect ClampTo(int width, int height)
    {
        int x = Math.Clamp(X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(Y, 0, Math.Max(0, height - 1));
        int w = Math.Clamp(Width, 1, width - x);
        int h = Math.Clamp(Height, 1, height - y);
        return new PixelRect(x, y, w, h);
    }
}

public sealed record CropSettings
{
    /// <summary>Clockwise rotation in degrees: 0, 90, 180 or 270.</summary>
    public int Rotation { get; init; }
    public bool FlipHorizontal { get; init; }
    public bool FlipVertical { get; init; }

    /// <summary>Kept area in oriented source pixels; null keeps everything.</summary>
    public PixelRect? Rect { get; init; }

    /// <summary>The aspect choice the rectangle was made with (free, original, 1:1, 16:9, ...).</summary>
    public string Aspect { get; init; } = "free";

    [JsonIgnore]
    public bool IsIdentity => Rotation % 360 == 0 && !FlipHorizontal && !FlipVertical && Rect is null;

    [JsonIgnore]
    public bool HasCrop => Rect is not null;
}

public sealed record AdjustSettings
{
    /// <summary>−100…100.</summary>
    public int Brightness { get; init; }
    public int Contrast { get; init; }
    public int Saturation { get; init; }

    /// <summary>0…100.</summary>
    public int Sharpen { get; init; }

    [JsonIgnore]
    public bool IsIdentity => Brightness == 0 && Contrast == 0 && Saturation == 0 && Sharpen == 0;
}

public sealed record BackgroundSettings
{
    public bool Enabled { get; init; }

    /// <summary>"preset:&lt;id&gt;", "#RRGGBB" or "image:&lt;path&gt;".</summary>
    public string Fill { get; init; } = "preset:dawn";

    /// <summary>Space around the image in output pixels.</summary>
    public int Padding { get; init; } = 120;

    /// <summary>Corner radius of the image in output pixels.</summary>
    public int Corner { get; init; } = 36;

    /// <summary>Shadow blur in output pixels (0 = no shadow).</summary>
    public int Shadow { get; init; } = 32;

    /// <summary>auto, 1:1, 16:9, 4:5, 9:16, 4:3, 3:2.</summary>
    public string Ratio { get; init; } = "auto";

    /// <summary>Blur applied to a picture background, in output pixels.</summary>
    public int ImageBlur { get; init; }
}

public enum AnnotationKind
{
    Rectangle,
    Ellipse,
    Arrow,
    Line,
    Pen,
    Highlight,
    Text,
    Step,
}

/// <summary>A drawn mark, in pixels of the cropped content (before background framing).</summary>
public sealed record Annotation
{
    public AnnotationKind Kind { get; init; }
    public List<double> Points { get; init; } = [];
    public string Color { get; init; } = "#E5484D";
    public double Stroke { get; init; } = 6;
    public string Text { get; init; } = "";
    public double FontSize { get; init; } = 48;
    public bool Filled { get; init; }
}

public enum RedactionMode
{
    /// <summary>Solid colour written into the pixels: the only mode described as removal.</summary>
    Solid,

    /// <summary>Obscuring only — not secure removal.</summary>
    Pixelate,

    /// <summary>Obscuring only — not secure removal.</summary>
    Blur,
}

public sealed record Redaction(PixelRect Rect, RedactionMode Mode = RedactionMode.Solid, string Color = "#000000");

public sealed record ExportSettings
{
    /// <summary>auto, png, jpg, webp, avif, tiff, bmp.</summary>
    public string Format { get; init; } = "auto";
    public int Quality { get; init; } = 90;

    /// <summary>Longest output side in pixels; null keeps the rendered size.</summary>
    public int? MaxEdge { get; init; }

    /// <summary>Requested maximum file size; met when possible within the quality and size limits.</summary>
    public long? TargetBytes { get; init; }

    public int MinQuality { get; init; } = 40;
    public bool AllowDownscale { get; init; } = true;

    /// <summary>Background for JPG/BMP when the result has transparency.</summary>
    public string Matte { get; init; } = "#FFFFFF";
}

/// <summary>
/// A complete, non-destructive editing session. All geometry is stored in full-resolution units,
/// so the same document renders the small live preview and the final export identically.
/// </summary>
/// <summary>Logo and caption drawn on the final canvas (resolved from a brand preset).</summary>
public sealed record BrandingSettings
{
    public string? LogoPath { get; init; }
    public string LogoPosition { get; init; } = "bottom-right";
    public double LogoScale { get; init; } = 0.14;
    public double LogoOpacity { get; init; } = 0.9;
    public string? Caption { get; init; }
    public string CaptionPosition { get; init; } = "bottom";
    public string CaptionColor { get; init; } = "#1F2937";
    public double CaptionScale { get; init; } = 0.035;
    public string FontFamily { get; init; } = "Segoe UI";

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(LogoPath) && string.IsNullOrWhiteSpace(Caption);

    public static BrandingSettings From(Toolbox.Core.Presets.BrandPreset p) => new()
    {
        LogoPath = p.LogoPath,
        LogoPosition = p.LogoPosition,
        LogoScale = p.LogoScale,
        LogoOpacity = p.LogoOpacity,
        Caption = p.Caption,
        CaptionPosition = p.CaptionPosition,
        CaptionColor = p.CaptionColor,
        CaptionScale = p.CaptionScale,
        FontFamily = p.FontFamily,
    };
}

public sealed record EditDocument
{
    /// <summary>Logo and caption; null for none.</summary>
    public BrandingSettings? Brand { get; init; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string SourcePath { get; init; } = "";

    /// <summary>For PDFs: the page (1-based) and render resolution.</summary>
    public int Page { get; init; } = 1;
    public double Dpi { get; init; } = 300;

    public CropSettings Crop { get; init; } = new();
    public AdjustSettings Adjust { get; init; } = new();
    public BackgroundSettings Background { get; init; } = new();
    public List<Annotation> Annotations { get; init; } = [];
    public List<Redaction> Redactions { get; init; } = [];
    public ExportSettings Export { get; init; } = new();

    /// <summary>Suffix explaining what was done, in pipeline order: " Cropped With Background".</summary>
    public string Suffix(string? fallback = null)
    {
        var parts = new List<string>();
        if (Crop.HasCrop) parts.Add("Cropped");
        else if (!Crop.IsIdentity) parts.Add("Rotated");
        if (!Adjust.IsIdentity) parts.Add("Edited");
        if (Redactions.Count > 0) parts.Add("Redacted");
        if (Annotations.Count > 0) parts.Add("Annotated");
        if (Background.Enabled) parts.Add("With Background");
        if (parts.Count == 0) return fallback ?? (Export.TargetBytes is not null || Export.MaxEdge is not null ? " Compressed" : " Edited");
        return " " + string.Join(" ", parts);
    }

    public bool HasEdits => !Crop.IsIdentity || !Adjust.IsIdentity || Background.Enabled || Annotations.Count > 0 || Redactions.Count > 0;

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static EditDocument FromJson(string json) =>
        JsonSerializer.Deserialize<EditDocument>(json, Json) ?? throw new FormatException("Invalid edit document.");
}
