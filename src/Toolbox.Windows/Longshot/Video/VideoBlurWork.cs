using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetVips;
using Toolbox.Core;
using Toolbox.Processing.Ocr;
using Toolbox.Processing.Studio;
using Toolbox.Processing.Video;

namespace Toolbox.Windows.Longshot.Video;

using Toolbox.Windows.Theme;
using Log = Toolbox.Core.Diagnostics.Log;
using PixelRect = Toolbox.Processing.Studio.PixelRect;

/// <summary>
/// Plays a video for the blur editor: exact frames from <see cref="VideoFrameReader"/> on a thread
/// of its own, in real time while playing (late frames are skipped, never shown in a rush), or
/// one frame on request (seek, step). Frames arrive through <see cref="Frame"/> on that thread;
/// the buffer is reused as soon as the handler returns.
/// </summary>
internal sealed class FramePump : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _opened = new(false);
    private Exception? _openError;
    private VideoFrameReader? _reader;
    private TimeSpan? _seek;
    private bool _play, _step, _stop;

    public FramePump(string path)
    {
        _thread = new Thread(() => Run(path)) { IsBackground = true, Name = "Video blur player" };
        _thread.Start();
        _opened.Wait(TimeSpan.FromSeconds(20));
        if (_openError is not null) throw new InvalidOperationException(_openError.Message, _openError);
        if (_reader is null) throw new TimeoutException("The video took too long to open.");
    }

    public int Width => _reader!.Width;
    public int Height => _reader!.Height;
    public TimeSpan Duration => _reader!.Duration;
    public TimeSpan FrameTime => _reader!.FrameTime;
    public bool HasSound => _reader!.HasSound;

    /// <summary>A frame (BGRx, Width × Height) and its time, on the player's thread.</summary>
    public event Action<byte[], TimeSpan>? Frame;

    /// <summary>Playing reached the end.</summary>
    public event Action? Ended;

    public bool Playing { get { lock (_gate) return _play; } }

    public void SeekTo(TimeSpan at) { lock (_gate) _seek = at < TimeSpan.Zero ? TimeSpan.Zero : at; _wake.Set(); }

    public void Play() { lock (_gate) _play = true; _wake.Set(); }

    public void Pause() { lock (_gate) _play = false; _wake.Set(); }

    /// <summary>The next frame (while paused).</summary>
    public void Step() { lock (_gate) _step = true; _wake.Set(); }

    private void Run(string path)
    {
        try { _reader = new VideoFrameReader(path); }
        catch (Exception ex) { _openError = ex; }
        finally { _opened.Set(); }
        if (_reader is null) return;
        var buffer = new byte[_reader.Width * _reader.Height * 4];
        var clock = new Stopwatch();
        TimeSpan startedAt = TimeSpan.Zero;
        bool wasPlaying = false;
        try
        {
            while (true)
            {
                TimeSpan? seek;
                bool play, step;
                lock (_gate)
                {
                    if (_stop) return;
                    seek = _seek;
                    _seek = null;
                    play = _play;
                    step = _step;
                    _step = false;
                }
                if (seek is { } at)
                {
                    if (_reader.SeekRead(at, buffer, out var t)) { Frame?.Invoke(buffer, t); startedAt = t; clock.Restart(); }
                    continue;
                }
                if (step && !play)
                {
                    if (_reader.Read(buffer, out var t)) { Frame?.Invoke(buffer, t); startedAt = t; }
                    continue;
                }
                if (play)
                {
                    if (!wasPlaying) { clock.Restart(); wasPlaying = true; }
                    if (!_reader.Read(buffer, out var t))
                    {
                        lock (_gate) _play = false;
                        wasPlaying = false;
                        Ended?.Invoke();
                        continue;
                    }
                    var due = t - startedAt;
                    // Wait for the frame's moment, but wake for a seek or a pause.
                    while (clock.Elapsed < due - TimeSpan.FromMilliseconds(2))
                    {
                        var wait = due - clock.Elapsed;
                        if (_wake.WaitOne(wait > TimeSpan.FromMilliseconds(30) ? TimeSpan.FromMilliseconds(30) : wait)) break;
                    }
                    lock (_gate) if (_seek is not null || !_play) { wasPlaying = _play; continue; }
                    // More than three frames late: skip showing it, so the picture keeps time.
                    if (clock.Elapsed - due < _reader.FrameTime * 3) Frame?.Invoke(buffer, t);
                    continue;
                }
                wasPlaying = false;
                _wake.WaitOne();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Video blur: the player stopped: {ex.Message}");
        }
        finally
        {
            _reader.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate) _stop = true;
        _wake.Set();
        _thread.Join(2000);
    }
}

/// <summary>How following a box ended.</summary>
internal enum FollowEnd { EndOfVideo, Gone, Lost, Plain, Cancelled }

internal sealed record FollowOutcome(List<BlurKey> Keys, FollowEnd End, TimeSpan EndedAt);

/// <summary>Following boxes, finding personal information, and the frame strip: the editor's slower work, off the UI thread.</summary>
internal static class VideoBlurWork
{
    /// <summary>
    /// Follows boxes from <paramref name="from"/> to the end in one pass over the video. Every few
    /// frames <paramref name="progress"/> gets a copy of the frame and the keys so far (on a worker thread).
    /// </summary>
    public static Task<Dictionary<int, FollowOutcome>> FollowAsync(string path, IReadOnlyList<(int Id, PixelRect Box)> boxes, TimeSpan from,
        Action<byte[], TimeSpan, Dictionary<int, List<BlurKey>>>? progress, CancellationToken cancel) => Task.Run(() =>
    {
        using var reader = new VideoFrameReader(path);
        int w = reader.Width, h = reader.Height;
        var frame = new byte[w * h * 4];
        var luma = new byte[w * h];
        var outcomes = new Dictionary<int, FollowOutcome>();
        if (!reader.SeekRead(from, frame, out var start)) return outcomes;
        BoxFollower.Luma(frame, w, h, luma);
        var followers = new Dictionary<int, BoxFollower>();
        var keys = new Dictionary<int, List<BlurKey>>();
        foreach (var (id, box) in boxes)
        {
            var follower = new BoxFollower(luma, w, h, box);
            keys[id] = [new BlurKey(start, box)];
            if (follower.IsPlain) outcomes[id] = new FollowOutcome(keys[id], FollowEnd.Plain, start);
            else followers[id] = follower;
        }
        int n = 0;
        var last = start;
        while (followers.Count > 0)
        {
            if (cancel.IsCancellationRequested)
            {
                foreach (var id in followers.Keys) outcomes[id] = new FollowOutcome(keys[id], FollowEnd.Cancelled, last);
                return outcomes;
            }
            if (!reader.Read(frame, out var at)) break;
            last = at;
            BoxFollower.Luma(frame, w, h, luma);
            foreach (var (id, follower) in followers.ToList())
            {
                var step = follower.Next(luma);
                switch (step.State)
                {
                    case FollowState.Gone:
                        outcomes[id] = new FollowOutcome(keys[id], FollowEnd.Gone, at);
                        followers.Remove(id);
                        break;
                    case FollowState.Lost:
                        outcomes[id] = new FollowOutcome(keys[id], FollowEnd.Lost, at);
                        followers.Remove(id);
                        break;
                    default:
                        keys[id].Add(new BlurKey(at, step.Box));
                        break;
                }
            }
            if (progress is not null && ++n % 5 == 0)
                progress((byte[])frame.Clone(), at, keys.ToDictionary(k => k.Key, k => new List<BlurKey>(k.Value)));
        }
        foreach (var id in followers.Keys) outcomes[id] = new FollowOutcome(keys[id], FollowEnd.EndOfVideo, last);
        return outcomes;
    }, cancel);

    public static string KindText(SensitiveKind kind) => kind switch
    {
        SensitiveKind.Email => Lang.T("E-mail", "بريد إلكتروني"),
        SensitiveKind.Phone => Lang.T("Phone", "هاتف"),
        SensitiveKind.Card => Lang.T("Card number", "رقم بطاقة"),
        SensitiveKind.Iban => "IBAN",
        SensitiveKind.IpAddress => Lang.T("IP address", "عنوان IP"),
        _ => Lang.T("Key or password", "مفتاح أو كلمة مرور"),
    };

    /// <summary>For tests: recognises a frame instead of Windows' OCR.</summary>
    internal static Func<byte[], int, int, OcrPage>? OcrOverride { get; set; }

    /// <summary>
    /// Finds personal information in the whole video. The text is read about once a second; each
    /// new find gets a box that is followed forward to where it leaves (or changes), and back
    /// through the second before it was seen, so it is hidden from its first frame. Finds already
    /// under a box are skipped.
    /// </summary>
    public static Task<List<BlurTrack>> FindPersonalInfoAsync(string path, VideoBlurPlan existing, RedactionMode mode, Action<TimeSpan, TimeSpan, int>? progress, CancellationToken cancel) => Task.Run(() =>
    {
        using var reader = new VideoFrameReader(path);
        int w = reader.Width, h = reader.Height;
        var duration = reader.Duration;
        var frame = new byte[w * h * 4];
        var found = new List<BlurTrack>();
        var active = new Dictionary<BlurTrack, BoxFollower>();
        var recent = new List<(TimeSpan At, byte[] Luma)>(); // the frames since the text was last read
        var interval = TimeSpan.FromSeconds(1);
        TimeSpan? lastRead = null;
        int id = existing.NextId();
        while (!cancel.IsCancellationRequested && reader.Read(frame, out var at))
        {
            var luma = BoxFollower.Luma(frame, w, h);
            foreach (var (track, follower) in active.ToList())
            {
                var step = follower.Next(luma);
                if (step.State is FollowState.Gone)
                {
                    track.End = at;
                    active.Remove(track);
                }
                else if (step.State is FollowState.Lost)
                {
                    // Changed in place (more typing, a new page): keep it hidden until the text is
                    // read again, which finds it anew if it is still there.
                    track.End = at + interval + reader.FrameTime;
                    active.Remove(track);
                }
                else track.SetKey(at, step.Box, TimeSpan.Zero);
            }

            if (lastRead is null || at - lastRead.Value >= interval)
            {
                foreach (var hit in Read(frame, w, h))
                {
                    var box = Inflate(hit, w, h);
                    if (Covered(box, at, existing, found)) continue;
                    var track = new BlurTrack { Id = id++, Mode = mode, Start = at, End = duration, Label = $"{KindText(hit.Kind)} {hit.Masked}" };
                    track.SetKey(at, box, TimeSpan.Zero);
                    var forward = new BoxFollower(luma, w, h, box);
                    // Back through the frames since the last reading: where was it then?
                    var back = new BoxFollower(luma, w, h, box);
                    for (int i = recent.Count - 1; i >= 0 && !back.IsPlain; i--)
                    {
                        var step = back.Next(recent[i].Luma);
                        if (step.State is FollowState.Gone) break;
                        // Lost going back: it was being typed or drawn there; keep the box there.
                        track.Start = recent[i].At;
                        if (step.State is FollowState.Lost) { track.SetKey(recent[0].At, track.Keys[0].Box, TimeSpan.Zero); track.Start = recent[0].At; break; }
                        track.SetKey(recent[i].At, step.Box, TimeSpan.Zero);
                    }
                    found.Add(track);
                    if (!forward.IsPlain) active[track] = forward;
                }
                lastRead = at;
                recent.Clear();
                progress?.Invoke(at, duration, found.Count);
            }
            recent.Add((at, luma));
        }
        foreach (var track in found)
        {
            var thin = BlurTrack.Simplify(track.Keys, 1.0);
            track.ClearKeysAfter(TimeSpan.MinValue);
            foreach (var k in thin) track.SetKey(k.At, k.Box, TimeSpan.Zero);
        }
        return found;
    }, cancel);

    private static IReadOnlyList<SensitiveHit> Read(byte[] frame, int w, int h)
    {
        try
        {
            OcrPage page;
            if (OcrOverride is { } fake) page = fake(frame, w, h);
            else
            {
                using var image = Image.NewFromMemory(frame, w, h, 4, Enums.BandFormat.Uchar);
                using var b = image.ExtractBand(0);
                using var g = image.ExtractBand(1);
                using var r = image.ExtractBand(2);
                using var rgb = r.Bandjoin(g, b);
                page = OcrService.Recognize(rgb, "ara+eng");
            }
            return SensitiveText.Find(page);
        }
        catch (Exception ex) when (ex is ProcessingException or VipsException or InvalidOperationException)
        {
            Log.Warn($"Video blur: reading a frame's text failed: {ex.Message}");
            return [];
        }
    }

    /// <summary>A little larger than the text (OCR boxes hug the letters), inside the picture.</summary>
    private static PixelRect Inflate(SensitiveHit hit, int w, int h)
    {
        int pad = Math.Max(3, (int)Math.Round(hit.Height * 0.2));
        int x = (int)Math.Floor(hit.X) - pad, y = (int)Math.Floor(hit.Y) - pad;
        int r = (int)Math.Ceiling(hit.X + hit.Width) + pad, b = (int)Math.Ceiling(hit.Y + hit.Height) + pad;
        x = Math.Max(0, x); y = Math.Max(0, y); r = Math.Min(w, r); b = Math.Min(h, b);
        return new PixelRect(x, y, Math.Max(1, r - x), Math.Max(1, b - y));
    }

    /// <summary>Is most of <paramref name="box"/> already under a box that is on at <paramref name="at"/>?</summary>
    private static bool Covered(PixelRect box, TimeSpan at, VideoBlurPlan existing, List<BlurTrack> found)
    {
        foreach (var other in existing.Tracks.Concat(found))
            if (other.BoxAt(at) is { } o)
            {
                int iw = Math.Min(box.X + box.Width, o.X + o.Width) - Math.Max(box.X, o.X);
                int ih = Math.Min(box.Y + box.Height, o.Y + o.Height) - Math.Max(box.Y, o.Y);
                if (iw > 0 && ih > 0 && iw * (long)ih >= box.Width * (long)box.Height / 2) return true;
            }
        return false;
    }

    /// <summary><paramref name="count"/> frames spread over the video, <paramref name="height"/> px tall, for the timeline.</summary>
    public static Task<BitmapSource[]> StripAsync(string path, int count, int height, CancellationToken cancel) => Task.Run(() =>
    {
        using var reader = new VideoFrameReader(path);
        int w = reader.Width, h = reader.Height;
        int tw = Math.Max(4, height * w / Math.Max(1, h));
        var frame = new byte[w * h * 4];
        var result = new List<BitmapSource>();
        for (int i = 0; i < count && !cancel.IsCancellationRequested; i++)
        {
            var at = TimeSpan.FromTicks((long)(reader.Duration.Ticks * (i + 0.5) / count));
            if (!reader.SeekRead(at, frame, out _)) break;
            var small = new byte[tw * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < tw; x++)
                {
                    int sx = x * w / tw, sy = y * h / height;
                    Buffer.BlockCopy(frame, (sy * w + sx) * 4, small, (y * tw + x) * 4, 4);
                }
            var bitmap = BitmapSource.Create(tw, height, 96, 96, PixelFormats.Bgr32, null, small, tw * 4);
            bitmap.Freeze();
            result.Add(bitmap);
        }
        return result.ToArray();
    }, cancel);
}
