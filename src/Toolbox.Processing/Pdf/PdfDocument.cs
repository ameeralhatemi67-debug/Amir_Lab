using System.Runtime.InteropServices;
using System.Text;
using NetVips;
using Toolbox.Core;
using Toolbox.Processing.Imaging;
using static Toolbox.Processing.Pdf.PdfiumNative;

namespace Toolbox.Processing.Pdf;

/// <summary>Size of a page in PDF points (1/72 inch), as displayed (page rotation applied).</summary>
public readonly record struct PageSize(double Width, double Height);

/// <summary>A rectangle in PDF user space (points, y up), as the page's content stream sees it.</summary>
public readonly record struct PdfBox(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
}

/// <summary>An entry of the document's outline (table of contents); <see cref="Page"/> is 1-based, null when it leads nowhere in the file.</summary>
public sealed record PdfOutlineItem(string Title, int? Page, double? UserY, IReadOnlyList<PdfOutlineItem> Children);

/// <summary>One place where searched text was found: its page (1-based) and the boxes it covers (several when it wraps).</summary>
public sealed record PdfSearchHit(int Page, int CharIndex, IReadOnlyList<PdfBox> Boxes);

/// <summary>Where a link leads: a page of this file (1-based, with a height on it when the link says), or a web address.</summary>
public sealed record PdfLinkTarget(int? Page, double? UserY, string? Uri);

/// <summary>
/// Converts between a page as displayed (points from its top-left corner, rotation applied, y down)
/// and PDF user space (the content stream's points, y up). Affine, so it is exact for every point.
/// </summary>
public readonly record struct PageMap(double Ox, double Oy, double Xx, double Xy, double Yx, double Yy, double Width, double Height)
{
    /// <summary>Display point (x right, y down, in points) → user space.</summary>
    public (double X, double Y) ToUser(double x, double y) => (Ox + x * Xx + y * Yx, Oy + x * Xy + y * Yy);

    /// <summary>User space → display point.</summary>
    public (double X, double Y) ToDisplay(double ux, double uy)
    {
        double det = Xx * Yy - Xy * Yx;
        double dx = ux - Ox, dy = uy - Oy;
        return ((dx * Yy - dy * Yx) / det, (Xx * dy - Xy * dx) / det);
    }

    /// <summary>A user-space box as a display rectangle (left, top, width, height in points).</summary>
    public (double X, double Y, double W, double H) ToDisplay(PdfBox box)
    {
        var a = ToDisplay(box.Left, box.Bottom);
        var b = ToDisplay(box.Right, box.Top);
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return (x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}

/// <summary>
/// A PDF opened read-only with PDFium. The file is read into unmanaged memory (so Unicode paths,
/// network shares and locked-for-write files all work) and never written. PDFium is not
/// thread-safe, so every call is serialised through one process-wide lock.
/// </summary>
public sealed unsafe class PdfDocument : IDisposable
{
    /// <summary>Largest page raster we produce; DPI is lowered (and reported) for huge pages.</summary>
    public const long MaxRenderPixels = 60_000_000;

    internal static readonly object Lock = new();
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

    // ---- for the viewer and editor --------------------------------------------------------------

    /// <summary>
    /// Renders one page (1-based) at exactly <paramref name="width"/>×<paramref name="height"/>
    /// pixels, white behind it, annotations included: 32-bit BGRx, rows top-down (WPF's Bgr32).
    /// </summary>
    public byte[] RenderBgra(int pageNumber, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if ((long)width * height > MaxRenderPixels) throw new ProcessingException("This page is too large to show at this zoom.");
        var pixels = new byte[width * height * 4];
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            fixed (byte* p = pixels)
            {
                var bitmap = FPDFBitmap_CreateEx(width, height, FPDFBitmap_BGRx, p, width * 4);
                try
                {
                    if (bitmap == IntPtr.Zero) throw new ProcessingException("Not enough memory to render this page.");
                    FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT);
                }
                finally
                {
                    if (bitmap != IntPtr.Zero) FPDFBitmap_Destroy(bitmap);
                    FPDF_ClosePage(page);
                }
            }
        }
        return pixels;
    }

    /// <summary>How the displayed page (rotation applied) maps to user space.</summary>
    public PageMap GetPageMap(int pageNumber)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            try
            {
                double w = FPDF_GetPageWidthF(page), h = FPDF_GetPageHeightF(page);
                // A device 64× the page in points keeps the mapping exact to 1/64 pt.
                const int k = 64;
                int sw = Math.Max(1, (int)Math.Round(w * k)), sh = Math.Max(1, (int)Math.Round(h * k));
                (double X, double Y) Map(int x, int y)
                {
                    double px, py;
                    FPDF_DeviceToPage(page, 0, 0, sw, sh, 0, x, y, &px, &py);
                    return (px, py);
                }
                var o = Map(0, 0);
                var right = Map(sw, 0);
                var down = Map(0, sh);
                double sx = (double)sw / k, sy = (double)sh / k;
                return new PageMap(o.X, o.Y, (right.X - o.X) / sx, (right.Y - o.Y) / sx, (down.X - o.X) / sy, (down.Y - o.Y) / sy, w, h);
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
    }

    /// <summary>Clockwise quarter turns the page asks to be shown at (its /Rotate, 0–3).</summary>
    public int GetRotation(int pageNumber)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            try { return FPDFPage_GetRotation(page) & 3; }
            finally { FPDF_ClosePage(page); }
        }
    }

    /// <summary>
    /// Where the pictures (image objects) on the page are, in user space, including pictures inside
    /// form XObjects. Used to keep photos as they are when a page is shown or saved dark.
    /// </summary>
    public IReadOnlyList<PdfBox> GetImageBoxes(int pageNumber)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            try
            {
                var boxes = new List<PdfBox>();
                int count = FPDFPage_CountObjects(page);
                for (int i = 0; i < count && boxes.Count < 2000; i++) Collect(FPDFPage_GetObject(page, i), Identity, boxes, 0);
                return boxes;
            }
            finally
            {
                FPDF_ClosePage(page);
            }
        }
    }

    private static readonly FS_MATRIX Identity = new() { A = 1, D = 1 };

    /// <summary>
    /// Adds the object's box if it is a picture; for a form, its pictures. Objects inside a form report
    /// their box in the form's own space, so the form's matrix (and its parents') is applied.
    /// </summary>
    private static void Collect(IntPtr obj, FS_MATRIX parent, List<PdfBox> boxes, int depth)
    {
        if (obj == IntPtr.Zero || depth > 8) return;
        int type = FPDFPageObj_GetType(obj);
        if (type == FPDF_PAGEOBJ_IMAGE)
        {
            float l, b, r, t;
            if (FPDFPageObj_GetBounds(obj, &l, &b, &r, &t) == 0) return;
            var corners = new[] { Apply(parent, l, b), Apply(parent, r, b), Apply(parent, l, t), Apply(parent, r, t) };
            var box = new PdfBox(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
            if (box.Width > 0.5 && box.Height > 0.5) boxes.Add(box);
        }
        else if (type == FPDF_PAGEOBJ_FORM)
        {
            FS_MATRIX m;
            if (FPDFPageObj_GetMatrix(obj, &m) == 0) m = Identity;
            var combined = Multiply(m, parent);
            int n = FPDFFormObj_CountObjects(obj);
            for (int i = 0; i < n && boxes.Count < 2000; i++) Collect(FPDFFormObj_GetObject(obj, (uint)i), combined, boxes, depth + 1);
        }
    }

    private static (double X, double Y) Apply(FS_MATRIX m, double x, double y) => (m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);

    /// <summary>First <paramref name="a"/>, then <paramref name="b"/> (PDF row-vector order).</summary>
    private static FS_MATRIX Multiply(FS_MATRIX a, FS_MATRIX b) => new()
    {
        A = a.A * b.A + a.B * b.C,
        B = a.A * b.B + a.B * b.D,
        C = a.C * b.A + a.D * b.C,
        D = a.C * b.B + a.D * b.D,
        E = a.E * b.A + a.F * b.C + b.E,
        F = a.E * b.B + a.F * b.D + b.F,
    };

    /// <summary>The outline (bookmarks), as the file stores it; empty when it has none.</summary>
    public IReadOnlyList<PdfOutlineItem> GetOutline()
    {
        lock (Lock)
        {
            var seen = new HashSet<IntPtr>();
            int budget = 5000;
            return Children(IntPtr.Zero, 0);

            List<PdfOutlineItem> Children(IntPtr parent, int depth)
            {
                var list = new List<PdfOutlineItem>();
                if (depth > 12) return list;
                for (var bm = FPDFBookmark_GetFirstChild(_document, parent); bm != IntPtr.Zero && budget-- > 0; bm = FPDFBookmark_GetNextSibling(_document, bm))
                {
                    if (!seen.Add(bm)) break; // a loop in a damaged file
                    uint bytes = FPDFBookmark_GetTitle(bm, null, 0);
                    string title = "";
                    if (bytes > 2)
                    {
                        var buffer = new byte[bytes];
                        fixed (byte* b = buffer) FPDFBookmark_GetTitle(bm, b, bytes);
                        title = Encoding.Unicode.GetString(buffer, 0, (int)bytes - 2).Trim();
                    }
                    var dest = FPDFBookmark_GetDest(_document, bm);
                    if (dest == IntPtr.Zero)
                    {
                        var action = FPDFBookmark_GetAction(bm);
                        if (action != IntPtr.Zero && FPDFAction_GetType(action) == PDFACTION_GOTO) dest = FPDFAction_GetDest(_document, action);
                    }
                    var (page, y) = Where(dest);
                    list.Add(new PdfOutlineItem(title.Length == 0 ? "…" : title, page, y, Children(bm, depth + 1)));
                }
                return list;
            }
        }
    }

    private (int? Page, double? Y) Where(IntPtr dest)
    {
        if (dest == IntPtr.Zero) return (null, null);
        int index = FPDFDest_GetDestPageIndex(_document, dest);
        if (index < 0 || index >= PageCount) return (null, null);
        int hasX, hasY, hasZoom;
        float x, y, zoom;
        double? top = FPDFDest_GetLocationInPage(dest, &hasX, &hasY, &hasZoom, &x, &y, &zoom) != 0 && hasY != 0 ? y : null;
        return (index + 1, top);
    }

    /// <summary>Every place <paramref name="text"/> occurs, page by page (all pages, or one).</summary>
    public IReadOnlyList<PdfSearchHit> Find(string text, bool matchCase, int? onlyPage = null, int limit = 5000)
    {
        var hits = new List<PdfSearchHit>();
        if (string.IsNullOrEmpty(text)) return hits;
        var wide = new ushort[text.Length + 1];
        for (int i = 0; i < text.Length; i++) wide[i] = text[i];
        int first = onlyPage ?? 1, last = onlyPage ?? PageCount;
        for (int p = first; p <= last && hits.Count < limit; p++)
        {
            lock (Lock)
            {
                var page = LoadPage(p);
                var textPage = FPDFText_LoadPage(page);
                IntPtr search = IntPtr.Zero;
                try
                {
                    if (textPage == IntPtr.Zero) continue;
                    fixed (ushort* w = wide) search = FPDFText_FindStart(textPage, w, matchCase ? FPDF_MATCHCASE : 0, 0);
                    if (search == IntPtr.Zero) continue;
                    while (FPDFText_FindNext(search) != 0 && hits.Count < limit)
                    {
                        int start = FPDFText_GetSchResultIndex(search), count = FPDFText_GetSchCount(search);
                        int rects = FPDFText_CountRects(textPage, start, count);
                        var boxes = new List<PdfBox>(rects);
                        for (int r = 0; r < rects; r++)
                        {
                            double l, t, rr, b;
                            if (FPDFText_GetRect(textPage, r, &l, &t, &rr, &b) != 0) boxes.Add(new PdfBox(l, b, rr, t));
                        }
                        if (boxes.Count > 0) hits.Add(new PdfSearchHit(p, start, boxes));
                    }
                }
                finally
                {
                    if (search != IntPtr.Zero) FPDFText_FindClose(search);
                    if (textPage != IntPtr.Zero) FPDFText_ClosePage(textPage);
                    FPDF_ClosePage(page);
                }
            }
        }
        return hits;
    }

    /// <summary>The link at a user-space point of a page, if there is one.</summary>
    public PdfLinkTarget? GetLinkAt(int pageNumber, double x, double y)
    {
        lock (Lock)
        {
            var page = LoadPage(pageNumber);
            try
            {
                var link = FPDFLink_GetLinkAtPoint(page, x, y);
                if (link == IntPtr.Zero) return null;
                var dest = FPDFLink_GetDest(_document, link);
                if (dest == IntPtr.Zero)
                {
                    var action = FPDFLink_GetAction(link);
                    if (action == IntPtr.Zero) return null;
                    uint type = FPDFAction_GetType(action);
                    if (type == PDFACTION_URI)
                    {
                        uint bytes = FPDFAction_GetURIPath(_document, action, null, 0);
                        if (bytes <= 1) return null;
                        var buffer = new byte[bytes];
                        fixed (byte* b = buffer) FPDFAction_GetURIPath(_document, action, b, bytes);
                        return new PdfLinkTarget(null, null, Encoding.UTF8.GetString(buffer, 0, (int)bytes - 1));
                    }
                    if (type != PDFACTION_GOTO) return null;
                    dest = FPDFAction_GetDest(_document, action);
                }
                var (target, top) = Where(dest);
                return target is null ? null : new PdfLinkTarget(target, top, null);
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

    internal static void EnsureLibrary()
    {
        if (_libraryReady) return;
        FPDF_InitLibrary();
        _libraryReady = true;
    }
}
