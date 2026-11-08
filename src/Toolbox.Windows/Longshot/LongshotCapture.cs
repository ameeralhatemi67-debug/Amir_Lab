using System.Windows;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Longshot;

/// <summary>
/// One capture from start to finish: optional countdown → choose the area → frame + panel + capture
/// session → save, copy and show the result. Only one runs at a time.
/// </summary>
internal sealed class LongshotCapture
{
    private static LongshotCapture? _current;

    private readonly ToolboxHost _host;
    private readonly LongshotTool _tool;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private SelectionOverlay? _overlay;
    private CaptureFrame? _frame;
    private CapturePanel? _panel;
    private CaptureSession? _session;
    private CountdownToast? _countdown;
    private bool _ready;
    private SessionCommand? _busy;
    private bool _canUndo;
    private bool _ended;

    private LongshotCapture(ToolboxHost host, LongshotTool tool)
    {
        _host = host;
        _tool = tool;
    }

    public static bool Active => _current is not null;

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
            Next(area);
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
            After(TimeSpan.FromMilliseconds(150), () => Next(area));
        };
        timer.Start();
    }

    private void Next(PixelRect? area)
    {
        if (area is { } a) StartSession(a);
        else SelectArea();
    }

    private void SelectArea()
    {
        try
        {
            var (shot, bounds) = GdiScreenSource.CaptureVirtualScreen();
            _overlay = new SelectionOverlay(shot, bounds);
            _overlay.Selected += area => After(TimeSpan.FromMilliseconds(150), () => StartSession(area)); // let the screen repaint first
            _overlay.Cancelled += End;
            _overlay.Show();
        }
        catch (Exception ex)
        {
            Fail("The screen could not be read", ex);
        }
    }

    private void StartSession(PixelRect area)
    {
        if (_ended) return;
        _overlay = null;
        _tool.LastArea = area;
        Log.Info($"Longshot area {area.Width}x{area.Height} at {area.X},{area.Y}.");

        _frame = new CaptureFrame(area);
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
        _panel.PlaceBeside(area);
        _panel.Show();
        SetBusy(null);

        var settings = _host.Settings.Current.Longshot;
        double scale = ScreenGeometry.MonitorAt(new Native.POINT { X = area.CenterX, Y = area.CenterY }).Scale;
        _session = new CaptureSession(() => new GdiScreenSource(area), new WheelScroller(area), area.Height, settings.StepFraction,
            (int)(CapturePanel.PreviewWidth * scale), (int)(CapturePanel.PreviewHeight * scale),
            e => _ui.BeginInvoke(() => Handle(e)));
        _session.Start();
    }

    private void Handle(SessionEvent e)
    {
        if (_ended && e is not SessionFinished) return;
        switch (e)
        {
            case SessionReady:
                _ready = true;
                SetBusy(null);
                switch (_host.Settings.Current.Longshot.AfterSelect)
                {
                    case "auto": Toggle(SessionCommand.Auto); break;
                    case "follow": Toggle(SessionCommand.Follow); break;
                }
                break;
            case SessionBusy busy:
                if (_busy == SessionCommand.Finish) return;
                SetBusy(busy.Command);
                break;
            case SessionUpdate u:
                _panel?.ShowUpdate(u.Preview, u.Width, u.Height, u.Parts, u.Message);
                _canUndo = u.CanUndo;
                SetBusy(_busy);
                break;
            case SessionFinished f:
                _ = Complete(f);
                break;
            case SessionFailed failed:
                Fail("Longshot stopped", new InvalidOperationException(failed.Message));
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
        _panel?.SetStatus("Saving…");
    }

    private void SetBusy(SessionCommand? state)
    {
        _busy = state;
        if (_panel is null) return;
        bool idle = _ready && state is null;
        _panel.Extend.IsEnabled = _ready && state is null or SessionCommand.Extend;
        _panel.Auto.Content = state == SessionCommand.Auto ? "■   Stop" : "▶   Auto";
        _panel.Auto.IsEnabled = _ready && state is null or SessionCommand.Auto;
        _panel.Follow.Content = state == SessionCommand.Follow ? "■   Stop" : "◎   Follow";
        _panel.Follow.IsEnabled = _ready && state is null or SessionCommand.Follow;
        _panel.Undo.IsEnabled = idle && _canUndo;
        _panel.Done.IsEnabled = _ready && state != SessionCommand.Finish;
        _frame?.SetMode(state switch { SessionCommand.Auto => FrameMode.Auto, SessionCommand.Follow => FrameMode.Follow, _ => FrameMode.Idle });
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
                var toast = new NoticeToast("Long screenshot saved", IOPath.GetFileName(path) + (copied ? " · copied to the clipboard" : ""), warning: false);
                toast.PlaceNear(ScreenGeometry.CursorPosition(), 24);
                toast.Show();
            }
        }
        catch (Exception ex)
        {
            Fail("The screenshot could not be saved", ex);
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
        _overlay?.Close();
        _countdown?.Close();
        _panel?.Close();
        _frame?.Close();
        _overlay = null;
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
