using System.Runtime.InteropServices;

namespace Toolbox.Processing.Pdf;

/// <summary>The subset of the PDFium C API used by the toolbox (see fpdfview.h, fpdf_text.h, fpdf_doc.h).</summary>
internal static unsafe partial class PdfiumNative
{
    private const string Lib = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDF_REVERSE_BYTE_ORDER = 0x10;
    public const int FPDFBitmap_BGRx = 3;

    public const uint FPDF_ERR_SUCCESS = 0;
    public const uint FPDF_ERR_UNKNOWN = 1;
    public const uint FPDF_ERR_FILE = 2;
    public const uint FPDF_ERR_FORMAT = 3;
    public const uint FPDF_ERR_PASSWORD = 4;
    public const uint FPDF_ERR_SECURITY = 5;
    public const uint FPDF_ERR_PAGE = 6;

    [LibraryImport(Lib)]
    public static partial void FPDF_InitLibrary();

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_LoadMemDocument64(void* data, nuint size, byte* password);

    [LibraryImport(Lib)]
    public static partial void FPDF_CloseDocument(IntPtr document);

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetLastError();

    [LibraryImport(Lib)]
    public static partial int FPDF_GetPageCount(IntPtr document);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_LoadPage(IntPtr document, int index);

    [LibraryImport(Lib)]
    public static partial void FPDF_ClosePage(IntPtr page);

    [LibraryImport(Lib)]
    public static partial float FPDF_GetPageWidthF(IntPtr page);

    [LibraryImport(Lib)]
    public static partial float FPDF_GetPageHeightF(IntPtr page);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);

    [LibraryImport(Lib)]
    public static partial int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport(Lib)]
    public static partial void FPDFBitmap_Destroy(IntPtr bitmap);

    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFText_LoadPage(IntPtr page);

    [LibraryImport(Lib)]
    public static partial void FPDFText_ClosePage(IntPtr textPage);

    [LibraryImport(Lib)]
    public static partial int FPDFText_CountChars(IntPtr textPage);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetText(IntPtr textPage, int startIndex, int count, ushort* result);

    [LibraryImport(Lib)]
    public static partial uint FPDF_GetMetaText(IntPtr document, byte* tag, void* buffer, uint bufferLength);
}
