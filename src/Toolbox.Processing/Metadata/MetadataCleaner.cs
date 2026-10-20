using System.Text;
using NetVips;
using PdfSharp.Pdf.IO;
using Toolbox.Core;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Imaging;

namespace Toolbox.Processing.Metadata;

/// <summary>What to remove. "All" keeps only the colour profile (and orientation, so photos stay upright).</summary>
[Flags]
public enum MetadataCategories
{
    None = 0,
    Location = 1,
    Camera = 2,
    Dates = 4,
    Author = 8,
    Xmp = 16,
    Iptc = 32,
    Thumbnail = 64,
    Comments = 128,
    All = Location | Camera | Dates | Author | Xmp | Iptc | Thumbnail | Comments | 256,
}

public static class MetadataCategoryNames
{
    public static MetadataCategories Parse(string text)
    {
        var result = MetadataCategories.None;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result |= part.ToLowerInvariant() switch
            {
                "all" => MetadataCategories.All,
                "location" or "gps" => MetadataCategories.Location,
                "camera" => MetadataCategories.Camera,
                "dates" => MetadataCategories.Dates,
                "author" => MetadataCategories.Author,
                "xmp" => MetadataCategories.Xmp,
                "iptc" => MetadataCategories.Iptc,
                "thumbnail" => MetadataCategories.Thumbnail,
                "comments" => MetadataCategories.Comments,
                _ => throw new ProcessingException($"Unknown metadata category '{part}'."),
            };
        }
        return result;
    }
}

/// <summary>
/// Removes metadata without touching the picture where the format allows it:
/// JPEG is rewritten segment by segment (the compressed image data is copied byte for byte);
/// PNG and TIFF are lossless anyway; WebP and AVIF are re-encoded at high quality (and say so).
/// PDFs lose their document-information dictionary and XMP stream.
/// </summary>
public static class MetadataCleaner
{
    private static readonly HashSet<ushort> CameraTags =
    [
        0x010F, 0x0110, 0x0131, 0x013C,             // Make, Model, Software, HostComputer
        0xA430, 0xA431, 0xA432, 0xA433, 0xA434, 0xA435, // owner, body serial, lens spec/make/model/serial
        0x927C,                                     // MakerNote (vendor data, can hold serials and locations)
    ];

    private static readonly HashSet<ushort> DateTags =
    [
        0x0132, 0x9003, 0x9004, 0x9010, 0x9011, 0x9012, 0x9290, 0x9291, 0x9292,
    ];

    private static readonly HashSet<ushort> AuthorTags =
    [
        0x010E, 0x013B, 0x8298,                     // ImageDescription, Artist, Copyright
        0x9C9B, 0x9C9C, 0x9C9D, 0x9C9E, 0x9C9F,     // XPTitle, XPComment, XPAuthor, XPKeywords, XPSubject
        0x9286, 0xA420,                             // UserComment, ImageUniqueID
    ];

    // libexif tag names as used in libvips "exif-ifdN-Name" fields.
    private static readonly HashSet<string> CameraFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Make", "Model", "Software", "HostComputer", "CameraOwnerName", "BodySerialNumber",
        "LensSpecification", "LensMake", "LensModel", "LensSerialNumber", "MakerNote",
    };

    private static readonly HashSet<string> DateFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DateTime", "DateTimeOriginal", "DateTimeDigitized", "OffsetTime", "OffsetTimeOriginal", "OffsetTimeDigitized",
        "SubSecTime", "SubSecTimeOriginal", "SubSecTimeDigitized", "SubsecTime", "SubSecTimeOriginal",
    };

    private static readonly HashSet<string> AuthorFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ImageDescription", "Artist", "Copyright", "XPTitle", "XPComment", "XPAuthor", "XPKeywords", "XPSubject",
        "UserComment", "ImageUniqueID",
    };

    public static IReadOnlyList<string> Clean(ProbedFile file, MetadataCategories remove, string outputPath)
    {
        return file.Kind switch
        {
            FileKind.Jpeg => CleanJpeg(File.ReadAllBytes(file.Path), remove, outputPath),
            FileKind.Pdf => CleanPdf(file.Path, outputPath),
            FileKind.Png or FileKind.Tiff or FileKind.WebP or FileKind.Avif => CleanByReencoding(file, remove, outputPath),
            _ => throw new ProcessingException($"Removing metadata from {file.Kind.Label()} files is not supported."),
        };
    }

    // ---- JPEG ---------------------------------------------------------------------------------

    internal static IReadOnlyList<string> CleanJpeg(byte[] jpeg, MetadataCategories remove, string outputPath)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) throw new ProcessingException("The JPEG file is not valid.");
        var notes = new List<string>();
        bool all = remove.HasFlag(MetadataCategories.All);
        using var output = new MemoryStream(jpeg.Length);
        output.Write(jpeg, 0, 2);
        int pos = 2;
        bool wroteExif = false;
        int imageEnd = jpeg.Length;

        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) throw new ProcessingException("The JPEG structure is damaged.");
            byte marker = jpeg[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // fill bytes
            if (marker == 0xDA)
            {
                // Start of scan: copy the rest of the image (all scans) unchanged up to the end-of-image marker.
                imageEnd = FindEndOfImage(jpeg, pos);
                output.Write(jpeg, pos, imageEnd - pos);
                break;
            }
            int length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            int segmentEnd = pos + 2 + length;
            if (length < 2 || segmentEnd > jpeg.Length) throw new ProcessingException("The JPEG structure is damaged.");
            var payload = jpeg.AsSpan(pos + 4, length - 2);

            bool keep = true;
            if (marker == 0xE1 && payload.StartsWith("Exif\0\0"u8))
            {
                keep = false;
                var cleaned = CleanExif(payload[6..].ToArray(), remove, all, notes);
                if (cleaned is not null && !wroteExif)
                {
                    WriteSegment(output, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. cleaned]);
                    wroteExif = true;
                }
            }
            else if (marker == 0xE1 && payload.StartsWith("http://ns.adobe.com/"u8))
            {
                keep = !(all || remove.HasFlag(MetadataCategories.Xmp) || remove.HasFlag(MetadataCategories.Location));
                if (!keep) Add(notes, "Removed the XMP block (it can repeat location, dates and authorship).");
            }
            else if (marker == 0xED)
            {
                keep = !(all || remove.HasFlag(MetadataCategories.Iptc));
                if (!keep) Add(notes, "Removed IPTC data (captions, keywords, credits).");
            }
            else if (marker == 0xFE)
            {
                keep = !(all || remove.HasFlag(MetadataCategories.Comments));
                if (!keep) Add(notes, "Removed comments.");
            }
            else if (marker == 0xE2 && payload.StartsWith("MPF\0"u8))
            {
                // Multi-picture index: refers to extra images after the main one, which may carry their own metadata.
                keep = !(all || remove.HasFlag(MetadataCategories.Location));
            }
            else if (marker is >= 0xE3 and <= 0xEF && marker != 0xEE)
            {
                // Vendor APPn blocks (other than APP14 "Adobe", which affects colour decoding).
                keep = !all;
                if (!keep) Add(notes, "Removed vendor-specific blocks.");
            }

            if (keep) output.Write(jpeg, pos, segmentEnd - pos);
            pos = segmentEnd;
        }

        if (imageEnd < jpeg.Length)
        {
            bool dropTrailer = all || remove.HasFlag(MetadataCategories.Location);
            if (dropTrailer) Add(notes, "Removed extra images stored after the main picture (such as depth or HDR gain maps), which can carry their own metadata.");
            else output.Write(jpeg, imageEnd, jpeg.Length - imageEnd);
        }
        Add(notes, "The picture data was copied unchanged (no recompression).");
        File.WriteAllBytes(outputPath, output.ToArray());
        return notes;
    }

    private static byte[]? CleanExif(byte[] tiff, MetadataCategories remove, bool all, List<string> notes)
    {
        var editor = ExifEditor.TryOpen(tiff);
        if (editor is null)
        {
            Add(notes, "Removed an unreadable EXIF block.");
            return null;
        }
        if (all)
        {
            Add(notes, "Removed all EXIF data (camera, location, dates, software, thumbnail).");
            return editor.Orientation is { } o and > 1 ? ExifEditor.MinimalWithOrientation(o) : null;
        }
        if (remove.HasFlag(MetadataCategories.Location) && editor.RemoveGps()) Add(notes, "Removed GPS location.");
        if (remove.HasFlag(MetadataCategories.Camera) && editor.RemoveTags(CameraTags) > 0) Add(notes, "Removed camera and device details.");
        if (remove.HasFlag(MetadataCategories.Dates) && editor.RemoveTags(DateTags) > 0) Add(notes, "Removed dates and times.");
        if (remove.HasFlag(MetadataCategories.Author) && editor.RemoveTags(AuthorTags) > 0) Add(notes, "Removed author, copyright and descriptions.");
        if ((remove.HasFlag(MetadataCategories.Thumbnail) || remove.HasFlag(MetadataCategories.Location)) && editor.RemoveThumbnail())
            Add(notes, "Removed the embedded thumbnail (it can show the original, uncropped picture).");
        return editor.Data;
    }

    /// <summary>Finds the end of the image (just after EOI), walking scans and the segments between them.</summary>
    private static int FindEndOfImage(byte[] jpeg, int sos)
    {
        int pos = sos;
        while (pos + 4 <= jpeg.Length)
        {
            byte marker = jpeg[pos + 1];
            if (marker == 0xD9) return pos + 2;
            int length = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            pos += 2 + length;
            if (marker == 0xDA)
            {
                // Entropy-coded data: ends at the next marker that is not a stuffed byte or a restart marker.
                while (pos + 1 < jpeg.Length)
                {
                    if (jpeg[pos] == 0xFF && jpeg[pos + 1] != 0x00 && (jpeg[pos + 1] < 0xD0 || jpeg[pos + 1] > 0xD7)) break;
                    pos++;
                }
            }
        }
        return jpeg.Length;
    }

    private static void WriteSegment(Stream output, byte marker, byte[] payload)
    {
        int length = payload.Length + 2;
        if (length > 0xFFFF) throw new ProcessingException("The EXIF block is too large.");
        output.WriteByte(0xFF);
        output.WriteByte(marker);
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)(length & 0xFF));
        output.Write(payload);
    }

    // ---- other image formats ------------------------------------------------------------------

    private static IReadOnlyList<string> CleanByReencoding(ProbedFile file, MetadataCategories remove, string outputPath)
    {
        var notes = new List<string>();
        using var loaded = ImageLoader.Load(file, autorotate: true);
        var image = loaded.Image;
        bool all = remove.HasFlag(MetadataCategories.All);
        Image prepared;
        if (all || !image.Contains("exif-data"))
        {
            // Drop every metadata field except the colour profile (EXIF, XMP, IPTC, PNG text chunks, ...).
            var fields = image.GetFields().Where(f =>
                f is "exif-data" or "xmp-data" or "iptc-data" or "photoshop-data"
                || f.StartsWith("exif-", StringComparison.Ordinal)
                || f.StartsWith("png-comment", StringComparison.Ordinal)).ToList();
            prepared = image.Mutate(m =>
            {
                foreach (var field in fields)
                    if (m.Contains(field)) m.Remove(field);
            });
            Add(notes, "Removed EXIF, XMP, IPTC and text metadata; kept the colour profile.");
        }
        else
        {
            // libvips re-serialises EXIF from its "exif-ifdN-Name" fields when saving and drops entries
            // whose field was removed, so selective removal works on those fields (no stale bytes remain).
            var fields = image.GetFields();
            var drop = new List<string>();
            foreach (var field in fields)
            {
                if (!field.StartsWith("exif-ifd", StringComparison.Ordinal) || field.Length < 10) continue;
                char ifd = field[8];
                var tagName = field[(field.IndexOf('-', 9) + 1)..];
                bool hit = (remove.HasFlag(MetadataCategories.Location) && ifd == '3')
                           || ((remove.HasFlag(MetadataCategories.Thumbnail) || remove.HasFlag(MetadataCategories.Location)) && ifd == '1')
                           || (remove.HasFlag(MetadataCategories.Camera) && CameraFieldNames.Contains(tagName))
                           || (remove.HasFlag(MetadataCategories.Dates) && DateFieldNames.Contains(tagName))
                           || (remove.HasFlag(MetadataCategories.Author) && AuthorFieldNames.Contains(tagName));
                if (hit) drop.Add(field);
            }
            if (remove.HasFlag(MetadataCategories.Xmp) || remove.HasFlag(MetadataCategories.Location)) drop.Add("xmp-data");
            if (remove.HasFlag(MetadataCategories.Iptc)) drop.Add("iptc-data");
            prepared = image.Mutate(m =>
            {
                foreach (var field in drop)
                    if (m.Contains(field)) m.Remove(field);
            });
            if (drop.Any(f => f.StartsWith("exif-ifd3", StringComparison.Ordinal))) Add(notes, "Removed GPS location.");
            if (drop.Count > 0) Add(notes, $"Removed {drop.Count} metadata field(s).");
        }
        using (prepared)
        {
            var settings = new EncodeSettings { Quality = 95, KeepMetadata = true };
            ImageEncoder.Save(prepared, file.Kind, outputPath, settings);
        }
        if (file.Kind is FileKind.WebP or FileKind.Avif)
            Add(notes, $"{file.Kind.Label()} files are re-encoded at high quality to remove metadata, so the copy is not bit-identical.");
        return notes;
    }

    // ---- PDF ------------------------------------------------------------------------------------

    private static IReadOnlyList<string> CleanPdf(string path, string outputPath)
    {
        var notes = new List<string>();
        try
        {
            using var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
            int count = document.Info.Elements.Count;
            document.Info.Elements.Clear();
            if (document.Internals.Catalog.Elements.ContainsKey("/Metadata"))
            {
                document.Internals.Catalog.Elements.Remove("/Metadata");
                Add(notes, "Removed the XMP metadata stream.");
            }
            Add(notes, count > 0 ? $"Removed the document information ({count} fields such as title, author and software)." : "The document information was already empty.");
            Add(notes, "Earlier saved revisions are not carried over. Attachments, form data, annotations and visible text are unchanged.");
            document.Save(outputPath);
        }
        catch (PdfReaderException ex)
        {
            throw new ProcessingException($"The PDF could not be rewritten: {ex.Message}", ex);
        }
        return notes;
    }

    private static void Add(List<string> notes, string note)
    {
        if (!notes.Contains(note)) notes.Add(note);
    }
}
