using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace Toolbox.Processing.Imaging;

/// <summary>A rectangle in a picture's own pixels.</summary>
public readonly record struct PixelBox(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Y},{Width},{Height}");

    /// <summary>Several boxes: "x,y,w,h; x,y,w,h" (separated by ; or /).</summary>
    public static IReadOnlyList<PixelBox> ParseMany(string? text) =>
        string.IsNullOrWhiteSpace(text) ? [] : text.Split([';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Parse).ToList();

    /// <summary>"x,y,w,h" in pixels.</summary>
    public static PixelBox Parse(string text)
    {
        var parts = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            throw new FormatException($"A box is x,y,width,height in pixels (for example 120,300,200,160), not '{text}'.");
        var n = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (n[2] <= 0 || n[3] <= 0) throw new FormatException($"The box '{text}' has no size.");
        return new PixelBox(n[0], n[1], n[2], n[3]);
    }
}

/// <summary>One part of a picture, saved at full resolution.</summary>
public sealed record PictureTile(string Name, string Path, PixelBox Area);

/// <summary>What <see cref="PictureCheck.Look"/> made: the whole picture with a labelled grid, and its tiles.</summary>
public sealed record PictureLook(string Picture, int Width, int Height, string Folder, string Whole, IReadOnlyList<PictureTile> Tiles);

/// <summary>How much one tile area changed, outside the boxes (0–100).</summary>
public sealed record TileChange(string Name, double ChangedPercent);

/// <summary>What <see cref="PictureCheck.Diff"/> measured between two versions of a picture.</summary>
public sealed record PictureDiff(
    string Old,
    string New,
    int OldWidth,
    int OldHeight,
    int NewWidth,
    int NewHeight,
    int Threshold,
    double ChangedPercent,
    double? ChangedOutsidePercent,
    double? ChangedInsidePercent,
    double MeanDelta,
    PixelBox? ChangedBounds,
    IReadOnlyList<PixelBox> Boxes,
    IReadOnlyList<TileChange> Tiles,
    string HeatMap)
{
    public bool SizeChanged => OldWidth != NewWidth || OldHeight != NewHeight;
}

/// <summary>
/// Tools a director uses to really look at a picture before approving it, and to measure an edit:
/// <list type="bullet">
/// <item><see cref="Look"/>: the whole picture with a labelled 3×2 grid, and each part at full
/// resolution, so small faults (a broken hand, a flat backdrop, people at the wrong size) are seen.</item>
/// <item><see cref="Diff"/>: what changed between two versions, and how much changed outside the
/// part that was meant to change, with a heat map.</item>
/// </list>
/// Both work on files alone (no Amir Lap window needed). Their pictures go to a folder of their own,
/// never next to the work.
/// </summary>
public static class PictureCheck
{
    public const int DefaultColumns = 3;
    public const int DefaultRows = 2;

    /// <summary>A pixel counts as changed when a colour channel moved more than this (of 255).</summary>
    public const int DefaultThreshold = 16;

    private const int WholeMax = 1600;

    /// <summary>Where look and diff save their pictures unless told otherwise.</summary>
    public static string DefaultRoot => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amirlap-look");

    public static PictureLook Look(string picture, string? folder = null, int columns = DefaultColumns, int rows = DefaultRows)
    {
        picture = System.IO.Path.GetFullPath(picture);
        columns = Math.Clamp(columns, 1, 6);
        rows = Math.Clamp(rows, 1, 6);
        using var bitmap = Load(picture);
        folder ??= System.IO.Path.Combine(DefaultRoot, Stem(picture) + "-" + Fingerprint(picture));
        Directory.CreateDirectory(folder);

        var tiles = new List<PictureTile>();
        foreach (var (name, area) in Grid(bitmap.Width, bitmap.Height, columns, rows))
        {
            var path = System.IO.Path.Combine(folder, $"tile-{name}.png");
            using (var part = new SKBitmap())
            {
                if (!bitmap.ExtractSubset(part, SKRectI.Create(area.X, area.Y, area.Width, area.Height)))
                    throw new InvalidDataException($"Could not cut the part {name} out of {picture}.");
                SavePng(part, path);
            }
            tiles.Add(new PictureTile(name, path, area));
        }

        var whole = System.IO.Path.Combine(folder, "whole.png");
        using (var small = Shrink(bitmap, WholeMax))
        {
            DrawGrid(small, (double)small.Width / bitmap.Width, columns, rows, bitmap.Width, bitmap.Height);
            SavePng(small, whole);
        }
        return new PictureLook(picture, bitmap.Width, bitmap.Height, folder, whole, tiles);
    }

    public static PictureDiff Diff(string oldPicture, string newPicture, IReadOnlyList<PixelBox>? boxes = null, int threshold = DefaultThreshold, string? folder = null)
    {
        oldPicture = System.IO.Path.GetFullPath(oldPicture);
        newPicture = System.IO.Path.GetFullPath(newPicture);
        boxes ??= [];
        threshold = Math.Clamp(threshold, 1, 254);
        using var before = Load(oldPicture);
        using var loaded = Load(newPicture);
        int w = before.Width, h = before.Height;
        // A new version of another size is compared after scaling it to the old one's size.
        using var after = loaded.Width == w && loaded.Height == h ? loaded.Copy() : loaded.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var a = before.GetPixelSpan();
        var b = after.GetPixelSpan();
        int rowA = before.RowBytes, rowB = after.RowBytes;

        var inside = new bool[w * h];
        foreach (var box in boxes)
        {
            for (int y = Math.Max(0, box.Y); y < Math.Min(h, box.Bottom); y++)
                for (int x = Math.Max(0, box.X); x < Math.Min(w, box.Right); x++)
                    inside[y * w + x] = true;
        }
        var grid = Grid(w, h, DefaultColumns, DefaultRows, overlap: 0).ToList();
        var tileChanged = new long[grid.Count];
        var tileCount = new long[grid.Count];

        long changed = 0, changedIn = 0, changedOut = 0, countIn = 0, countOut = 0;
        double deltaSum = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var deltas = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            int tileRow = Math.Min(DefaultRows - 1, y * DefaultRows / h);
            for (int x = 0; x < w; x++)
            {
                int ia = y * rowA + x * 4, ib = y * rowB + x * 4;
                int d = Math.Max(Math.Abs(a[ia] - b[ib]), Math.Max(Math.Abs(a[ia + 1] - b[ib + 1]), Math.Abs(a[ia + 2] - b[ib + 2])));
                deltas[y * w + x] = (byte)d;
                deltaSum += d;
                bool isChanged = d > threshold;
                bool isIn = inside[y * w + x];
                if (isIn) countIn++;
                else
                {
                    countOut++;
                    int tile = tileRow * DefaultColumns + Math.Min(DefaultColumns - 1, x * DefaultColumns / w);
                    tileCount[tile]++;
                    if (isChanged) tileChanged[tile]++;
                }
                if (!isChanged) continue;
                changed++;
                if (isIn) changedIn++;
                else
                {
                    changedOut++;
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        folder ??= DefaultRoot;
        Directory.CreateDirectory(folder);
        var heat = System.IO.Path.Combine(folder, $"diff-{Stem(oldPicture)}-vs-{Stem(newPicture)}-{Fingerprint(newPicture)}.png");
        using (var map = HeatMap(before, deltas, threshold, boxes))
        using (var small = Shrink(map, WholeMax))
            SavePng(small, heat);

        double total = (double)w * h;
        var tiles = grid.Select((g, i) => new TileChange(g.Name, tileCount[i] == 0 ? 0 : Math.Round(100.0 * tileChanged[i] / tileCount[i], 2))).ToList();
        return new PictureDiff(
            oldPicture, newPicture, w, h, loaded.Width, loaded.Height, threshold,
            Math.Round(100.0 * changed / total, 2),
            boxes.Count == 0 ? null : countOut == 0 ? 0 : Math.Round(100.0 * changedOut / countOut, 2),
            boxes.Count == 0 ? null : countIn == 0 ? 0 : Math.Round(100.0 * changedIn / countIn, 2),
            Math.Round(deltaSum / total, 2),
            maxX < 0 ? null : new PixelBox(minX, minY, maxX - minX + 1, maxY - minY + 1),
            boxes, tiles, heat);
    }

    /// <summary>A plain report of a look, for the agent that asked.</summary>
    public static string Describe(PictureLook look)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileName(look.Picture)}: {look.Width}x{look.Height}.\n");
        sb.Append("Open each of these and look for faults before you approve: the whole first, then every part at full size.\n");
        sb.Append("Whole (with the grid of parts): ").Append(look.Whole).Append('\n');
        foreach (var t in look.Tiles)
            sb.Append(CultureInfo.InvariantCulture, $"Part {t.Name} (x {t.Area.X}-{t.Area.Right}, y {t.Area.Y}-{t.Area.Bottom}): {t.Path}\n");
        sb.Append("Check: perspective of doors, windows and screens; people's size for their distance; hands, arms and fingers; faces, twins and copies; flat backdrops; light and time of day; the brief's hard rules; sameness with the approved master.");
        return sb.ToString();
    }

    /// <summary>A plain report of a diff, for the agent that asked.</summary>
    public static string Describe(PictureDiff diff)
    {
        var sb = new StringBuilder();
        string Name(string p) => System.IO.Path.GetFileName(p);
        sb.Append(CultureInfo.InvariantCulture, $"{Name(diff.Old)} → {Name(diff.New)}\n");
        if (diff.SizeChanged)
            sb.Append(CultureInfo.InvariantCulture, $"Size changed: {diff.OldWidth}x{diff.OldHeight} → {diff.NewWidth}x{diff.NewHeight} (compared after scaling the new one to the old size; the whole picture was redrawn).\n");
        else sb.Append(CultureInfo.InvariantCulture, $"Same size: {diff.OldWidth}x{diff.OldHeight}.\n");
        sb.Append(CultureInfo.InvariantCulture, $"Changed: {diff.ChangedPercent:0.##}% of the picture (a pixel counts when a colour moved more than {diff.Threshold}/255); average change {diff.MeanDelta:0.##}/255.\n");
        if (diff.Boxes.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Inside the box{(diff.Boxes.Count == 1 ? "" : "es")} ({string.Join("; ", diff.Boxes)}): {diff.ChangedInsidePercent:0.##}% changed.\n");
            sb.Append(CultureInfo.InvariantCulture, $"Outside: {diff.ChangedOutsidePercent:0.##}% changed");
            sb.Append(diff.ChangedOutsidePercent switch
            {
                < 1 => " (the rest is kept).\n",
                < 5 => " (small changes leaked outside; look at the heat map).\n",
                _ => " (the edit changed far more than the box; treat it as a new picture and check it all again).\n",
            });
        }
        if (diff.ChangedBounds is { } bounds)
            sb.Append(CultureInfo.InvariantCulture, $"Changes{(diff.Boxes.Count > 0 ? " outside the box" : "")} span x {bounds.X}-{bounds.Right}, y {bounds.Y}-{bounds.Bottom}.\n");
        var worst = diff.Tiles.Where(t => t.ChangedPercent >= 1).OrderByDescending(t => t.ChangedPercent).Take(3).ToList();
        if (worst.Count > 0)
            sb.Append("Most change").Append(diff.Boxes.Count > 0 ? " outside the box" : "").Append(" in parts ").Append(string.Join(", ", worst.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.Name} ({t.ChangedPercent:0.#}%)")))).Append(" (A-C left to right, 1-2 top to bottom).\n");
        sb.Append("Heat map (red = changed, green = the box): ").Append(diff.HeatMap);
        return sb.ToString();
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <summary>The parts of a picture, named A1 (top left) to C2, overlapping a little so nothing on an edge is cut in half.</summary>
    internal static IEnumerable<(string Name, PixelBox Area)> Grid(int width, int height, int columns, int rows, double overlap = 0.08)
    {
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                double cellW = (double)width / columns, cellH = (double)height / rows;
                int padX = (int)Math.Round(cellW * overlap), padY = (int)Math.Round(cellH * overlap);
                int x0 = Math.Max(0, (int)Math.Floor(c * cellW) - padX);
                int y0 = Math.Max(0, (int)Math.Floor(r * cellH) - padY);
                int x1 = Math.Min(width, (int)Math.Ceiling((c + 1) * cellW) + padX);
                int y1 = Math.Min(height, (int)Math.Ceiling((r + 1) * cellH) + padY);
                yield return ($"{(char)('A' + c)}{r + 1}", new PixelBox(x0, y0, x1 - x0, y1 - y0));
            }
        }
    }

    private static SKBitmap Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"There is no picture at {path}.", path);
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException($"{path} isn't a picture this tool can read (PNG, JPEG, WebP, GIF, BMP).");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        return SKBitmap.Decode(codec, info) ?? throw new InvalidDataException($"{path} could not be read.");
    }

    private static SKBitmap Shrink(SKBitmap source, int max)
    {
        if (source.Width <= max && source.Height <= max) return source.Copy();
        double scale = (double)max / Math.Max(source.Width, source.Height);
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)), SKColorType.Rgba8888, SKAlphaType.Unpremul);
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
    }

    private static void DrawGrid(SKBitmap target, double scale, int columns, int rows, int width, int height)
    {
        using var canvas = new SKCanvas(target);
        using var line = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 170), StrokeWidth = 2, Style = SKPaintStyle.Stroke };
        using var shadow = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), StrokeWidth = 4, Style = SKPaintStyle.Stroke };
        for (int c = 1; c < columns; c++)
        {
            float x = (float)(c * (double)width / columns * scale);
            canvas.DrawLine(x, 0, x, target.Height, shadow);
            canvas.DrawLine(x, 0, x, target.Height, line);
        }
        for (int r = 1; r < rows; r++)
        {
            float y = (float)(r * (double)height / rows * scale);
            canvas.DrawLine(0, y, target.Width, y, shadow);
            canvas.DrawLine(0, y, target.Width, y, line);
        }
        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, Math.Max(14, target.Width / 60f));
        using var plate = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 150) };
        using var ink = new SKPaint { IsAntialias = true, Color = SKColors.White };
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                var label = $"{(char)('A' + c)}{r + 1}";
                float x = (float)(c * (double)width / columns * scale) + 8, y = (float)(r * (double)height / rows * scale) + 8;
                float w = font.MeasureText(label) + 12;
                canvas.DrawRoundRect(SKRect.Create(x, y, w, font.Size + 10), 5, 5, plate);
                canvas.DrawText(label, x + 6, y + font.Size + 1, SKTextAlign.Left, font, ink);
            }
        }
    }

    private static SKBitmap HeatMap(SKBitmap before, byte[] deltas, int threshold, IReadOnlyList<PixelBox> boxes)
    {
        int w = before.Width, h = before.Height;
        var map = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var src = before.GetPixelSpan();
        int rowSrc = before.RowBytes, rowDst = map.RowBytes;
        var bytes = new byte[rowDst * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * rowSrc + x * 4, o = y * rowDst + x * 4;
                // The old picture, grey and dimmed, so the changes stand out.
                byte grey = (byte)((src[i] * 30 + src[i + 1] * 59 + src[i + 2] * 11) / 100 * 45 / 100);
                int d = deltas[y * w + x];
                if (d > threshold)
                {
                    bytes[o] = (byte)Math.Min(255, 110 + d * 2);
                    bytes[o + 1] = (byte)(grey / 3);
                    bytes[o + 2] = (byte)(grey / 3);
                }
                else
                {
                    bytes[o] = grey;
                    bytes[o + 1] = grey;
                    bytes[o + 2] = grey;
                }
                bytes[o + 3] = 255;
            }
        }
        System.Runtime.InteropServices.Marshal.Copy(bytes, 0, map.GetPixels(), bytes.Length);
        using var canvas = new SKCanvas(map);
        using var pen = new SKPaint { IsAntialias = true, Color = new SKColor(0x22, 0xC5, 0x5E), StrokeWidth = Math.Max(2, w / 400f), Style = SKPaintStyle.Stroke };
        foreach (var box in boxes) canvas.DrawRect(SKRect.Create(box.X, box.Y, box.Width, box.Height), pen);
        return map;
    }

    private static void SavePng(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100) ?? throw new InvalidDataException($"Could not write {path}.");
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }

    private static string Stem(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-').ToArray());
        return safe.Length > 60 ? safe[..60] : safe;
    }

    /// <summary>Short and stable for one version of one file: its full path and when it was last written.</summary>
    private static string Fingerprint(string path)
    {
        var key = path.ToLowerInvariant() + "|" + File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..10].ToLowerInvariant();
    }
}
