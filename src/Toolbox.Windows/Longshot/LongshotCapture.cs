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
/// One capture from start to finish: optional countdown → the panel and frame appear at once
/// around the window in front (the start of the screenshot) → move or resize the frame if needed →
/// Extend / Auto / Follow → save, copy and show the result. Only one runs at a time.
/// </summary>
internal sealed class LongshotCapture
{
    private static LongshotCapture? _current;

    private readonly ToolboxHost _host;
    private readonly LongshotTool _tool;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private CaptureFrame? _frame;
    private CapturePanel? _panel;
    private CaptureSession? _session;
    private CountdownToast? _countdown;
    private int _generation; // events from a session replaced after the area changed are ignored
    private bool _ready;
    private SessionCommand? _busy;
    private bool _canUndo;
    private bool _ended;
    private PixelRect _area;
    private int _parts;
    private double? _pxPerDelta;  // how far one notch scrolls this page, learnt once and kept across restarts
    private long _carriedDelta;   // scrolling done by sessions replaced after the area changed (for Refresh)

    private LongshotCapture(ToolboxHost host, LongshotTool tool)
    {
        _host = host;
        _tool = tool;
    }

    public static bool Active => _current is not null;

    /// <summary>For tests: the capture in progress and its panel.</summary>
    internal static LongshotCapture? Current => _current;
    internal CapturePanel? Panel => _panel;

    /// <summary>For tests: what dragging the frame to <paramref name="area"/> does.</summary>
    internal void ChangeArea(PixelRect area) => OnAreaChanged(area);

    /// <summary>For tests: a simulated screen and wheel instead of the real ones, and faster settling.</summary>
    internal static Func<PixelRect, IScreenSource>? ScreenOverride { get; set; }
    internal static Func<PixelRect, IScroller>? ScrollerOverride { get; set; }
    internal static SessionTiming? TimingOverride { get; set; }

    public static void Start(ToolboxHost host, LongshotTool tool, PixelRect? area = null, int delaySeconds = 0)
    {
        if (_current is not null) return; // one at a time
        _current = new LongshotCapture(host, tool);
        _current.Begin(area, delaySeconds);
    }

    public static void CancelActive() => _current?.Cancel();

    private void Begin(PixelRect? area, int delaySeconds)
    {
        if (delaySeconds <= 0)
        {
            Open(area);
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
            After(TimeSpan.FromMilliseconds(150), () => Open(area));
        };
        timer.Start();
    }

    /// <summary>Shows the frame and panel straight away; the area is the window in front unless one was given.</summary>
    private void Open(PixelRect? given)
    {
        if (_ended) return;
        PixelRect area;
        try
        {
            area = given ?? StartArea.Find();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            Fail(Lang.T("Longshot couldn't find the window", "تعذّر على اللقطة الطويلة إيجاد النافذة"), ex);
            return;
        }
        _tool.LastArea = area;
        Log.Info($"Longshot area {area.Width}x{area.Height} at {area.X},{area.Y}.");

        _frame = new CaptureFrame(area);
        _frame.AreaChanged += OnAreaChanged;
        _frame.Show();
        _panel = new CapturePanel();
        _panel.Extend.Click += (_, _) => { if (_ready && _busy is null or SessionCommand.Extend) _session?.Enqueue(SessionCommand.Extend); };
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
        _panel.PlaceBeside(area);
        _panel.Show();
        // The session first, so nothing in the panel can keep the capture from starting.
        _area = area;
        OpenSession(area, autoStart: true);
        SetBusy(null);
    }

    private IScreenSource Screen(PixelRect area) => ScreenOverride?.Invoke(area) ?? new GdiScreenSource(area);

    private IScroller Scroller(PixelRect area) => ScrollerOverride?.Invoke(area) ?? new WheelScroller(area);

    private void OpenSession(PixelRect area, bool autoStart, long rewindDelta = 0, bool rewindToTop = false)
    {
        int generation = ++_generation;
        _ready = false;
        _canUndo = false;
        _parts = 0;
        var settings = _host.Settings.Current.Longshot;
        double scale = ScreenGeometry.MonitorAt(new Native.POINT { X = area.CenterX, Y = area.CenterY }).Scale;
        _session = new CaptureSession(() => Screen(area), Scroller(area), area.Height, settings.StepFraction,
            (int)(CapturePanel.PreviewWidth * scale), 0,
            e => _ui.BeginInvoke(() => { if (generation == _generation) Handle(e, autoStart); }), TimingOverride)
        {
            KnownPxPerDelta = _pxPerDelta,
            RewindDelta = rewindDelta,
            RewindToTop = rewindToTop,
        };
        _session.Start();
    }

    /// <summary>
    /// The frame was moved or resized. Pulling only its bottom edge down keeps what was captured and
    /// adds what is now below; any other change starts again from the new area.
    /// </summary>
    private void OnAreaChanged(PixelRect area)
    {
        if (_busy is SessionCommand.Auto or SessionCommand.Follow || _ended) return; // the frame is locked meanwhile
        var old = _area;
        bool grewDown = area.X == old.X && area.Y == old.Y && area.Width == old.Width && area.Height > old.Height;
        if (grewDown && _session is not null && _ready && _parts > 1 && _busy is null or SessionCommand.Extend)
        {
            _area = area;
            _tool.LastArea = area;
            Log.Info($"Longshot area grew down to {area.Width}x{area.Height}; adding below.");
            _panel?.SetStatus(Lang.T("Adding what's below…", "إضافة ما في الأسفل…"));
            _session.GrowDown(() => Screen(area), Scroller(area));
            return;
        }
        Log.Info($"Longshot area changed to {area.Width}x{area.Height} at {area.X},{area.Y}; starting again.");
        Restart(area, Lang.T("Starting again from the new area…", "البدء من جديد من المنطقة الجديدة…"), rewind: false);
    }

    /// <summary>Refresh: scrolls back to where the capture started and takes the first part again.</summary>
    private void Refresh()
    {
        if (_ended || _busy is not null) return; // between steps, so no scroll is half done
        Log.Info("Longshot refresh: back to the start.");
        Restart(_area, Lang.T("Going back to the start…", "العودة إلى البداية…"), rewind: true);
    }

    /// <summary>Drops what was captured and takes the first part again (Refresh, or a new area).</summary>
    private void Restart(PixelRect area, string message, bool rewind)
    {
        if (_ended || _busy == SessionCommand.Finish) return;
        long delta = _carriedDelta + (_session?.ScrolledDelta ?? 0);
        bool followed = _session?.FollowUsed ?? false;
        if (_session?.PxPerDelta is { } known) _pxPerDelta = known;
        _session?.Dispose();
        _session = null;
        _busy = null;
        _area = area;
        _tool.LastArea = area;
        _carriedDelta = rewind ? 0 : delta;
        _panel?.ClearPreview();
        _panel?.SetStatus(message);
        OpenSession(area, autoStart: false, rewindDelta: rewind ? delta : 0, rewindToTop: rewind && followed);
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
                if (autoStart)
                {
                    switch (_host.Settings.Current.Longshot.AfterSelect)
                    {
                        case "auto": Toggle(SessionCommand.Auto); break;
                        case "follow": Toggle(SessionCommand.Follow); break;
                    }
                }
                break;
            case SessionBusy busy:
                if (_busy == SessionCommand.Finish) return;
                SetBusy(busy.Command);
                break;
            case SessionUpdate u:
                _panel?.ShowUpdate(u.Preview, u.Width, u.Height, u.Parts, u.Message, u.AddedRows);
                _canUndo = u.CanUndo;
                _parts = u.Parts;
                SetBusy(_busy);
                break;
            case SessionRestartNeeded restart:
                Restart(_area, restart.Message, rewind: false);
                break;
            case SessionFinished f:
                _ = Complete(f);
                break;
            case SessionFailed failed:
                Fail(Lang.T("Longshot stopped", "توقفت اللقطة الطويلة"), new InvalidOperationException(failed.Message));
                break;
        }
    }

    private void Toggle(SessionCommand mode)
    {
        if (!_ready || _session is null) return;
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
        _session.Stop();
        _session.Enqueue(SessionCommand.Finish);
        SetBusy(SessionCommand.Finish);
        _panel?.SetStatus(Lang.T("Saving…", "جارٍ الحفظ…"));
    }

    private void SetBusy(SessionCommand? state)
    {
        _busy = state;
        if (_panel is null) return;
        bool idle = _ready && state is null;
        _panel.Extend.IsEnabled = _ready && state is null or SessionCommand.Extend;
        bool auto = state == SessionCommand.Auto, follow = state == SessionCommand.Follow;
        CapturePanel.SetButton(_panel.Auto, auto ? "lap-stop" : "lap-play", auto ? Lang.T("Stop", "إيقاف") : Lang.T("Auto", "تلقائي"));
        _panel.Auto.IsEnabled = _ready && state is null or SessionCommand.Auto;
        CapturePanel.SetButton(_panel.Follow, follow ? "lap-stop" : "lap-follow", follow ? Lang.T("Stop", "إيقاف") : Lang.T("Follow", "تتبّع"));
        _panel.Follow.IsEnabled = _ready && state is null or SessionCommand.Follow;
        _panel.Undo.IsEnabled = idle && _canUndo;
        _panel.RefreshButton.IsEnabled = state is null;
        _panel.Done.IsEnabled = _ready && state != SessionCommand.Finish;
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
        _countdown?.Close();
        _panel?.Close();
        _frame?.Close();
        _countdown = null;
        _panel = null;
        _frame = null;
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
