using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using NetVips;
using Windows.Media.Editing;
using Windows.Storage;
using Image = NetVips.Image;

namespace Toolbox.Windows.Longshot.Video;

/// <summary>
/// Where recordings go and what can be done with one afterwards, with Windows' own media
/// pipeline: its length, a frame as a picture, a trimmed copy (MP4), and a GIF for chats and
/// READMEs. Every result is a new file; nothing is overwritten.
/// </summary>
internal static class VideoFiles
{
    /// <summary>Videos\Screen Recordings, where Windows' Snipping Tool keeps recordings too.</summary>
    public static string DefaultFolder => IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Screen Recordings");

    /// <summary>A free name like "Longshot video 2026-11-13 214512.mp4" (a number is added if it exists).</summary>
    public static string NewPath(string folder, DateTime when, string suffix = "", string ext = ".mp4")
    {
        Directory.CreateDirectory(folder);
        string stem = $"Longshot video {when:yyyy-MM-dd HHmmss}{suffix}";
        string path = IOPath.Combine(folder, stem + ext);
        for (int i = 2; File.Exists(path); i++) path = IOPath.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    /// <summary>A name beside <paramref name="path"/> with a suffix ("… Trimmed.mp4"), never an existing file.</summary>
    public static string Beside(string path, string suffix, string ext)
    {
        var folder = IOPath.GetDirectoryName(path)!;
        var stem = IOPath.GetFileNameWithoutExtension(path) + suffix;
        var result = IOPath.Combine(folder, stem + ext);
        for (int i = 2; File.Exists(result); i++) result = IOPath.Combine(folder, $"{stem} ({i}){ext}");
        return result;
    }

    private static async Task<MediaComposition> Open(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var clip = await MediaClip.CreateFromFileAsync(file);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        return composition;
    }

    public static async Task<TimeSpan> DurationAsync(string path) => (await Open(path)).Clips[0].OriginalDuration;

    /// <summary>The frame nearest <paramref name="at"/>, as a picture (0 width/height: its own size).</summary>
    public static async Task<BitmapSource> FrameAsync(string path, TimeSpan at, int width = 0, int height = 0)
    {
        var composition = await Open(path);
        var thumb = await composition.GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame);
        using var stream = System.IO.WindowsRuntimeStreamExtensions.AsStream(thumb);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>A copy of the part between <paramref name="from"/> and <paramref name="to"/>, as MP4 (frame-accurate).</summary>
    /// <remarks>
    /// Done with MediaTranscoder's trim (the same encoder that records). MediaComposition's
    /// RenderToFileAsync was tried first and failed with a bare COM error in this desktop app.
    /// </remarks>
    public static async Task TrimAsync(string path, TimeSpan from, TimeSpan to, string output, IProgress<double>? progress = null)
    {
        var source = await StorageFile.GetFileFromPathAsync(path);
        var composition = await Open(path);
        var clip = composition.Clips[0];
        var duration = clip.OriginalDuration;
        // The profile is built from the clip's own video. For recordings Windows reports a 0 × 0
        // size (they come from a MediaStreamSource), which made the transcoder fail with E_POINTER,
        // so the size is read from a frame then.
        var video = clip.GetVideoEncodingProperties();
        uint width = video.Width, height = video.Height;
        if (width == 0 || height == 0)
        {
            var first = await FrameAsync(path, TimeSpan.Zero);
            (width, height) = ((uint)first.PixelWidth, (uint)first.PixelHeight);
        }
        var profile = global::Windows.Media.MediaProperties.MediaEncodingProfile.CreateMp4(global::Windows.Media.MediaProperties.VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = width;
        profile.Video.Height = height;
        if (video.FrameRate.Numerator > 0)
        {
            profile.Video.FrameRate.Numerator = video.FrameRate.Numerator;
            profile.Video.FrameRate.Denominator = Math.Max(1, video.FrameRate.Denominator);
        }
        profile.Video.Bitrate = Math.Max(1_500_000, video.Bitrate);
        var transcoder = new global::Windows.Media.Transcoding.MediaTranscoder
        {
            HardwareAccelerationEnabled = true,
            TrimStartTime = from < TimeSpan.Zero ? TimeSpan.Zero : from,
            // A position, not an amount cut from the end (measured; the documentation suggests otherwise).
            TrimStopTime = to >= duration ? TimeSpan.Zero : to,
        };
        // Plain streams, as the recorder uses: the StorageFile overload failed with E_POINTER here.
        using var input = new FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read);
        var written = false;
        try
        {
            using var outputStream = new FileStream(output, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
            var prepared = await transcoder.PrepareStreamTranscodeAsync(
                System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(input),
                System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(outputStream), profile);
            if (!prepared.CanTranscode) throw new InvalidOperationException($"The trimmed copy could not be made ({prepared.FailureReason}).");
            var run = prepared.TranscodeAsync();
            run.Progress = (_, p) => progress?.Report(p / 100.0);
            await run;
            written = true;
        }
        finally
        {
            if (!written && File.Exists(output)) File.Delete(output);
        }
    }

    /// <summary>
    /// An animated GIF of the part between <paramref name="from"/> and <paramref name="to"/>, at most
    /// <paramref name="maxWidth"/> wide and <paramref name="fps"/> frames a second, looping. Frames are
    /// read from the video and written by libvips (GIF has 256 colours, so it suits short clips of a screen).
    /// </summary>
    public static async Task GifAsync(string path, TimeSpan from, TimeSpan to, string output, int maxWidth = 800, int fps = 12, IProgress<double>? progress = null)
    {
        var composition = await Open(path);
        var clip = composition.Clips[0];
        var duration = clip.OriginalDuration;
        if (to > duration || to <= from) to = duration;
        var step = TimeSpan.FromSeconds(1.0 / fps);
        var times = new List<TimeSpan>();
        for (var t = from; t < to; t += step) times.Add(t);
        if (times.Count == 0) times.Add(from);
        var frames = new List<Image>();
        try
        {
            int width = 0, height = 0;
            for (int i = 0; i < times.Count; i++)
            {
                var thumb = await composition.GetThumbnailAsync(times[i], 0, 0, VideoFramePrecision.NearestFrame);
                using var stream = System.IO.WindowsRuntimeStreamExtensions.AsStream(thumb);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                var image = Image.NewFromBuffer(memory.ToArray());
                if (image.Width > maxWidth)
                {
                    var small = image.Resize((double)maxWidth / image.Width);
                    image.Dispose();
                    image = small;
                }
                if (image.Bands > 3)
                {
                    var rgb = image.ExtractBand(0, n: 3);
                    image.Dispose();
                    image = rgb;
                }
                if (i == 0) (width, height) = (image.Width, image.Height);
                else if (image.Width != width || image.Height != height)
                {
                    var fit = image.ThumbnailImage(width, height: height, size: Enums.Size.Force);
                    image.Dispose();
                    image = fit;
                }
                frames.Add(image);
                progress?.Report(0.8 * (i + 1) / times.Count);
            }
            using var strip = Image.Arrayjoin([.. frames], across: 1);
            int delay = (int)Math.Round(1000.0 / fps);
            using var animated = strip.Mutate(m =>
            {
                m.Set(GValue.GIntType, "page-height", height);
                m.Set(GValue.ArrayIntType, "delay", Enumerable.Repeat(delay, frames.Count).ToArray());
                m.Set(GValue.GIntType, "loop", 0);
            });
            animated.Gifsave(output, effort: 7, bitdepth: 8);
            progress?.Report(1);
        }
        finally
        {
            foreach (var f in frames) f.Dispose();
        }
    }
}
