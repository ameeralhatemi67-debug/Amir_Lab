using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Toolbox.Core.Diagnostics;
using Toolbox.Processing.Video;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace Toolbox.Windows.Longshot.Video;

/// <summary>What saving a blurred copy made.</summary>
internal sealed record BlurredVideo(string Path, int Frames, bool SoundLeftOut);

/// <summary>
/// Saves a copy of a video with its boxes hidden: every frame is read exactly (VideoFrameReader),
/// the boxes on at that moment are blurred, pixelated or painted over (FrameBlur), and the frame
/// goes to Windows' H.264 encoder, the same way Longshot records. The original is never changed;
/// a cancelled or failed save leaves no file behind.
/// </summary>
internal static class VideoBlurRenderer
{
    public static async Task<BlurredVideo> SaveAsync(string source, VideoBlurPlan plan, string output, IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        // The reader lives on a thread without a UI (Media Foundation prefers it), and is only used
        // from the encoder's requests, one at a time.
        var reader = await Task.Run(() => new VideoFrameReader(source), cancel);
        FileStream? file = null;
        bool written = false;
        try
        {
            int width = reader.Width & ~1, height = reader.Height & ~1; // the encoder needs even sizes
            if (width < 2 || height < 2) throw new InvalidOperationException("The video is too small.");
            var plans = plan.Clone();
            int frameBytes = width * height * 4;
            var raw = new byte[reader.Width * reader.Height * 4];
            var row = new byte[width * 4];
            TimeSpan? first = null;
            int frames = 0;
            var duration = reader.Duration > TimeSpan.Zero ? reader.Duration : TimeSpan.FromSeconds(1);

            var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
            var mss = new MediaStreamSource(new VideoStreamDescriptor(props)) { BufferTime = TimeSpan.Zero, CanSeek = false };
            mss.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
            mss.SampleRequested += async (_, args) =>
            {
                var request = args.Request;
                var deferral = request.GetDeferral();
                try
                {
                    var next = await Task.Run(() =>
                    {
                        if (cancel.IsCancellationRequested || !reader.Read(raw, out var at)) return ((byte[], TimeSpan)?)null;
                        var pixels = ArrayPool<byte>.Shared.Rent(frameBytes);
                        if (width == reader.Width && height == reader.Height) Buffer.BlockCopy(raw, 0, pixels, 0, frameBytes);
                        else for (int y = 0; y < height; y++) Buffer.BlockCopy(raw, y * reader.Width * 4, pixels, y * width * 4, width * 4);
                        foreach (var (box, mode) in plans.BoxesAt(at)) FrameBlur.Apply(pixels, width, height, box, mode);
                        ScreenRecorder.FlipRows(pixels, width * 4, height, row); // the encoder reads rows bottom-up (docs/LESSONS.md)
                        return (pixels, at);
                    });
                    if (next is not { } frame) { request.Sample = null; return; }
                    first ??= frame.Item2;
                    var sample = MediaStreamSample.CreateFromBuffer(frame.Item1.AsBuffer(0, frameBytes), frame.Item2 - first.Value);
                    sample.Duration = reader.FrameTime;
                    var pooled = frame.Item1;
                    sample.Processed += (_, _) => ArrayPool<byte>.Shared.Return(pooled);
                    request.Sample = sample;
                    frames++;
                    progress?.Report(Math.Clamp(frame.Item2.TotalSeconds / duration.TotalSeconds, 0, 1));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Video blur: a frame could not be read: {ex.Message}");
                    request.Sample = null;
                }
                finally
                {
                    deferral.Complete();
                }
            };

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Audio = null;
            profile.Video.Width = (uint)width;
            profile.Video.Height = (uint)height;
            profile.Video.FrameRate.Numerator = (uint)Math.Round(reader.FrameRate * 1000);
            profile.Video.FrameRate.Denominator = 1000;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;
            // A little above the recorder's rate: this is a second generation of the picture.
            profile.Video.Bitrate = (uint)Math.Clamp(width * (long)height * reader.FrameRate * 0.15, 2_000_000, 30_000_000);

            file = new FileStream(output, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None);
            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(mss, System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(file), profile);
            if (!prepared.CanTranscode) throw new InvalidOperationException($"Windows can't encode this video ({prepared.FailureReason}).");
            await prepared.TranscodeAsync().AsTask(cancel);
            cancel.ThrowIfCancellationRequested();
            if (frames == 0) throw new InvalidOperationException("No frames could be read from the video.");
            written = true;
            progress?.Report(1);
            return new BlurredVideo(output, frames, reader.HasSound);
        }
        finally
        {
            file?.Dispose();
            reader.Dispose();
            if (!written)
                try { if (File.Exists(output)) File.Delete(output); } catch (IOException) { }
        }
    }
}
