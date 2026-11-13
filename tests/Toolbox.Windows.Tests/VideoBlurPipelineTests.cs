using System.IO;
using Toolbox.Processing.Studio;
using Toolbox.Processing.Video;
using Toolbox.Windows.Longshot.Video;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Video blur on real MP4 files made by Longshot's recorder from made-up frames (no screen): the
/// frame reader gives every frame upright, at its visible size and time, and can seek; a box is
/// followed through the decoded (compressed) frames of scrolling text; and the blurred copy has
/// the box hidden only while it is on.
/// </summary>
public sealed class VideoBlurPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amirlap-videoblur-" + Guid.NewGuid().ToString("N"));

    public VideoBlurPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// A page of word-like blocks scrolling up <see cref="Speed"/> px a frame (by frame count, so the
    /// motion is exact whatever the timing), with a red bar on top and a green bar at the bottom.
    /// </summary>
    internal sealed class ScrollingPage(int width, int height) : IFrameGrabber
    {
        public const int Speed = 6, Bar = 16;
        private readonly byte[] _page = MakePage(width, 3000);
        private int _n;
        public int Width => width;
        public int Height => height;
        public int Grabbed => _n;

        private static byte[] MakePage(int width, int height)
        {
            var page = new byte[width * height];
            Array.Fill(page, (byte)245);
            var rnd = new Random(11);
            for (int y = 8; y < height - 20; y += 22)
                for (int x = rnd.Next(6, 30); x < width - 40;)
                {
                    int word = rnd.Next(14, 60);
                    byte shade = (byte)rnd.Next(10, 120);
                    for (int yy = y; yy < y + 11; yy++)
                        for (int xx = x; xx < Math.Min(x + word, width - 4); xx++) page[yy * width + xx] = shade;
                    x += word + rnd.Next(7, 14);
                }
            return page;
        }

        public void Grab(byte[] into)
        {
            int scroll = Speed * _n++;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    if (y < Bar) { into[i] = 0; into[i + 1] = 0; into[i + 2] = 230; }
                    else if (y >= height - Bar) { into[i] = 0; into[i + 1] = 200; into[i + 2] = 0; }
                    else { byte v = _page[(y + scroll) * width + x]; into[i] = into[i + 1] = into[i + 2] = v; }
                    into[i + 3] = 255;
                }
        }

        public void Dispose() { }
    }

    /// <summary>Records a short video of the scrolling page (200 px tall: not a multiple of 16, so the decoder pads it).</summary>
    private async Task<string> Record(double seconds = 1.6)
    {
        var path = Path.Combine(_dir, "page.mp4");
        var recorder = new ScreenRecorder(new ScrollingPage(320, 200), path, new RecordingOptions(Fps: 20, Cursor: false, Clicks: false));
        await recorder.StartAsync();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await recorder.StopAsync();
        recorder.Dispose();
        return path;
    }

    private static (int R, int G, int B) At(byte[] px, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return (px[i + 2], px[i + 1], px[i]);
    }

    [Fact]
    public async Task Frames_are_read_upright_in_order_and_a_box_follows_the_scrolling_text()
    {
        var path = await Record();
        using var reader = new VideoFrameReader(path);
        Assert.Equal(320, reader.Width);
        Assert.Equal(200, reader.Height); // the 8 padding rows (208) are not handed out
        Assert.InRange(reader.FrameRate, 19.5, 20.5);
        Assert.InRange(reader.Duration.TotalSeconds, 1.2, 2.2);
        Assert.False(reader.HasSound);

        var frames = new List<(byte[] Px, TimeSpan At)>();
        var buffer = new byte[320 * 200 * 4];
        while (reader.Read(buffer, out var at)) frames.Add(((byte[])buffer.Clone(), at));
        Assert.InRange(frames.Count, 22, 44);
        for (int i = 1; i < frames.Count; i++) Assert.True(frames[i].At > frames[i - 1].At, "frames in time order");
        var top = At(frames[0].Px, 320, 160, 6);
        var bottom = At(frames[0].Px, 320, 160, 194);
        Assert.True(top.R > 180 && top.G < 70, $"red bar on top (upright), got {top}");
        Assert.True(bottom.G > 150 && bottom.R < 70, $"green bar at the bottom, got {bottom}");

        // Seeking lands on the frame shown at that time.
        var seekTo = frames[frames.Count / 2].At;
        Assert.True(reader.SeekRead(seekTo, buffer, out var landed));
        Assert.True((landed - seekTo).Duration() < TimeSpan.FromMilliseconds(30), $"asked {seekTo}, got {landed}");

        // A box on a line of text, followed through the compressed frames: it moves up with the
        // page, 6 px a frame for every frame the recorder grabbed in between.
        var box = new PixelRect(60, 150, 140, 18);
        var follower = new BoxFollower(BoxFollower.Luma(frames[0].Px, 320, 200), 320, 200, box);
        int lastY = box.Y;
        for (int i = 1; i < frames.Count && lastY > 40; i++)
        {
            var step = follower.Next(BoxFollower.Luma(frames[i].Px, 320, 200));
            Assert.True(step.State is FollowState.Following or FollowState.Still, $"frame {i}: {step.State}, score {step.Score:0.0}");
            int moved = lastY - step.Box.Y;
            Assert.True(moved >= 0 && moved % ScrollingPage.Speed is 0 or 1 or 5, $"frame {i}: moved {moved} px");
            Assert.Equal(60, step.Box.X);
            lastY = step.Box.Y;
        }
        Assert.True(lastY < 100, "it followed the text a good way up");
    }

    [Fact]
    public async Task The_blurred_copy_hides_the_box_only_while_it_is_on()
    {
        var path = await Record(1.2);
        var plan = new VideoBlurPlan();
        var track = new BlurTrack { Id = 1, Mode = RedactionMode.Solid, Start = TimeSpan.FromSeconds(0.3), End = TimeSpan.FromSeconds(0.7) };
        track.SetKey(TimeSpan.FromSeconds(0.3), new PixelRect(100, 60, 120, 80), TimeSpan.FromMilliseconds(10));
        plan.Tracks.Add(track);
        var output = Path.Combine(_dir, "page blurred.mp4");
        var saved = await VideoBlurRenderer.SaveAsync(path, plan, output);
        Assert.True(File.Exists(output));
        Assert.False(saved.SoundLeftOut);

        using var original = new VideoFrameReader(path);
        using var copy = new VideoFrameReader(output);
        Assert.Equal(original.Width, copy.Width);
        Assert.Equal(original.Height, copy.Height);
        var buffer = new byte[copy.Width * copy.Height * 4];
        int frames = 0, black = 0;
        while (copy.Read(buffer, out var at))
        {
            frames++;
            var inside = At(buffer, copy.Width, 160, 100);
            bool dark = inside.R < 25 && inside.G < 25 && inside.B < 25;
            if (at > TimeSpan.FromSeconds(0.34) && at < TimeSpan.FromSeconds(0.66)) Assert.True(dark, $"hidden at {at}: {inside}");
            if (at < TimeSpan.FromSeconds(0.25) || at > TimeSpan.FromSeconds(0.75)) Assert.False(dark && At(buffer, copy.Width, 104, 64) is { R: < 25, G: < 25 } && At(buffer, copy.Width, 216, 136) is { R: < 25, G: < 25 }, $"not hidden at {at}");
            if (dark) black++;
            var bar = At(buffer, copy.Width, 160, 6);
            Assert.True(bar.R > 180, $"outside the box nothing changed (top bar at {at}: {bar})");
        }
        Assert.InRange(frames, saved.Frames - 1, saved.Frames + 1);
        Assert.InRange(black, 6, 10); // 0.4 s at 20 fps

        // Cancelling leaves no file.
        var cancelled = Path.Combine(_dir, "cancelled.mp4");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoBlurRenderer.SaveAsync(path, plan, cancelled, cancel: cts.Token));
        Assert.False(File.Exists(cancelled));
    }
}
