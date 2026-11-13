using Toolbox.Processing.Studio;
using Toolbox.Processing.Video;

namespace Toolbox.Processing.Tests;

/// <summary>
/// Video blur's pieces on made-up frames: boxes moving between keys, keys thinned out, the blur
/// leaving no letter shapes, and the follower keeping a box on content that scrolls, slides,
/// leaves the picture, changes, or is plain.
/// </summary>
public sealed class VideoBlurTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static byte[] L(byte[] bgra) => BoxFollower.Luma(bgra, Screen.W, Screen.H);

    [Fact]
    public void A_box_moves_in_a_straight_line_between_keys_and_is_held_outside_them()
    {
        var track = new BlurTrack { Start = S(1), End = S(5) };
        track.SetKey(S(1), new PixelRect(0, 100, 50, 20), S(0.01));
        track.SetKey(S(3), new PixelRect(100, 0, 50, 20), S(0.01));
        Assert.Null(track.BoxAt(S(0.5)));                                   // not on yet
        Assert.Equal(new PixelRect(0, 100, 50, 20), track.BoxAt(S(1)));
        Assert.Equal(new PixelRect(50, 50, 50, 20), track.BoxAt(S(2)));     // half way
        Assert.Equal(new PixelRect(100, 0, 50, 20), track.BoxAt(S(4.5)));   // held after the last key
        Assert.Null(track.BoxAt(S(5.2)));                                   // off again

        // The same frame again replaces its key; another time adds one in order.
        track.SetKey(S(3.005), new PixelRect(110, 0, 50, 20), S(0.01));
        Assert.Equal(2, track.Keys.Count);
        track.SetKey(S(2), new PixelRect(10, 10, 50, 20), S(0.01));
        Assert.Equal([S(1), S(2), S(3)], track.Keys.Select(k => k.At));
    }

    [Fact]
    public void Followed_keys_are_thinned_to_where_the_movement_changes()
    {
        var keys = new List<BlurKey>();
        for (int i = 0; i <= 60; i++)
        {
            int y = i <= 30 ? 400 - 7 * i : 400 - 7 * 30;  // scrolls for a second, then stops
            keys.Add(new BlurKey(S(i / 30.0), new PixelRect(40, y, 120, 24)));
        }
        var thin = BlurTrack.Simplify(keys, 1.0);
        Assert.Equal(3, thin.Count);
        Assert.Equal(S(1), thin[1].At);
        var track = new BlurTrack { Start = S(0), End = S(2) };
        track.SetKey(S(0), keys[0].Box, S(0.01));
        track.ReplaceKeysAfter(S(0), keys);
        foreach (var k in keys) Assert.Equal(k.Box, track.BoxAt(k.At));    // still exactly where it was followed
    }

    /// <summary>Text-like strokes: 2 px dark, 3 px light, on a light background.</summary>
    private static byte[] Strokes(int width, int height)
    {
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                byte v = x % 5 < 2 && y % 12 > 2 ? (byte)30 : (byte)235;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        return px;
    }

    private static double[] Columns(byte[] px, int width, PixelRect r)
    {
        var columns = new double[r.Width];
        for (int x = 0; x < r.Width; x++)
        {
            double sum = 0;
            for (int y = 0; y < r.Height; y++) sum += px[((r.Y + y) * width + r.X + x) * 4 + 1];
            columns[x] = sum / r.Height;
        }
        return columns;
    }

    /// <summary>Fine detail across the box: the largest bend between columns 2 px apart (letters are all bends; smooth shading has none).</summary>
    private static double Detail(byte[] px, int width, PixelRect r)
    {
        var c = Columns(px, width, r);
        double worst = 0;
        for (int x = 2; x < c.Length - 2; x++) worst = Math.Max(worst, Math.Abs(c[x - 2] - 2 * c[x] + c[x + 2]));
        return worst;
    }

    [Theory]
    [InlineData(RedactionMode.Blur)]
    [InlineData(RedactionMode.Pixelate)]
    [InlineData(RedactionMode.Solid)]
    public void The_box_keeps_no_letter_shapes_and_nothing_outside_changes(RedactionMode mode)
    {
        const int w = 200, h = 80;
        var px = Strokes(w, h);
        var before = (byte[])px.Clone();
        var box = new PixelRect(30, 20, 120, 24);
        double original = Detail(px, w, box);
        FrameBlur.Apply(px, w, h, box, mode);
        if (mode == RedactionMode.Pixelate)
        {
            // Blocks: the columns only change every 6 px or more.
            var c = Columns(px, w, box);
            int run = 1, shortest = int.MaxValue;
            for (int x = 1; x < c.Length; x++)
                if (Math.Abs(c[x] - c[x - 1]) < 0.01) run++;
                else { shortest = Math.Min(shortest, run); run = 1; }
            Assert.True(shortest >= 6, $"a block only {shortest} px wide");
        }
        else Assert.True(Detail(px, w, box) < original / 12, $"{mode}: {Detail(px, w, box):0.0} of {original:0.0} left");
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inside = x >= box.X && x < box.X + box.Width && y >= box.Y && y < box.Y + box.Height;
                if (!inside) Assert.Equal(before[(y * w + x) * 4 + 1], px[(y * w + x) * 4 + 1]);
            }
        // A box hanging off the picture is clipped, not an error.
        FrameBlur.Apply(px, w, h, new PixelRect(-20, h - 10, 60, 30), mode);
    }

    /// <summary>A screen: a fixed grey sidebar and a page of word-like blocks that can be moved.</summary>
    internal sealed class Screen
    {
        public const int W = 640, H = 400, Sidebar = 120;
        private readonly byte[] _page;
        private readonly int _pageW = W - Sidebar, _pageH = 2400;

        public Screen()
        {
            _page = new byte[_pageW * _pageH];
            Array.Fill(_page, (byte)245);
            var rnd = new Random(5);
            for (int y = 10; y < _pageH - 20; y += 20)
                for (int x = rnd.Next(6, 30); x < _pageW - 40;)
                {
                    int word = rnd.Next(12, 60);
                    byte shade = (byte)rnd.Next(10, 140);
                    for (int yy = y; yy < y + 10; yy++)
                        for (int xx = x; xx < Math.Min(x + word, _pageW - 4); xx++)
                            if ((xx + yy) % 3 != 0) _page[yy * _pageW + xx] = shade;
                    x += word + rnd.Next(6, 14);
                }
        }

        /// <summary>The screen with the page scrolled by <paramref name="scroll"/> and slid by (dx, dy).</summary>
        public byte[] Frame(int scroll, int dx = 0, int dy = 0, Func<int, int, byte?>? paint = null)
        {
            var px = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    byte v;
                    if (x < Sidebar) v = (byte)(70 + (y / 40) * 3);
                    else
                    {
                        int pxl = x - Sidebar - dx, py = y + scroll - dy;
                        v = pxl >= 0 && pxl < _pageW && py >= 0 && py < _pageH ? _page[py * _pageW + pxl] : (byte)200;
                    }
                    if (paint?.Invoke(x, y) is { } p) v = p;
                    int i = (y * W + x) * 4;
                    px[i] = px[i + 1] = px[i + 2] = v;
                    px[i + 3] = 255;
                }
            return px;
        }
    }

    [Fact]
    public void It_follows_scrolling_text_and_keeps_going_as_it_leaves_the_top()
    {
        var screen = new Screen();
        var box = new PixelRect(200, 300, 160, 22);
        var follower = new BoxFollower(L(screen.Frame(0)), Screen.W, Screen.H, box);
        Assert.False(follower.IsPlain);
        FollowState last = FollowState.Following;
        int frame;
        for (frame = 1; frame < 80; frame++)
        {
            var step = follower.Next(L(screen.Frame(9 * frame)));
            int expectedY = 300 - 9 * frame;
            last = step.State;
            if (step.State == FollowState.Gone) break;
            Assert.True(step.State is FollowState.Following or FollowState.Leaving, $"frame {frame}: {step.State} (score {step.Score:0.0})");
            Assert.Equal(200, step.Box.X);
            Assert.InRange(step.Box.Y, expectedY - 1, expectedY + 1);
        }
        Assert.Equal(FollowState.Gone, last);
        Assert.InRange(frame, 35, 38); // 300 + 22 px at 9 px a frame
    }

    [Fact]
    public void It_follows_text_sliding_under_a_fixed_header_and_ends_there()
    {
        var screen = new Screen();
        Func<int, int, byte?> header = (x, y) => y < 40 ? (byte)(60 + x % 7) : null; // a header bar that stays put
        var box = new PixelRect(200, 130, 160, 22);
        var follower = new BoxFollower(L(screen.Frame(0, paint: header)), Screen.W, Screen.H, box);
        FollowState state = FollowState.Following;
        int frame;
        for (frame = 1; frame < 40; frame++)
        {
            var step = follower.Next(L(screen.Frame(8 * frame, paint: header)));
            state = step.State;
            if (state == FollowState.Gone) break;
            Assert.True(state == FollowState.Following, $"frame {frame}: {state} (score {step.Score:0.0})");
            Assert.InRange(step.Box.Y, 130 - 8 * frame - 1, 130 - 8 * frame + 1);
        }
        Assert.Equal(FollowState.Gone, state);
        // Gone once most of it is under the header (its bottom edge near the header's).
        Assert.InRange(frame, 10, 14);
    }

    [Fact]
    public void It_follows_a_window_dragged_across_the_screen()
    {
        var screen = new Screen();
        var box = new PixelRect(260, 150, 90, 20);
        var follower = new BoxFollower(L(screen.Frame(100)), Screen.W, Screen.H, box);
        for (int f = 1; f <= 20; f++)
        {
            var step = follower.Next(L(screen.Frame(100, 6 * f, -4 * f)));
            Assert.Equal(FollowState.Following, step.State);
            Assert.Equal(new PixelRect(260 + 6 * f, 150 - 4 * f, 90, 20), step.Box);
        }
        var still = follower.Next(L(screen.Frame(100, 120, -80)));
        Assert.Equal(FollowState.Still, still.State);
    }

    [Fact]
    public void Changed_content_keeps_the_box_where_it_was_and_plain_boxes_stay_put()
    {
        var screen = new Screen();
        var box = new PixelRect(220, 200, 120, 20);
        var follower = new BoxFollower(L(screen.Frame(0)), Screen.W, Screen.H, box);
        var rnd = new Random(1);
        // The text in the box is replaced (someone typed over it, or the page changed there).
        var step = follower.Next(L(screen.Frame(0, paint: (x, y) => x >= 200 && x < 360 && y >= 190 && y < 230 ? (byte)rnd.Next(0, 256) : null)));
        Assert.Equal(FollowState.Lost, step.State);
        Assert.Equal(box, step.Box);

        var plain = new BoxFollower(L(screen.Frame(0, paint: (x, y) => y > 380 ? (byte)250 : null)), Screen.W, Screen.H, new PixelRect(300, 385, 100, 12));
        Assert.True(plain.IsPlain);
        Assert.Equal(FollowState.Plain, plain.Next(L(screen.Frame(50))).State);
    }
}
