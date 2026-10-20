using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toolbox.Windows.Shell;

/// <summary>
/// Draws the app glyph (a small segmented wheel) at runtime, so no binary icon assets are needed.
/// </summary>
internal static class AppIcon
{
    public static BitmapSource Render(int size, Color accent)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            double c = size / 2.0, ro = size * 0.47, ri = size * 0.2;
            var tile = new SolidColorBrush(Color.FromRgb(0xF4, 0xF4, 0xF6));
            var rim = new SolidColorBrush(Color.FromRgb(0x2A, 0x2B, 0x30));
            dc.DrawEllipse(rim, null, new Point(c, c), ro + size * 0.03, ro + size * 0.03);
            int n = 6;
            for (int i = 0; i < n; i++)
            {
                double sweep = 2 * Math.PI / n, gap = 0.12;
                double a0 = i * sweep - sweep / 2 + gap, a1 = i * sweep + sweep / 2 - gap;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(P(c, a0, ro), true, true);
                    ctx.ArcTo(P(c, a1, ro), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, false);
                    ctx.LineTo(P(c, a1, ri + size * 0.05), true, false);
                    ctx.ArcTo(P(c, a0, ri + size * 0.05), new Size(ri, ri), 0, false, SweepDirection.Counterclockwise, true, false);
                }
                dc.DrawGeometry(i == 1 ? new SolidColorBrush(accent) : tile, null, g);
            }
            dc.DrawEllipse(new SolidColorBrush(accent), null, new Point(c, c), ri * 0.75, ri * 0.75);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Builds a System.Drawing.Icon (PNG-in-ICO) for the notification area.</summary>
    public static System.Drawing.Icon CreateTrayIcon(Color accent)
    {
        var sizes = new[] { 16, 20, 24, 32, 48 };
        var images = sizes.Select(s =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Render(s, accent)));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return (Size: s, Data: ms.ToArray());
        }).ToList();

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
        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }

    private static Point P(double c, double angle, double r) => new(c + Math.Sin(angle) * r, c - Math.Cos(angle) * r);
}
