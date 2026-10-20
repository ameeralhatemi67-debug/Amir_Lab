using Toolbox.Core.Inputs;

namespace Toolbox.Core.Tests;

public class FileProbeTests
{
    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ".png", FileKind.Jpeg)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0 }, ".jpg", FileKind.Png)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ".gif", FileKind.Gif)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ".tif", FileKind.Tiff)]
    public void Sniffs_content_not_extension(byte[] head, string extension, FileKind expected)
    {
        Assert.Equal(expected, FileProbe.Sniff(head, extension));
    }

    [Fact]
    public void Recognises_pdf_header_after_leading_junk()
    {
        var head = new byte[64];
        "\r\n%PDF-1.7"u8.CopyTo(head.AsSpan(3));
        Assert.Equal(FileKind.Pdf, FileProbe.Sniff(head, ".pdf"));
    }

    [Fact]
    public void Distinguishes_avif_from_heic_by_brand()
    {
        static byte[] Ftyp(string major, params string[] compatible)
        {
            var bytes = new List<byte> { 0, 0, 0, (byte)(16 + 4 * compatible.Length) };
            bytes.AddRange("ftyp"u8.ToArray());
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(major));
            bytes.AddRange([0, 0, 0, 0]);
            foreach (var c in compatible) bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(c));
            return bytes.ToArray();
        }

        Assert.Equal(FileKind.Avif, FileProbe.Sniff(Ftyp("avif", "mif1", "miaf"), ".avif"));
        Assert.Equal(FileKind.Avif, FileProbe.Sniff(Ftyp("mif1", "avif"), ".heic"));
        Assert.Equal(FileKind.Heic, FileProbe.Sniff(Ftyp("heic", "mif1"), ".heic"));
        Assert.Equal(FileKind.Unknown, FileProbe.Sniff(Ftyp("isom", "mp41"), ".mp4"));
    }

    [Fact]
    public void Webp_requires_riff_and_webp_tag()
    {
        var webp = "RIFF\0\0\0\0WEBPVP8 "u8.ToArray();
        var wav = "RIFF\0\0\0\0WAVEfmt "u8.ToArray();
        Assert.Equal(FileKind.WebP, FileProbe.Sniff(webp, ".webp"));
        Assert.Equal(FileKind.Unknown, FileProbe.Sniff(wav, ".wav"));
    }

    [Fact]
    public void Probe_reports_problems_instead_of_throwing()
    {
        var dir = Directory.CreateTempSubdirectory("ftb-probe-");
        try
        {
            var empty = Path.Combine(dir.FullName, "empty.png");
            File.WriteAllBytes(empty, []);
            var junk = Path.Combine(dir.FullName, "notes.xyz");
            File.WriteAllText(junk, "hello");

            Assert.Equal("The file is empty.", FileProbe.Probe(empty).Problem);
            Assert.Contains("Unsupported", FileProbe.Probe(junk).Problem);
            Assert.Contains("no longer exists", FileProbe.Probe(Path.Combine(dir.FullName, "missing.pdf")).Problem);
            Assert.Contains("Folders", FileProbe.Probe(dir.FullName).Problem);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Probe_handles_arabic_file_names()
    {
        var dir = Directory.CreateTempSubdirectory("ftb-probe-");
        try
        {
            var path = Path.Combine(dir.FullName, "تقرير الربع الأول.pdf");
            File.WriteAllBytes(path, "%PDF-1.4\n%âã\n"u8.ToArray());
            var probed = FileProbe.Probe(path);
            Assert.Equal(FileKind.Pdf, probed.Kind);
            Assert.Equal("تقرير الربع الأول", probed.BaseName);
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
