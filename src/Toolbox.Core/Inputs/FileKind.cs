namespace Toolbox.Core.Inputs;

/// <summary>The actual content type of a file, decided from its bytes rather than its extension.</summary>
public enum FileKind
{
    Unknown,
    Pdf,
    Jpeg,
    Png,
    WebP,
    Avif,
    Heic,
    Tiff,
    Gif,
    Bmp,
    Svg,
    Docx,
    Text,
}

public static class FileKinds
{
    public static readonly IReadOnlySet<FileKind> RasterKinds = new HashSet<FileKind>
    {
        FileKind.Jpeg, FileKind.Png, FileKind.WebP, FileKind.Avif, FileKind.Heic,
        FileKind.Tiff, FileKind.Gif, FileKind.Bmp,
    };

    public static bool IsRaster(this FileKind kind) => RasterKinds.Contains(kind);

    /// <summary>Raster images plus SVG (which is rasterised on load).</summary>
    public static bool IsImage(this FileKind kind) => kind.IsRaster() || kind == FileKind.Svg;

    /// <summary>Preferred extension, including the dot.</summary>
    public static string Extension(this FileKind kind) => kind switch
    {
        FileKind.Pdf => ".pdf",
        FileKind.Jpeg => ".jpg",
        FileKind.Png => ".png",
        FileKind.WebP => ".webp",
        FileKind.Avif => ".avif",
        FileKind.Heic => ".heic",
        FileKind.Tiff => ".tiff",
        FileKind.Gif => ".gif",
        FileKind.Bmp => ".bmp",
        FileKind.Svg => ".svg",
        FileKind.Docx => ".docx",
        FileKind.Text => ".txt",
        _ => "",
    };

    /// <summary>Short uppercase label used on the wheel and in messages.</summary>
    public static string Label(this FileKind kind) => kind switch
    {
        FileKind.Jpeg => "JPG",
        FileKind.WebP => "WebP",
        FileKind.Text => "TXT",
        FileKind.Unknown => "Unknown",
        _ => kind.ToString().ToUpperInvariant(),
    };

    /// <summary>Whether the format can carry an alpha channel.</summary>
    public static bool SupportsTransparency(this FileKind kind) =>
        kind is FileKind.Png or FileKind.WebP or FileKind.Avif or FileKind.Tiff or FileKind.Gif or FileKind.Heic;
}
