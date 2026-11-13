using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using Toolbox.Core;
using static Toolbox.Processing.Pdf.PdfiumNative;

namespace Toolbox.Processing.Pdf;

/// <summary>A point on a page as displayed (points from its top-left corner, y down).</summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>Something added to a page, in the page's display coordinates (points, top-left origin, the page's own rotation applied).</summary>
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PdfInkMark), "ink")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PdfTextMark), "text")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PdfImageMark), "image")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PdfRectMark), "rect")]
public abstract record PdfMark;

/// <summary>Pen or highlighter strokes; <see cref="Argb"/> carries the opacity (a highlighter is see-through).</summary>
public sealed record PdfInkMark(IReadOnlyList<IReadOnlyList<PdfPoint>> Strokes, uint Argb, double Width) : PdfMark;

/// <summary>Text in a box whose top-left is (X, Y); lines wrap at <see cref="Width"/>. Arabic is shaped and right-to-left.</summary>
public sealed record PdfTextMark(double X, double Y, double Width, string Text, double Size, uint Argb, string Font = "Segoe UI", bool Bold = false) : PdfMark;

/// <summary>A picture (PNG or JPEG bytes): a signature, a stamp, a logo.</summary>
public sealed record PdfImageMark(double X, double Y, double Width, double Height, byte[] Image) : PdfMark;

/// <summary>A filled rectangle, for example white to cover something before typing over it.</summary>
public sealed record PdfRectMark(double X, double Y, double Width, double Height, uint Argb) : PdfMark;

/// <summary>One page of the result: which source file (index) and page (1-based), extra clockwise quarter turns, and what is added.</summary>
public sealed record PdfPagePlan(int Source, int Page, int Turns, IReadOnlyList<PdfMark> Marks);

/// <summary>The edited document: its source files and its pages in their new order.</summary>
public sealed record PdfEditPlan(IReadOnlyList<string> Sources, IReadOnlyList<PdfPagePlan> Pages)
{
    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this);

    public static PdfEditPlan FromJson(string json) =>
        System.Text.Json.JsonSerializer.Deserialize<PdfEditPlan>(json) ?? throw new ProcessingException("The edits could not be read.");
}

/// <summary>
/// Writes an edited PDF with PDFium: pages are copied as they are (vector content, text and links
/// stay), in the new order, from one or more files, turned when asked; what was added is drawn
/// with Skia into a PDF layer of its own (real, searchable text, Arabic shaped by HarfBuzz) and
/// placed on its page as a form, through the page's exact display-to-user-space mapping, so it
/// lands where it was drawn on rotated and cropped pages too. The sources are never written.
/// </summary>
public static unsafe class PdfEditor
{
    [ThreadStatic] private static Stream? _output;

    /// <param name="overwrite">Only for a temp file the job pipeline reserved; otherwise an existing file is never replaced.</param>
    public static void Save(PdfEditPlan plan, string output, Action<double>? progress = null, CancellationToken cancellation = default, bool overwrite = false)
    {
        if (plan.Pages.Count == 0) throw new ProcessingException("There are no pages left to save.");
        var buffers = new List<IntPtr>();
        var sources = new List<IntPtr>();
        IntPtr overlay = IntPtr.Zero, dest = IntPtr.Zero;
        try
        {
            lock (PdfDocument.Lock)
            {
                PdfDocument.EnsureLibrary();
                foreach (var path in plan.Sources) sources.Add(Load(File.ReadAllBytes(path), buffers, Path.GetFileName(path)));
            }

            // The added things, one overlay page per edited page, drawn at each source page's display size.
            var marked = plan.Pages.Select((p, i) => (Plan: p, Index: i)).Where(x => x.Plan.Marks.Count > 0).ToList();
            var overlayPage = new Dictionary<int, int>();
            if (marked.Count > 0)
            {
                var sizes = new List<(double W, double H)>();
                lock (PdfDocument.Lock)
                {
                    foreach (var (p, _) in marked) sizes.Add(DisplaySize(sources[p.Source], p.Page));
                }
                var layer = DrawLayer(marked.Select(m => m.Plan.Marks).ToList(), sizes);
                lock (PdfDocument.Lock) overlay = Load(layer, buffers, "the added layer");
                for (int k = 0; k < marked.Count; k++) overlayPage[marked[k].Index] = k;
            }
            progress?.Invoke(0.2);

            lock (PdfDocument.Lock)
            {
                dest = FPDF_CreateNewDocument();
                if (dest == IntPtr.Zero) throw new ProcessingException("A new PDF could not be started.");
                for (int i = 0; i < plan.Pages.Count; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var p = plan.Pages[i];
                    if (p.Source < 0 || p.Source >= sources.Count) throw new ProcessingException("A page comes from a file that is not open.");
                    int index = p.Page - 1;
                    if (FPDF_ImportPagesByIndex(dest, sources[p.Source], &index, 1, i) == 0)
                        throw new ProcessingException($"Page {p.Page} of {Path.GetFileName(plan.Sources[p.Source])} could not be copied.");
                    var page = FPDF_LoadPage(dest, i);
                    if (page == IntPtr.Zero) throw new ProcessingException($"Page {i + 1} of the result could not be opened.");
                    try
                    {
                        if (overlayPage.TryGetValue(i, out int k))
                        {
                            // The mapping is the page's as it was shown while editing (before any new turn).
                            var map = MapOf(page);
                            var xobject = FPDF_NewXObjectFromPage(dest, overlay, k);
                            if (xobject == IntPtr.Zero) throw new ProcessingException("The added layer could not be placed.");
                            try
                            {
                                var form = FPDF_NewFormObjectFromXObject(xobject);
                                if (form == IntPtr.Zero) throw new ProcessingException("The added layer could not be placed.");
                                // Layer space (x right, y up from the bottom of a W×H page) → display (x, H − y) → user space.
                                double h = map.Height;
                                FPDFPageObj_Transform(form, map.Xx, map.Xy, -map.Yx, -map.Yy, map.Ox + h * map.Yx, map.Oy + h * map.Yy);
                                FPDFPage_InsertObject(page, form);
                                if (FPDFPage_GenerateContent(page) == 0) throw new ProcessingException($"Page {i + 1} could not be written.");
                            }
                            finally
                            {
                                FPDF_CloseXObject(xobject);
                            }
                        }
                        if ((p.Turns & 3) != 0) FPDFPage_SetRotation(page, (FPDFPage_GetRotation(page) + p.Turns) & 3);
                    }
                    finally
                    {
                        FPDF_ClosePage(page);
                    }
                    progress?.Invoke(0.2 + 0.7 * (i + 1) / plan.Pages.Count);
                }

                using var file = new FileStream(output, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write);
                _output = file;
                try
                {
                    var writer = new FPDF_FILEWRITE { Version = 1, WriteBlock = &WriteBlock };
                    if (FPDF_SaveAsCopy(dest, &writer, FPDF_NO_INCREMENTAL) == 0) throw new ProcessingException("The PDF could not be saved.");
                }
                finally
                {
                    _output = null;
                }
            }
            progress?.Invoke(1);
        }
        finally
        {
            lock (PdfDocument.Lock)
            {
                if (dest != IntPtr.Zero) FPDF_CloseDocument(dest);
                if (overlay != IntPtr.Zero) FPDF_CloseDocument(overlay);
                foreach (var s in sources) FPDF_CloseDocument(s);
                foreach (var b in buffers) NativeMemory.Free((void*)b);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static int WriteBlock(FPDF_FILEWRITE* self, void* data, uint size)
    {
        try
        {
            _output!.Write(new ReadOnlySpan<byte>(data, (int)size));
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private static IntPtr Load(byte[] bytes, List<IntPtr> buffers, string name)
    {
        var buffer = NativeMemory.Alloc((nuint)bytes.Length);
        buffers.Add((IntPtr)buffer);
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        var doc = FPDF_LoadMemDocument64(buffer, (nuint)bytes.Length, null);
        if (doc == IntPtr.Zero)
            throw new ProcessingException(FPDF_GetLastError() == FPDF_ERR_PASSWORD
                ? $"{name} is password-protected. Password-protected PDFs are not supported yet."
                : $"{name} could not be opened as a PDF.");
        return doc;
    }

    private static (double W, double H) DisplaySize(IntPtr document, int pageNumber)
    {
        var page = FPDF_LoadPage(document, pageNumber - 1);
        if (page == IntPtr.Zero) throw new ProcessingException($"Page {pageNumber} could not be loaded.");
        try { return (FPDF_GetPageWidthF(page), FPDF_GetPageHeightF(page)); }
        finally { FPDF_ClosePage(page); }
    }

    /// <summary>The same mapping as <see cref="PdfDocument.GetPageMap"/>, for a page that is already open.</summary>
    private static PageMap MapOf(IntPtr page)
    {
        double w = FPDF_GetPageWidthF(page), h = FPDF_GetPageHeightF(page);
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

    // ---- the added layer, drawn with Skia ------------------------------------------------------------

    /// <summary>A PDF with one page per edited page (each at its page's display size) holding what was added.</summary>
    internal static byte[] DrawLayer(IReadOnlyList<IReadOnlyList<PdfMark>> pages, IReadOnlyList<(double W, double H)> sizes)
    {
        using var stream = new SKDynamicMemoryWStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            for (int i = 0; i < pages.Count; i++)
            {
                var canvas = document.BeginPage((float)sizes[i].W, (float)sizes[i].H);
                foreach (var mark in pages[i]) Draw(canvas, mark);
                document.EndPage();
            }
            document.Close();
        }
        using var data = stream.DetachAsData();
        return data.ToArray();
    }

    private static SKColor Colour(uint argb) => new((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb, (byte)(argb >> 24));

    private static void Draw(SKCanvas canvas, PdfMark mark)
    {
        switch (mark)
        {
            case PdfRectMark r:
            {
                using var paint = new SKPaint { Color = Colour(r.Argb), Style = SKPaintStyle.Fill, IsAntialias = true };
                canvas.DrawRect((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height, paint);
                break;
            }
            case PdfInkMark ink:
            {
                // Each stroke on its own layer, so a see-through highlighter is even where it crosses itself.
                using var paint = new SKPaint { Color = Colour(ink.Argb | 0xFF000000), Style = SKPaintStyle.Stroke, StrokeWidth = (float)ink.Width, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
                byte alpha = (byte)(ink.Argb >> 24);
                foreach (var stroke in ink.Strokes)
                {
                    if (stroke.Count == 0) continue;
                    using var builder = new SKPathBuilder();
                    builder.MoveTo((float)stroke[0].X, (float)stroke[0].Y);
                    if (stroke.Count == 1) builder.LineTo((float)stroke[0].X + 0.01f, (float)stroke[0].Y);
                    // Smooth: quadratic curves through the midpoints.
                    for (int i = 1; i < stroke.Count - 1; i++)
                        builder.QuadTo((float)stroke[i].X, (float)stroke[i].Y, (float)(stroke[i].X + stroke[i + 1].X) / 2, (float)(stroke[i].Y + stroke[i + 1].Y) / 2);
                    if (stroke.Count > 1) builder.LineTo((float)stroke[^1].X, (float)stroke[^1].Y);
                    using var path = builder.Detach();
                    if (alpha < 255)
                    {
                        using var layer = new SKPaint { Color = new SKColor(0, 0, 0, alpha) };
                        canvas.SaveLayer(layer);
                        canvas.DrawPath(path, paint);
                        canvas.Restore();
                    }
                    else canvas.DrawPath(path, paint);
                }
                break;
            }
            case PdfImageMark image:
            {
                using var bitmap = SKBitmap.Decode(image.Image) ?? throw new ProcessingException("A picture could not be read.");
                using var picture = SKImage.FromBitmap(bitmap);
                using var paint = new SKPaint { IsAntialias = true };
                canvas.DrawImage(picture, new SKRect((float)image.X, (float)image.Y, (float)(image.X + image.Width), (float)(image.Y + image.Height)), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
                break;
            }
            case PdfTextMark text when !string.IsNullOrWhiteSpace(text.Text):
            {
                using var typeface = SKTypeface.FromFamilyName(text.Font, text.Bold ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
                using var font = new SKFont(typeface, (float)text.Size);
                using var shaper = new SKShaper(typeface);
                using var paint = new SKPaint { Color = Colour(text.Argb), Style = SKPaintStyle.Fill, IsAntialias = true };
                float y = (float)text.Y, lineHeight = (float)text.Size * 1.3f;
                foreach (var line in Wrap(text.Text, font, (float)text.Width))
                {
                    bool rtl = IsRightToLeft(line);
                    float width = font.MeasureText(line);
                    float x = rtl ? (float)(text.X + text.Width - width) : (float)text.X;
                    canvas.DrawShapedText(shaper, line, x, y + (float)text.Size, SKTextAlign.Left, font, paint);
                    y += lineHeight;
                }
                break;
            }
        }
    }

    /// <summary>Lines as typed, each broken at spaces to fit <paramref name="width"/> (a long word is left whole).</summary>
    internal static IEnumerable<string> Wrap(string text, SKFont font, float width)
    {
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (width <= 0 || font.MeasureText(paragraph) <= width) { yield return paragraph; continue; }
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && font.MeasureText(candidate) > width)
                {
                    yield return line;
                    line = word;
                }
                else line = candidate;
            }
            yield return line;
        }
    }

    /// <summary>True when the first strong letter is Arabic or Hebrew.</summary>
    public static bool IsRightToLeft(string line)
    {
        foreach (char c in line)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }
}
