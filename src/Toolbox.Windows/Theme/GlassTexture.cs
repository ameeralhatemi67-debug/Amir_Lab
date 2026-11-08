using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toolbox.Windows.Theme;

/// <summary>
/// The fine frosted grain that makes see-through panels read as glass or ice rather than a ghost:
/// faint light and dark specks with a few sparkles, generated once per theme and tiled.
/// </summary>
internal static class GlassTexture
{
    private static ImageBrush? _light, _dark;

    public static ImageBrush Grain(bool dark)
    {
        ref var cached = ref dark ? ref _dark : ref _light;
        if (cached is not null) return cached;
        const int size = 128;
        var rng = new Random(dark ? 7 : 42);
        var px = new byte[size * size * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            double r = rng.NextDouble();
            byte a, v;
            if (r < 0.012) { v = 255; a = (byte)(dark ? 70 : 150); }            // a sparkle
            else if (r < 0.55) { v = 255; a = (byte)rng.Next(0, dark ? 22 : 46); } // light grain
            else { v = 0; a = (byte)rng.Next(0, dark ? 30 : 18); }                 // dark grain
            byte pre = (byte)(v * a / 255); // premultiplied
            px[i] = pre;
            px[i + 1] = pre;
            px[i + 2] = pre;
            px[i + 3] = a;
        }
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, px, size * 4);
        bitmap.Freeze();
        var brush = new ImageBrush(bitmap) { TileMode = TileMode.Tile, Viewport = new System.Windows.Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze();
        cached = brush;
        return brush;
    }
}
