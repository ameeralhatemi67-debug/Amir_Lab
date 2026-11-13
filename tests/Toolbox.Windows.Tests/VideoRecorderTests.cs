using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Windows.Longshot.Video;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Longshot's video recorder end to end on made-up frames (no screen involved): it writes an MP4
/// that Windows reads back with the right number of frames and length, upright (a picture flipped
/// by the pipeline would show green on top), changing at the right moment; and the trimmed copy
/// and the GIF come out right.
/// </summary>
public sealed class VideoRecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amirlap-video-" + Guid.NewGuid().ToString("N"));

    public VideoRecorderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Top half red until 0.6 s of recording, then blue; bottom half green.</summary>
    private sealed class Frames(int width, int height) : IFrameGrabber
    {
        public Func<double> Seconds { get; set; } = () => 0;
        public int Width => width;
        public int Height => height;

        public void Grab(byte[] into)
        {
            bool late = Seconds() >= 0.6;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    bool top = y < height / 2;
                    into[i] = (byte)(top && late ? 255 : 0);           // B
                    into[i + 1] = (byte)(top ? 0 : 255);                // G
                    into[i + 2] = (byte)(top && !late ? 255 : 0);       // R
                    into[i + 3] = 255;
                }
        }

        public void Dispose() { }
    }

    private static (byte R, byte G, byte B) Pixel(BitmapSource image, double fx, double fy)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int x = (int)(fx * bgra.PixelWidth), y = (int)(fy * bgra.PixelHeight);
        var px = new byte[4];
        bgra.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }

    private static void Near((byte R, byte G, byte B) p, byte r, byte g, byte b, string what) =>
        Assert.True(Math.Abs(p.R - r) < 60 && Math.Abs(p.G - g) < 60 && Math.Abs(p.B - b) < 60, $"{what}: expected about ({r}, {g}, {b}), got ({p.R}, {p.G}, {p.B})");

    [Fact]
    public async Task Records_upright_in_time_then_trims_and_makes_a_gif()
    {
        var path = Path.Combine(_dir, "test.mp4");
        var frames = new Frames(320, 240);
        var recorder = new ScreenRecorder(frames, path, new RecordingOptions(Fps: 20, Cursor: false, Clicks: false));
        frames.Seconds = () => recorder.Elapsed.TotalSeconds;
        await recorder.StartAsync();
        await Task.Delay(1300);
        var result = await recorder.StopAsync();
        recorder.Dispose();

        Assert.True(new FileInfo(path).Length > 1000, "an MP4 was written");
        Assert.InRange(result.Frames, 18, 28);
        var duration = await VideoFiles.DurationAsync(path);
        Assert.InRange(duration.TotalSeconds, 1.0, 1.6);

        var early = await VideoFiles.FrameAsync(path, TimeSpan.FromSeconds(0.25));
        Assert.Equal(320, early.PixelWidth);
        Near(Pixel(early, 0.5, 0.2), 255, 0, 0, "top early (red; green here would mean upside down)");
        Near(Pixel(early, 0.5, 0.8), 0, 255, 0, "bottom");
        var late = await VideoFiles.FrameAsync(path, TimeSpan.FromSeconds(1.0));
        Near(Pixel(late, 0.5, 0.2), 0, 0, 255, "top late (blue)");

        var trimmed = Path.Combine(_dir, "trimmed.mp4");
        await VideoFiles.TrimAsync(path, TimeSpan.FromSeconds(0.75), TimeSpan.FromSeconds(1.2), trimmed);
        var trimmedLength = await VideoFiles.DurationAsync(trimmed);
        Assert.InRange(trimmedLength.TotalSeconds, 0.3, 0.7);
        Near(Pixel(await VideoFiles.FrameAsync(trimmed, TimeSpan.FromSeconds(0.05)), 0.5, 0.2), 0, 0, 255, "the trimmed copy starts blue");

        var gif = Path.Combine(_dir, "clip.gif");
        await VideoFiles.GifAsync(path, TimeSpan.Zero, TimeSpan.FromSeconds(1), gif, maxWidth: 160, fps: 10);
        using var animated = NetVips.Image.NewFromFile(gif, kwargs: new NetVips.VOption { { "n", -1 } });
        Assert.Equal(160, animated.Width);
        // Identical neighbouring frames are merged (their delays added), so check the playing time.
        int pages = animated.Height / animated.PageHeight;
        Assert.InRange(pages, 2, 10);
        var delays = (int[])animated.Get("delay");
        Assert.InRange(delays.Take(pages).Sum(), 900, 1100);
    }

    [Fact]
    public void New_names_never_overwrite()
    {
        var when = new DateTime(2026, 11, 13, 21, 45, 12);
        var first = VideoFiles.NewPath(_dir, when);
        Assert.EndsWith("Longshot video 2026-11-13 214512.mp4", first);
        File.WriteAllText(first, "x");
        Assert.EndsWith("Longshot video 2026-11-13 214512 (2).mp4", VideoFiles.NewPath(_dir, when));
        Assert.EndsWith("Longshot video 2026-11-13 214512 Trimmed.mp4", VideoFiles.Beside(first, " Trimmed", ".mp4"));
    }

    [Fact]
    public void Click_rings_are_drawn_into_the_frame()
    {
        var px = new byte[100 * 100 * 4];
        RegionGrabber.DrawRing(px, 100, 100, 50, 50, 20, 3, 255);
        Assert.True(px[(50 * 100 + 70) * 4 + 2] > 200, "on the ring: yellow");
        Assert.Equal(0, px[(50 * 100 + 50) * 4 + 2]); // the middle stays as it was
    }
}
