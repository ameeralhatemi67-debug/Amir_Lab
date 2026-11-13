using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Inputs;
using Toolbox.Core.Recipes;
using Toolbox.Core.Settings;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Wheel;

namespace Toolbox.Windows.Drag;

/// <summary>A segment chosen by dropping the real dragged files onto it.</summary>
internal sealed record WheelDropRequest(string ActionId, IReadOnlyList<string> Paths, Native.POINT ScreenPoint);

/// <summary>
/// Turns "activation key tapped during a file drag" into the wheel.
///
/// 1. The keyboard hook sees the key while a mouse button is down.
/// 2. An invisible but hit-testable overlay is placed under the pointer and the pointer is nudged,
///    so the OLE drag loop re-evaluates the window under it.
/// 3. Only when OLE delivers DragEnter with a real file payload is the wheel revealed.
/// 4. Drops always answer COPY (never MOVE), so the source is never moved by the drag source.
/// </summary>
internal sealed class DragActivationService : IDisposable
{
    private enum State { Idle, Probing, Open }

    private readonly KeyboardHook _hook = new();
    private readonly WheelOverlayWindow _overlay;
    private readonly Func<AppSettings> _settings;
    private readonly Func<IReadOnlyList<Recipe>> _recipes;
    private readonly DiagnosticsChannel _diagnostics;
    private readonly DispatcherTimer _probeTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };
    private readonly DispatcherTimer _buttonPoll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    // After the button goes up, OLE still has to deliver Drop/DragLeave; give it time before closing.
    private readonly DispatcherTimer _releaseGrace = new() { Interval = TimeSpan.FromMilliseconds(350) };
    // The hub (Preview) takes drops after this, or once the pointer has left it.
    private readonly DispatcherTimer _hubTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };

    private State _state = State.Idle;
    private long _activationTimestamp;
    private IReadOnlyList<ProbedFile> _payload = [];
    private IReadOnlyList<string> _paths = [];
    private IReadOnlyList<WheelRingModel> _rings = [];
    private int _ringIndex;
    private bool _reducedMotion;

    public DragActivationService(WheelOverlayWindow overlay, Func<AppSettings> settings,
        Func<IReadOnlyList<Recipe>> recipes, DiagnosticsChannel diagnostics)
    {
        _overlay = overlay;
        _settings = settings;
        _recipes = recipes;
        _diagnostics = diagnostics;

        _probeTimer.Tick += (_, _) => { if (_state == State.Probing) Close("probe-timeout"); };
        _leaveTimer.Tick += (_, _) => Close("left-wheel");
        _releaseGrace.Tick += (_, _) => Close("button-released");
        _buttonPoll.Tick += (_, _) => OnButtonPoll();
        _hubTimer.Tick += (_, _) =>
        {
            _hubTimer.Stop();
            if (_state == State.Open) _overlay.Menu.HubArmed = true;
        };

        _overlay.DragEnter += OnDragEnter;
        _overlay.DragOver += OnDragOver;
        _overlay.DragLeave += OnDragLeave;
        _overlay.Drop += OnDrop;
    }

    /// <summary>Raised after the user drops onto an enabled segment.</summary>
    public event Action<WheelDropRequest>? Dropped;

    /// <summary>Milliseconds from the activation key to the wheel being revealed, for the last activation.</summary>
    public double LastRevealLatencyMs { get; private set; }

    public void Start()
    {
        _hook.KeyPressed += OnKeyPressed;
        _hook.Install();
        Log.Info("Drag activation ready (keyboard hook installed).");
    }

    public void Dispose() => _hook.Dispose();

    /// <summary>Builds and lays out every ring once (invisibly) so the first real reveal is as fast as later ones.</summary>
    public void WarmUp()
    {
        var settings = _settings();
        var menu = _overlay.Menu;
        menu.ApplyPalette(Palette.Current(settings.Theme, settings.Accent));
        menu.SetShare(ShareService.WheelModel(1));
        foreach (var kind in new[] { FileKind.Pdf, FileKind.Png })
        {
            var payload = new[] { new ProbedFile($"C:\\warm-up{kind.Extension()}", kind, 1024, null) };
            var rings = WheelModels.Build(payload, settings, _recipes());
            for (int i = 0; i < rings.Count; i++)
            {
                menu.SetRing(rings[i], i, rings.Count, animate: false);
                menu.SetHover(0);
                menu.SetHover(RadialMenu.ShareHit);
                menu.SetHover(RadialMenu.OutsideHit);
                menu.UpdateLayout();
            }
        }
        _ = Native.IsDragButtonDown();
        _ = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition());
    }

    private void OnKeyPressed(KeyEvent key)
    {
        var settings = _settings();
        if (key.IsEscape)
        {
            if (_state != State.Idle) Close("escape");
            return;
        }
        if (!key.Matches(settings.ActivationKey)) return;

        if (_state == State.Open)
        {
            CycleRing();
            return;
        }
        if (_state == State.Idle && Native.IsDragButtonDown())
            BeginProbe(key);
    }

    private void OnButtonPoll()
    {
        if (_state == State.Idle || Native.IsDragButtonDown()) return;
        if (_state == State.Probing)
        {
            Close("button-released");
        }
        else if (!_releaseGrace.IsEnabled)
        {
            _releaseGrace.Start();
        }
    }

    private void BeginProbe(KeyEvent key)
    {
        var settings = _settings();
        _reducedMotion = Motion.Reduced(settings.ReduceMotion);
        _state = State.Probing;
        _activationTimestamp = key.Timestamp;

        _overlay.ResetVisual();
        _overlay.SetCatching(true);
        _overlay.MoveCenteredOn(ScreenGeometry.CursorPosition());
        Native.NudgePointer();

        _probeTimer.Stop();
        _probeTimer.Start();
        _buttonPoll.Start();
        Log.Info("Activation key during button-down: probing for a file drag.");
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        _leaveTimer.Stop();
        if (_state == State.Idle)
        {
            // A drag wandered into the off-screen/idle window; never accept it.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (_state == State.Probing)
        {
            _probeTimer.Stop();
            if (!e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
            {
                Log.Info($"Drag payload is not a file list (formats: {string.Join(", ", e.Data.GetFormats().Take(6))}).");
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                Close("not-a-file-drag");
                return;
            }
            Reveal(paths);
        }
        UpdateHover(e);
    }

    private void Reveal(string[] paths)
    {
        var settings = _settings();
        _paths = paths;
        _payload = FileProbe.ProbeAll(paths);
        _rings = WheelModels.Build(_payload, settings, _recipes());
        _ringIndex = 0;

        var menu = _overlay.Menu;
        menu.ApplyPalette(Palette.Current(settings.Theme, settings.Accent));
        menu.SetReducedMotion(_reducedMotion);
        var (primary, secondary) = WheelModels.IdleText(_payload);
        menu.SetIdleText(primary, secondary);
        menu.SetShare(ShareService.WheelModel(paths.Length));
        menu.SetHub(WheelModels.Preview(paths.Length));
        menu.SetRing(_rings[0], 0, _rings.Count, animate: false);
        CaptureBackdrop();
        _hubTimer.Stop();
        _hubTimer.Start();

        _state = State.Open;
        _overlay.AnimateIn(_reducedMotion);
        LastRevealLatencyMs = Stopwatch.GetElapsedTime(_activationTimestamp).TotalMilliseconds;
        Log.Info($"Wheel revealed for {paths.Length} item(s) [{string.Join(", ", _payload.Select(p => p.Kind).Distinct())}] in {LastRevealLatencyMs:F0} ms.");
        _overlay.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, WriteWheelDiagnostics);
    }

    /// <summary>
    /// Reads the screen behind the wheel (its window is on screen but still invisible) so the glass
    /// can blur it. In memory only; dropped when the wheel closes.
    /// </summary>
    private void CaptureBackdrop()
    {
        var menu = _overlay.Menu;
        try
        {
            if (!Native.GetWindowRect(_overlay.Handle, out var r) || r.Width <= 0 || r.Height <= 0)
            {
                menu.SetBackdrop(null, default);
                return;
            }
            using var source = new Longshot.GdiScreenSource(Longshot.PixelRect.From(r));
            var frame = source.Grab();
            var picture = Longshot.LongshotOutput.ToBitmap(frame.Width, frame.Height, frame.Pixels);
            double offset = (_overlay.SizeDip - menu.Diameter) / 2;
            menu.SetBackdrop(picture, new Rect(-offset, -offset, _overlay.SizeDip, _overlay.SizeDip));
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException or ArgumentException)
        {
            Log.Warn($"The wheel's frosted background could not be made: {ex.Message}");
            menu.SetBackdrop(null, default);
        }
    }

    private void CycleRing()
    {
        if (_rings.Count < 2) return;
        _ringIndex = (_ringIndex + 1) % _rings.Count;
        _overlay.Menu.SetRing(_rings[_ringIndex], _ringIndex, _rings.Count, animate: true);
        Log.Info($"Ring switched to {_rings[_ringIndex].Name}.");
        _overlay.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, WriteWheelDiagnostics);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (_state != State.Open)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        UpdateHover(e);
    }

    private void UpdateHover(DragEventArgs e)
    {
        var menu = _overlay.Menu;
        int index = menu.HitTest(e.GetPosition(menu));
        if (index != RadialMenu.CenterHit && _state == State.Open) menu.HubArmed = true; // it has left the hub
        menu.SetHover(index);

        // Outside the disc (but still inside the invisible catch area): if the pointer stays out,
        // close and stop catching, so the drag continues to whatever is underneath.
        if (index == RadialMenu.OutsideHit)
        {
            if (!_leaveTimer.IsEnabled) _leaveTimer.Start();
        }
        else
        {
            _leaveTimer.Stop();
        }

        var segment = menu.SegmentAt(index);
        bool canCopy = (e.AllowedEffects & DragDropEffects.Copy) != 0;
        e.Effects = segment is { Enabled: true } && canCopy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (_state == State.Open)
        {
            _overlay.Menu.SetHover(RadialMenu.OutsideHit);
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var menu = _overlay.Menu;
        int index = menu.HitTest(e.GetPosition(menu));
        var segment = menu.SegmentAt(index);
        bool canCopy = (e.AllowedEffects & DragDropEffects.Copy) != 0;
        var accepted = _state == State.Open && segment is { Enabled: true } && canCopy;

        // Never MOVE: the drag source must leave the original exactly where it was.
        e.Effects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        var point = ScreenGeometry.CursorPosition();
        _diagnostics.AppendDrop(new
        {
            time = DateTimeOffset.Now,
            accepted,
            actionId = segment?.ActionId,
            ring = _rings.Count > 0 ? _rings[_ringIndex].Name : null,
            paths = _paths,
            allowedEffects = e.AllowedEffects.ToString(),
            effect = e.Effects.ToString(),
            keyStates = e.KeyStates.ToString(),
            revealLatencyMs = LastRevealLatencyMs,
        });
        Log.Info(accepted
            ? $"Dropped {_paths.Count} item(s) on '{segment!.ActionId}' (allowed {e.AllowedEffects}, answered Copy)."
            : $"Drop ignored (segment {index}, allowed {e.AllowedEffects}).");

        if (accepted)
        {
            menu.Confirm(index);
            var request = new WheelDropRequest(segment!.ActionId, _paths, point);
            Close("dropped");
            Dropped?.Invoke(request);
        }
        else
        {
            Close("dropped-outside");
        }
    }

    private void Close(string reason)
    {
        if (_state == State.Idle) return;
        var wasOpen = _state == State.Open;
        _state = State.Idle;
        _probeTimer.Stop();
        _leaveTimer.Stop();
        _buttonPoll.Stop();
        _releaseGrace.Stop();
        _hubTimer.Stop();
        _payload = [];
        _rings = [];

        if (wasOpen)
        {
            // Stop catching at once so a continuing drag reaches the window underneath.
            _overlay.SetCatching(false);
            _overlay.Menu.IsHitTestVisible = false;
            _overlay.AnimateOut(_reducedMotion, () =>
            {
                _overlay.MoveOffscreen();
                _overlay.Menu.IsHitTestVisible = true;
                _overlay.Menu.SetBackdrop(null, default);
            }, confirmed: reason == "dropped");
        }
        else
        {
            _overlay.SetCatching(false);
            _overlay.MoveOffscreen();
        }
        _diagnostics.WriteWheel(new { visible = false, reason, time = DateTimeOffset.Now });
        Log.Info($"Wheel closed ({reason}).");
    }

    private void WriteWheelDiagnostics()
    {
        if (!_diagnostics.Enabled || _state != State.Open) return;
        var menu = _overlay.Menu;
        var center = _overlay.WheelToScreen(new Point(menu.Center, menu.Center));
        var ring = _rings[_ringIndex];
        _diagnostics.WriteWheel(new
        {
            visible = true,
            ring = ring.Name,
            ringIndex = _ringIndex,
            ringCount = _rings.Count,
            centerX = center.X,
            centerY = center.Y,
            revealLatencyMs = LastRevealLatencyMs,
            segments = ring.Segments.Select((s, i) =>
            {
                var p = _overlay.WheelToScreen(menu.SegmentCenter(i));
                return new { index = i, actionId = s.ActionId, label = s.Label, enabled = s.Enabled, x = p.X, y = p.Y };
            }).ToList(),
            time = DateTimeOffset.Now,
        });
    }
}
