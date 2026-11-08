using SkiaSharp;
using Toolbox.Processing.Capture;

namespace Toolbox.Processing.Tests;

/// <summary>
/// Fakes a scrolling page (text lines, boxes, a blank gap, identical repeating rows, sticky bars)
/// and checks that the stitched result is pixel-identical to the page. Ported from the Python
/// prototype's tests in tools/LongshotPrototype.
/// </summary>
public class ScrollStitcherTests
{
    private const int W = 640;
    private const int View = 330;

    // ---- fixtures ----------------------------------------------------------------------------

    private static byte[] Draw(int height, Action<SKCanvas, SKFont, SKPaint> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(W, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(SKTypeface.Default, 16))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            draw(canvas, font, paint);
        }
        return bitmap.Bytes;
    }

    private static byte[] MakePage(int height = 3200, int seed = 1, bool gap = true, bool repeat = true) => Draw(height, (canvas, font, paint) =>
    {
        var rnd = new Random(seed);
        int y = 10, n = 1;
        while (y < height - 30)
        {
            double kind = rnd.NextDouble();
            if (gap && y > 1200 && y < 1500) { y = 1500; continue; }           // blank gap
            if (repeat && y > 2000 && y < 2300)                                // identical repeating rows
            {
                paint.Style = SKPaintStyle.Stroke; paint.Color = new SKColor(200, 200, 200);
                canvas.DrawRect(20, y, W - 40, 26, paint);
                paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.Black;
                canvas.DrawText("same row", 30, y + 19, SKTextAlign.Left, font, paint);
                y += 30;
                continue;
            }
            if (kind < 0.12)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor((byte)rnd.Next(60, 230), (byte)rnd.Next(60, 230), (byte)rnd.Next(60, 230));
                int hh = rnd.Next(40, 120), x0 = rnd.Next(10, 200), x1 = rnd.Next(300, W - 10);
                canvas.DrawRect(x0, y, x1 - x0, hh, paint);
                y += hh + 12;
            }
            else
            {
                paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.Black;
                var text = $"Line {n:000}  " + string.Concat(Enumerable.Repeat("lorem ipsum ", rnd.Next(1, 4)));
                canvas.DrawText(text, rnd.Next(10, 60), y + 16, SKTextAlign.Left, font, paint);
                n++;
                y += 24;
            }
        }
    });

    private static byte[] MakeLookalikePage(int height = 3000) => Draw(height, (canvas, font, paint) =>
    {
        int i = 1;
        for (int y = 8; y < height - 30; y += 40, i++)
        {
            paint.Style = SKPaintStyle.Stroke; paint.Color = new SKColor(210, 210, 210);
            canvas.DrawRect(12, y, W - 24, 32, paint);
            paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.Black;
            canvas.DrawText($"Item {i:000}   The quick brown fox jumps over the lazy dog", 22, y + 22, SKTextAlign.Left, font, paint);
        }
    });

    private static byte[] Bar(int height, SKColor color, string label) => Draw(height, (canvas, font, paint) =>
    {
        canvas.Clear(color);
        paint.Color = SKColors.White;
        canvas.DrawText(label, 10, height - 8, SKTextAlign.Left, font, paint);
    });

    private static byte[] Rows(byte[] image, int from, int count) => image.AsSpan(from * W * 4, count * W * 4).ToArray();

    private static Frame FrameAt(byte[] page, int offset, byte[] header, byte[] footer) =>
        new(W, (header.Length + footer.Length) / (W * 4) + View, [.. header, .. Rows(page, offset, View), .. footer]);

    private static Frame WithNoise(Frame f, int amount, Random rnd)
    {
        var px = (byte[])f.Pixels.Clone();
        for (int i = 0; i < px.Length; i++)
            if (i % 4 != 3) px[i] = (byte)Math.Clamp(px[i] + rnd.Next(-amount, amount + 1), 0, 255);
        return new Frame(f.Width, f.Height, px);
    }

    private static readonly byte[] Header = Bar(50, new SKColor(40, 70, 160), "Sticky header");
    private static readonly byte[] Footer = Bar(30, new SKColor(30, 30, 30), "Sticky footer");
    private static readonly byte[] None = [];

    private static GrayFrame Gray(Frame f) => GrayFrame.From(f);

    /// <summary>Scrolls through <paramref name="offsets"/>, asserting each step finds the true distance.</summary>
    private static ScrollStitcher Scroll(byte[] page, IReadOnlyList<int> offsets, byte[] header, byte[] footer, bool useExpected = true, int noise = 0)
    {
        var rnd = new Random(0);
        Frame At(int o) => noise > 0 ? WithNoise(FrameAt(page, o, header, footer), noise, rnd) : FrameAt(page, o, header, footer);
        var st = new ScrollStitcher(At(offsets[0]));
        var deltas = new List<int>();
        for (int i = 1; i < offsets.Count; i++)
        {
            double? expected = useExpected && deltas.Count > 0 ? deltas.OrderBy(d => d).ElementAt(deltas.Count / 2) : null;
            var m = st.Add(At(offsets[i]), expected);
            Assert.True(m.Moved && m.Ok, $"offset {offsets[i - 1]}->{offsets[i]}: {m}");
            Assert.Equal(offsets[i] - offsets[i - 1], m.Distance);
            deltas.Add(m.Distance);
        }
        return st;
    }

    private static byte[] Expected(byte[] page, int first, int last, byte[] header, byte[] footer) =>
        [.. header, .. Rows(page, first, last + View - first), .. footer];

    // ---- tests -------------------------------------------------------------------------------

    [Fact]
    public void Regular_scroll_with_sticky_bars_is_pixel_identical()
    {
        var page = MakePage();
        int height = page.Length / (W * 4);
        var offsets = Enumerable.Range(0, (height - View) / 111 + 1).Select(i => i * 111).Append(height - View).Distinct().ToList();
        var st = Scroll(page, offsets, Header, Footer);
        Assert.Equal(Expected(page, 0, offsets[^1], Header, Footer), st.Image.ToArray());
    }

    [Fact]
    public void Irregular_hand_scrolling_without_sticky_bars()
    {
        // A screenful of identical rows has no answer by hand (shifts a row apart match equally well),
        // so that block is left out here; look-alike rows have their own tests below.
        var page = MakePage(seed: 3, gap: false, repeat: false);
        int height = page.Length / (W * 4);
        var rnd = new Random(7);
        var offsets = new List<int> { 0 };
        while (offsets[^1] < height - View - 10) offsets.Add(Math.Min(offsets[^1] + rnd.Next(20, 260), height - View));
        var st = Scroll(page, offsets, None, None, useExpected: false);
        Assert.Equal(Expected(page, 0, offsets[^1], None, None), st.Image.ToArray());
    }

    [Fact]
    public void Tolerates_rendering_noise()
    {
        var page = MakePage(seed: 5);
        var offsets = Enumerable.Range(0, 8).Select(i => i * 150).ToList();
        var st = Scroll(page, offsets, Header, Footer, noise: 2);
        Assert.Equal(50 + offsets[^1] + View + 30, st.Image.Height);
    }

    [Fact]
    public void Nothing_moved_and_undo()
    {
        var page = MakePage();
        var f0 = FrameAt(page, 0, Header, Footer);
        var st = new ScrollStitcher(f0);
        Assert.False(st.Add(FrameAt(page, 0, Header, Footer)).Moved);
        int h0 = st.Image.Height;
        st.Add(FrameAt(page, 120, Header, Footer), info: new StepInfo(2, 120));
        Assert.Equal(h0 + 120, st.Image.Height);
        var step = st.Undo();
        Assert.Equal(new StepInfo(2, 120), step!.Info);
        Assert.Equal(h0, st.Image.Height);
        Assert.Equal(f0.Pixels, st.Image.ToArray());
        Assert.Null(st.Undo());
    }

    [Fact]
    public void Soft_edge_of_a_fixed_footer_never_lands_mid_image()
    {
        // Where a fixed footer meets the page, browsers blend the two, and those rows change on every
        // scroll. Without treating them as part of the footer they'd leave a line at every seam.
        var page = MakePage(seed: 9, gap: false);
        int edge = (50 + View) * W * 4;
        Frame Soft(int o)
        {
            var f = FrameAt(page, o, Header, Footer);
            var px = f.Pixels;
            for (int x = 0; x < W * 4; x++)
            {
                if (x % 4 == 3) continue;
                px[edge - 2 * W * 4 + x] = (byte)(0.5 * px[edge - 2 * W * 4 + x] + 0.5 * 30);
                px[edge - W * 4 + x] = (byte)(0.2 * px[edge - W * 4 + x] + 0.8 * 30);
            }
            return f;
        }
        var offsets = Enumerable.Range(0, 13).Select(i => i * 123).ToList();
        var st = new ScrollStitcher(Soft(0));
        foreach (int o in offsets.Skip(1))
        {
            var m = st.Add(Soft(o), expected: 123);
            Assert.True(m.Ok && m.Distance == 123, m.ToString());
        }
        var got = st.Image.ToArray();
        var want = Expected(page, 0, offsets[^1], Header, Footer);
        int contentEnd = (got.Length / (W * 4) - 30 - 2) * W * 4; // the last frame's own soft edge is fine
        Assert.Equal(want.Length, got.Length);
        Assert.True(got.AsSpan(0, contentEnd).SequenceEqual(want.AsSpan(0, contentEnd)), "a seam row differs from the page");
    }

    [Fact]
    public void Blank_stretch_is_refused_by_hand_and_estimated_when_scrolling_ourselves()
    {
        var page = MakePage();
        var f0 = FrameAt(page, 1081, None, None);
        var f1 = FrameAt(page, 1250, None, None);
        Assert.False(ShiftFinder.Find(Gray(f0), Gray(f1)).Ok);
        var m = ShiftFinder.Find(Gray(f0), Gray(f1), expected: 169);
        Assert.True(m.Ok && m.Estimated && m.Distance == 169, m.ToString());
    }

    [Theory]
    [InlineData(0, 90)]
    [InlineData(100, 200)]
    [InlineData(400, 263)]
    [InlineData(700, 290)]
    public void Look_alike_rows_in_range_find_the_true_shift(int offset, int distance)
    {
        var page = MakeLookalikePage();
        var m = ShiftFinder.Find(Gray(FrameAt(page, offset, None, None)), Gray(FrameAt(page, offset + distance, None, None)));
        Assert.True(m.Ok && m.Distance == distance, m.ToString());
    }

    [Theory]
    [InlineData(0, 340)]
    [InlineData(100, 400)]
    [InlineData(400, 520)]
    [InlineData(700, 610)]
    public void Look_alike_rows_out_of_range_are_refused_not_guessed(int offset, int distance)
    {
        var page = MakeLookalikePage();
        var m = ShiftFinder.Find(Gray(FrameAt(page, offset, None, None)), Gray(FrameAt(page, offset + distance, None, None)));
        Assert.False(m.Ok, $"a jump of {distance} px was accepted as {m.Distance} (score {m.Score:F2}, worst {m.Worst:F1})");
    }

    [Fact]
    public void Jump_beyond_the_area_is_refused()
    {
        var page = MakePage();
        var m = ShiftFinder.Find(Gray(FrameAt(page, 100, None, None)), Gray(FrameAt(page, 600, None, None)));
        Assert.True(m.Moved && !m.Ok, m.ToString());
    }

    [Fact]
    public void Long_image_trims_tails_and_appends_across_chunks()
    {
        var first = new Frame(2, 3, Enumerable.Range(0, 24).Select(i => (byte)i).ToArray());
        var img = new LongImage(first);
        img.Append(Enumerable.Range(100, 16).Select(i => (byte)i).ToArray()); // 2 more rows
        Assert.Equal(5, img.Height);
        Assert.Equal(Enumerable.Range(16, 8).Select(i => (byte)i).Concat(Enumerable.Range(100, 16).Select(i => (byte)i)), img.Tail(3));
        var removed = img.Trim(3);
        Assert.Equal(2, img.Height);
        Assert.Equal(24, removed.Length);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i), img.ToArray());
    }
}
