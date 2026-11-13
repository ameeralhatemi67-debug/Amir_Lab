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

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetCharBox(IntPtr textPage, int index, double* left, double* right, double* bottom, double* top);

    [LibraryImport(Lib)]
    public static partial int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        double pageX, double pageY, int* deviceX, int* deviceY);

    [LibraryImport(Lib)]
    public static partial int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate,
        int deviceX, int deviceY, double* pageX, double* pageY);

    // ---- page objects (fpdf_edit.h): where the pictures are, for the dark copy and dark viewing ----

    public const int FPDF_PAGEOBJ_IMAGE = 3;
    public const int FPDF_PAGEOBJ_FORM = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct FS_MATRIX
    {
        public float A, B, C, D, E, F;
    }

    [LibraryImport(Lib)]
    public static partial int FPDFPage_CountObjects(IntPtr page);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFPage_GetObject(IntPtr page, int index);

    [LibraryImport(Lib)]
    public static partial int FPDFPageObj_GetType(IntPtr pageObject);

    [LibraryImport(Lib)]
    public static partial int FPDFPageObj_GetBounds(IntPtr pageObject, float* left, float* bottom, float* right, float* top);

    [LibraryImport(Lib)]
    public static partial int FPDFPageObj_GetMatrix(IntPtr pageObject, FS_MATRIX* matrix);

    [LibraryImport(Lib)]
    public static partial int FPDFFormObj_CountObjects(IntPtr formObject);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFFormObj_GetObject(IntPtr formObject, uint index);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_GetRotation(IntPtr page);

    // ---- search (fpdf_text.h) ----

    public const uint FPDF_MATCHCASE = 0x1;

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFText_FindStart(IntPtr textPage, ushort* findWhat, uint flags, int startIndex);

    [LibraryImport(Lib)]
    public static partial int FPDFText_FindNext(IntPtr search);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetSchResultIndex(IntPtr search);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetSchCount(IntPtr search);

    [LibraryImport(Lib)]
    public static partial void FPDFText_FindClose(IntPtr search);

    [LibraryImport(Lib)]
    public static partial int FPDFText_CountRects(IntPtr textPage, int startIndex, int count);

    [LibraryImport(Lib)]
    public static partial int FPDFText_GetRect(IntPtr textPage, int rectIndex, double* left, double* top, double* right, double* bottom);

    // ---- outline and links (fpdf_doc.h) ----

    public const uint PDFACTION_GOTO = 1;
    public const uint PDFACTION_URI = 3;

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFBookmark_GetFirstChild(IntPtr document, IntPtr bookmark);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFBookmark_GetNextSibling(IntPtr document, IntPtr bookmark);

    [LibraryImport(Lib)]
    public static partial uint FPDFBookmark_GetTitle(IntPtr bookmark, void* buffer, uint length);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFBookmark_GetDest(IntPtr document, IntPtr bookmark);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFBookmark_GetAction(IntPtr bookmark);

    [LibraryImport(Lib)]
    public static partial uint FPDFAction_GetType(IntPtr action);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFAction_GetDest(IntPtr document, IntPtr action);

    [LibraryImport(Lib)]
    public static partial uint FPDFAction_GetURIPath(IntPtr document, IntPtr action, void* buffer, uint length);

    [LibraryImport(Lib)]
    public static partial int FPDFDest_GetDestPageIndex(IntPtr document, IntPtr dest);

    [LibraryImport(Lib)]
    public static partial int FPDFDest_GetLocationInPage(IntPtr dest, int* hasX, int* hasY, int* hasZoom, float* x, float* y, float* zoom);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFLink_GetLinkAtPoint(IntPtr page, double x, double y);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFLink_GetDest(IntPtr document, IntPtr link);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDFLink_GetAction(IntPtr link);

    // ---- writing (fpdf_edit.h, fpdf_ppo.h, fpdf_save.h): the PDF editor's Save ----

    public const uint FPDF_NO_INCREMENTAL = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct FPDF_FILEWRITE
    {
        public int Version;
        public delegate* unmanaged<FPDF_FILEWRITE*, void*, uint, int> WriteBlock;
    }

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_CreateNewDocument();

    [LibraryImport(Lib)]
    public static partial int FPDF_ImportPagesByIndex(IntPtr destDocument, IntPtr sourceDocument, int* pageIndices, uint length, int index);

    [LibraryImport(Lib)]
    public static partial void FPDFPage_SetRotation(IntPtr page, int rotate);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_NewXObjectFromPage(IntPtr destDocument, IntPtr sourceDocument, int sourcePageIndex);

    [LibraryImport(Lib)]
    public static partial void FPDF_CloseXObject(IntPtr xobject);

    [LibraryImport(Lib)]
    public static partial IntPtr FPDF_NewFormObjectFromXObject(IntPtr xobject);

    [LibraryImport(Lib)]
    public static partial void FPDFPageObj_Transform(IntPtr pageObject, double a, double b, double c, double d, double e, double f);

    [LibraryImport(Lib)]
    public static partial void FPDFPage_InsertObject(IntPtr page, IntPtr pageObject);

    [LibraryImport(Lib)]
    public static partial int FPDFPage_GenerateContent(IntPtr page);

    [LibraryImport(Lib)]
    public static partial int FPDF_SaveAsCopy(IntPtr document, FPDF_FILEWRITE* fileWrite, uint flags);
}
