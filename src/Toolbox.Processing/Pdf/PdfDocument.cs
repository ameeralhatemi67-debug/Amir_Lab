using System.Runtime.InteropServices;
using System.Text;
using NetVips;
using Toolbox.Core;
using Toolbox.Processing.Imaging;
using static Toolbox.Processing.Pdf.PdfiumNative;

namespace Toolbox.Processing.Pdf;

/// <summary>Size of a page in PDF points (1/72 inch), as displayed (page rotation applied).</summary>
public readonly record struct PageSize(double Width, double Height);

/// <summary>
/// A PDF opened read-only with PDFium. The file is read into unmanaged memory (so Unicode paths,
/// network shares and locked-for-write files all work) and never written. PDFium is not
/// thread-safe, so every call is serialised through one process-wide lock.
/// </summary>
public sealed unsafe class PdfDocument : IDisposable
{
    /// <summary>Largest page raster we produce; DPI is lowered (and reported) for huge pages.</summary>
    public const long MaxRenderPixels = 60_000_000;

    private static readonly object Lock = new();
    private static bool _libraryReady;

    private IntPtr _document;
    private void* _buffer;

    private PdfDocument(IntPtr document, void* buffer, int pageCount)
    {
        _document = document;
        _buffer = buffer;
        PageCount = pageCount;
    }

    public int PageCount { get; }

    public static PdfDocument Open(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new ProcessingException($"The PDF could not be read: {ex.Message}", ex);
        }
        return Open(bytes);
    }

    public static PdfDocument Open(ReadOnlySpan<byte> bytes)
    {
        lock (Lock)
        {
            EnsureLibrary();
            var buffer = NativeMemory.Alloc((nuint)bytes.Length);
            bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
            var doc = FPDF_LoadMemDocument64(buffer, (nuint)bytes.Length, null);
            if (doc == IntPtr.Zero)
            {
                var error = FPDF_GetLastError();
                NativeMemory.Free(buffer);
                throw new ProcessingException(error switch
                {
                    FPDF_ERR_PASSWORD => "This PDF is password-protected. Password-protected PDFs are not supported yet.",
                    FPDF_ERR_SECURITY => "This PDF uses an unsupported security handler.",
                    FPDF_ERR_FORMAT => "The file is not a valid PDF or is damaged.",
                    _ => $"The PDF could not be opened (PDFium error {error}).",
                });
            }
            int count = FPDF_GetPageCount(doc);
            return new PdfDocument(doc, buffer, count);
        }
    }

    public PageSize GetPageSize(int pageNumber)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            try
            {
                return new PageSize(FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page));
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Pixel size a page would have at <paramref name="dpi"/>, with the safety cap applied.</summary>
    public (int Width, int Height, double Dpi) RenderSize(int pageNumber, double dpi)
    {
        var size = GetPageSize(pageNumber);
        double w = size.Width / 72.0 * dpi, h = size.Height / 72.0 * dpi;
        if (w * h > MaxRenderPixels)
        {
            double scale = Math.Sqrt(MaxRenderPixels / (w * h));
            dpi *= scale;
            w *= scale;
            h *= scale;
        }
        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)), dpi);
    }

    /// <summary>Renders one page (1-based) to an opaque sRGB image on a white background.</summary>
    public Image RenderPage(int pageNumber, double dpi, out double effectiveDpi)
    {
        var (width, height, actualDpi) = RenderSize(pageNumber, dpi);
        effectiveDpi = actualDpi;
        int stride = width * 4;
        long bytes = (long)stride * height;
        var pixels = NativeMemory.Alloc((nuint)bytes);
        try
        {
            lock (Lock)
            {
                var page = LoadPage(pageNumber);
                var bitmap = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRx, pixels, stride);
                try
                {
                    if (bitmap == IntPtr.Zero) throw new ProcessingException("Not enough memory to render this page.");
                    FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    // REVERSE_BYTE_ORDER makes PDFium write RGBx instead of BGRx.
                    FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT | FPDF_REVERSE_BYTE_ORDER);
                }
                finally
                {
                    if (bitmap != IntPtr.Zero) FPDFBitmap_Destroy(bitmap);
                    FPDF_ClosePage(page);
                }
            }

            VipsRuntime.EnsureInitialized();
            using var rgbx = Image.NewFromMemoryCopy((IntPtr)pixels, (ulong)bytes, width, height, 4, Enums.BandFormat.Uchar);
            using var rgb = rgbx.ExtractBand(0, n: 3);
            double pixelsPerMm = actualDpi / 25.4;
            return rgb.Copy(interpretation: Enums.Interpretation.Srgb, xres: pixelsPerMm, yres: pixelsPerMm);
        }
        finally
        {
            NativeMemory.Free(pixels);
        }
    }

    /// <summary>The page's existing text layer (empty for scans). Lines are separated by CRLF.</summary>
    public string GetText(int pageNumber)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            var text = FPDFText_LoadPage(page);
            try
            {
                if (text == IntPtr.Zero) return "";
                int count = FPDFText_CountChars(text);
                if (count <= 0) return "";
                var buffer = new ushort[count + 1];
                int written;
                fixed (ushort* p = buffer) written = FPDFText_GetText(text, 0, count, p);
                int length = Math.Max(0, written - 1); // includes the terminator
                fixed (ushort* p = buffer) return new string((char*)p, 0, Math.Min(length, count));
            }
            finally
            {
                if (text != IntPtr.Zero) FPDFText_ClosePage(text);
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>
    /// Maps pixels of a <paramref name="width"/>×<paramref name="height"/> render of the page (as
    /// produced by <see cref="RenderPage"/>) to PDF user space, including page rotation and box offsets.
    /// </summary>
    public Ocr.PageTransform GetRenderTransform(int pageNumber, int width, int height)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            try
            {
                (double X, double Y) Map(int x, int y)
                {
                    double px, py;
                    FPDF_DeviceToPage(page, 0, 0, width, height, 0, x, y, &px, &py);
                    return (px, py);
                }
                return Ocr.PageTransform.FromCorners(Map(0, 0), Map(width, 0), Map(0, height), width, height);
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Box of one character of the page's text layer, in PDF user space.</summary>
    public (double Left, double Right, double Bottom, double Top) GetTextBox(int pageNumber, int charIndex)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            var text = FPDFText_LoadPage(page);
            try
            {
                double l, r, b, t;
                if (text == IntPtr.Zero || FPDFText_GetCharBox(text, charIndex, &l, &r, &b, &t) == 0)
                    throw new ProcessingException($"Character {charIndex} on page {pageNumber} has no box.");
                return (l, r, b, t);
            }
            finally
            {
                if (text != IntPtr.Zero) FPDFText_ClosePage(text);
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Where a user-space point appears in a <paramref name="width"/>×<paramref name="height"/> render of the page.</summary>
    public (int X, int Y) PageToDisplay(int pageNumber, double x, double y, int width, int height)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            try
            {
                int dx, dy;
                FPDF_PageToDevice(page, 0, 0, width, height, 0, x, y, &dx, &dy);
                return (dx, dy);
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Document-information value such as "Title", "Author", "Creator", "Producer", "CreationDate".</summary>
    public string GetMetaText(string tag)
    {
        lock (Lock)
        {
            var tagBytes = Encoding.ASCII.GetBytes(tag + "\0");
            fixed (byte* t = tagBytes)
            {
                uint needed = FPDF_GetMetaText(_document, t, null, 0);
                if (needed <= 2) return "";
                var buffer = new byte[needed];
                fixed (byte* b = buffer) FPDF_GetMetaText(_document, t, b, needed);
                return Encoding.Unicode.GetString(buffer, 0, (int)needed - 2);
            }
        }
    }

    public void Dispose()
    {
        lock (Lock)
        {
            if (_document != IntPtr.Zero)
            {
                FPDF_CloseDocument(_document);
                _document = IntPtr.Zero;
            }
            if (_buffer is not null)
            {
                NativeMemory.Free(_buffer);
                _buffer = null;
            }
        }
    }

    private IntPtr LoadPage(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > PageCount)
            throw new ProcessingException($"Page {pageNumber} does not exist; the document has {PageCount} pages.");
        var page = FPDF_LoadPage(_document, pageNumber - 1);
        if (page == IntPtr.Zero) throw new ProcessingException($"Page {pageNumber} could not be loaded; it may be damaged.");
        return page;
    }

    private static void EnsureLibrary()
    {
        if (_libraryReady) return;
        FPDF_InitLibrary();
        _libraryReady = true;
    }
}
