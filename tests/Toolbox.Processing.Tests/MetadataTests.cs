using System.Buffers.Binary;
using System.Text;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using MetadataExtractor.Formats.Xmp;
using NetVips;
using PdfSharp.Pdf.IO;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Processing.Metadata;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

namespace Toolbox.Processing.Tests;

public sealed class MetadataTests : IDisposable
{
    private const string SecretPlace = "SECRET-PLACE-123";
    private const string Serial = "SERIAL-XYZ-789";
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    [Fact]
    public async Task Removing_location_erases_gps_bytes_but_keeps_camera_and_pixels()
    {
        var src = MakeTaggedJpeg("holiday.jpg", orientation: 1);
        var job = await _ws.Run(Ids.Metadata, src, ("remove", "location"));
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error);
        var output = item.Outputs[0].Path;
        Assert.Equal(_ws.PathOf("holiday Clean.jpg"), output);

        var bytes = File.ReadAllBytes(output);
        Assert.Equal(-1, IndexOf(bytes, SecretPlace)); // not just unlinked: erased
        var dirs = ImageMetadataReader.ReadMetadata(output);
        Assert.True(dirs.OfType<GpsDirectory>().All(d => d.TagCount == 0));
        Assert.Equal("TestCam", dirs.OfType<ExifIfd0Directory>().Single().GetDescription(ExifDirectoryBase.TagMake));
        Assert.Empty(dirs.OfType<ExifThumbnailDirectory>().Where(d => d.TagCount > 0));
        Assert.Empty(dirs.OfType<XmpDirectory>());
        Assert.True(SameScanData(File.ReadAllBytes(src), bytes), "compressed picture data must be byte-identical");
    }

    [Fact]
    public async Task Removing_camera_details_erases_serial_numbers()
    {
        var src = MakeTaggedJpeg("camera.jpg", orientation: 1);
        var job = await _ws.Run(Ids.Metadata, src, ("remove", "camera"));
        var output = job.Items[0].Outputs[0].Path;
        var bytes = File.ReadAllBytes(output);
        Assert.Equal(-1, IndexOf(bytes, Serial));
        Assert.Equal(-1, IndexOf(bytes, "TestCam"));
        var dirs = ImageMetadataReader.ReadMetadata(output);
        Assert.Contains(dirs.OfType<GpsDirectory>(), d => d.TagCount > 0); // location untouched when not requested
    }

    [Fact]
    public async Task Removing_everything_keeps_orientation_so_photos_stay_upright()
    {
        var src = MakeTaggedJpeg("portrait.jpg", orientation: 6);
        var job = await _ws.Run(Ids.Metadata, src, ("remove", "all"));
        var output = job.Items[0].Outputs[0].Path;
        var bytes = File.ReadAllBytes(output);
        Assert.Equal(-1, IndexOf(bytes, SecretPlace));
        Assert.Equal(-1, IndexOf(bytes, Serial));
        Assert.Equal(-1, IndexOf(bytes, "TestCam"));
        Assert.Equal(-1, IndexOf(bytes, "a private comment"));
        var ifd0 = ImageMetadataReader.ReadMetadata(output).OfType<ExifIfd0Directory>().Single();
        Assert.Equal(6, ifd0.GetInt32(ExifDirectoryBase.TagOrientation));
        Assert.True(SameScanData(File.ReadAllBytes(src), bytes));
    }

    [Fact]
    public void Inspector_groups_sensitive_metadata()
    {
        var src = MakeTaggedJpeg("inspect.jpg", orientation: 1);
        var groups = MetadataInspector.Inspect(FileProbe.Probe(src));
        Assert.Contains(groups, g => g.Key == "location" && g.Sensitive);
        Assert.Contains(groups, g => g.Key == "camera" && g.Items.Any(i => i.Value.Contains("TestCam")));
        Assert.Contains(groups, g => g.Key == "thumbnail");
    }

    [Fact]
    public async Task Png_location_can_be_removed_losslessly()
    {
        var png = _ws.PathOf("map.png");
        using (var img = Image.Black(64, 48, bands: 3).Copy(interpretation: Enums.Interpretation.Srgb))
        using (var tagged = img.Mutate(m => m.Set(GValue.BlobType, "exif-data", (byte[])[.. "Exif\0\0"u8.ToArray(), .. BuildExif(1, withThumbnail: false)])))
            tagged.Pngsave(png, keep: Enums.ForeignKeep.All);
        Assert.Contains(ImageMetadataReader.ReadMetadata(png).OfType<GpsDirectory>(), d => d.TagCount > 0);

        var job = await _ws.Run(Ids.Metadata, png, ("remove", "location"));
        var output = job.Items[0].Outputs[0].Path;
        Assert.True(ImageMetadataReader.ReadMetadata(output).OfType<GpsDirectory>().All(d => d.TagCount == 0));
        Assert.Equal(-1, IndexOf(File.ReadAllBytes(output), SecretPlace));
    }

    [Fact]
    public async Task Pdf_document_information_is_removed()
    {
        var path = _ws.Pdf("contract.pdf", 2);
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            doc.Info.Author = "Jane Q. Example";
            doc.Info.Title = "Secret contract";
            doc.Save(path);
        }
        var job = await _ws.Run(Ids.Metadata, path);
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error);
        using var cleaned = Pdf.PdfDocument.Open(item.Outputs[0].Path);
        Assert.Equal("", cleaned.GetMetaText("Author"));
        Assert.Equal("", cleaned.GetMetaText("Title"));
        Assert.Equal(2, cleaned.PageCount);
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private string MakeTaggedJpeg(string name, ushort orientation)
    {
        using var img = Image.Black(320, 200, bands: 3).Linear([1, 1, 1], [90, 140, 200]).Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        var plain = img.JpegsaveBuffer(q: 85, keep: Enums.ForeignKeep.None);
        var exif = BuildExif(orientation, withThumbnail: true);
        var xmp = Encoding.UTF8.GetBytes("http://ns.adobe.com/xap/1.0/\0<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/' dc:creator='" + SecretPlace + "'/></rdf:RDF></x:xmpmeta>");
        var comment = Encoding.ASCII.GetBytes("a private comment");

        using var ms = new MemoryStream();
        ms.Write(plain, 0, 2); // SOI
        Segment(ms, 0xE1, [.. "Exif\0\0"u8.ToArray(), .. exif]);
        Segment(ms, 0xE1, xmp);
        Segment(ms, 0xFE, comment);
        ms.Write(plain, 2, plain.Length - 2);
        var path = _ws.PathOf(name);
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    private static void Segment(Stream s, byte marker, byte[] payload)
    {
        s.WriteByte(0xFF);
        s.WriteByte(marker);
        int len = payload.Length + 2;
        s.WriteByte((byte)(len >> 8));
        s.WriteByte((byte)len);
        s.Write(payload);
    }

    /// <summary>Little-endian TIFF with IFD0 (make, model, orientation, date), Exif IFD (serial, date), GPS IFD (lat/lon + marker) and IFD1 thumbnail.</summary>
    private static byte[] BuildExif(ushort orientation, bool withThumbnail)
    {
        var data = new List<byte>();
        var ifd0 = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (0x010F, 2, 0, Ascii("TestCam")),
            (0x0110, 2, 0, Ascii("X100")),
            (0x0112, 3, 1, U16(orientation)),
            (0x0132, 2, 0, Ascii("2026:10:20 10:00:00")),
            (0x8769, 4, 1, U32(0)), // patched
            (0x8825, 4, 1, U32(0)), // patched
        };
        var exif = new List<(ushort, ushort, uint, byte[])> { (0xA431, 2, 0, Ascii(Serial)), (0x9003, 2, 0, Ascii("2026:10:20 10:00:00")) };
        var gps = new List<(ushort, ushort, uint, byte[])>
        {
            (0x0001, 2, 0, Ascii("N")),
            (0x0002, 5, 3, [.. Rational(29, 1), .. Rational(22, 1), .. Rational(1234, 100)]),
            (0x0003, 2, 0, Ascii("E")),
            (0x0004, 5, 3, [.. Rational(47, 1), .. Rational(58, 1), .. Rational(5678, 100)]),
            (0x001C, 7, 0, Encoding.ASCII.GetBytes("ASCII\0\0\0" + SecretPlace)), // GPSAreaInformation
        };
        byte[] thumb = withThumbnail ? Image.Black(16, 10, bands: 3).Cast(Enums.BandFormat.Uchar).JpegsaveBuffer(q: 50, keep: Enums.ForeignKeep.None) : [];
        var ifd1 = withThumbnail
            ? new List<(ushort, ushort, uint, byte[])> { (0x0103, 3, 1, U16(6)), (0x0201, 4, 1, U32(0)), (0x0202, 4, 1, U32((uint)thumb.Length)) }
            : new List<(ushort, ushort, uint, byte[])>();

        int Size(int n) => 2 + n * 12 + 4;
        int ifd0At = 8, exifAt = ifd0At + Size(ifd0.Count), gpsAt = exifAt + Size(exif.Count), ifd1At = gpsAt + Size(gps.Count);
        int dataAt = ifd1At + (withThumbnail ? Size(ifd1.Count) : 0);
        ifd0[4] = (0x8769, 4, 1, U32((uint)exifAt));
        ifd0[5] = (0x8825, 4, 1, U32((uint)gpsAt));

        var buffer = new byte[dataAt + 4096];
        "II"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), (uint)ifd0At);
        int cursor = dataAt;

        int thumbOffset = 0;
        if (withThumbnail)
        {
            thumbOffset = cursor;
            thumb.CopyTo(buffer, cursor);
            cursor += thumb.Length;
            ifd1[1] = (0x0201, 4, 1, U32((uint)thumbOffset));
        }

        void WriteIfd(int at, List<(ushort Tag, ushort Type, uint Count, byte[] Value)> entries, uint next)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(at), (ushort)entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                var (tag, type, count, value) = entries[i];
                uint n = count == 0 ? (uint)value.Length : count;
                int p = at + 2 + i * 12;
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(p), tag);
                BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(p + 2), type);
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(p + 4), n);
                if (value.Length <= 4) value.CopyTo(buffer, p + 8);
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(p + 8), (uint)cursor);
                    value.CopyTo(buffer, cursor);
                    cursor += value.Length + (value.Length % 2);
                }
            }
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 2 + entries.Count * 12), next);
        }

        WriteIfd(ifd0At, ifd0, withThumbnail ? (uint)ifd1At : 0);
        WriteIfd(exifAt, exif, 0);
        WriteIfd(gpsAt, gps, 0);
        if (withThumbnail) WriteIfd(ifd1At, ifd1, 0);
        return buffer[..cursor];
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");

    private static byte[] U16(ushort v) { var b = new byte[4]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }

    private static byte[] Rational(uint num, uint den) { var b = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(b, num); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), den); return b; }

    private static int IndexOf(byte[] haystack, string needle)
    {
        var n = Encoding.ASCII.GetBytes(needle);
        return haystack.AsSpan().IndexOf(n);
    }

    /// <summary>True when everything from the main image's start-of-scan marker on is identical.</summary>
    private static bool SameScanData(byte[] a, byte[] b)
    {
        int sa = MainScan(a), sb = MainScan(b);
        return sa > 0 && sb > 0 && a.AsSpan(sa).SequenceEqual(b.AsSpan(sb));
    }

    /// <summary>Walks the segment structure (not a byte search: an EXIF thumbnail contains its own markers).</summary>
    private static int MainScan(byte[] jpeg)
    {
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos + 1] == 0xDA) return pos;
            pos += 2 + ((jpeg[pos + 2] << 8) | jpeg[pos + 3]);
        }
        return -1;
    }
}
