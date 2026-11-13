using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core.Diagnostics;
using Toolbox.Processing.Capture;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot;

internal enum SessionCommand { Extend, Auto, Follow, Undo, Finish, Grow, Quit }

internal enum StepOutcome { Added, End, Failed, Full }

internal abstract record SessionEvent;

internal sealed record SessionReady : SessionEvent;

/// <summary>The command now running, or null when the session is idle again.</summary>
internal sealed record SessionBusy(SessionCommand? Command) : SessionEvent;

/// <summary>
/// The capture so far: <see cref="Preview"/> is the whole long image shrunk to the preview width;
/// <see cref="AddedRows"/> how many rows the last step added at the bottom (0 after Undo);
/// <see cref="AtEnd"/> that the content stopped moving (the next thing to do is Done).
/// </summary>
internal sealed record SessionUpdate(int Width, int Height, int Parts, BitmapSource Preview, string? Message, bool CanUndo, int AddedRows = 0, bool AtEnd = false) : SessionEvent;

internal sealed record SessionFinished(int Width, int Height, byte[] Pixels) : SessionEvent;

internal sealed record SessionFailed(string Message) : SessionEvent;

/// <summary>The area grew, but the page had moved and the new part couldn't be lined up: start again from the new area.</summary>
internal sealed record SessionRestartNeeded(string Message) : SessionEvent;

/// <summary>Only part of the area scrolls: from now on the capture covers just <see cref="Area"/>.</summary>
internal sealed record SessionAreaChanged(PixelRect Area) : SessionEvent;

/// <summary>The wheel now turns at <see cref="At"/> (something scrolled there after nothing did at the old spot).</summary>
internal sealed record SessionAnchorMoved(PixelPoint At) : SessionEvent;

/// <summary>The wheel is about to turn at <see cref="At"/> (the frame shows it).</summary>
internal sealed record SessionWheel(PixelPoint At, bool Down) : SessionEvent;

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
/// <para>
/// The wheel turns at the scroll point (<see cref="Anchor"/>), so what scrolls is whatever is under
/// it, not necessarily the whole area. When only part of the area moves, the capture narrows to
/// that part before the second part goes in (<see cref="SessionAreaChanged"/>). When nothing moves
/// at the scroll point on the very first step, a few other spots in the area are tried.
/// </para>
/// </summary>
internal sealed class CaptureSession : IDisposable
{
    public const int MaxHeight = 40000;

    private readonly Func<PixelRect, IScreenSource> _openScreen;
    private readonly IScroller _scroller;
    private readonly Action<SessionEvent> _post;
    private readonly SessionTiming _timing;
    private readonly double _stepFraction;
    private readonly int _previewWidth;
    private int _shownHeight;
    private readonly BlockingCollection<SessionCommand> _commands = new();
    private readonly List<double> _pxPerDelta = []; // pixels the page moves per 120 of wheel delta (one notch)
    private double _estimate = double.NaN;          // their median, readable from the UI thread
    private long _scrolledDelta;                     // net wheel delta sent down since the start (for Refresh)
    private volatile bool _followUsed;
    private PixelRect? _grow;
    private Thread? _thread;
    private volatile bool _stop;
    private bool _finished;
    private IScreenSource? _screen;
    private ScrollStitcher? _stitcher;
    private int _unit;
    private int _region;
    private PixelRect _area;
    private readonly object _anchorLock = new();
    private PixelPoint _anchor;
    private bool _anchorSet;

    public CaptureSession(PixelRect area, Func<PixelRect, IScreenSource> openScreen, IScroller scroller, double stepFraction,
        int previewWidth, Action<SessionEvent> post, SessionTiming? timing = null)
    {
        _area = area;
        _openScreen = openScreen;
        _scroller = scroller;
        _post = post;
        _timing = timing ?? SessionTiming.Real;
        _stepFraction = stepFraction;
        _previewWidth = previewWidth; // the preview shows the whole image at this width
        // Wheel delta per step: until it is known how far one notch moves, a short area starts with half
        // a notch, so the first step can't jump past what the area shows. Halved if a step jumps too far.
        _unit = area.Height >= 900 ? 120 : 60;
        _region = area.Height;
    }

    /// <summary>How far the page moves per notch, learnt earlier in this capture (carried over after Refresh or a new area).</summary>
    public double? KnownPxPerDelta { get; init; }

    /// <summary>Wheel delta to scroll back up before the first part (Refresh: back to where the capture started).</summary>
    public long RewindDelta { get; init; }

    /// <summary>Scroll back up to the very top before the first part (after Follow, when the user did the scrolling).</summary>
    public bool RewindToTop { get; init; }

    /// <summary>
    /// The area was chosen to fit what scrolls (a picked scroll area, a region drawn by hand): if only
    /// part of it moves, still rows at its top and bottom stay in as its own header and footer. For a
    /// whole window the capture shrinks to the rows that scroll.
    /// </summary>
    public bool KeepRows { get; init; }

    /// <summary>Don't look for a part that scrolls (the whole area must line up, as before).</summary>
    public bool WholeAreaOnly { get; init; }

    /// <summary>Where the wheel turns (screen pixels). Starts in the middle of the area unless set.</summary>
    public PixelPoint Anchor
    {
        get { lock (_anchorLock) return _anchorSet ? _anchor : _area.Center; }
        init { lock (_anchorLock) { _anchor = value; _anchorSet = true; } }
    }

    /// <summary>Moves the scroll point (the user dragged the marker). Takes effect from the next scroll.</summary>
    public void SetAnchor(PixelPoint at)
    {
        lock (_anchorLock) { _anchor = at; _anchorSet = true; }
    }

    /// <summary>Net wheel delta this session has scrolled down so far.</summary>
    public long ScrolledDelta => Interlocked.Read(ref _scrolledDelta);

    /// <summary>True once Follow ran (the user scrolled, so the distance isn't known).</summary>
    public bool FollowUsed => _followUsed;

    /// <summary>Pixels per notch, once known.</summary>
    public double? PxPerDelta => Volatile.Read(ref _estimate) is var e && double.IsNaN(e) ? null : e;

    /// <summary>The frame's bottom edge moved down: take in the rows below without starting again.</summary>
    public void GrowDown(PixelRect area)
    {
        _grow = area;
        Enqueue(SessionCommand.Grow);
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
            _screen = _openScreen(_area);
            if (KnownPxPerDelta is { } known && known > 0)
            {
                _pxPerDelta.Add(known);
                Volatile.Write(ref _estimate, known);
                _unit = 120;
            }
            bool rewound = Rewind();
            var first = GrabSettled(TimeSpan.FromMilliseconds(50));
            _stitcher = new ScrollStitcher(first);
            _post(new SessionReady());
            Update(rewound
                ? Lang.T("Back at the start. Press Auto to scroll to the end, or Extend for one step.", "عدنا إلى البداية. اضغط «تلقائي» للتمرير حتى النهاية، أو «مدّ» لخطوة واحدة.")
                : Lang.T("Ready. Press Auto to scroll to the end, or Extend for one step.", "جاهز. اضغط «تلقائي» للتمرير حتى النهاية، أو «مدّ» لخطوة واحدة."));
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                if (command == SessionCommand.Quit) break;
                _post(new SessionBusy(command));
                try
                {
                    switch (command)
                    {
                        case SessionCommand.Extend:
                            var (outcome, text) = ExtendOnce();
                            Update(text, atEnd: outcome is StepOutcome.End or StepOutcome.Full && St.Steps > 0);
                            break;
                        case SessionCommand.Auto: RunAuto(); break;
                        case SessionCommand.Follow: RunFollow(); break;
                        case SessionCommand.Undo: RunUndo(); break;
                        case SessionCommand.Finish: Finish(); break;
                        case SessionCommand.Grow: RunGrow(); break;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"Longshot {command} failed", ex);
                    Update(Lang.T("Something went wrong: ", "حدث خطأ: ") + ex.Message);
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

    /// <summary>Pixels one step of the current wheel delta moves, once known (it holds when the delta is halved).</summary>
    private double? PerNotch => _pxPerDelta.Count == 0 ? null : Median(_pxPerDelta) * _unit / 120.0;

    private int Notches() => PerNotch is { } per ? Math.Clamp((int)(_stepFraction * _region / Math.Max(per, 1)), 1, 15) : 1;

    private void Scroll(int notches, bool down, int unit)
    {
        var at = Anchor;
        _post(new SessionWheel(at, down));
        _scroller.Scroll(at, notches, down, unit);
        Interlocked.Add(ref _scrolledDelta, (down ? 1L : -1L) * notches * unit);
    }

    /// <summary>
    /// Refresh: scrolls back to where the capture started (or to the top after Follow), so the new
    /// first part is the start of the page again. Returns whether it scrolled.
    /// </summary>
    private bool Rewind()
    {
        var at = Anchor;
        if (RewindToTop)
        {
            var prev = GrabSettled();
            for (int i = 0; i < 80; i++)
            {
                _scroller.Scroll(at, 10, down: false, 120);
                var cur = GrabSettled();
                if (Same(cur, prev)) break;
                prev = cur;
            }
            return true;
        }
        long left = RewindDelta;
        if (left <= 0) return false;
        while (left > 0)
        {
            int notches = (int)Math.Min(15, left / 120);
            if (notches == 0)
            {
                _scroller.Scroll(at, 1, down: false, (int)left);
                break;
            }
            _scroller.Scroll(at, notches, down: false, 120);
            left -= notches * 120L;
        }
        return true;
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Matches a new frame: against the whole area first; while the capture is still one part, also
    /// against the part of it that scrolls, if only part does. Returns the match to use (with
    /// <see cref="ShiftMatch.Part"/> set when it is a part) and why no part was found, for the log.
    /// </summary>
    private (ShiftMatch Match, GrayFrame Gray, string PartNote) MatchFrame(Frame frame, double? expected)
    {
        var (m, gray) = St.Match(frame, expected);
        bool whole = m.Moved && m.Ok && (m.Worst <= ShiftFinder.WorstTolerance || St.Steps > 0);
        if (whole || St.Steps > 0 || WholeAreaOnly) return (m, gray, "");
        var part = ShiftFinder.FindPart(St.LastGray, gray, expected, KeepRows, out string why);
        if (part is not null) return (part, gray, "");
        return (m, gray, why);
    }

    /// <summary>Only part of the area scrolls: the capture (one part so far) and the screen reading shrink to it.</summary>
    private void NarrowTo(PartRect part)
    {
        St.Narrow(part);
        var area = new PixelRect(_area.X + part.X, _area.Y + part.Y, part.Width, part.Height);
        var old = _screen;
        _screen = _openScreen(area);
        old?.Dispose();
        _area = area;
        _shownHeight = St.Image.Height; // rows added from here on light up in the preview
        var anchor = Anchor;
        if (!area.Contains(anchor))
        {
            SetAnchor(area.Center);
            _post(new SessionAnchorMoved(area.Center));
        }
        Log.Info($"Longshot: only part of the area scrolls; now capturing {area.Width}x{area.Height} at {area.X},{area.Y}.");
        _post(new SessionAreaChanged(area));
    }

    /// <summary>Adds a matched frame (cut down to the part, if the match is for one).</summary>
    private void Add(Frame frame, GrayFrame gray, ShiftMatch m, StepInfo info = default)
    {
        if (m.Part is { } part)
        {
            NarrowTo(part);
            St.Apply(frame.Crop(part), gray.Crop(part), m, info);
        }
        else St.Apply(frame, gray, m, info);
        _region = m.Region;
    }

    /// <summary>Other spots to try when nothing moved at the scroll point on the first step.</summary>
    private IEnumerable<PixelPoint> Probes(PixelPoint tried)
    {
        var a = _area;
        PixelPoint[] spots =
        [
            a.Center,
            new(a.X + a.Width / 4, a.CenterY),
            new(a.X + a.Width * 3 / 4, a.CenterY),
            new(a.CenterX, a.Y + a.Height * 3 / 4),
        ];
        foreach (var p in spots)
            if (Math.Abs(p.X - tried.X) > 40 || Math.Abs(p.Y - tried.Y) > 40) yield return p;
    }

    internal (StepOutcome Outcome, string Message) ExtendOnce()
    {
        if (St.Image.Height >= MaxHeight) return (StepOutcome.Full, Lang.T($"Stopped at {MaxHeight:N0} px, the size limit. Press Done to save.", $"توقفت عند {MaxHeight:N0} بكسل، وهو الحد الأقصى. اضغط «تم» للحفظ."));
        int n = Notches();
        ShiftMatch? match = null;
        var start = Anchor;
        using var probes = St.Steps == 0 && _pxPerDelta.Count == 0 ? Probes(start).GetEnumerator() : null;
        bool probing = false;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            double? expected = PerNotch * n;
            Scroll(n, down: true, _unit);
            var frame = GrabSettled();
            var (m, gray, note) = MatchFrame(frame, expected);
            match = m;
            Log.Info($"Longshot extend n={n} unit={_unit} at={Anchor.X},{Anchor.Y} expected={expected:F0} -> moved={m.Moved} ok={m.Ok} d={m.Distance} score={m.Score:F2} worst={m.Worst:F1} top={m.Top} bottom={m.Bottom} est={m.Estimated} part={m.Part?.ToString() ?? "-"} {m.Reason}{(note.Length > 0 ? " | part: " + note : "")}");
            if (!m.Moved)
            {
                Interlocked.Add(ref _scrolledDelta, -(long)n * _unit); // nothing moved: as if it never scrolled
                // Some apps only scroll on whole notches: try a whole one before calling it the end.
                if (_unit < 120 && _pxPerDelta.Count == 0)
                {
                    _unit = 120;
                    n = 1;
                    continue;
                }
                // Nothing scrolls under the scroll point: on the first step, try a few other spots.
                if (probes is not null && probes.MoveNext())
                {
                    probing = true;
                    SetAnchor(probes.Current);
                    _post(new SessionAnchorMoved(probes.Current));
                    Update(Lang.T("Nothing scrolled there. Looking for the part that scrolls…", "لم يتحرك شيء هناك. جارٍ البحث عن الجزء الذي يتمرّر…"));
                    continue;
                }
                if (probing)
                {
                    SetAnchor(start);
                    _post(new SessionAnchorMoved(start));
                }
                return (StepOutcome.End, St.Steps == 0
                    ? Lang.T("Nothing scrolled. Drag the round scroll marker onto what should scroll, or choose Scroll area.", "لم يتحرك شيء. اسحب علامة التمرير الدائرية فوق ما يجب أن يتمرّر، أو اختر «منطقة تمرير».")
                    : Lang.T("Reached the end. Press Done to save.", "وصلنا إلى النهاية. اضغط «تم» للحفظ."));
            }
            if (m.Ok)
            {
                bool narrowed = m.Part is not null;
                Add(frame, gray, m, new StepInfo(n, _unit));
                if (!m.Estimated && (expected is null || m.Distance >= 0.8 * expected))
                {
                    _pxPerDelta.Add(m.Distance / (n * _unit / 120.0));
                    if (_pxPerDelta.Count > 8) _pxPerDelta.RemoveAt(0);
                    Volatile.Write(ref _estimate, Median(_pxPerDelta));
                }
                string added = Lang.T($"Added {m.Distance} px", $"أُضيف {m.Distance} بكسل") + (m.Estimated ? Lang.T(" (blank area, estimated).", " (منطقة فارغة، بالتقدير).") : ".");
                return (StepOutcome.Added, narrowed
                    ? Lang.T("Only this part scrolls, so Longshot captures just this part. ", "هذا الجزء وحده يتمرّر، لذا تلتقطه اللقطة الطويلة وحده. ") + added
                    : added);
            }
            // It moved, but too far to line up: go back and take a smaller step. What one notch moves
            // still holds, so the smaller step keeps an expected distance to check against.
            Scroll(n, down: false, _unit);
            GrabSettled();
            if (n > 1) n = Math.Max(1, n / 2);
            else if (_unit > 30) _unit /= 2;
            else break;
        }
        string reason = match?.Reason is { Length: > 0 } r ? Lang.T(r, "تعذّرت مطابقة الجزء الجديد") : Lang.T("Couldn't line it up", "تعذّرت المطابقة");
        return (StepOutcome.Failed, reason + Lang.T(". Try Extend again, or make the frame taller.", ". جرّب «مدّ» مرة أخرى، أو اجعل الإطار أطول."));
    }

    private void RunAuto()
    {
        string message;
        bool atEnd = false;
        while (true)
        {
            if (_stop || EscapePressed()) { message = Lang.T("Stopped. Press Done to save, or Auto to carry on.", "توقف. اضغط «تم» للحفظ أو «تلقائي» للمتابعة."); break; }
            var (outcome, text) = ExtendOnce();
            if (outcome != StepOutcome.Added)
            {
                message = text;
                atEnd = outcome is StepOutcome.End or StepOutcome.Full && St.Steps > 0;
                break;
            }
            Update(text + Lang.T("  (Esc stops)", "  (Esc للإيقاف)"));
        }
        Update(message, atEnd: atEnd);
    }

    private void RunFollow()
    {
        _followUsed = true;
        Update(Lang.T("Scroll it yourself: Longshot adds as you go. Stop or Esc when done.", "مرّر بنفسك وستُضاف الأجزاء تباعًا. «إيقاف» أو Esc عند الانتهاء."));
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
                Update(Lang.T($"Reached the {MaxHeight:N0} px size limit. Press Done to save.", $"وصلنا إلى الحد الأقصى {MaxHeight:N0} بكسل. اضغط «تم» للحفظ."), atEnd: true);
                break;
            }
            var (m, gray, _) = MatchFrame(cur, null);
            // While the page is still moving, only near-perfect matches are taken: this keeps up with
            // fast scrolling without risking a bad seam.
            if (m.Moved && m.Ok && (settled || m.Score <= 0.5))
            {
                bool narrowed = m.Part is not null;
                Add(cur, gray, m);
                if (narrowed) prev = Grab(); // the screen is read from the part alone now
                Update((narrowed ? Lang.T("Only this part scrolls, so Longshot captures just this part. ", "هذا الجزء وحده يتمرّر، لذا تلتقطه اللقطة الطويلة وحده. ") : "")
                    + Lang.T($"Added {m.Distance} px. Keep scrolling.", $"أُضيف {m.Distance} بكسل. واصل التمرير."));
                reported = null;
            }
            else if (settled && m.Moved && !(reported is not null && Same(cur, reported)))
            {
                reported = cur;
                var back = ShiftFinder.Find(gray, St.LastGray); // the same content, further up?
                Log.Info($"Longshot follow: no match ({m.Reason}); scrolled up={back.Ok}");
                Update(back is { Moved: true, Ok: true }
                    ? Lang.T("You scrolled up. Scroll down again to continue.", "مرّرت للأعلى. مرّر للأسفل مجددًا للمتابعة.")
                    : Lang.T("Lost track. Scroll back up a little, then go slower.", "فُقد التتبّع. مرّر للأعلى قليلًا ثم تمهّل."));
            }
        }
        Update(Lang.T("Stopped following. Press Done to save.", "توقف التتبّع. اضغط «تم» للحفظ."));
    }

    private void RunUndo()
    {
        var step = St.Undo();
        if (step is null)
        {
            Update(Lang.T("Nothing to undo.", "لا شيء للتراجع عنه."));
            return;
        }
        if (step.Info.Notches > 0)
        {
            Scroll(step.Info.Notches, down: false, step.Info.WheelUnit);
            GrabSettled();
            Update(Lang.T("Removed the last part.", "أُزيل آخر جزء."));
        }
        else
        {
            Update(Lang.T("Removed the last part. Scroll back up a little to continue.", "أُزيل آخر جزء. مرّر للأعلى قليلًا للمتابعة."));
        }
    }

    /// <summary>
    /// The frame's bottom edge moved down (same top, left and width): reads the taller area and adds
    /// the rows below the old bottom. If the page moved meanwhile, the top part is lined up first;
    /// if that fails, the controller starts again from the new area.
    /// </summary>
    private void RunGrow()
    {
        if (_grow is not { } area) return;
        _grow = null;
        var old = _screen;
        _screen = _openScreen(area);
        _area = area;
        old?.Dispose();

        var taller = GrabSettled();
        var last = St.Last;
        if (taller.Width != last.Width || taller.Height <= last.Height)
        {
            _post(new SessionRestartNeeded(Lang.T("Starting again from the new area…", "البدء من جديد من المنطقة الجديدة…")));
            return;
        }
        var top = new Frame(taller.Width, last.Height, taller.Pixels[..(last.Height * taller.Stride)]);
        var (m, gray) = St.Match(top);
        if (m.Moved)
        {
            if (!m.Ok)
            {
                _post(new SessionRestartNeeded(Lang.T("The page moved, so it starts again from the new area…", "تحرّكت الصفحة، لذا يبدأ الالتقاط من جديد من المنطقة الجديدة…")));
                return;
            }
            St.Apply(top, gray, m);
        }
        int added = St.GrowDown(taller);
        _region += added;
        Log.Info($"Longshot area grew by {added} px; added below.");
        Update(Lang.T($"Added {added} px below.", $"أُضيف {added} بكسل في الأسفل."));
    }

    private void Finish()
    {
        _finished = true;
        _post(new SessionFinished(St.Image.Width, St.Image.Height, St.Image.ToArray()));
    }

    private void Update(string? message, bool atEnd = false)
    {
        var image = St.Image;
        int width = Math.Max(1, Math.Min(_previewWidth, image.Width));
        var pixels = image.Overview(width, out int height);
        var preview = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        preview.Freeze();
        int added = Math.Max(0, image.Height - _shownHeight);
        bool fresh = _shownHeight == 0; // the first part: nothing was "added"
        _shownHeight = image.Height;
        _post(new SessionUpdate(image.Width, image.Height, St.Steps + 1, preview, message, St.CanUndo, fresh ? 0 : added, atEnd));
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
