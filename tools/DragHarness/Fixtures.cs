using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace DragHarness;

/// <summary>Creates the files the harness drags. Names include Arabic to exercise Unicode paths.</summary>
internal static class Fixtures
{
    public const string PdfName = "Report 001.pdf";
    public const string ImageName = "صورة اختبار.png";
    public const string SecondImageName = "Screenshot 02.png";

    public static void Create(string folder)
    {
        Directory.CreateDirectory(folder);
        WritePng(Path.Combine(folder, ImageName), 1200, 800, Colors.SteelBlue, transparentCorner: true);
        WritePng(Path.Combine(folder, SecondImageName), 1600, 900, Colors.IndianRed, transparentCorner: false);
        WritePdf(Path.Combine(folder, PdfName), pages: 1);
        WritePdf(Path.Combine(folder, "Handbook 3 pages.pdf"), pages: 3);
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void WritePng(string path, int width, int height, Color color, bool transparentCorner)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(color, Colors.White, 35), null, new Rect(0, 0, width, height));
            dc.DrawEllipse(Brushes.Gold, new Pen(Brushes.Black, 6), new Point(width * 0.35, height * 0.45), height * 0.22, height * 0.22);
            dc.DrawRectangle(Brushes.White, null, new Rect(width * 0.55, height * 0.25, width * 0.35, height * 0.08));
            dc.DrawRectangle(Brushes.White, null, new Rect(width * 0.55, height * 0.40, width * 0.30, height * 0.08));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        BitmapSource source = bitmap;
        if (transparentCorner)
        {
            // Punch a transparent square into the top-left corner.
            var wb = new WriteableBitmap(bitmap);
            int size = Math.Min(width, height) / 5;
            var clear = new byte[size * size * 4];
            wb.WritePixels(new Int32Rect(0, 0, size, size), clear, size * 4, 0);
            source = wb;
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WritePdf(string path, int pages)
    {
        using var document = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.WhiteSmoke, 0, 0, 612, 792);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 40 + 60 * i, 90, 160)), 60, 60, 492, 120);
            for (int line = 0; line < 18; line++)
                gfx.DrawRectangle(XBrushes.DimGray, 60, 230 + line * 26, 300 + (line * 37 % 190), 9);
        }
        document.Save(path);
    }
}
