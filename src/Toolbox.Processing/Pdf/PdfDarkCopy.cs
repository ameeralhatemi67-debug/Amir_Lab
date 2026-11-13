using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Toolbox.Core;

namespace Toolbox.Processing.Pdf;

/// <summary>
/// A dark copy of a PDF for reading at night: white pages become black and black text white, while
/// the pages stay what they were (vector, searchable, the same size, a few hundred bytes larger).
/// </summary>
/// <remarks>
/// How: a white rectangle is painted under each page's content, and a white rectangle with the
/// Difference blend mode over it (PDF 1.4 transparency): white − colour inverts every colour.
/// With <c>keepPictures</c>, the pictures' areas get a second Difference rectangle, which inverts
/// them back, so photos and figures look as they did. Annotations (form fields, comments) are drawn
/// by viewers on top of the page and stay as they are.
/// </remarks>
public static class PdfDarkCopy
{
    public static void Make(string input, string output, bool keepPictures, Action<double>? progress = null, CancellationToken cancellation = default)
    {
        // Where the pictures are, from PDFium (user space, as the content streams see it).
        var pictures = new List<IReadOnlyList<PdfBox>>();
        using (var reader = PdfDocument.Open(input))
        {
            for (int p = 1; p <= reader.PageCount; p++)
            {
                cancellation.ThrowIfCancellationRequested();
                pictures.Add(keepPictures ? reader.GetImageBoxes(p) : []);
                progress?.Invoke(0.4 * p / reader.PageCount);
            }
        }

        PdfSharp.Pdf.PdfDocument pdf;
        try
        {
            pdf = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        }
        catch (Exception ex) when (ex is PdfReaderException or InvalidOperationException or NotImplementedException)
        {
            throw new ProcessingException("This PDF could not be changed: " + ex.Message, ex);
        }
        using (pdf)
        {
            if (pdf.PageCount != pictures.Count) throw new ProcessingException("The PDF's pages could not be matched up.");
            for (int i = 0; i < pdf.PageCount; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                var page = pdf.Pages[i];
                var box = page.MediaBox;
                double x = box.X1, y = box.Y1, w = box.Width, h = box.Height;

                var gs = new PdfExtGState(pdf);
                gs.Elements["/BM"] = new PdfName("/Difference");
                string name = page.Resources.AddExtGState(gs);
                if (gs.Reference is null) pdf.Internals.AddObject(gs);

                // Under the page: white, so empty paper is something to invert. Then the page's own
                // content, in a saved state of its own (it may leave its matrix changed).
                Write(page.Contents.PrependContent(), $"q 1 g {F(x)} {F(y)} {F(w)} {F(h)} re f Q q\n");
                var over = new StringBuilder();
                over.Append($"Q q {name} gs 1 g {F(x)} {F(y)} {F(w)} {F(h)} re f\n");
                foreach (var pic in pictures[i])
                    over.Append($"{F(pic.Left)} {F(pic.Bottom)} {F(pic.Width)} {F(pic.Height)} re f\n");
                over.Append("Q\n");
                Write(page.Contents.AppendContent(), over.ToString());
                progress?.Invoke(0.4 + 0.5 * (i + 1) / pdf.PageCount);
            }
            pdf.Info.Creator = string.IsNullOrEmpty(pdf.Info.Creator) ? "Amir Lap" : pdf.Info.Creator;
            pdf.Save(output);
        }
        progress?.Invoke(1);
    }

    private static void Write(PdfContent content, string operators)
    {
        var bytes = Encoding.ASCII.GetBytes(operators);
        if (content.Stream is null) content.CreateStream(bytes);
        else content.Stream.Value = bytes;
    }

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
