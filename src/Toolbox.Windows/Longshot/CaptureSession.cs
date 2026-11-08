using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core.Diagnostics;
using Toolbox.Processing.Capture;

namespace Toolbox.Windows.Longshot;

internal enum SessionCommand { Extend, Auto, Follow, Undo, Finish, Quit }

internal enum StepOutcome { Added, End, Failed, Full }

internal abstract record SessionEvent;

internal sealed record SessionReady : SessionEvent;

/// <summary>The command now running, or null when the session is idle again.</summary>
internal sealed record SessionBusy(SessionCommand? Command) : SessionEvent;

internal sealed record SessionUpdate(int Width, int Height, int Parts, BitmapSource Preview, string? Message, bool CanUndo) : SessionEvent;

internal sealed record SessionFinished(int Width, int Height, byte[] Pixels) : SessionEvent;

internal sealed record SessionFailed(string Message) : SessionEvent;

/// <summary>How long to wait for scrolling to settle. Tests use shorter times with a simulated screen.</summary>
internal sealed record SessionTiming(TimeSpan SettleMin, TimeSpan Poll, TimeSpan SettleTimeout, TimeSpan FollowPoll)
{
    public static readonly SessionTiming Real = new(
        TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(45), TimeSpan.FromMilliseconds(1600), TimeSpan.FromMilliseconds(50));
}

/// <summary>
/// One scrolling capture: grabs, scrolls and stitches on a background thread, driven by commands
/// from the panel (Extend, Auto, Follow, Undo, Done). Events go back through <c>post</c>, on the
/// worker thread (the caller marshals them to the UI).
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    public const int MaxHeight = 40000;

    private readonly Func<IScreenSource> _openScreen;
    private readonly IScroller _scroller;
    private readonly Action<SessionEvent> _post;
    private readonly SessionTiming _timing;
    private readonly double _stepFraction;
    private readonly int _previewWidth, _previewHeight;
    private readonly BlockingCollection<SessionCommand> _commands = new();
    private readonly List<double> _pxPerUnit = [];
    private Thread? _thread;
    private volatile bool _stop;
    private bool _finished;
    private IScreenSource? _screen;
    private ScrollStitcher? _stitcher;
    private int _unit;
    private int _region;

    public CaptureSession(Func<IScreenSource> openScreen, IScroller scroller, int areaHeight, double stepFraction,
        int previewWidth, int previewHeight, Action<SessionEvent> post, SessionTiming? timing = null)
    {
        _openScreen = openScreen;
        _scroller = scroller;
        _post = post;
        _timing = timing ?? SessionTiming.Real;
        _stepFraction = stepFraction;
        _previewWidth = previewWidth;
        _previewHeight = previewHeight;
        _unit = areaHeight >= 300 ? 120 : 60; // wheel delta per step we send; halved if one step jumps too far
        _region = areaHeight;
    }

    /// <summary>Checked while Auto or Follow runs; Esc stops them even though the panel never has focus.</summary>
    public Func<bool> EscapePressed { get; init; } = CaptureWindows.EscapePressed;

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Longshot capture" };
        _thread.SetApartmentState(ApartmentState.STA); // it creates the (frozen) preview bitmaps
        _thread.Start();
    }

    public void Enqueue(SessionCommand command)
    {
        if (!_commands.IsAddingCompleted) _commands.Add(command);
    }

    /// <summary>Starts Auto or Follow (clears an earlier Stop first).</summary>
    public void StartMode(SessionCommand mode)
    {
        _stop = false;
        Enqueue(mode);
    }

    /// <summary>Stops Auto or Follow after the current step.</summary>
    public void Stop() => _stop = true;

    public void Dispose()
    {
        _stop = true;
        Enqueue(SessionCommand.Quit);
        _commands.CompleteAdding();
        if (_thread is not null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromMilliseconds(500)); // it ends on its own; events after this are ignored
    }

    // ---- worker ------------------------------------------------------------------------------

    private void Run()
    {
        try
        {
            _screen = _openScreen();
            var first = GrabSettled(TimeSpan.FromMilliseconds(50));
            _stitcher = new ScrollStitcher(first);
            _post(new SessionReady());
            Update("Press Extend to add what's below.");
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                if (command == SessionCommand.Quit) break;
                _post(new SessionBusy(command));
                try
                {
                    switch (command)
                    {
                        case SessionCommand.Extend: Update(ExtendOnce().Message); break;
                        case SessionCommand.Auto: RunAuto(); break;
                        case SessionCommand.Follow: RunFollow(); break;
                        case SessionCommand.Undo: RunUndo(); break;
                        case SessionCommand.Finish: Finish(); break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Longshot {command} failed", ex);
                    Update($"Something went wrong: {ex.Message}");
                }
                if (_finished) break;
                _post(new SessionBusy(null));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Longshot capture stopped", ex);
            _post(new SessionFailed(ex.Message));
        }
        finally
        {
            _screen?.Dispose();
        }
    }

    private ScrollStitcher St => _stitcher!;

    private Frame Grab() => _screen!.Grab();

    /// <summary>Waits for smooth scrolling and animations to finish, then grabs.</summary>
    private Frame GrabSettled(TimeSpan? minWait = null)
    {
        var clock = Stopwatch.StartNew();
        Thread.Sleep(minWait ?? _timing.SettleMin);
        var prev = Grab();
        int stable = 0;
        while (true)
        {
            Thread.Sleep(_timing.Poll);
            var cur = Grab();
            stable = Same(cur, prev) ? stable + 1 : 0;
            if (stable >= 2 || clock.Elapsed > _timing.SettleTimeout) return cur;
            prev = cur;
        }
    }

    /// <summary>Close enough to identical (a blinking text cursor doesn't count as a change).</summary>
    internal static bool Same(Frame a, Frame b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        var pa = a.Pixels;
        var pb = b.Pixels;
        long sum = 0, count = 0;
        for (int y = 0; y < a.Height; y += 2)
        {
            int row = y * a.Stride;
            for (int x = 0; x < a.Width; x += 2)
            {
                int i = row + x * 4;
                sum += Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
                count += 3;
            }
        }
        return count == 0 || sum / (double)count < 0.25;
    }

    private int Notches()
    {
        if (_pxPerUnit.Count == 0) return 1;
        double per = Median(_pxPerUnit);
        return Math.Clamp((int)(_stepFraction * _region / Math.Max(per, 1)), 1, 15);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    internal (StepOutcome Outcome, string Message) ExtendOnce()
    {
        if (St.Image.Height >= MaxHeight) return (StepOutcome.Full, $"Stopped at {MaxHeight:N0} px, the size limit.");
        int n = Notches();
        double? per = _pxPerUnit.Count > 0 ? Median(_pxPerUnit) : null;
        ShiftMatch? match = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            double? expected = per * n;
            _scroller.Scroll(n, down: true, _unit);
            var frame = GrabSettled();
            var (m, gray) = St.Match(frame, expected);
            match = m;
            Log.Info($"Longshot extend n={n} unit={_unit} -> moved={m.Moved} ok={m.Ok} d={m.Distance} score={m.Score:F2} worst={m.Worst:F1} top={m.Top} bottom={m.Bottom} est={m.Estimated} {m.Reason}");
            if (!m.Moved) return (StepOutcome.End, "Reached the bottom. Nothing moved. Press Done to save.");
            if (m.Ok)
            {
                St.Apply(frame, gray, m, new StepInfo(n, _unit));
                _region = m.Region;
                if (!m.Estimated && (expected is null || m.Distance >= 0.8 * expected))
                {
                    _pxPerUnit.Add(m.Distance / (double)n);
                    if (_pxPerUnit.Count > 8) _pxPerUnit.RemoveAt(0);
                }
                return (StepOutcome.Added, $"Added {m.Distance} px" + (m.Estimated ? " (blank area, estimated)." : "."));
            }
            // It moved, but too far to line up: go back and take a smaller step.
            _scroller.Scroll(n, down: false, _unit);
            GrabSettled();
            if (n > 1) n = Math.Max(1, n / 2);
            else if (_unit > 30)
            {
                _unit /= 2;
                _pxPerUnit.Clear();
                per = null;
            }
            else break;
        }
        return (StepOutcome.Failed, (match?.Reason is { Length: > 0 } r ? r : "Couldn't line it up") + ". Try a taller area.");
    }

    private void RunAuto()
    {
        string message;
        while (true)
        {
            if (_stop || EscapePressed()) { message = "Stopped."; break; }
            var (outcome, text) = ExtendOnce();
            if (outcome != StepOutcome.Added) { message = text; break; }
            Update($"Auto: {text}  (Esc to stop)");
        }
        Update(message);
    }

    private void RunFollow()
    {
        Update("Scroll the page yourself. Longshot adds as you go. Stop or Esc when done.");
        var prev = Grab();
        Frame? reported = null; // the still frame we last complained about
        while (!_stop && !EscapePressed())
        {
            Thread.Sleep(_timing.FollowPoll);
            var cur = Grab();
            bool settled = Same(cur, prev);
            prev = cur;
            if (Same(cur, St.Last)) continue;
            if (St.Image.Height >= MaxHeight)
            {
                Update($"Reached the {MaxHeight:N0} px size limit.");
                break;
            }
            var (m, gray) = St.Match(cur);
            // While the page is still moving, only near-perfect matches are taken: this keeps up with
            // fast scrolling without risking a bad seam.
            if (m.Moved && m.Ok && (settled || m.Score <= 0.5))
            {
                St.Apply(cur, gray, m);
                _region = m.Region;
                Update($"Added {m.Distance} px. Keep scrolling.");
                reported = null;
            }
            else if (settled && m.Moved && !(reported is not null && Same(cur, reported)))
            {
                reported = cur;
                var up = ShiftFinder.Find(gray, St.LastGray);
                Log.Info($"Longshot follow: no match ({m.Reason}); scrolled up={up.Ok}");
                Update(up.Moved && up.Ok
                    ? "You scrolled up. Scroll down again to continue."
                    : "Lost track. Scroll back up a little, then go slower.");
            }
        }
        Update("Stopped following.");
    }

    private void RunUndo()
    {
        var step = St.Undo();
        if (step is null)
        {
            Update("Nothing to undo.");
            return;
        }
        if (step.Info.Notches > 0)
        {
            _scroller.Scroll(step.Info.Notches, down: false, step.Info.WheelUnit);
            GrabSettled();
            Update("Removed the last part.");
        }
        else
        {
            Update("Removed the last part. Scroll back up a little to continue.");
        }
    }

    private void Finish()
    {
        _finished = true;
        _post(new SessionFinished(St.Image.Width, St.Image.Height, St.Image.ToArray()));
    }

    private void Update(string? message)
    {
        var image = St.Image;
        int rows = Math.Min(image.Height, (int)Math.Ceiling(_previewHeight * (double)image.Width / _previewWidth) + 1);
        var tail = image.Tail(rows);
        var preview = Downscale(tail, image.Width, tail.Length / (image.Width * 4), _previewWidth);
        _post(new SessionUpdate(image.Width, image.Height, St.Steps + 1, preview, message, St.CanUndo));
    }

    /// <summary>Area-averaged downscale of BGRA rows to a frozen bitmap <paramref name="width"/> px wide.</summary>
    internal static BitmapSource Downscale(byte[] pixels, int srcW, int srcH, int width)
    {
        width = Math.Max(1, Math.Min(width, srcW));
        int height = Math.Max(1, (int)Math.Round(srcH * (double)width / srcW));
        var dst = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int y0 = y * srcH / height, y1 = Math.Max(y0 + 1, (y + 1) * srcH / height);
            for (int x = 0; x < width; x++)
            {
                int x0 = x * srcW / width, x1 = Math.Max(x0 + 1, (x + 1) * srcW / width);
                int b = 0, g = 0, r = 0, n = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * srcW * 4;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = row + sx * 4;
                        b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; n++;
                    }
                }
                int o = (y * width + x) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = 255;
            }
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, dst, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
