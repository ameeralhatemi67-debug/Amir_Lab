using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Longshot;

/// <summary>
/// One capture from start to finish: optional countdown → the panel and frame appear at once on
/// the window in front, then glide onto the part that scrolls under the pointer if there is one →
/// move or resize the frame, drag its scroll marker, or pick another target (window, scroll area,
/// region) if needed → Auto / Extend / Follow → save, copy and show the result. Pressing the
/// shortcut again saves. Only one runs at a time.
/// </summary>
internal sealed class LongshotCapture
{
    private const byte DimStrength = 88;
    private static LongshotCapture? _current;

    private readonly ToolboxHost _host;
    private readonly LongshotTool _tool;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private CaptureFrame? _frame;
    private CapturePanel? _panel;
    private CaptureScrim? _scrim;     // dims the rest of the screen (null when that is switched off)
    private CaptureScrim? _pickScrim; // used only while picking, when dimming is off
    private AreaPicker? _picker;
    private CaptureSession? _session;
    private CountdownToast? _countdown;
    private int _generation; // events from a session replaced after the area changed are ignored
    private bool _ready;
    private SessionCommand? _busy;
    private bool _canUndo;
    private bool _ended;
    private CaptureTarget _target;
    private PixelRect _window;     // the window in front when Longshot started (the Window button goes back to it)
    private PixelPoint _pointer;   // where the pointer was then
    private int _parts;
    private double? _pxPerDelta;  // how far one notch scrolls this page, learnt once and kept across restarts
    private long _carriedDelta;   // scrolling done by sessions replaced after the area changed (for Refresh)
    private bool _lookingUp;      // still finding the scroll area under the pointer
    private bool _startPending;   // "When Longshot starts: Auto or Follow" hasn't run yet (it waits for that)

    private LongshotCapture(ToolboxHost host, LongshotTool tool)
    {
        _host = host;
        _tool = tool;
    }

    public static bool Active => _current is not null;

    /// <summary>For tests: the capture in progress, its panel, frame, target and picker.</summary>
    internal static LongshotCapture? Current => _current;
    internal CapturePanel? Panel => _panel;
    internal CaptureFrame? Frame => _frame;
    internal CaptureTarget Target => _target;
    internal AreaPicker? Picker => _picker;

    /// <summary>For tests: what dragging the frame to <paramref name="area"/> does.</summary>
    internal void ChangeArea(PixelRect area) => OnAreaChanged(area);

    /// <summary>For tests: what dropping the scroll marker at <paramref name="at"/> does.</summary>
    internal void MoveAnchor(PixelPoint at) => OnAnchorChanged(at);

    /// <summary>For tests: a simulated screen and wheel instead of the real ones, and faster settling.</summary>
    internal static Func<PixelRect, IScreenSource>? ScreenOverride { get; set; }
    internal static Func<PixelRect, IScroller>? ScrollerOverride { get; set; }
    internal static SessionTiming? TimingOverride { get; set; }

    /// <summary>For tests: the window in front and the pointer at the start, and the scroll area found under it.</summary>
    internal static (PixelRect Window, PixelPoint Pointer)? StartOverride { get; set; }
    internal static Func<PixelPoint, PixelRect?>? ScrollAreaOverride { get; set; }

    public static void Start(ToolboxHost host, LongshotTool tool, PixelRect? area = null, int delaySeconds = 0) =>
        Start(host, tool, area is { } a ? new CaptureTarget(TargetKind.Region, a, a.Center) : null, delaySeconds);

    public static void Start(ToolboxHost host, LongshotTool tool, CaptureTarget? target, int delaySeconds = 0)
    {
        if (_current is not null)
        {
            _current.ShortcutAgain(); // pressing the shortcut again saves
            return;
        }
        _current = new LongshotCapture(host, tool);
        _current.Begin(target, delaySeconds);
    }

    public static void CancelActive() => _current?.Cancel();

    private void ShortcutAgain()
    {
        if (_picker is not null) { _picker.Dispose(); EndPick(); RestoreAfterPick(); return; }
        Done();
    }

    private void Begin(CaptureTarget? target, int delaySeconds)
    {
        if (delaySeconds <= 0)
        {
            Open(target);
            return;
        }
        int left = delaySeconds;
        _countdown = new CountdownToast();
        _countdown.SetSeconds(left);
        _countdown.Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (_ended) { timer.Stop(); return; }
            if (--left > 0) { _countdown?.SetSeconds(left); return; }
            timer.Stop();
            _countdown?.Close();
            _countdown = null;
            After(TimeSpan.FromMilliseconds(150), () => Open(target));
        };
        timer.Start();
    }

    /// <summary>
    /// Shows the frame and panel straight away on the window in front (or the target given), then,
    /// unless switched off, looks for the part that scrolls under the pointer and glides onto it.
    /// </summary>
    private void Open(CaptureTarget? given)
    {
        if (_ended) return;
        var settings = _host.Settings.Current.Longshot;
        try
        {
            if (StartOverride is { } start) (_window, _pointer) = start;
            else
            {
                _pointer = PixelPoint.From(ScreenGeometry.CursorPosition());
                _window = TargetFinder.ForegroundWindow()?.Rect ?? TargetFinder.WorkAreaAt(_pointer);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            Fail(Lang.T("Longshot couldn't find the window", "تعذّر على اللقطة الطويلة إيجاد النافذة"), ex);
            return;
        }
        bool lookUp = given is null && settings.StartOn != "window" && _window.Contains(_pointer);
        _startPending = true;
        _lookingUp = lookUp;
        var target = given ?? new CaptureTarget(TargetKind.Window, _window, _window.Contains(_pointer) ? _pointer : _window.Center);
        Log.Info($"Longshot {target.Kind} {target.Area.Width}x{target.Area.Height} at {target.Area.X},{target.Area.Y}; scrolls at {target.Anchor.X},{target.Anchor.Y}.");

        if (settings.DimOutside && !CapturePanel.KeepOffScreen)
        {
            _scrim = new CaptureScrim { Strength = DimStrength };
            _scrim.Show(target.Area);
        }
        ShowTarget(target);
        string? shortcut = null;
        try { shortcut = App.ShortcutText(LongshotTool.ToolId, "capture"); } catch (Exception) { /* not running as the app (tests) */ }
        _panel = new CapturePanel(shortcut);
        _panel.Extend.Click += (_, _) => { if (_ready && _picker is null && _busy is null or SessionCommand.Extend) _session?.Enqueue(SessionCommand.Extend); };
        _panel.Auto.Click += (_, _) => Toggle(SessionCommand.Auto);
        _panel.Follow.Click += (_, _) => Toggle(SessionCommand.Follow);
        _panel.Undo.Click += (_, _) =>
        {
            if (!_ready || _busy is not null || !_canUndo) return;
            SetBusy(SessionCommand.Undo);
            _session?.Enqueue(SessionCommand.Undo);
        };
        _panel.Done.Click += (_, _) => Done();
        _panel.CloseButton.Click += (_, _) => Cancel();
        _panel.RefreshButton.Click += (_, _) => Refresh();
        _panel.TargetChosen += OnTargetChosen;
        _panel.SetTarget(target.Kind);
        _panel.PlaceBeside(given is null ? _window : target.Area);
        _panel.Show();
        // The session first, so nothing in the panel can keep the capture from starting.
        OpenSession(target, autoStart: true);
        SetBusy(null);
        if (lookUp) _ = LookForScrollArea();
    }

    /// <summary>
    /// The part under the pointer that scrolls (a chat, a list, a panel): when it isn't the whole
    /// window, the frame glides onto it before anything is captured.
    /// </summary>
    private async Task LookForScrollArea()
    {
        _lookingUp = true;
        PixelRect? found;
        try
        {
            found = ScrollAreaOverride is { } fake ? fake(_pointer) : await TargetFinder.ScrollAreaAtAsync(_pointer, TimeSpan.FromMilliseconds(900));
        }
        catch (Exception ex)
        {
            Log.Error("Longshot: looking for the scroll area failed", ex);
            found = null;
        }
        _lookingUp = false;
        if (_ended) return;
        bool untouched = _busy is null && _parts <= 1 && _picker is null && _target.Kind == TargetKind.Window && _target.Area == _window;
        if (found is { } area && untouched && !NearlySame(area, _window))
        {
            Log.Info($"Longshot: the part under the pointer scrolls: {area.Width}x{area.Height} at {area.X},{area.Y}.");
            // The new session runs "When Longshot starts" once it is ready, if it hasn't run yet.
            Restart(new CaptureTarget(TargetKind.ScrollArea, area, area.Contains(_pointer) ? _pointer : area.Center),
                Lang.T("Found the part that scrolls here. The Window button takes the whole window instead.", "وُجد الجزء الذي يتمرّر هنا. زر «نافذة» يلتقط النافذة كاملة بدلًا منه."),
                rewind: false, carry: false, autoStart: _startPending);
        }
        else if (_ready && _startPending) AfterSelect(); // the session was ready and waiting for this
    }

    private static bool NearlySame(PixelRect a, PixelRect b) =>
        Math.Abs(a.X - b.X) <= 16 && Math.Abs(a.Y - b.Y) <= 16 && Math.Abs(a.Right - b.Right) <= 16 && Math.Abs(a.Bottom - b.Bottom) <= 16;

    private IScreenSource Screen(PixelRect area) => ScreenOverride?.Invoke(area) ?? new GdiScreenSource(area);

    private IScroller Scroller(PixelRect area) => ScrollerOverride?.Invoke(area) ?? new WheelScroller();

    /// <summary>Puts the frame (and the dim layer's hole) on the target.</summary>
    private void ShowTarget(CaptureTarget target)
    {
        _target = target;
        _tool.LastArea = target.Area;
        _tool.LastAnchor = target.Anchor;
        _tool.LastKind = target.Kind;
        if (_frame is null)
        {
            _frame = new CaptureFrame(target.Area, target.Anchor, target.Kind);
            _frame.AreaChanged += OnAreaChanged;
            _frame.AnchorChanged += OnAnchorChanged;
            _frame.Moving += area => _scrim?.SetHole(area);
            _frame.Show();
        }
        else
        {
            _frame.SetKind(target.Kind);
            _frame.AnimateTo(target.Area, target.Anchor);
        }
        _panel?.SetTarget(target.Kind);
    }

    private void OpenSession(CaptureTarget target, bool autoStart, long rewindDelta = 0, bool rewindToTop = false)
    {
        int generation = ++_generation;
        _ready = false;
        _canUndo = false;
        _parts = 0;
        var settings = _host.Settings.Current.Longshot;
        double scale = ScreenGeometry.MonitorAt(target.Area.Center.ToPoint()).Scale;
        _session = new CaptureSession(target.Area, Screen, Scroller(target.Area), settings.StepFraction,
            (int)(CapturePanel.PreviewWidth * scale),
            e => _ui.BeginInvoke(() => { if (generation == _generation) Handle(e, autoStart); }), TimingOverride)
        {
            KnownPxPerDelta = _pxPerDelta,
            RewindDelta = rewindDelta,
            RewindToTop = rewindToTop,
            Anchor = target.Anchor,
            KeepRows = target.Kind != TargetKind.Window,
        };
        _session.Start();
    }

    /// <summary>
    /// The frame was moved or resized by hand. Pulling only its bottom edge down keeps what was
    /// captured and adds what is now below; any other change starts again from the new area.
    /// </summary>
    private void OnAreaChanged(PixelRect area)
    {
        if (_busy is SessionCommand.Auto or SessionCommand.Follow || _ended) return; // the frame is locked meanwhile
        var old = _target.Area;
        var anchor = _frame?.Anchor ?? area.Center;
        bool grewDown = area.X == old.X && area.Y == old.Y && area.Width == old.Width && area.Height > old.Height;
        if (grewDown && _session is not null && _ready && _parts > 1 && _busy is null or SessionCommand.Extend)
        {
            _target = _target with { Area = area, Anchor = anchor };
            _tool.LastArea = area;
            Log.Info($"Longshot area grew down to {area.Width}x{area.Height}; adding below.");
            _panel?.SetStatus(Lang.T("Adding what's below…", "إضافة ما في الأسفل…"));
            _session.GrowDown(area);
            return;
        }
        Log.Info($"Longshot area changed to {area.Width}x{area.Height} at {area.X},{area.Y}; starting again.");
        Restart(new CaptureTarget(TargetKind.Region, area, anchor), Lang.T("Starting again from the new area…", "البدء من جديد من المنطقة الجديدة…"), rewind: false, carry: true);
    }

    /// <summary>The scroll marker was dropped somewhere new: the wheel turns there from now on.</summary>
    private void OnAnchorChanged(PixelPoint at)
    {
        if (_ended) return;
        _target = _target with { Anchor = at };
        _tool.LastAnchor = at;
        _session?.SetAnchor(at);
        _frame?.SetAnchor(at);
        Log.Info($"Longshot now scrolls at {at.X},{at.Y}.");
        _panel?.SetStatus(Lang.T("Longshot scrolls where the round marker is now. Press Auto or Extend.", "تمرّر اللقطة الطويلة الآن حيث العلامة الدائرية. اضغط «تلقائي» أو «مدّ»."));
    }

    /// <summary>Window, Scroll area or Region was pressed on the panel.</summary>
    private void OnTargetChosen(TargetKind kind)
    {
        if (_ended) return;
        if (_busy is not null || !_ready && kind != TargetKind.Window)
        {
            _panel?.SetTarget(_target.Kind); // finish or stop the current step first
            return;
        }
        if (kind == TargetKind.Window)
        {
            if (_picker is not null) { _picker.Dispose(); EndPick(); }
            if (_target.Kind == TargetKind.Window && _target.Area == _window) { RestoreAfterPick(); return; }
            Restart(new CaptureTarget(TargetKind.Window, _window, _window.Contains(_pointer) ? _pointer : _window.Center),
                Lang.T("Capturing the whole window.", "التقاط النافذة كاملة."), rewind: false, carry: false);
            return;
        }
        StartPick(kind);
    }

    private void StartPick(TargetKind kind)
    {
        _picker?.Dispose();
        CaptureScrim scrim;
        if (_scrim is not null) scrim = _scrim;
        else scrim = _pickScrim = new CaptureScrim();
        _frame?.SetHidden(true);
        _panel?.SetTarget(kind);
        _panel?.SetPicking(true, kind == TargetKind.Region
            ? Lang.T("Drag to draw the area to capture. Esc goes back.", "اسحب لرسم المنطقة المراد التقاطها. Esc للرجوع.")
            : Lang.T("Point at the part that scrolls and click it, or drag to draw an area. Esc goes back.", "أشر إلى الجزء الذي يتمرّر وانقره، أو اسحب لرسم منطقة. Esc للرجوع."));
        _picker = new AreaPicker(kind, scrim,
            picked: t =>
            {
                EndPick();
                Log.Info($"Longshot picked {t.Kind} {t.Area.Width}x{t.Area.Height} at {t.Area.X},{t.Area.Y}.");
                Restart(t, t.Kind == TargetKind.Region
                    ? Lang.T("Region chosen. Press Auto to scroll to the end, or Done for just this.", "اختيرت المنطقة. اضغط «تلقائي» للتمرير حتى النهاية، أو «تم» لها وحدها.")
                    : Lang.T("Ready. Press Auto to scroll to the end, or Extend for one step.", "جاهز. اضغط «تلقائي» للتمرير حتى النهاية، أو «مدّ» لخطوة واحدة."),
                    rewind: false, carry: false);
            },
            cancelled: () => { EndPick(); RestoreAfterPick(); });
        _picker.Start();
    }

    private void EndPick()
    {
        _picker = null;
        _frame?.SetHidden(false);
        if (_pickScrim is not null)
        {
            _pickScrim.Dispose();
            _pickScrim = null;
        }
        if (_scrim is not null)
        {
            _scrim.Strength = DimStrength;
            _scrim.Show(_target.Area);
        }
    }

    private void RestoreAfterPick()
    {
        _panel?.SetTarget(_target.Kind);
        _panel?.SetStatus(Lang.T("Kept the area as it was.", "بقيت المنطقة كما هي."));
        SetBusy(_busy);
    }

    /// <summary>Refresh: scrolls back to where the capture started and takes the first part again.</summary>
    private void Refresh()
    {
        if (_ended || _busy is not null || _picker is not null) return; // between steps, so no scroll is half done
        Log.Info("Longshot refresh: back to the start.");
        Restart(_target, Lang.T("Going back to the start…", "العودة إلى البداية…"), rewind: true, carry: true);
    }

    /// <summary>
    /// Drops what was captured and takes the first part again (Refresh, a new area or target).
    /// <paramref name="carry"/>: the same content is still being scrolled, so how far it scrolled and
    /// how far one notch moves it carry over; a different target starts afresh.
    /// </summary>
    private void Restart(CaptureTarget target, string message, bool rewind, bool carry, bool autoStart = false)
    {
        if (_ended || _busy == SessionCommand.Finish) return;
        long delta = _carriedDelta + (_session?.ScrolledDelta ?? 0);
        bool followed = _session?.FollowUsed ?? false;
        if (_session?.PxPerDelta is { } known) _pxPerDelta = known;
        _session?.Dispose();
        _session = null;
        _busy = null;
        if (!carry)
        {
            delta = 0;
            _pxPerDelta = null;
        }
        _carriedDelta = rewind ? 0 : delta;
        ShowTarget(target);
        _panel?.ClearPreview();
        _panel?.SetStatus(message);
        OpenSession(target, autoStart, rewindDelta: rewind ? delta : 0, rewindToTop: rewind && followed);
        SetBusy(null);
    }

    private void Handle(SessionEvent e, bool autoStart)
    {
        if (_ended && e is not SessionFinished) return;
        switch (e)
        {
            case SessionReady:
                _ready = true;
                SetBusy(null);
                if (autoStart && _startPending && !_lookingUp) AfterSelect(); // else the scroll-area lookup runs it
                break;
            case SessionBusy busy:
                if (_busy == SessionCommand.Finish) return;
                SetBusy(busy.Command);
                break;
            case SessionUpdate u:
                _panel?.ShowUpdate(u.Preview, u.Width, u.Height, u.Parts, u.Message, u.AddedRows);
                _panel?.SetFinishReady(u.AtEnd);
                if (u.AddedRows > 0) _frame?.FlashAdded(u.AddedRows);
                _canUndo = u.CanUndo;
                _parts = u.Parts;
                SetBusy(_busy);
                break;
            case SessionAreaChanged changed:
                // Only part of the area scrolls: the frame glides onto it.
                _target = _target with { Kind = TargetKind.ScrollArea, Area = changed.Area, Anchor = changed.Area.Contains(_target.Anchor) ? _target.Anchor : changed.Area.Center };
                _tool.LastArea = changed.Area;
                _tool.LastKind = TargetKind.ScrollArea;
                _frame?.SetKind(TargetKind.ScrollArea);
                _frame?.AnimateTo(changed.Area, _target.Anchor);
                _panel?.SetTarget(TargetKind.ScrollArea);
                break;
            case SessionAnchorMoved moved:
                _target = _target with { Anchor = moved.At };
                _tool.LastAnchor = moved.At;
                _frame?.SetAnchor(moved.At);
                break;
            case SessionWheel:
                _frame?.PulseAnchor();
                break;
            case SessionRestartNeeded restart:
                Restart(_target, restart.Message, rewind: false, carry: true);
                break;
            case SessionFinished f:
                _ = Complete(f);
                break;
            case SessionFailed failed:
                Fail(Lang.T("Longshot stopped", "توقفت اللقطة الطويلة"), new InvalidOperationException(failed.Message));
                break;
        }
    }

    /// <summary>"When Longshot starts": Auto or Follow straight away (once per capture).</summary>
    private void AfterSelect()
    {
        _startPending = false;
        switch (_host.Settings.Current.Longshot.AfterSelect)
        {
            case "auto": Toggle(SessionCommand.Auto); break;
            case "follow": Toggle(SessionCommand.Follow); break;
        }
    }

    private void Toggle(SessionCommand mode)
    {
        if (!_ready || _session is null || _picker is not null) return;
        if (_busy == mode) _session.Stop();
        else if (_busy is null)
        {
            SetBusy(mode);
            _session.StartMode(mode);
        }
    }

    private void Done()
    {
        if (!_ready || _busy == SessionCommand.Finish || _session is null) return;
        if (_picker is not null) { _picker.Dispose(); EndPick(); }
        _session.Stop();
        _session.Enqueue(SessionCommand.Finish);
        SetBusy(SessionCommand.Finish);
        _panel?.SetStatus(Lang.T("Saving…", "جارٍ الحفظ…"));
    }

    private void SetBusy(SessionCommand? state)
    {
        _busy = state;
        if (_panel is null) return;
        bool picking = _picker is not null;
        bool idle = _ready && state is null && !picking;
        _panel.Extend.IsEnabled = _ready && !picking && state is null or SessionCommand.Extend;
        bool auto = state == SessionCommand.Auto, follow = state == SessionCommand.Follow;
        CapturePanel.SetButton(_panel.Auto, auto ? "lap-stop" : "lap-play", auto ? Lang.T("Stop", "إيقاف") : Lang.T("Auto", "تلقائي"));
        _panel.Auto.IsEnabled = _ready && !picking && state is null or SessionCommand.Auto;
        CapturePanel.SetButton(_panel.Follow, follow ? "lap-stop" : "lap-follow", follow ? Lang.T("Stop", "إيقاف") : Lang.T("Follow", "تتبّع"));
        _panel.Follow.IsEnabled = _ready && !picking && state is null or SessionCommand.Follow;
        _panel.Undo.IsEnabled = idle && _canUndo;
        _panel.RefreshButton.IsEnabled = state is null && !picking;
        _panel.Done.IsEnabled = _ready && !picking && state != SessionCommand.Finish;
        foreach (var kind in new[] { TargetKind.Window, TargetKind.ScrollArea, TargetKind.Region })
            _panel.TargetButton(kind).IsEnabled = state is null;
        _panel.SetLive(auto ? Lang.T("Auto", "تلقائي") : follow ? Lang.T("Following", "يتتبّع") : null);
        _frame?.SetMode(auto ? FrameMode.Auto : follow ? FrameMode.Follow : FrameMode.Idle);
    }

    private async Task Complete(SessionFinished f)
    {
        var settings = _host.Settings.Current.Longshot;
        string folder = string.IsNullOrWhiteSpace(settings.SaveFolder) ? LongshotOutput.DefaultFolder : settings.SaveFolder!;
        string path = LongshotOutput.NewPath(folder, settings.Format, DateTime.Now);
        CloseWindows();
        _session?.Dispose();
        _session = null;
        try
        {
            double scale = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition()).Scale;
            int previewWidth = (int)Math.Min(f.Width, 700 * scale);
            if ((long)f.Height * previewWidth / f.Width > 16000) previewWidth = Math.Max(1, (int)(16000L * f.Width / f.Height));
            var (image, preview) = await LongshotOutput.OnStaThread(() =>
            {
                var bitmap = LongshotOutput.ToBitmap(f.Width, f.Height, f.Pixels);
                LongshotOutput.Save(bitmap, path);
                return (bitmap, CaptureSession.Downscale(f.Pixels, f.Width, f.Height, previewWidth));
            });
            bool copied = settings.CopyToClipboard && LongshotOutput.CopyToClipboard(image);
            Log.Info($"Longshot saved {path} ({f.Width}x{f.Height}){(copied ? ", copied" : "")}.");
            if (settings.ShowResult)
            {
                var window = new LongshotResultWindow(image, preview, path, copied, () => _tool.Capture(), App.IsToolEnabled(Tools.FileToolboxTool.ToolId));
                window.Show();
                Foreground.Activate(window);
            }
            else
            {
                var toast = new NoticeToast(Lang.T("Long screenshot saved", "حُفظت اللقطة الطويلة"), IOPath.GetFileName(path) + (copied ? Lang.T(" · copied to the clipboard", " · نُسخت إلى الحافظة") : ""), warning: false);
                toast.PlaceNear(ScreenGeometry.CursorPosition(), 24);
                toast.Show();
            }
        }
        catch (Exception ex)
        {
            Fail(Lang.T("The screenshot could not be saved", "تعذّر حفظ اللقطة"), ex);
            return;
        }
        End();
    }

    private void Cancel()
    {
        _generation++; // a finish already on its way is dropped: a cancelled capture is never saved
        _session?.Dispose();
        _session = null;
        End();
    }

    private void Fail(string title, Exception ex)
    {
        Log.Error(title, ex);
        var toast = new NoticeToast(title, ex.Message, warning: true);
        toast.PlaceNear(ScreenGeometry.CursorPosition(), 24);
        toast.Show();
        _session?.Dispose();
        _session = null;
        End();
    }

    private void CloseWindows()
    {
        _picker?.Dispose();
        _picker = null;
        _countdown?.Close();
        _panel?.Close();
        _frame?.Close();
        _scrim?.Dispose();
        _pickScrim?.Dispose();
        _countdown = null;
        _panel = null;
        _frame = null;
        _scrim = null;
        _pickScrim = null;
    }

    private void End()
    {
        _ended = true;
        CloseWindows();
        if (_current == this) _current = null;
    }

    private void After(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); if (!_ended) action(); };
        timer.Start();
    }
}
