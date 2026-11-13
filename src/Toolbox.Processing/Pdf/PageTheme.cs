namespace Toolbox.Processing.Pdf;

/// <summary>
/// How the viewer colours a rendered page: white paper becomes <see cref="Paper"/> and black ink
/// <see cref="Ink"/>, every grey and colour in between follows (per channel), so Dark is a true
/// negative and Sepia a warm page. With <see cref="KeepPictures"/>, the pictures' areas are left as
/// they are (photos and raster figures; vector drawings are coloured like the rest of the page).
/// It only changes what is shown, never the file.
/// </summary>
public sealed record PageTheme(string Id, uint Paper, uint Ink, bool KeepPictures = false)
{
    public static readonly PageTheme Light = new("light", 0xFFFFFF, 0x000000);
    public static readonly PageTheme Sepia = new("sepia", 0xF4ECD8, 0x3B2F20);
    public static readonly PageTheme Grey = new("grey", 0x3A3B40, 0xE4E4E6);
    public static readonly PageTheme Dark = new("dark", 0x000000, 0xFFFFFF);
    public static readonly PageTheme Night = new("night", 0x121214, 0xC2C2C2);

    public static IReadOnlyList<PageTheme> Presets { get; } = [Light, Sepia, Grey, Dark, Night];

    /// <summary>A shade between light (0) and dark (1), for the level slider.</summary>
    public static PageTheme Level(double level, bool keepPictures = false)
    {
        level = Math.Clamp(level, 0, 1);
        return new PageTheme("level", Mix(0xFFFFFF, 0x0E0E10, level), Mix(0x000000, 0xDCDCDC, level), keepPictures);
    }

    public static PageTheme Find(string? id, double level, bool keepPictures)
    {
        if (id == "level") return Level(level, keepPictures);
        var preset = Presets.FirstOrDefault(p => p.Id == id) ?? Light;
        return preset with { KeepPictures = keepPictures };
    }

    public bool IsPlain => Paper == 0xFFFFFF && Ink == 0x000000;

    /// <summary>The page's background colour under this theme (for the area around a page that is still loading).</summary>
    public uint Background => Paper;

    /// <summary>
    /// Recolours a BGRx render in place. <paramref name="keep"/> are pixel rectangles (x, y, width,
    /// height) left untouched when <see cref="KeepPictures"/> is on.
    /// </summary>
    public void Apply(byte[] bgrx, int width, int height, IReadOnlyList<(int X, int Y, int W, int H)>? keep = null)
    {
        if (IsPlain) return;
        var lutB = Lut((byte)Ink, (byte)Paper);
        var lutG = Lut((byte)(Ink >> 8), (byte)(Paper >> 8));
        var lutR = Lut((byte)(Ink >> 16), (byte)(Paper >> 16));

        // Pictures keep their pixels: copied out first, put back after.
        var saved = new List<((int X, int Y, int W, int H) Rect, byte[] Pixels)>();
        if (KeepPictures && keep is not null)
        {
            foreach (var r in keep)
            {
                var c = Clip(r, width, height);
                if (c.W <= 0 || c.H <= 0) continue;
                var copy = new byte[c.W * c.H * 4];
                for (int y = 0; y < c.H; y++) Buffer.BlockCopy(bgrx, ((c.Y + y) * width + c.X) * 4, copy, y * c.W * 4, c.W * 4);
                saved.Add((c, copy));
            }
        }
        for (int i = 0; i + 3 < bgrx.Length && i < width * height * 4; i += 4)
        {
            bgrx[i] = lutB[bgrx[i]];
            bgrx[i + 1] = lutG[bgrx[i + 1]];
            bgrx[i + 2] = lutR[bgrx[i + 2]];
        }
        foreach (var (c, copy) in saved)
            for (int y = 0; y < c.H; y++) Buffer.BlockCopy(copy, y * c.W * 4, bgrx, ((c.Y + y) * width + c.X) * 4, c.W * 4);
    }

    private static (int X, int Y, int W, int H) Clip((int X, int Y, int W, int H) r, int width, int height)
    {
        int x0 = Math.Clamp(r.X, 0, width), y0 = Math.Clamp(r.Y, 0, height);
        int x1 = Math.Clamp(r.X + r.W, 0, width), y1 = Math.Clamp(r.Y + r.H, 0, height);
        return (x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>0 (black ink) → ink, 255 (white paper) → paper, straight in between.</summary>
    private static byte[] Lut(byte ink, byte paper)
    {
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)Math.Round(ink + (paper - ink) * v / 255.0);
        return lut;
    }

    private static uint Mix(uint a, uint b, double t)
    {
        uint Channel(int shift) => (uint)Math.Round(((a >> shift) & 0xFF) + ((((b >> shift) & 0xFF) - (double)((a >> shift) & 0xFF)) * t));
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }
}
