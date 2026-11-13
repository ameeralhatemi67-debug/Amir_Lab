using Toolbox.Processing.Pdf;

namespace Toolbox.Windows.Viewer;

/// <summary>
/// A PDF opened for the viewer, with what the viewer asks for often kept at hand: page sizes,
/// page maps and picture boxes. PDFium itself is serialised by <see cref="PdfDocument"/>; this only
/// caches, so it may be used from the render thread and the UI.
/// </summary>
internal sealed class OpenPdf : IDisposable
{
    private readonly PageSize[] _sizes;
    private readonly PageMap?[] _maps;
    private readonly IReadOnlyList<PdfBox>?[] _pictures;
    private IReadOnlyList<PdfOutlineItem>? _outline;

    private OpenPdf(string path, PdfDocument document)
    {
        Path = path;
        Document = document;
        PageCount = document.PageCount;
        _sizes = new PageSize[PageCount];
        for (int i = 0; i < PageCount; i++) _sizes[i] = document.GetPageSize(i + 1);
        _maps = new PageMap?[PageCount];
        _pictures = new IReadOnlyList<PdfBox>?[PageCount];
    }

    public string Path { get; }
    public PdfDocument Document { get; }
    public int PageCount { get; }
    public string Name => IOPath.GetFileName(Path);

    /// <summary>Opens and reads every page size (call off the UI thread for large files).</summary>
    public static OpenPdf Open(string path) => new(path, PdfDocument.Open(path));

    /// <summary>Displayed size in points (the page's own rotation applied), 1-based page.</summary>
    public PageSize Size(int page) => _sizes[page - 1];

    public PageMap Map(int page)
    {
        lock (_maps) if (_maps[page - 1] is { } m) return m;
        var map = Document.GetPageMap(page);
        lock (_maps) _maps[page - 1] = map;
        return map;
    }

    public IReadOnlyList<PdfBox> Pictures(int page)
    {
        lock (_pictures) if (_pictures[page - 1] is { } p) return p;
        IReadOnlyList<PdfBox> boxes;
        try { boxes = Document.GetImageBoxes(page); }
        catch (Toolbox.Core.ProcessingException) { boxes = []; }
        lock (_pictures) _pictures[page - 1] = boxes;
        return boxes;
    }

    public IReadOnlyList<PdfOutlineItem> Outline => _outline ??= Document.GetOutline();

    public void Dispose() => Document.Dispose();
}

/// <summary>One page shown in a view: from which open PDF, which page (1-based), and extra clockwise quarter turns.</summary>
internal sealed record PageRef(OpenPdf Pdf, int Page, int Turns = 0)
{
    /// <summary>Displayed size in points with the extra turns applied.</summary>
    public (double W, double H) Size
    {
        get
        {
            var s = Pdf.Size(Page);
            return (Turns & 1) == 1 ? (s.Height, s.Width) : (s.Width, s.Height);
        }
    }
}
