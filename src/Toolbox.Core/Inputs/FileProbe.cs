using System.Text;

namespace Toolbox.Core.Inputs;

/// <summary>A dropped or selected path together with what it actually contains.</summary>
public sealed record ProbedFile(string Path, FileKind Kind, long SizeBytes, string? Problem)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string BaseName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";
    public bool IsUsable => Problem is null && Kind != FileKind.Unknown;
}

/// <summary>
/// Identifies file content from magic bytes. Extensions are only a tie-breaker for formats that
/// have no reliable signature (SVG, plain text, DOCX inside a ZIP container).
/// </summary>
public static class FileProbe
{
    private const int HeadSize = 1024;

    public static ProbedFile Probe(string path)
    {
        try
        {
            if (System.IO.Directory.Exists(path))
                return new ProbedFile(path, FileKind.Unknown, 0, "Folders are not supported yet.");

            var info = new FileInfo(path);
            if (!info.Exists)
                return new ProbedFile(path, FileKind.Unknown, 0, "The file no longer exists.");
            if (info.Length == 0)
                return new ProbedFile(path, FileKind.Unknown, 0, "The file is empty.");

            Span<byte> head = stackalloc byte[HeadSize];
            int read;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                read = stream.ReadAtLeast(head, Math.Min(HeadSize, (int)Math.Min(info.Length, HeadSize)), throwOnEndOfStream: false);

            var kind = Sniff(head[..read], info.Extension);
            var problem = kind == FileKind.Unknown ? $"Unsupported file type ({DescribeExtension(info.Extension)})." : null;
            return new ProbedFile(path, kind, info.Length, problem);
        }
        catch (UnauthorizedAccessException)
        {
            return new ProbedFile(path, FileKind.Unknown, 0, "Access to the file was denied.");
        }
        catch (IOException ex)
        {
            return new ProbedFile(path, FileKind.Unknown, 0, $"The file could not be read: {ex.Message}");
        }
    }

    public static IReadOnlyList<ProbedFile> ProbeAll(IEnumerable<string> paths) => paths.Select(Probe).ToList();

    public static FileKind Sniff(ReadOnlySpan<byte> head, string extension)
    {
        var ext = extension.ToLowerInvariant();

        if (StartsWith(head, [0xFF, 0xD8, 0xFF])) return FileKind.Jpeg;
        if (StartsWith(head, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return FileKind.Png;
        if (StartsWith(head, "GIF87a"u8) || StartsWith(head, "GIF89a"u8)) return FileKind.Gif;
        if (StartsWith(head, [0x49, 0x49, 0x2A, 0x00]) || StartsWith(head, [0x4D, 0x4D, 0x00, 0x2A])) return FileKind.Tiff;
        if (head.Length >= 12 && StartsWith(head, "RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8)) return FileKind.WebP;
        if (head.Length >= 14 && StartsWith(head, "BM"u8) && IsPlausibleBmp(head)) return FileKind.Bmp;

        var isoBrand = IsoBmffKind(head);
        if (isoBrand != FileKind.Unknown) return isoBrand;

        // PDF headers may be preceded by junk bytes; the spec allows the header within the first 1024 bytes.
        if (head.IndexOf("%PDF-"u8) >= 0) return FileKind.Pdf;

        if (StartsWith(head, [0x50, 0x4B, 0x03, 0x04]) && ext == ".docx") return FileKind.Docx;

        if (ext is ".svg" && LooksLikeSvg(head)) return FileKind.Svg;
        if (ext is ".txt" or ".md" or ".csv" && LooksLikeText(head)) return FileKind.Text;

        return FileKind.Unknown;
    }

    private static FileKind IsoBmffKind(ReadOnlySpan<byte> head)
    {
        if (head.Length < 16 || !head.Slice(4, 4).SequenceEqual("ftyp"u8)) return FileKind.Unknown;

        int boxSize = (head[0] << 24) | (head[1] << 16) | (head[2] << 8) | head[3];
        int end = Math.Min(head.Length, boxSize > 16 ? boxSize : 16);

        bool avif = false, heic = false;
        void Check(ReadOnlySpan<byte> brand)
        {
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8)) avif = true;
            else if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("hevc"u8)
                     || brand.SequenceEqual("hevx"u8) || brand.SequenceEqual("heim"u8) || brand.SequenceEqual("heis"u8))
                heic = true;
        }

        Check(head.Slice(8, 4)); // major brand
        for (int i = 16; i + 4 <= end; i += 4) Check(head.Slice(i, 4)); // compatible brands

        if (avif) return FileKind.Avif;
        if (heic) return FileKind.Heic;
        // "mif1"/"msf1" alone is a generic HEIF container; treat it as HEIC (HEVC is by far the most common payload).
        var major = head.Slice(8, 4);
        if (major.SequenceEqual("mif1"u8) || major.SequenceEqual("msf1"u8)) return FileKind.Heic;
        return FileKind.Unknown;
    }

    private static bool IsPlausibleBmp(ReadOnlySpan<byte> head)
    {
        // DIB header size at offset 14 is one of the known BITMAPINFOHEADER variants.
        if (head.Length < 18) return false;
        int dib = head[14] | (head[15] << 8) | (head[16] << 16) | (head[17] << 24);
        return dib is 12 or 40 or 52 or 56 or 64 or 108 or 124;
    }

    private static bool LooksLikeSvg(ReadOnlySpan<byte> head)
    {
        var text = Encoding.UTF8.GetString(head);
        return text.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        foreach (var b in head)
            if (b == 0) return false;
        return true;
    }

    private static bool StartsWith(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prefix) =>
        data.Length >= prefix.Length && data[..prefix.Length].SequenceEqual(prefix);

    private static string DescribeExtension(string extension) =>
        string.IsNullOrEmpty(extension) ? "no extension" : extension.ToLowerInvariant();
}
