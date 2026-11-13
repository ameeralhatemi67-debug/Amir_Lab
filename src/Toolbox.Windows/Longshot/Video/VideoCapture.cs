using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Settings;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Longshot.Video;

/// <summary>
/// One Longshot video from start to finish: the frame opens on the window in front with the Video
/// panel beside it → move or resize the frame, or choose Window, Region or Screen → Record (after
/// 3, 2, 1 unless switched off) → the panel goes away and a small pill outside the area shows the
/// time with Pause, Discard and Stop → Stop saves the MP4 and opens the result window. The
/// shortcut works the whole way: again starts recording, and again stops it. Only one runs at a
/// time, and never alongside a long screenshot.
/// </summary>
internal sealed class VideoCapture
{
    private const byte DimStrength = 88;
    private static VideoCapture? _current;

    internal enum Stage { Setup, Countdown, Starting, Recording, Stopping }

    private readonly ToolboxHost _host;
    private readonly LongshotTool _tool;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private CaptureFrame? _frame;
    private VideoPanel? _panel;
    private RecordingPill? _pill;
    private CaptureScrim? _scrim;
    private CaptureScrim? _pickScrim;
    private AreaPicker? _picker;
    private ScreenRecorder? _recorder;
    private DispatcherTimer? _ticker;
    private DispatcherTimer? _countdown;
    private VideoTarget _kind;
    private PixelRect _area;
    private PixelRect _window;
    private bool _ended;

    private VideoCapture(ToolboxHost host, LongshotTool tool)
    {
        _host = host;
        _tool = tool;
    }

    public static bool Active => _current is not null;

    /// <summary>For tests: the video in progress and its windows.</summary>
    internal static VideoCapture? Current => _current;
    internal VideoPanel? Panel => _panel;
    internal CaptureFrame? Frame => _frame;
    internal RecordingPill? Pill => _pill;
    internal ScreenRecorder? Recorder => _recorder;
    internal PixelRect Area => _area;
    internal VideoTarget Kind => _kind;
    internal Stage State { get; private set; }

    /// <summary>For tests: made-up frames instead of the screen.</summary>
    internal static Func<PixelRect, IFrameGrabber>? GrabberOverride { get; set; }

    /// <summary>For tests: the window in front and the pointer at the start.</summary>
    internal static (PixelRect Window, PixelPoint Pointer)? StartOverride { get; set; }

    /// <summary>For tests: called with the finished recording instead of opening the result window.</summary>
    internal static Action<RecordingResult>? FinishedOverride { get; set; }

    /// <summary>For tests: where recordings are saved.</summary>
    internal static string? FolderOverride { get; set; }

    /// <summary>For tests: what moving or resizing the frame to <paramref name="area"/> does.</summary>
    internal void ChangeArea(PixelRect area) => OnAreaChanged(area);

    /// <summary>
    /// Starts a video on <paramref name="area"/> (or the window in front). While one is running this
    /// is the shortcut pressed again: it starts recording, or stops it.
    /// </summary>
    public static void Start(ToolboxHost host, LongshotTool tool, VideoTarget kind = VideoTarget.Window, PixelRect? area = null, PixelRect? window = null)
    {
        if (_current is not null)
        {
            _current.ShortcutAgain();
            return;
        }
        LongshotCapture.CancelActive(); // one Longshot at a time
        _current = new VideoCapture(host, tool);
        _current.Open(kind, area, window);
    }

    public static void CancelActive() => _current?.Cancel();

    /// <summary>The shortcut while a video is open: frame → record → stop.</summary>
    public static void ShortcutAgainActive() => _current?.ShortcutAgain();

    private void ShortcutAgain()
    {
        switch (State)
        {
            case Stage.Setup when _picker is not null:
                _picker.Dispose();
                EndPick();
                _panel?.SetTarget(_kind);
                break;
            case Stage.Setup: Record(); break;
            case Stage.Countdown: BackToSetup(); break;
            case Stage.Recording: _ = Stop(); break;
        }
    }

    private LongshotSettings Settings => _host.Settings.Current.Longshot;

    private void Save(Action<LongshotSettings> edit)
    {
        var next = _host.Settings.Current.Clone();
        edit(next.Longshot);
        _host.Settings.Save(next);
    }

    private void Open(VideoTarget kind, PixelRect? given, PixelRect? window)
    {
        PixelPoint pointer;
        try
        {
            if (StartOverride is { } start) (_window, pointer) = start;
            else
            {
                pointer = PixelPoint.From(ScreenGeometry.CursorPosition());
                _window = window ?? TargetFinder.ForegroundWindow()?.Rect ?? TargetFinder.WorkAreaAt(pointer);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
        {
            Fail(Lang.T("Longshot couldn't find the window", "تعذّر على اللقطة الطويلة إيجاد النافذة"), ex.Message);
            return;
        }
        var area = given ?? (kind == VideoTarget.Screen ? ScreenAt(pointer) : _window);
        Log.Info($"Longshot video on {kind} {area.Width}x{area.Height} at {area.X},{area.Y}.");
        State = Stage.Setup;
        if (Settings.DimOutside && !VideoPanel.KeepOffScreen)
        {
            _scrim = new CaptureScrim { Strength = DimStrength };
            _scrim.Show(area);
        }
        string? shortcut = null;
        try { shortcut = App.ShortcutText(LongshotTool.ToolId, "video"); } catch (Exception) { /* not running as the app (tests) */ }
        _panel = new VideoPanel(() => Settings, Save, shortcut);
        _panel.RecordButton.Click += (_, _) => Record();
        _panel.CloseButton.Click += (_, _) => Cancel();
        _panel.TargetChosen += OnTargetChosen;
        _panel.ScreenshotChosen += SwitchToScreenshot;
        ShowArea(kind, area);
        _panel.SetStatus(Lang.T("Move or resize the frame if needed, then press Record.", "حرّك الإطار أو غيّر حجمه إن لزم، ثم اضغط «تسجيل»."));
        _panel.Show();
    }

    /// <summary>The whole screen (monitor) at <paramref name="p"/>, taskbar included.</summary>
    private static PixelRect ScreenAt(PixelPoint p) => PixelRect.From(ScreenGeometry.MonitorAt(p.ToPoint()).Bounds);

    /// <summary>Puts the frame (and the dim layer's hole) on the area, and tells the panel.</summary>
    private void ShowArea(VideoTarget kind, PixelRect area)
    {
        _kind = kind;
        _area = area;
        var frameKind = kind == VideoTarget.Window ? TargetKind.Window : TargetKind.Region;
        if (_frame is null)
        {
            _frame = new CaptureFrame(area, null, frameKind);
            _frame.SetMode(FrameMode.Video);
            _frame.AreaChanged += OnAreaChanged;
            _frame.Moving += a => _scrim?.SetHole(a);
            _frame.Show();
        }
        else
        {
            _frame.SetKind(frameKind);
            _frame.AnimateTo(area);
        }
        _scrim?.SetHole(area);
        _panel?.SetTarget(kind);
        _panel?.SetArea(area);
        _panel?.PlaceBeside(area);
    }

    /// <summary>The frame was moved or resized by hand: that is the area now (a region).</summary>
    private void OnAreaChanged(PixelRect area)
    {
        if (_ended || State != Stage.Setup) return;
        _kind = VideoTarget.Region;
        _area = area;
        _scrim?.SetHole(area);
        _panel?.SetTarget(VideoTarget.Region);
        _panel?.SetArea(area);
    }

    private void OnTargetChosen(VideoTarget kind)
    {
        if (_ended || State != Stage.Setup)
        {
            _panel?.SetTarget(_kind);
            return;
        }
        if (_picker is not null && kind != VideoTarget.Region) { _picker.Dispose(); EndPick(); }
        switch (kind)
        {
            case VideoTarget.Window: ShowArea(VideoTarget.Window, _window); break;
            case VideoTarget.Screen: ShowArea(VideoTarget.Screen, ScreenAt(_area.Center)); break;
            default: StartPick(); break;
        }
    }

    private void StartPick()
    {
        _picker?.Dispose();
        var scrim = _scrim ?? (_pickScrim = new CaptureScrim());
        _frame?.SetHidden(true);
        _panel?.SetTarget(VideoTarget.Region);
        _panel?.SetStatus(Lang.T("Drag to draw the area to record. Esc goes back.", "اسحب لرسم المنطقة المراد تسجيلها. Esc للرجوع."));
        _picker = new AreaPicker(TargetKind.Region, scrim,
            picked: t =>
            {
                EndPick();
                ShowArea(VideoTarget.Region, t.Area);
                _panel?.SetStatus(Lang.T("Region chosen. Press Record when you're ready.", "اختيرت المنطقة. اضغط «تسجيل» عندما تكون جاهزًا."));
            },
            cancelled: () =>
            {
                EndPick();
                _panel?.SetTarget(_kind);
                _panel?.SetStatus(Lang.T("Kept the area as it was.", "بقيت المنطقة كما هي."));
            });
        _picker.Start();
    }

    private void EndPick()
    {
        _picker = null;
        _frame?.SetHidden(false);
        _pickScrim?.Dispose();
        _pickScrim = null;
        if (_scrim is not null)
        {
            _scrim.Strength = DimStrength;
            _scrim.Show(_area);
        }
    }

    /// <summary>The Long screenshot side of the switch: the same area, as a long screenshot.</summary>
    private void SwitchToScreenshot()
    {
        if (_ended || State != Stage.Setup) return;
        var (kind, area) = (_kind, _area);
        End();
        LongshotCapture.Start(_host, _tool, kind == VideoTarget.Window ? new CaptureTarget(TargetKind.Window, area, area.Center) : new CaptureTarget(TargetKind.Region, area, area.Center));
    }

    /// <summary>Record: the panel goes away, then 3, 2, 1 (unless switched off), then recording.</summary>
    private void Record()
    {
        if (_ended || State != Stage.Setup || _picker is not null) return;
        var area = Even(_area);
        if (area.Width < 32 || area.Height < 32)
        {
            _panel?.SetStatus(Lang.T("The area is too small to record. Make the frame bigger.", "المنطقة أصغر من أن تُسجَّل. كبّر الإطار."));
            return;
        }
        _area = area;
        _panel?.Hide();
        _frame?.SetMode(FrameMode.Recording);
        _pill = new RecordingPill();
        _pill.PauseButton.Click += (_, _) => TogglePause();
        _pill.StopButton.Click += (_, _) => _ = Stop();
        _pill.DiscardButton.Click += (_, _) => Discard();
        _pill.PlaceNear(area);
        if (!Settings.VideoCountdown)
        {
            _pill.Show();
            _ = BeginRecording();
            return;
        }
        State = Stage.Countdown;
        int left = 3;
        ShowCountdown(left);
        _pill.Show();
        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) =>
        {
            if (_ended || State != Stage.Countdown) { _countdown?.Stop(); return; }
            if (--left > 0) { ShowCountdown(left); return; }
            _countdown.Stop();
            _countdown = null;
            _ = BeginRecording();
        };
        _countdown.Start();
    }

    private void ShowCountdown(int left)
    {
        string text = Lang.T($"Recording in {left}…", $"التسجيل بعد {left}…");
        _frame?.SetLabel(text);
        _pill?.SetCountdown(left);
    }

    /// <summary>The countdown was stopped (Discard or the shortcut): back to the panel, nothing recorded.</summary>
    private void BackToSetup()
    {
        _countdown?.Stop();
        _countdown = null;
        _pill?.Close();
        _pill = null;
        State = Stage.Setup;
        _frame?.SetMode(FrameMode.Video);
        _frame?.SetLabel(null);
        _panel?.Show();
        _panel?.SetStatus(Lang.T("Not recording. Press Record when you're ready.", "لا تسجيل. اضغط «تسجيل» عندما تكون جاهزًا."));
    }

    /// <summary>Even sizes: the H.264 encoder needs them.</summary>
    internal static PixelRect Even(PixelRect r) => r with { Width = r.Width & ~1, Height = r.Height & ~1 };

    private async Task BeginRecording()
    {
        if (_ended) return;
        State = Stage.Starting;
        var settings = Settings;
        var area = _area;
        double scale = ScreenGeometry.MonitorAt(area.Center.ToPoint()).Scale;
        string folder = FolderOverride ?? (settings.VideoFolder is { Length: > 0 } f ? f : VideoFiles.DefaultFolder);
        try
        {
            var grabber = GrabberOverride?.Invoke(area) ?? new RegionGrabber(area, settings.VideoCursor, settings.VideoClicks, scale);
            string path = VideoFiles.NewPath(folder, DateTime.Now);
            _recorder = new ScreenRecorder(grabber, path, new RecordingOptions(settings.VideoFps == 60 ? 60 : 30, settings.VideoCursor, settings.VideoClicks));
            _recorder.Failed += message => _ui.BeginInvoke(() => OnEncoderFailed(message));
            await _recorder.StartAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or COMException)
        {
            Log.Error("Longshot video could not start", ex);
            _recorder?.Dispose();
            _recorder = null;
            Fail(Lang.T("The recording couldn't start", "تعذّر بدء التسجيل"), ex.Message);
            return;
        }
        if (_ended)
        {
            await _recorder.DiscardAsync();
            _recorder.Dispose();
            return;
        }
        State = Stage.Recording;
        Log.Info($"Longshot video recording {area.Width}x{area.Height} at {settings.VideoFps} fps to {_recorder.Path}.");
        Tick();
        _ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _ticker.Tick += (_, _) => Tick();
        _ticker.Start();
    }

    private void Tick()
    {
        if (_recorder is null) return;
        var t = _recorder.Elapsed;
        string time = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        _frame?.SetLabel(_recorder.Paused ? Lang.T($"Paused  {time}", $"متوقف مؤقتًا  {time}") : $"● REC  {time}");
        _pill?.SetTime(t, _recorder.Paused);
    }

    private void TogglePause()
    {
        if (State != Stage.Recording || _recorder is null) return;
        if (_recorder.Paused) _recorder.Resume();
        else _recorder.Pause();
        Tick();
    }

    /// <summary>Stop: the encoder finishes the file, then the result window opens.</summary>
    private async Task Stop()
    {
        if (State != Stage.Recording || _recorder is null) return;
        State = Stage.Stopping;
        _ticker?.Stop();
        _pill?.SetSaving();
        _frame?.SetLabel(Lang.T("Saving…", "جارٍ الحفظ…"));
        RecordingResult result;
        try
        {
            result = await _recorder.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Longshot video could not be finished", ex);
            Fail(Lang.T("The recording couldn't be saved", "تعذّر حفظ التسجيل"), ex.Message);
            return;
        }
        _recorder.Dispose();
        _recorder = null;
        Log.Info($"Longshot video saved {result.Path} ({result.Width}x{result.Height}, {result.Duration.TotalSeconds:0.0} s, {result.Frames} frames, {result.Dropped} dropped).");
        CloseWindows();
        if (FinishedOverride is { } finished) finished(result);
        else
        {
            var window = new VideoResultWindow(result, () => _tool.RecordVideo(), App.IsToolEnabled(FileToolboxTool.ToolId));
            window.Show();
            Foreground.Activate(window);
        }
        End();
    }

    /// <summary>Discard: during the countdown, back to the panel; while recording, the file is deleted.</summary>
    private void Discard()
    {
        if (State == Stage.Countdown) { BackToSetup(); return; }
        if (State != Stage.Recording) return;
        _ = DiscardAndEnd();
    }

    private async Task DiscardAndEnd()
    {
        State = Stage.Stopping;
        _ticker?.Stop();
        _pill?.SetSaving();
        if (_recorder is { } recorder)
        {
            _recorder = null;
            await recorder.DiscardAsync();
            recorder.Dispose();
            Log.Info("Longshot video discarded.");
        }
        End();
    }

    /// <summary>The encoder stopped by itself: keep what was written, and say so.</summary>
    private void OnEncoderFailed(string message)
    {
        if (_ended) return;
        if (_recorder is { } recorder)
        {
            _recorder = null;
            _ = recorder.StopAsync().ContinueWith(_ => recorder.Dispose(), TaskScheduler.Default);
        }
        Fail(Lang.T("The recording stopped", "توقف التسجيل"), message);
    }

    private void Cancel()
    {
        if (_recorder is { } recorder)
        {
            _recorder = null;
            _ = recorder.DiscardAsync().ContinueWith(_ => recorder.Dispose(), TaskScheduler.Default);
        }
        End();
    }

    private void Fail(string title, string message)
    {
        var toast = new NoticeToast(title, message, warning: true);
        toast.PlaceNear(ScreenGeometry.CursorPosition(), 24);
        toast.Show();
        if (_recorder is { } recorder)
        {
            _recorder = null;
            recorder.Dispose();
        }
        End();
    }

    private void CloseWindows()
    {
        _countdown?.Stop();
        _ticker?.Stop();
        _picker?.Dispose();
        _picker = null;
        _pill?.Close();
        _panel?.Close();
        _frame?.Close();
        _scrim?.Dispose();
        _pickScrim?.Dispose();
        _countdown = null;
        _ticker = null;
        _pill = null;
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
}
