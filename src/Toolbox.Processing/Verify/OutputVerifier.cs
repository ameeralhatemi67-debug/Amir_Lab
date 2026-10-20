using System.Text;
using DocumentFormat.OpenXml.Packaging;
using NetVips;
using Toolbox.Core.Inputs;
using Toolbox.Core.Output;
using Toolbox.Processing.Imaging;

namespace Toolbox.Processing.Verify;

/// <summary>
/// Re-opens every produced file with an independent decoder and checks format, size and pages
/// before it is allowed to appear under its final name. All handles are closed before returning.
/// </summary>
public sealed class OutputVerifier : IOutputVerifier
{
    private static readonly Dictionary<string, FileKind> LoaderKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpegload"] = FileKind.Jpeg,
        ["pngload"] = FileKind.Png,
        ["webpload"] = FileKind.WebP,
        ["heifload"] = FileKind.Avif, // only AV1 payloads decode in this build (no HEVC)
        ["tiffload"] = FileKind.Tiff,
        ["gifload"] = FileKind.Gif,
    };

    public OutputVerification Verify(string path, ExpectedOutput expected)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return OutputVerification.Fail("the file was not written");
            if (info.Length == 0 && expected.Kind != FileKind.Text) return OutputVerification.Fail("the file is empty");

            return expected.Kind switch
            {
                FileKind.Pdf => VerifyPdf(path, expected),
                FileKind.Docx => VerifyDocx(path),
                FileKind.Text => VerifyText(path),
                FileKind.Bmp => VerifyBmp(path, expected),
                _ when expected.Kind.IsRaster() => VerifyRaster(path, expected),
                _ => OutputVerification.Fail($"no verifier for {expected.Kind}"),
            };
        }
        catch (Exception ex)
        {
            return OutputVerification.Fail($"could not be re-opened ({ImageLoader.FirstLine(ex.Message)})");
        }
    }

    private static OutputVerification VerifyRaster(string path, ExpectedOutput expected)
    {
        VipsRuntime.EnsureInitialized();
        using var source = Source.NewFromFile(path);
        using var image = Image.NewFromSource(source, "", Enums.Access.Sequential);
        var loader = image.Contains("vips-loader") ? image.Get("vips-loader") as string ?? "" : "";
        var baseLoader = loader.Replace("_source", "").Replace("_buffer", "");
        if (!LoaderKinds.TryGetValue(baseLoader, out var actual))
            return OutputVerification.Fail($"unexpected decoder '{loader}'");
        if (actual != expected.Kind)
            return OutputVerification.Fail($"expected {expected.Kind.Label()} but the file is {actual.Label()}");
        // Decode the last row to catch truncated files.
        using (var lastRow = image.ExtractArea(0, image.Height - 1, image.Width, 1)) _ = lastRow.Avg();
        return CheckSize(image.Width, image.Height, expected);
    }

    private static OutputVerification VerifyBmp(string path, ExpectedOutput expected)
    {
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[26];
        if (stream.Read(header) < 26 || header[0] != 'B' || header[1] != 'M') return OutputVerification.Fail("not a BMP file");
        int declared = BitConverter.ToInt32(header[2..]);
        if (declared != stream.Length) return OutputVerification.Fail("BMP size field does not match the file");
        int width = BitConverter.ToInt32(header[18..]);
        int height = Math.Abs(BitConverter.ToInt32(header[22..]));
        return CheckSize(width, height, expected);
    }

    private static OutputVerification VerifyPdf(string path, ExpectedOutput expected)
    {
        using var doc = Pdf.PdfDocument.Open(path);
        if (doc.PageCount < 1) return OutputVerification.Fail("the PDF has no pages");
        if (expected.Pages is { } pages && pages != doc.PageCount)
            return OutputVerification.Fail($"expected {pages} pages but found {doc.PageCount}");
        _ = doc.GetPageSize(doc.PageCount); // the last page must load
        return new OutputVerification(true, null, Pages: doc.PageCount);
    }

    private static OutputVerification VerifyDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        return body is null ? OutputVerification.Fail("the document has no body") : new OutputVerification(true, null);
    }

    private static OutputVerification VerifyText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return OutputVerification.Fail("the text is not valid UTF-8");
        }
        return new OutputVerification(true, null);
    }

    private static OutputVerification CheckSize(int width, int height, ExpectedOutput expected)
    {
        if (width <= 0 || height <= 0) return OutputVerification.Fail("the image has no pixels");
        if (expected.Width is { } w && w != width) return OutputVerification.Fail($"expected width {w}px but got {width}px");
        if (expected.Height is { } h && h != height) return OutputVerification.Fail($"expected height {h}px but got {height}px");
        return new OutputVerification(true, null, width, height);
    }
}
