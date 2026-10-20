using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Icc;
using MetadataExtractor.Formats.Iptc;
using MetadataExtractor.Formats.Jpeg;
using MetadataExtractor.Formats.Xmp;
using Toolbox.Core;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Pdf;
using Directory = MetadataExtractor.Directory;

namespace Toolbox.Processing.Metadata;

public sealed record MetadataItem(string Name, string Value);

/// <summary>One category of metadata as shown to the user, with the removal option that clears it.</summary>
public sealed record MetadataGroup(string Key, string Title, string Explanation, IReadOnlyList<MetadataItem> Items, bool Sensitive, MetadataCategories RemovedBy);

/// <summary>Reads what a file carries besides its picture or pages, grouped the way people think about it.</summary>
public static class MetadataInspector
{
    public static IReadOnlyList<MetadataGroup> Inspect(ProbedFile file)
    {
        if (file.Kind == FileKind.Pdf) return InspectPdf(file.Path);
        IReadOnlyList<Directory> directories;
        try
        {
            directories = ImageMetadataReader.ReadMetadata(file.Path);
        }
        catch (Exception ex) when (ex is ImageProcessingException or IOException)
        {
            throw new ProcessingException($"The metadata could not be read: {ex.Message}", ex);
        }

        var groups = new List<MetadataGroup>();
        var gps = directories.OfType<GpsDirectory>().FirstOrDefault();
        if (gps is not null && gps.Tags.Count > 0)
        {
            var items = new List<MetadataItem>();
            if (gps.TryGetGeoLocation(out var location))
                items.Add(new("Coordinates", $"{location.Latitude:F5}, {location.Longitude:F5}"));
            items.AddRange(Items(gps, maxItems: 8));
            groups.Add(new("location", "Location (GPS)", "Where the photo was taken. Anyone who receives the file can see this.", items, true, MetadataCategories.Location));
        }

        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var sub = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var camera = Pick(ifd0, ExifDirectoryBase.TagMake, ExifDirectoryBase.TagModel, ExifDirectoryBase.TagSoftware, ExifDirectoryBase.TagHostComputer)
            .Concat(Pick(sub, ExifDirectoryBase.TagBodySerialNumber, ExifDirectoryBase.TagLensMake, ExifDirectoryBase.TagLensModel,
                ExifDirectoryBase.TagLensSerialNumber, ExifDirectoryBase.TagCameraOwnerName)).ToList();
        var makerNotes = directories.Where(d => d.Name.Contains("Makernote", StringComparison.OrdinalIgnoreCase)).ToList();
        if (makerNotes.Count > 0) camera.Add(new("Maker notes", string.Join(", ", makerNotes.Select(d => $"{d.Name} ({d.TagCount} fields)"))));
        if (camera.Count > 0)
            groups.Add(new("camera", "Camera and device", "The device, lens, serial numbers and software that made or edited the file.", camera, true, MetadataCategories.Camera));

        var dates = Pick(ifd0, ExifDirectoryBase.TagDateTime)
            .Concat(Pick(sub, ExifDirectoryBase.TagDateTimeOriginal, ExifDirectoryBase.TagDateTimeDigitized, ExifDirectoryBase.TagTimeZone, ExifDirectoryBase.TagTimeZoneOriginal)).ToList();
        if (dates.Count > 0)
            groups.Add(new("dates", "Dates and times", "When the photo was taken or last changed.", dates, false, MetadataCategories.Dates));

        var author = Pick(ifd0, ExifDirectoryBase.TagImageDescription, ExifDirectoryBase.TagArtist, ExifDirectoryBase.TagCopyright,
                ExifDirectoryBase.TagWinTitle, ExifDirectoryBase.TagWinAuthor, ExifDirectoryBase.TagWinComment, ExifDirectoryBase.TagWinKeywords, ExifDirectoryBase.TagWinSubject)
            .Concat(Pick(sub, ExifDirectoryBase.TagUserComment, ExifDirectoryBase.TagImageUniqueId)).ToList();
        if (author.Count > 0)
            groups.Add(new("author", "Author and description", "Names, copyright notices, titles and comments.", author, true, MetadataCategories.Author));

        var thumbnail = directories.OfType<ExifThumbnailDirectory>().FirstOrDefault();
        if (thumbnail is not null && thumbnail.TagCount > 0)
            groups.Add(new("thumbnail", "Embedded thumbnail", "A small preview stored inside the file. After cropping in other apps it can still show the original picture.",
                [new("Thumbnail", "present")], true, MetadataCategories.Thumbnail));

        var xmp = directories.OfType<XmpDirectory>().FirstOrDefault();
        if (xmp is not null)
        {
            var props = xmp.GetXmpProperties();
            groups.Add(new("xmp", "XMP", "Extensible metadata used by editing apps; it can repeat location, dates and authorship.",
                props.Take(10).Select(p => new MetadataItem(p.Key, p.Value)).Append(new("Fields", props.Count.ToString())).ToList(), true, MetadataCategories.Xmp));
        }

        var iptc = directories.OfType<IptcDirectory>().FirstOrDefault();
        if (iptc is not null && iptc.TagCount > 0)
            groups.Add(new("iptc", "IPTC", "Captions, keywords and credits used by news and photo agencies.", Items(iptc, 10).ToList(), true, MetadataCategories.Iptc));

        var comments = directories.OfType<JpegCommentDirectory>().SelectMany(d => Items(d, 5))
            .Concat(directories.Where(d => d.Name.StartsWith("PNG-tEXt", StringComparison.Ordinal) || d.Name.StartsWith("PNG-iTXt", StringComparison.Ordinal)).SelectMany(d => Items(d, 5)))
            .ToList();
        if (comments.Count > 0)
            groups.Add(new("comments", "Comments and text", "Free text stored by the software that saved the file.", comments, false, MetadataCategories.Comments));

        var icc = directories.OfType<IccDirectory>().FirstOrDefault();
        if (icc is not null)
            groups.Add(new("icc", "Colour profile", "Describes the colours so they display correctly. It is always kept.",
                Pick(icc, IccDirectory.TagTagDesc, IccDirectory.TagColorSpace, IccDirectory.TagTagCprt).ToList(), false, MetadataCategories.None));

        var other = Pick(sub, ExifDirectoryBase.TagExposureTime, ExifDirectoryBase.TagFNumber, ExifDirectoryBase.TagIsoEquivalent,
            ExifDirectoryBase.TagFocalLength, ExifDirectoryBase.TagFlash, ExifDirectoryBase.TagWhiteBalance).ToList();
        if (other.Count > 0)
            groups.Add(new("other", "Other camera settings", "Exposure settings. Removed only by \"Remove everything\".", other, false, MetadataCategories.All));

        return groups;
    }

    private static IReadOnlyList<MetadataGroup> InspectPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        var items = new List<MetadataItem>();
        foreach (var (tag, label) in new[] { ("Title", "Title"), ("Author", "Author"), ("Subject", "Subject"), ("Keywords", "Keywords"),
                     ("Creator", "Created with"), ("Producer", "Produced by"), ("CreationDate", "Created"), ("ModDate", "Modified") })
        {
            var value = pdf.GetMetaText(tag);
            if (!string.IsNullOrWhiteSpace(value)) items.Add(new(label, value));
        }
        var groups = new List<MetadataGroup>();
        if (items.Count > 0)
            groups.Add(new("pdfinfo", "Document information", "Title, author, software and dates stored in the PDF.", items, true, MetadataCategories.All));
        groups.Add(new("pdfnote", "Not covered", "Attachments, form data, annotations, hidden layers and the visible text are not changed by metadata removal.",
            [new("Pages", pdf.PageCount.ToString())], false, MetadataCategories.None));
        return groups;
    }

    private static IEnumerable<MetadataItem> Pick(Directory? directory, params int[] tags)
    {
        if (directory is null) yield break;
        foreach (var tag in tags)
        {
            if (!directory.ContainsTag(tag)) continue;
            var value = directory.GetDescription(tag);
            if (!string.IsNullOrWhiteSpace(value)) yield return new MetadataItem(directory.GetTagName(tag), value.Trim());
        }
    }

    private static IEnumerable<MetadataItem> Items(Directory directory, int maxItems) =>
        directory.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Description)).Take(maxItems)
            .Select(t => new MetadataItem(t.Name, t.Description!.Length > 120 ? t.Description[..120] + "…" : t.Description));
}
