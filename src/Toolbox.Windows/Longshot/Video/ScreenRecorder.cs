using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using Toolbox.Core.Diagnostics;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;

namespace Toolbox.Windows.Longshot.Video;

/// <summary>How a recording is made.</summary>
internal sealed record RecordingOptions(int Fps = 30, bool Cursor = true, bool Clicks = true);

/// <summary>What a finished recording is: the file, its size in pixels and how long it is.</summary>
internal sealed record RecordingResult(string Path, int Width, int Height, TimeSpan Duration, int Frames, int Dropped);

/// <summary>
/// Records a screen rectangle to an MP4 (H.264) with Windows' own media pipeline: frames are read
/// from the screen at a steady rate on a thread of their own and handed, as raw pictures, to a
/// MediaStreamSource that Windows' transcoder encodes (with the graphics card's encoder when there
/// is one). No FFmpeg and no extra library. Pause leaves a gap out of the timeline; if the encoder
/// falls behind, frames are dropped rather than the recording drifting out of time.
/// </summary>
internal sealed partial class ScreenRecorder : IDisposable
{
    private readonly IFrameGrabber _grabber;
    private readonly RecordingOptions _options;
    private readonly string _path;
    private readonly Channel<(byte[] Pixels, TimeSpan Time)> _frames = Channel.CreateBounded<(byte[], TimeSpan)>(new BoundedChannelOptions(6) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true }, dropped => ArrayPool<byte>.Shared.Return(dropped.Item1));
    private readonly Stopwatch _clock = new();
    private readonly TimeSpan _frameTime;
    private readonly int _frameBytes;
    private Thread? _thread;
    private Task? _encoding;
    private FileStream? _file;
    private volatile bool _stopping;
    private volatile bool _paused;
    private TimeSpan _pausedFor;
    private TimeSpan _pausedClock;
    private int _frameCount;
    private int _dropped;
    private TimeSpan _last;
    private byte[] _row = [];

    public ScreenRecorder(IFrameGrabber grabber, string path, RecordingOptions options)
    {
        if (grabber.Width < 2 || grabber.Height < 2) throw new ArgumentException("The area is too small to record.");
        _grabber = grabber;
        _options = options;
        _path = path;
        _frameTime = TimeSpan.FromSeconds(1.0 / Math.Clamp(options.Fps, 5, 60));
        _frameBytes = grabber.Width * grabber.Height * 4;
        _row = new byte[grabber.Width * 4];
    }

    public string Path => _path;

    /// <summary>Recorded time so far (pauses not counted).</summary>
    public TimeSpan Elapsed => (_paused ? _pausedClock : _clock.Elapsed) - _pausedFor;

    public bool Paused => _paused;

    /// <summary>The encoder stopped by itself (a full disk, a codec problem): the message, for the UI.</summary>
    public event Action<string>? Failed;

    /// <summary>Opens the file and the encoder, then starts reading frames.</summary>
    public async Task StartAsync()
    {
        var props = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_grabber.Width, (uint)_grabber.Height);
        var source = new MediaStreamSource(new VideoStreamDescriptor(props)) { BufferTime = TimeSpan.Zero, CanSeek = false };
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)_grabber.Width;
        profile.Video.Height = (uint)_grabber.Height;
        profile.Video.FrameRate.Numerator = (uint)_options.Fps;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        // About 0.1 bit per pixel per frame: sharp text without huge files (1080p at 30 fps ≈ 6 Mbit/s).
        profile.Video.Bitrate = (uint)Math.Clamp(_grabber.Width * (long)_grabber.Height * _options.Fps / 10, 1_500_000, 24_000_000);

        _file = new FileStream(_path, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(_file), profile);
        if (!prepared.CanTranscode)
        {
            _file.Dispose();
            File.Delete(_path);
            throw new InvalidOperationException($"Windows can't encode this video ({prepared.FailureReason}).");
        }
        _clock.Start();
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Longshot video", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _encoding = prepared.TranscodeAsync().AsTask().ContinueWith(t =>
        {
            if (t.IsFaulted && !_stopping)
            {
                Log.Warn($"Longshot video: the encoder stopped: {t.Exception?.InnerException?.Message}");
                Failed?.Invoke(t.Exception?.InnerException?.Message ?? "The encoder stopped.");
            }
            if (t.IsFaulted) throw t.Exception!.InnerException!;
        });
    }

    public void Pause()
    {
        if (_paused) return;
        _pausedClock = _clock.Elapsed;
        _paused = true;
    }

    public void Resume()
    {
        if (!_paused) return;
        _pausedFor += _clock.Elapsed - _pausedClock;
        _paused = false;
    }

    /// <summary>Stops reading frames, lets the encoder finish the file, and says what it is.</summary>
    public async Task<RecordingResult> StopAsync()
    {
        _stopping = true;
        _thread?.Join(2000);
        _frames.Writer.TryComplete();
        if (_encoding is not null) await _encoding.ConfigureAwait(false);
        _file?.Dispose();
        _file = null;
        return new RecordingResult(_path, _grabber.Width, _grabber.Height, _last + _frameTime, _frameCount, _dropped);
    }

    /// <summary>Stops and deletes what was recorded.</summary>
    public async Task DiscardAsync()
    {
        try { await StopAsync(); }
        catch (Exception ex) { Log.Warn($"Longshot video: discarding: {ex.Message}"); }
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { }
    }

    /// <summary>
    /// Turns a frame upside down in place. Windows' raw RGB input to the encoder is bottom-up (as
    /// Media Foundation's RGB32 always is), while screen frames are top-down; without this every
    /// recording came out upside down (caught by VideoRecorderTests).
    /// </summary>
    internal static void FlipRows(byte[] px, int stride, int height, byte[] row)
    {
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(px, top * stride, row, 0, stride);
            Buffer.BlockCopy(px, bottom * stride, px, top * stride, stride);
            Buffer.BlockCopy(row, 0, px, bottom * stride, stride);
        }
    }

    [LibraryImport("winmm.dll")] private static partial uint timeBeginPeriod(uint period);
    [LibraryImport("winmm.dll")] private static partial uint timeEndPeriod(uint period);

    /// <summary>Reads a frame every 1/fps of a second (pausing reads none) and queues it for the encoder.</summary>
    private void CaptureLoop()
    {
        timeBeginPeriod(1); // 1 ms sleeps, so 60 fps keeps time
        try
        {
            var next = TimeSpan.Zero;
            while (!_stopping)
            {
                if (_paused) { Thread.Sleep(15); continue; }
                var now = Elapsed;
                if (now < next)
                {
                    var wait = next - now;
                    if (wait > TimeSpan.FromMilliseconds(2)) Thread.Sleep(wait - TimeSpan.FromMilliseconds(1));
                    continue;
                }
                var pixels = ArrayPool<byte>.Shared.Rent(_frameBytes);
                try
                {
                    _grabber.Grab(pixels);
                    FlipRows(pixels, _grabber.Width * 4, _grabber.Height, _row);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    next += _frameTime;
                    continue;
                }
                // The time this frame belongs to, on the frame grid (no drift, no duplicates).
                var at = TimeSpan.FromTicks(next.Ticks);
                if (!_frames.Writer.TryWrite((pixels, at))) { ArrayPool<byte>.Shared.Return(pixels); _dropped++; }
                next += _frameTime;
                // Fell behind (a slow read): skip ahead instead of catching up with a burst.
                if (Elapsed - next > _frameTime * 2) { _dropped += (int)((Elapsed - next).Ticks / _frameTime.Ticks); next = Elapsed; }
            }
        }
        finally
        {
            timeEndPeriod(1);
        }
    }

    private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;
        var deferral = request.GetDeferral();
        try
        {
            while (await _frames.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                if (!_frames.Reader.TryRead(out var frame)) continue;
                if (_frameCount > 0 && frame.Time <= _last) { ArrayPool<byte>.Shared.Return(frame.Pixels); continue; }
                var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels.AsBuffer(0, _frameBytes), frame.Time);
                sample.Duration = _frameTime;
                var pixels = frame.Pixels;
                sample.Processed += (_, _) => ArrayPool<byte>.Shared.Return(pixels);
                _last = frame.Time;
                _frameCount++;
                request.Sample = sample;
                return;
            }
            request.Sample = null; // the end: no more frames
        }
        finally
        {
            deferral.Complete();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _frames.Writer.TryComplete();
        _file?.Dispose();
        _grabber.Dispose();
    }
}
