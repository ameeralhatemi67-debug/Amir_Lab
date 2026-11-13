using SkiaSharp;
using Toolbox.Processing.Capture;

namespace Toolbox.Processing.Tests;

/// <summary>
/// Screens where only part scrolls: a panel beside a fixed sidebar (as on documentation sites and
/// chat apps), a scroll box inside a page that stays put, content beside its own scrollbar thumb.
/// The part must be found, and the stitched result must be that part's content, pixel for pixel.
/// Also: what must not be mistaken for a scrolling part (a caret, an animation, nothing at all).
/// </summary>
public class ScrollPartTests
{
    private const int SW = 900, SH = 600; // the "screen" (the frame)
    private static readonly SKColor Border = new(176, 180, 188);

    private static byte[] Draw(int width, int height, Action<SKCanvas, SKFont, SKPaint> draw, SKColor? clear = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(SKTypeface.Default, 15))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            canvas.Clear(clear ?? SKColors.White);
            draw(canvas, font, paint);
        }
        return bitmap.Bytes;
    }

    /// <summary>Tall content (text lines and boxes) for the part that scrolls.</summary>
    private static byte[] Content(int width, int height, int seed) => Draw(width, height, (canvas, font, paint) =>
    {
        var rnd = new Random(seed);
        int y = 12, n = 1;
        while (y < height - 30)
        {
            if (rnd.NextDouble() < 0.12)
            {
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor((byte)rnd.Next(60, 230), (byte)rnd.Next(60, 230), (byte)rnd.Next(60, 230));
                int hh = rnd.Next(30, 90), bx0 = rnd.Next(14, width / 3), bx1 = rnd.Next(width / 2, width - 14);
                canvas.DrawRect(bx0, y, bx1 - bx0, hh, paint);
                y += hh + 12;
                continue;
            }
            paint.Style = SKPaintStyle.Fill;
            paint.Color = SKColors.Black;
            canvas.DrawText($"Message {n:000}  " + string.Concat(Enumerable.Repeat("lorem ipsum dolor ", rnd.Next(1, 3))), rnd.Next(16, 40), y + 15, SKTextAlign.Left, font, paint);
            n++;
            y += 23;
        }
    });

    /// <summary>
    /// The still screen around the part: a top bar, a sidebar with items, page text everywhere else,
    /// and a 1-px border round the part (like a real scroll box or panel divider).
    /// </summary>
    private static byte[] Around(PartRect part, bool sidebar, bool pageText) => Draw(SW, SH, (canvas, font, paint) =>
    {
        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(36, 64, 150);
        canvas.DrawRect(0, 0, SW, 52, paint);
        paint.Color = SKColors.White;
        canvas.DrawText("App title   Search   Settings", 14, 32, SKTextAlign.Left, font, paint);
        if (sidebar)
        {
            paint.Color = new SKColor(240, 242, 246);
            canvas.DrawRect(0, 52, 220, SH - 52, paint);
            paint.Color = new SKColor(40, 40, 48);
            for (int i = 0; i < 20; i++) canvas.DrawText($"Channel {i + 1}", 18, 84 + i * 26, SKTextAlign.Left, font, paint);
        }
        if (pageText)
        {
            paint.Color = new SKColor(30, 30, 30);
            for (int y = 70; y < SH - 10; y += 22)
                canvas.DrawText("Still page text around the box that does not scroll at all " + y, sidebar ? 236 : 12, y, SKTextAlign.Left, font, paint);
        }
        // Clear the part itself and draw its border just outside it.
        paint.Color = SKColors.White;
        canvas.DrawRect(part.X, part.Y, part.Width, part.Height, paint);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        paint.Color = Border;
        canvas.DrawRect(part.X - 0.5f, part.Y - 0.5f, part.Width + 1, part.Height + 1, paint);
    });

    /// <summary>A screen whose <paramref name="part"/> shows <paramref name="content"/> scrolled to an offset.</summary>
    private sealed class Screen(PartRect part, byte[] around, byte[] content, int thumb = 0)
    {
        public PartRect Part => part;
        public int ContentHeight => content.Length / (ContentWidth * 4);
        public int ContentWidth => part.Width - thumb;
        public int MaxOffset => ContentHeight - part.Height;

        public Frame At(int offset)
        {
            var px = (byte[])around.Clone();
            int rowBytes = ContentWidth * 4;
            for (int y = 0; y < part.Height; y++)
                Buffer.BlockCopy(content, (offset + y) * rowBytes, px, ((part.Y + y) * SW + part.X) * 4, rowBytes);
            if (thumb > 0)
            {
                // The scrollbar: a pale track with a thumb sized and placed like a real one.
                int thumbH = Math.Max(30, part.Height * part.Height / ContentHeight);
                int thumbY = (int)((part.Height - thumbH) * (offset / (double)Math.Max(1, MaxOffset)));
                for (int y = 0; y < part.Height; y++)
                    for (int x = 0; x < thumb; x++)
                    {
                        bool on = y >= thumbY && y < thumbY + thumbH && x >= 3 && x < thumb - 3;
                        byte v = on ? (byte)120 : (byte)236;
                        int i = ((part.Y + y) * SW + part.X + ContentWidth + x) * 4;
                        px[i] = px[i + 1] = px[i + 2] = v;
                    }
            }
            return new Frame(SW, SH, px);
        }

        /// <summary>What a perfect capture of the part from offset 0 to <paramref name="last"/> looks like.</summary>
        public byte[] Expected(int last) => content.AsSpan(0, (last + part.Height) * ContentWidth * 4).ToArray();
    }

    private static GrayFrame Gray(Frame f) => GrayFrame.From(f);

    /// <summary>Stitches the screen at each offset in turn; the first step must find the part.</summary>
    private static ScrollStitcher Capture(Screen screen, IReadOnlyList<int> offsets, bool keepRows, out PartRect found)
    {
        var st = new ScrollStitcher(screen.At(offsets[0]));
        found = default;
        var cut = new PartRect(0, 0, SW, SH);
        for (int i = 1; i < offsets.Count; i++)
        {
            var frame = screen.At(offsets[i]);
            var m = i == 1 ? st.AddOrNarrow(frame, keepRows: keepRows) : st.Add(frame.Crop(cut));
            Assert.True(m.Moved && m.Ok, $"step {offsets[i - 1]}->{offsets[i]}: {m}");
            Assert.Equal(offsets[i] - offsets[i - 1], m.Distance);
            if (i == 1)
            {
                Assert.NotNull(m.Part);
                found = m.Part!.Value;
                cut = found;
            }
        }
        return st;
    }

    private static List<int> Offsets(int max, int step) =>
        Enumerable.Range(0, max / step + 1).Select(i => i * step).Append(max).Distinct().ToList();

    [Fact]
    public void A_panel_beside_a_fixed_sidebar_is_found_and_captured_on_its_own()
    {
        // A documentation site or chat app: the sidebar and top bar stay, the panel on the right scrolls.
        var part = new PartRect(221, 53, SW - 221, SH - 53);
        var screen = new Screen(part, Around(part, sidebar: true, pageText: false), Content(part.Width, 2600, seed: 2));
        var st = Capture(screen, Offsets(screen.MaxOffset, 137), keepRows: false, out var found);
        Assert.Equal(part, found);
        Assert.Equal(screen.Expected(screen.MaxOffset), st.Image.ToArray());
    }

    [Fact]
    public void Dark_mode_works_the_same()
    {
        static Frame Invert(Frame f)
        {
            var px = (byte[])f.Pixels.Clone();
            for (int i = 0; i < px.Length; i++) if (i % 4 != 3) px[i] = (byte)(255 - px[i]);
            return new Frame(f.Width, f.Height, px);
        }
        var part = new PartRect(221, 53, SW - 221, SH - 53);
        var screen = new Screen(part, Around(part, sidebar: true, pageText: false), Content(part.Width, 2600, seed: 12));
        var m = ShiftFinder.FindPart(Gray(Invert(screen.At(300))), Gray(Invert(screen.At(410))), expected: null, keepRows: false, out string why);
        Assert.True(m is { Ok: true, Distance: 110 } && m.Part == part, m?.ToString() ?? why);
    }

    [Fact]
    public void A_scroll_box_inside_a_still_page_is_found_and_captured_on_its_own()
    {
        var part = new PartRect(300, 190, 380, 250);
        var screen = new Screen(part, Around(part, sidebar: true, pageText: true), Content(part.Width, 1800, seed: 4));
        var st = Capture(screen, Offsets(screen.MaxOffset, 83), keepRows: false, out var found);
        Assert.Equal(part, found);
        Assert.Equal(screen.Expected(screen.MaxOffset), st.Image.ToArray());
    }

    [Fact]
    public void A_moving_scrollbar_thumb_is_left_out()
    {
        // Picked exactly (a scroll area): its scrollbar is inside it, and the thumb moves less than the content.
        var part = new PartRect(260, 120, 520, 400);
        var screen = new Screen(part, Around(part, sidebar: false, pageText: true), Content(part.Width - 14, 2400, seed: 6), thumb: 14);
        var frames = Offsets(screen.MaxOffset, 151);
        var st = new ScrollStitcher(screen.At(0).Crop(part)); // the frame is the scroll area itself
        var m = st.AddOrNarrow(screen.At(frames[1]).Crop(part), keepRows: true);
        Assert.True(m.Ok && m.Part is { } p && p == new PartRect(0, 0, part.Width - 14, part.Height), m.ToString());
        var cut = new PartRect(part.X, part.Y, part.Width - 14, part.Height);
        foreach (int o in frames.Skip(2)) Assert.True(st.Add(screen.At(o).Crop(cut)).Ok);
        Assert.Equal(screen.Expected(screen.MaxOffset), st.Image.ToArray());
    }

    [Fact]
    public void A_small_list_that_moved_a_little_is_still_found()
    {
        var part = new PartRect(560, 300, 220, 160);
        var screen = new Screen(part, Around(part, sidebar: true, pageText: true), Content(part.Width, 900, seed: 8));
        var m = ShiftFinder.FindPart(Gray(screen.At(0)), Gray(screen.At(21)));
        Assert.True(m is { Ok: true, Distance: 21 } && m.Part == part, m?.ToString() ?? "null");
    }

    [Fact]
    public void A_whole_window_that_scrolls_except_a_thin_scrollbar_keeps_its_header()
    {
        // The page scrolls under a sticky top bar; only its scrollbar (inside the frame) moves differently.
        var part = new PartRect(0, 53, SW, SH - 53);
        var around = Around(new PartRect(0, 53, SW, SH - 53), sidebar: false, pageText: false);
        var screen = new Screen(part, around, Content(SW - 14, 2400, seed: 10), thumb: 14);
        var m = ShiftFinder.FindPart(Gray(screen.At(0)), Gray(screen.At(120)));
        Assert.True(m is { Ok: true, Distance: 120 }, m?.ToString() ?? "null");
        Assert.Equal(new PartRect(0, 0, SW - 14, SH), m!.Part); // the top bar stays in, as a sticky header
        Assert.True(m.Top >= 52, $"header {m.Top}");
    }

    [Fact]
    public void Nothing_scrolling_but_a_caret_is_not_a_part()
    {
        var part = new PartRect(300, 190, 380, 250);
        var screen = new Screen(part, Around(part, sidebar: true, pageText: true), Content(part.Width, 1200, seed: 3));
        var a = screen.At(200);
        var b = screen.At(200);
        for (int y = 400; y < 418; y++) for (int x = 90; x < 92; x++) { int i = (y * SW + x) * 4; b.Pixels[i] = b.Pixels[i + 1] = b.Pixels[i + 2] = 0; }
        Assert.Null(ShiftFinder.FindPart(Gray(a), Gray(b)));
        Assert.False(ShiftFinder.Find(Gray(a), Gray(b)).Moved);
    }

    [Fact]
    public void Something_animating_in_place_is_not_taken_for_scrolling()
    {
        // A "video" in the still page: its pixels change, but no shift explains it.
        var part = new PartRect(300, 190, 380, 250);
        var screen = new Screen(part, Around(part, sidebar: true, pageText: true), Content(part.Width, 1200, seed: 3));
        var a = screen.At(0);
        var b = screen.At(0);
        var rnd = new Random(1);
        for (int y = 200; y < 330; y++)
            for (int x = 300; x < 520; x++)
            {
                int i = (y * SW + x) * 4;
                a.Pixels[i] = a.Pixels[i + 1] = a.Pixels[i + 2] = (byte)rnd.Next(256);
                b.Pixels[i] = b.Pixels[i + 1] = b.Pixels[i + 2] = (byte)rnd.Next(256);
            }
        Assert.Null(ShiftFinder.FindPart(Gray(a), Gray(b)));
    }

    [Fact]
    public void A_video_inside_the_scrolling_part_stops_it_rather_than_cutting_the_part_in_two()
    {
        var part = new PartRect(221, 53, SW - 221, SH - 53);
        var screen = new Screen(part, Around(part, sidebar: true, pageText: false), Content(part.Width, 2600, seed: 2));
        var a = screen.At(0);
        var b = screen.At(140);
        var rnd = new Random(5);
        for (int y = 260; y < 420; y++)
            for (int x = 480; x < 640; x++)
            {
                int i = (y * SW + x) * 4;
                b.Pixels[i] = b.Pixels[i + 1] = b.Pixels[i + 2] = (byte)rnd.Next(256);
            }
        var m = ShiftFinder.FindPart(Gray(a), Gray(b));
        Assert.True(m is null || m.Part is { } p && p.X <= 480 && p.Right >= 640, m?.ToString());
    }

    [Fact]
    public void Narrowing_is_only_for_the_first_part()
    {
        var part = new PartRect(221, 53, SW - 221, SH - 53);
        var screen = new Screen(part, Around(part, sidebar: true, pageText: false), Content(part.Width, 2600, seed: 2));
        var st = new ScrollStitcher(screen.At(0));
        st.GrowDown(new Frame(SW, SH + 10, [.. screen.At(0).Pixels, .. new byte[SW * 10 * 4]]));
        Assert.Throws<InvalidOperationException>(() => st.Narrow(part));
    }

    [Fact]
    public void Cropping_takes_exactly_the_part()
    {
        var px = Enumerable.Range(0, 6 * 4 * 4).Select(i => (byte)i).ToArray();
        var f = new Frame(6, 4, px);
        var c = f.Crop(new PartRect(2, 1, 3, 2));
        Assert.Equal((3, 2), (c.Width, c.Height));
        Assert.Equal(px.AsSpan((1 * 6 + 2) * 4, 12).ToArray(), c.Pixels.AsSpan(0, 12).ToArray());
        Assert.Equal(px.AsSpan((2 * 6 + 2) * 4, 12).ToArray(), c.Pixels.AsSpan(12, 12).ToArray());
        Assert.Same(f, f.Crop(new PartRect(0, 0, 6, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => f.Crop(new PartRect(4, 0, 3, 1)));
    }
}
