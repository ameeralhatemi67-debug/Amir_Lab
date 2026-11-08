using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toolbox.Windows.Shell;

/// <summary>
/// Draws the Amir Lap glyph at runtime, so no binary icon assets are needed: four tiles on an
/// accent square, one tool per tile. Top-left holds a small wheel (File Toolbox), bottom-right a down
/// arrow (Longshot); the two pale tiles stand for tools still to come. Drawn on a 64-unit grid.
/// </summary>
internal static class AppIcon
{
    public static BitmapSource Render(int size, Color accent)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
            var accentBrush = new SolidColorBrush(accent);
            dc.DrawRoundedRectangle(accentBrush, null, new Rect(2, 2, 60, 60), 14, 14);

            var solid = Brushes.White;
            var pale = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
            dc.DrawRoundedRectangle(solid, null, new Rect(11, 11, 19, 19), 4.5, 4.5);
            dc.DrawRoundedRectangle(pale, null, new Rect(34, 11, 19, 19), 4.5, 4.5);
            dc.DrawRoundedRectangle(pale, null, new Rect(11, 34, 19, 19), 4.5, 4.5);
            dc.DrawRoundedRectangle(solid, null, new Rect(34, 34, 19, 19), 4.5, 4.5);

            // The details only read from 24 px up; below that they would just blur the tiles.
            if (size >= 24)
            {
                var ring = new Pen(accentBrush, 3) { DashStyle = new DashStyle([6.5 / 3, 1.4 / 3], 0), DashCap = PenLineCap.Flat };
                dc.DrawEllipse(null, ring, new Point(20.5, 20.5), 5, 5);
                var arrow = new Pen(accentBrush, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, arrow, Geometry.Parse("M43.5,38.5 L43.5,48 M39.5,44 L43.5,48 L47.5,44"));
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The fixed accent of the program icon (the tray icon follows the Windows accent instead).</summary>
    public static readonly Color BrandAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    /// <summary>Builds a System.Drawing.Icon (PNG-in-ICO) for the notification area.</summary>
    public static System.Drawing.Icon CreateTrayIcon(Color accent)
    {
        using var ico = new MemoryStream(IcoBytes(accent, [16, 20, 24, 32, 48]));
        return new System.Drawing.Icon(ico);
    }

    /// <summary>
    /// An .ico file: classic 32-bit bitmaps below 256 px (readable by every Windows component and
    /// tool) and a PNG entry at 256 px, as Windows' own icons are stored.
    /// </summary>
    public static byte[] IcoBytes(Color accent, IReadOnlyList<int> sizes)
    {
        var images = sizes.Select(s => (Size: s, Data: s >= 256 ? Png(Render(s, accent)) : Dib(Render(s, accent)))).ToList();

        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);
            w.Write((short)1);
            w.Write((short)images.Count);
            int offset = 6 + 16 * images.Count;
            foreach (var (s, data) in images)
            {
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((short)1);
                w.Write((short)32);
                w.Write(data.Length);
                w.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in images) w.Write(data);
        }
        return ico.ToArray();
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>ICO bitmap entry: BITMAPINFOHEADER (double height), bottom-up straight-alpha BGRA, empty AND mask.</summary>
    private static byte[] Dib(BitmapSource bitmap)
    {
        int size = bitmap.PixelWidth, stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte alpha = pixels[i + 3];
            if (alpha is 0 or 255) continue;
            for (int c = 0; c < 3; c++) pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / alpha); // un-premultiply
        }
        int maskStride = (size + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(40);
            w.Write(size);
            w.Write(size * 2);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(0); // BI_RGB
            w.Write(stride * size + maskStride * size);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            w.Write(0);
            for (int row = size - 1; row >= 0; row--) w.Write(pixels, row * stride, stride);
            w.Write(new byte[maskStride * size]);
        }
        return ms.ToArray();
    }
}
