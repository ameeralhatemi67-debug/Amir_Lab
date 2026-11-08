using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Shell;

/// <summary>
/// Draws the Amir Lap logo ("Glass tiles") at runtime, so no binary icon assets are needed: four
/// tiles on a blue-to-indigo square, two solid and two frosted, with a light edge along the top.
/// Top-left holds a small wheel (File Toolbox), bottom-right a down arrow (Longshot); the frosted
/// tiles stand for tools still to come. Drawn on a 64-unit grid.
/// </summary>
internal static class AppIcon
{
    private static readonly Color BrandLight = Color.FromRgb(0x6A, 0xA6, 0xFF);
    private static readonly Color BrandDeep = Color.FromRgb(0x43, 0x30, 0xC9);
    private static readonly Color Indigo = Color.FromRgb(0x1E, 0x1B, 0x4B);

    public static BitmapSource Render(int size, Color accent)
    {
        bool brand = accent == Palette.BrandBlue;
        var light = brand ? BrandLight : ThemeResources.Mix(accent, Colors.White, 0.32);
        var deep = brand ? BrandDeep : ThemeResources.Mix(accent, Indigo, 0.45);
        bool detail = size >= 24; // the wheel, arrow and edges only read from 24 px up

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 64.0, size / 64.0));
            var square = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            square.GradientStops.Add(new GradientStop(light, 0));
            square.GradientStops.Add(new GradientStop(accent, 0.5));
            square.GradientStops.Add(new GradientStop(deep, 1));
            dc.DrawRoundedRectangle(square, null, new Rect(2, 2, 60, 60), 18, 18);

            var pane = new LinearGradientBrush(Colors.White, Color.FromArgb(0xD6, 0xFF, 0xFF, 0xFF), 90);
            Brush frost = detail
                ? new LinearGradientBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), 45)
                : new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
            var frostEdge = detail ? new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 0.8) : null;
            if (detail)
            {
                var shadow = new SolidColorBrush(Color.FromArgb(0x38, 0x1B, 0x2F, 0x8A));
                dc.DrawRoundedRectangle(shadow, null, new Rect(11, 12.6, 19, 19), 6, 6);
                dc.DrawRoundedRectangle(shadow, null, new Rect(34, 35.6, 19, 19), 6, 6);
            }
            dc.DrawRoundedRectangle(pane, null, new Rect(11, 11, 19, 19), 6, 6);
            dc.DrawRoundedRectangle(frost, frostEdge, new Rect(34, 11, 19, 19), 6, 6);
            dc.DrawRoundedRectangle(frost, frostEdge, new Rect(11, 34, 19, 19), 6, 6);
            dc.DrawRoundedRectangle(pane, null, new Rect(34, 34, 19, 19), 6, 6);

            if (detail)
            {
                var accentBrush = new SolidColorBrush(accent);
                var ring = new Pen(accentBrush, 2.6) { DashStyle = new DashStyle([6.3 / 2.6, 1.55 / 2.6], 0), DashCap = PenLineCap.Flat };
                dc.DrawEllipse(null, ring, new Point(20.5, 20.5), 5, 5);
                var arrow = new Pen(accentBrush, 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(null, arrow, Geometry.Parse("M43.5,38.5 L43.5,48 M39.6,44.2 L43.5,48 L47.4,44.2"));

                // The glass edge: bright along the top, faint at the sides, a little light at the bottom.
                var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), 0));
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.45));
                rim.GradientStops.Add(new GradientStop(Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF), 1));
                dc.DrawRoundedRectangle(null, new Pen(rim, 1.5), new Rect(2.75, 2.75, 58.5, 58.5), 17.25, 17.25);
            }
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The program icon's accent (Amir Lap blue). The tray icon follows the chosen accent.</summary>
    public static readonly Color BrandAccent = Palette.BrandBlue;

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
