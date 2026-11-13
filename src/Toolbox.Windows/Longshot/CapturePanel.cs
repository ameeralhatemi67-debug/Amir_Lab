using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot;

internal enum FrameMode { Idle, Auto, Follow, Video, Recording }

/// <summary>
/// The glowing frame around the capture area. Hover an edge and a handle appears under the pointer
/// (wiggle and it grows, to make it easy to grab); drag it, or a corner knob, to resize; drag the
/// Move grip (inside the top edge) to move. The round scroll marker shows where the wheel turns,
/// and can be dragged onto whatever should scroll. A label says what is being captured (window,
/// scroll area or region) and how big it is; rows that were just added glow for a moment.
/// Everywhere else clicks and the wheel go straight through to the app below. It never takes focus
/// and is left out of screenshots, so it never ends up in the capture. Resizing stays inside the
/// screen's work area, so the taskbar is never taken in.
/// </summary>
internal sealed class CaptureFrame : Window
{
    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16, Marker = 32 }

    private const double Reach = 22;     // room around the area (DIPs) for the handles
    private const double Line = 3;
    private const double Band = 24;       // how far from the line an edge can be grabbed (both sides together)
    private const int MinSize = 120;      // smallest area, in physical pixels
    private const double MarkerSize = 34; // the scroll marker's ring (its middle is see-through, so the wheel reaches the app)

    private readonly Border _line = new() { BorderThickness = new Thickness(Line), CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
    private readonly DropShadowEffect _glow = new() { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.85 };
    private readonly Grid _handles = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Canvas _markerLayer = new();
    private readonly Border _grab = new() { CornerRadius = new CornerRadius(5), Background = Brushes.White, BorderThickness = new Thickness(2.5), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _grabScale = new(1, 1);
    private readonly TextBlock _kindText = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _sizeText = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _kindIcon = new() { Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Border _sizePill = new() { Height = 26, CornerRadius = new CornerRadius(13), Padding = new Thickness(10, 0, 11, 0), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly Border _added = new() { VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0, IsHitTestVisible = false, CornerRadius = new CornerRadius(0, 0, 4, 4), BorderThickness = new Thickness(0, 2, 0, 0) };
    private readonly Grid _marker = new() { Width = MarkerSize, Height = MarkerSize, Cursor = Cursors.SizeAll, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _markerScale = new(1, 1);
    private readonly Ellipse _markerPulse = new() { Width = MarkerSize, Height = MarkerSize, StrokeThickness = 2, Opacity = 0, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly DispatcherTimer _calm = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer _tween = new() { Interval = TimeSpan.FromMilliseconds(15) };
    private PixelRect _area;
    private PixelPoint _anchor;
    private IntPtr _hwnd;
    private double _scale = 1;
    private Edge _drag;
    private Edge _hover;
    private PixelRect _dragFrom;
    private PixelPoint _anchorFrom;
    private Native.POINT _cursorFrom;
    private Point? _lastPointer;
    private double _wiggle; // how much the pointer moved near an edge lately (makes the handle grow)
    private Rect _limits;    // the work area (physical pixels) a resize stays inside
    private PixelRect _tweenFrom, _tweenTo;
    private DateTime _tweenStart;
    private bool _hidden;
    private FrameMode _mode;

    public CaptureFrame(PixelRect area, PixelPoint? anchor = null, TargetKind kind = TargetKind.Window)
    {
        _area = area;
        _anchor = area.Clamp(anchor ?? area.Center, 18);
        Title = "Longshot frame";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent; // fully transparent: clicks pass through to the app below
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;

        _line.Margin = new Thickness(Reach - Line);
        _line.Effect = _glow;
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = Lang.Flow };
        pillRow.Children.Add(_kindIcon);
        pillRow.Children.Add(_kindText);
        pillRow.Children.Add(SettingsPage.Ltr(_sizeText));
        _sizePill.Child = pillRow;
        _sizePill.Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x10, 0x12, 0x18));
        _grab.RenderTransform = _grabScale;
        _grab.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.4 };
        _grab.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        _overlay.Children.Add(_grab);
        _added.Margin = new Thickness(Reach, 0, Reach, Reach);
        BuildMarker();
        var root = new Grid();
        root.Children.Add(_added);
        root.Children.Add(_line);
        BuildHandles();
        root.Children.Add(_handles);
        root.Children.Add(_markerLayer);
        root.Children.Add(_overlay);
        Content = root;
        SetKind(kind);
        SetMode(FrameMode.Idle);
        UpdateSizeText();
        _calm.Tick += (_, _) => Calm();
        _tween.Tick += (_, _) => TweenStep();
        SizeChanged += (_, _) => PlaceMarker();
        PlaceMarker();
    }

    /// <summary>Raised when a move or resize by hand ends, with the new area (physical pixels).</summary>
    public event Action<PixelRect>? AreaChanged;

    /// <summary>Raised whenever the area on screen changes (dragging, or gliding to a new area): the dim layer follows it.</summary>
    public event Action<PixelRect>? Moving;

    /// <summary>Raised when the scroll marker is dropped somewhere new (physical pixels).</summary>
    public event Action<PixelPoint>? AnchorChanged;

    public PixelRect Area => _area;

    public PixelPoint Anchor => _anchor;

    public void SetMode(FrameMode mode)
    {
        _mode = mode;
        var color = mode switch
        {
            FrameMode.Auto => Color.FromRgb(0xFF, 0xB2, 0x24),
            FrameMode.Follow => Color.FromRgb(0x30, 0xC2, 0x7A),
            FrameMode.Recording => Color.FromRgb(0xEF, 0x44, 0x44),
            _ => ThemeResources.Lap.Accent,
        };
        _line.BorderBrush = new SolidColorBrush(color);
        _glow.Color = color;
        _added.BorderBrush = new SolidColorBrush(color);
        _added.Background = new LinearGradientBrush(Color.FromArgb(0x10, color.R, color.G, color.B), Color.FromArgb(0x48, color.R, color.G, color.B), 90);
        _markerPulse.Stroke = new SolidColorBrush(color);
        // While Longshot scrolls or follows, the area stays put; in Follow you scroll, so no marker.
        // Video: the area can still be moved and resized, but nothing scrolls, so no marker.
        bool idle = mode is FrameMode.Idle or FrameMode.Video;
        _handles.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        _marker.IsHitTestVisible = mode == FrameMode.Idle;
        _marker.Visibility = mode is FrameMode.Video or FrameMode.Recording ? Visibility.Collapsed : Visibility.Visible;
        _marker.Opacity = mode == FrameMode.Follow ? 0 : 1;
        if (!idle) HideGrab();
        // Auto and Recording: the glow breathes while it works.
        if (mode is FrameMode.Auto or FrameMode.Recording && !Motion.IsReduced)
            _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(12, 26, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
        else _glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
    }

    /// <summary>Text instead of the size in the label at the bottom ("● REC 0:12"); null puts the size back.</summary>
    public void SetLabel(string? text)
    {
        _label = text;
        UpdateSizeText();
    }

    private string? _label;

    /// <summary>Window, scroll area or region: shown in the label at the bottom.</summary>
    public void SetKind(TargetKind kind)
    {
        _kindText.Text = PickHighlight.KindText(kind);
        _kindIcon.Content = SettingsPage.Glyph(PickHighlight.KindIcon(kind), 13, brush: Brushes.White, stroke: 2);
    }

    /// <summary>Hides the frame while another area is being picked (and shows it again).</summary>
    public void SetHidden(bool hidden)
    {
        _hidden = hidden;
        Place();
    }

    /// <summary>Where the wheel turns now (it moved, or it was found by trying other spots).</summary>
    public void SetAnchor(PixelPoint at)
    {
        _anchor = _area.Clamp(at, 18);
        PlaceMarker();
        Pop(_marker, 1.25);
        After(220, () => Pop(_marker, 1));
    }

    /// <summary>The wheel is turning at the marker: a ring spreads from it.</summary>
    public void PulseAnchor()
    {
        if (Motion.IsReduced || _mode == FrameMode.Follow) return;
        var grow = new DoubleAnimation(0.6, 2.1, TimeSpan.FromMilliseconds(520)) { EasingFunction = Motion.Out };
        _markerPulse.RenderTransform = new ScaleTransform(1, 1);
        ((ScaleTransform)_markerPulse.RenderTransform).BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        ((ScaleTransform)_markerPulse.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        _markerPulse.BeginAnimation(OpacityProperty, new DoubleAnimation(0.9, 0, TimeSpan.FromMilliseconds(520)));
    }

    /// <summary>The bottom <paramref name="rows"/> pixel rows are new: they glow, then fade.</summary>
    public void FlashAdded(int rows)
    {
        if (rows <= 0) return;
        double inner = Math.Max(6, _line.ActualHeight - 2 * Line); // the area's height on screen
        _added.Height = Math.Clamp(rows / _scale, 6, inner);
        _added.Opacity = Motion.IsReduced ? 0 : 1;
        _added.BeginAnimation(OpacityProperty, Motion.IsReduced ? null : new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1100)) { EasingFunction = Motion.InOut });
    }

    /// <summary>Glides to a new area (the part that scrolls was found, or another one was picked).</summary>
    public void AnimateTo(PixelRect area, PixelPoint? anchor = null)
    {
        if (anchor is { } a) _anchor = area.Clamp(a, 18);
        else if (!area.Contains(_anchor)) _anchor = area.Center;
        if (_hwnd == IntPtr.Zero || Motion.IsReduced || area == _area)
        {
            _tween.Stop();
            _area = area;
            Place();
            UpdateSizeText();
            return;
        }
        _tweenFrom = _area;
        _tweenTo = area;
        _tweenStart = DateTime.UtcNow;
        _tween.Start();
    }

    private void TweenStep()
    {
        double t = Math.Clamp((DateTime.UtcNow - _tweenStart).TotalMilliseconds / 300, 0, 1);
        double e = Motion.Out.Ease(t);
        int L(int from, int to) => (int)Math.Round(from + (to - from) * e);
        int l = L(_tweenFrom.X, _tweenTo.X), tp = L(_tweenFrom.Y, _tweenTo.Y), r = L(_tweenFrom.Right, _tweenTo.Right), b = L(_tweenFrom.Bottom, _tweenTo.Bottom);
        _area = t >= 1 ? _tweenTo : new PixelRect(l, tp, r - l, b - tp);
        Place();
        UpdateSizeText();
        if (t >= 1) _tween.Stop();
    }

    private void BuildMarker()
    {
        // A ring: white core with an accent edge, see-through in the middle (the wheel goes through there).
        var outer = new Ellipse { Width = MarkerSize - 4, Height = MarkerSize - 4, StrokeThickness = 3.2, Stroke = Brushes.White, Fill = new SolidColorBrush(Color.FromArgb(0x01, 0xFF, 0xFF, 0xFF)) };
        var edge = new Ellipse { Width = MarkerSize - 1, Height = MarkerSize - 1, StrokeThickness = 1.6, Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.45 } };
        edge.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        var inner = new Ellipse { Width = 13, Height = 13, StrokeThickness = 1.6 };
        inner.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        // Clear the middle completely: per-pixel see-through windows let input through where nothing is drawn.
        var ring = new Grid { Width = MarkerSize, Height = MarkerSize };
        ring.Children.Add(edge);
        ring.Children.Add(outer);
        ring.Children.Add(inner);
        ring.Clip = Geometry.Combine(new EllipseGeometry(new Point(MarkerSize / 2, MarkerSize / 2), MarkerSize / 2, MarkerSize / 2),
            new EllipseGeometry(new Point(MarkerSize / 2, MarkerSize / 2), 5, 5), GeometryCombineMode.Exclude, null);
        _marker.Children.Add(ring);
        _marker.RenderTransform = _markerScale;
        _marker.ToolTip = Lang.T("Longshot scrolls here. Drag it onto the part that should scroll.", "هنا يمرّر Longshot. اسحبها فوق الجزء الذي يجب أن يتمرّر.");
        _marker.MouseEnter += (_, _) => Pop(_marker, 1.15);
        _marker.MouseLeave += (_, _) => { if (_drag == Edge.None) Pop(_marker, 1); };
        _marker.MouseLeftButtonDown += (_, e) =>
        {
            _drag = Edge.Marker;
            _anchorFrom = _anchor;
            _cursorFrom = ScreenGeometry.CursorPosition();
            _marker.CaptureMouse();
            e.Handled = true;
        };
        _marker.MouseMove += (_, _) =>
        {
            if (_drag != Edge.Marker) return;
            var now = ScreenGeometry.CursorPosition();
            _anchor = _area.Clamp(new PixelPoint(_anchorFrom.X + now.X - _cursorFrom.X, _anchorFrom.Y + now.Y - _cursorFrom.Y), 18);
            PlaceMarker();
        };
        void Drop()
        {
            if (_drag != Edge.Marker) return;
            _drag = Edge.None;
            if (_marker.IsMouseCaptured) _marker.ReleaseMouseCapture();
            Pop(_marker, 1);
            if (_anchor != _anchorFrom) AnchorChanged?.Invoke(_anchor);
        }
        _marker.MouseLeftButtonUp += (_, _) => Drop();
        _marker.LostMouseCapture += (_, _) => Drop();
        _markerLayer.Children.Add(_markerPulse);
        _markerLayer.Children.Add(_marker);
    }

    private void PlaceMarker()
    {
        double x = Reach + (_anchor.X - _area.X) / _scale - MarkerSize / 2;
        double y = Reach + (_anchor.Y - _area.Y) / _scale - MarkerSize / 2;
        Canvas.SetLeft(_marker, x);
        Canvas.SetTop(_marker, y);
        Canvas.SetLeft(_markerPulse, x);
        Canvas.SetTop(_markerPulse, y);
    }

    private void BuildHandles()
    {
        double edge = Reach - Line / 2;      // where the line runs, from the window's edge
        var catchAll = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)); // invisible but grabbable
        catchAll.Freeze();

        void Add(FrameworkElement e, Edge what, Cursor cursor)
        {
            e.Cursor = cursor;
            e.MouseLeftButtonDown += (_, args) => Begin(e, what, args);
            e.MouseMove += (_, args) => { Hover(what, args.GetPosition(_overlay)); Drag(); };
            e.MouseEnter += (_, args) => Hover(what, args.GetPosition(_overlay));
            e.MouseLeave += (_, _) => { if (_drag == Edge.None) Leave(what); };
            e.MouseLeftButtonUp += (_, _) => End(e);
            e.LostMouseCapture += (_, _) => End(e);
            _handles.Children.Add(e);
        }

        // Edges: invisible bands over the line (mostly outside the area), where a handle appears.
        double inset = edge + 14;
        Add(new Border { Background = catchAll, Height = Band, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(inset, edge - Band * 0.6, inset, 0) }, Edge.Top, Cursors.SizeNS);
        Add(new Border { Background = catchAll, Height = Band, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(inset, 0, inset, edge - Band * 0.6) }, Edge.Bottom, Cursors.SizeNS);
        Add(new Border { Background = catchAll, Width = Band, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(edge - Band * 0.6, inset, 0, inset) }, Edge.Left, Cursors.SizeWE);
        Add(new Border { Background = catchAll, Width = Band, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, inset, edge - Band * 0.6, inset) }, Edge.Right, Cursors.SizeWE);

        // Corners: glass knobs.
        foreach (var (h, v, what, cursor) in new[]
        {
            (HorizontalAlignment.Left, VerticalAlignment.Top, Edge.Left | Edge.Top, Cursors.SizeNWSE),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom, Edge.Right | Edge.Bottom, Cursors.SizeNWSE),
            (HorizontalAlignment.Right, VerticalAlignment.Top, Edge.Right | Edge.Top, Cursors.SizeNESW),
            (HorizontalAlignment.Left, VerticalAlignment.Bottom, Edge.Left | Edge.Bottom, Cursors.SizeNESW),
        })
        {
            var knob = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Brushes.White,
                BorderThickness = new Thickness(2.5),
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Margin = new Thickness(edge - 9),
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35 },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            knob.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
            knob.MouseEnter += (_, _) => Pop(knob, 1.35);
            knob.MouseLeave += (_, _) => { if (_drag == Edge.None) Pop(knob, 1); };
            Add(knob, what, cursor);
        }

        // The move grip, just inside the top edge, so it can always be reached.
        var grip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        grip.Children.Add(SettingsPage.Glyph("lap-move", 14, brush: Brushes.White, stroke: 2));
        grip.Children.Add(new TextBlock { Text = Lang.T("Move", "تحريك"), Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        var pill = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, edge + 10, 0, 0),
            Child = grip,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 },
            ToolTip = Lang.T("Drag to move the area", "اسحب لتحريك المنطقة"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        pill.SetResourceReference(Border.BackgroundProperty, "Brush.AccentButton");
        pill.MouseEnter += (_, _) => Pop(pill, 1.08);
        pill.MouseLeave += (_, _) => { if (_drag == Edge.None) Pop(pill, 1); };
        Add(pill, Edge.Move, Cursors.SizeAll);

        _sizePill.Margin = new Thickness(0, 0, 0, edge + 10);
        _handles.Children.Add(_sizePill);
    }

    private static void Pop(FrameworkElement e, double to)
    {
        if (e.RenderTransform is not ScaleTransform s) return;
        Motion.To(s, ScaleTransform.ScaleXProperty, to, 320, Motion.Spring);
        Motion.To(s, ScaleTransform.ScaleYProperty, to, 320, Motion.Spring);
    }

    private void After(int ms, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    /// <summary>The pointer is near an edge: a handle sits under it, following along the edge and growing as the pointer wiggles.</summary>
    private void Hover(Edge what, Point at)
    {
        if (what is not (Edge.Left or Edge.Right or Edge.Top or Edge.Bottom))
        {
            if (_drag == Edge.None) HideGrab();
            return;
        }
        if (_lastPointer is { } last && _hover == what) _wiggle = Math.Min(160, _wiggle + (at - last).Length);
        _lastPointer = at;
        if (_hover != what)
        {
            _hover = what;
            _grab.BeginAnimation(OpacityProperty, null);
            Motion.FromTo(_grab, OpacityProperty, 0, 1, 160);
            Motion.FromTo(_grabScale, ScaleTransform.ScaleXProperty, 0.4, 1, 320, Motion.Spring);
            Motion.FromTo(_grabScale, ScaleTransform.ScaleYProperty, 0.4, 1, 320, Motion.Spring);
        }
        bool vertical = what is Edge.Left or Edge.Right;
        double grow = 1 + Math.Min(0.9, _wiggle / 110); // up to almost twice as big
        double length = 46 * grow, thickness = 10 + 3 * (grow - 1);
        double edge = Reach - Line / 2;
        double w = ActualWidth, h = ActualHeight;
        _grab.Width = vertical ? thickness : length;
        _grab.Height = vertical ? length : thickness;
        double x = what switch
        {
            Edge.Left => edge - thickness / 2,
            Edge.Right => w - edge - thickness / 2,
            _ => Math.Clamp(at.X, edge + length / 2, Math.Max(edge + length / 2, w - edge - length / 2)) - length / 2,
        };
        double y = what switch
        {
            Edge.Top => edge - thickness / 2,
            Edge.Bottom => h - edge - thickness / 2,
            _ => Math.Clamp(at.Y, edge + length / 2, Math.Max(edge + length / 2, h - edge - length / 2)) - length / 2,
        };
        Canvas.SetLeft(_grab, x);
        Canvas.SetTop(_grab, y);
        if (!_calm.IsEnabled) _calm.Start();
    }

    private void Leave(Edge what)
    {
        if (_hover == what) HideGrab();
    }

    private void HideGrab()
    {
        _hover = Edge.None;
        _lastPointer = null;
        _wiggle = 0;
        _calm.Stop();
        Motion.To(_grab, OpacityProperty, 0, 200);
    }

    /// <summary>Once the pointer rests, the handle eases back to its normal size.</summary>
    private void Calm()
    {
        if (_drag != Edge.None) return;
        _wiggle *= 0.9;
        if (_wiggle < 1) _calm.Stop();
    }

    private void Begin(UIElement handle, Edge what, MouseButtonEventArgs e)
    {
        _tween.Stop();
        _drag = what;
        _dragFrom = _area;
        _anchorFrom = _anchor;
        _cursorFrom = ScreenGeometry.CursorPosition();
        var work = ScreenGeometry.MonitorAt(new Native.POINT { X = _area.CenterX, Y = _area.CenterY }).WorkArea;
        _limits = new Rect(work.Left, work.Top, work.Width, work.Height);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void Drag()
    {
        if (_drag is Edge.None or Edge.Marker) return;
        var now = ScreenGeometry.CursorPosition();
        int dx = now.X - _cursorFrom.X, dy = now.Y - _cursorFrom.Y;
        int l = _dragFrom.X, t = _dragFrom.Y, r = _dragFrom.Right, b = _dragFrom.Bottom;
        int left = (int)_limits.Left, top = (int)_limits.Top, right = (int)_limits.Right, bottom = (int)_limits.Bottom;
        if (_drag == Edge.Move)
        {
            int w = r - l, h = b - t;
            l = Math.Clamp(l + dx, left, Math.Max(left, right - w));
            t = Math.Clamp(t + dy, top, Math.Max(top, bottom - h));
            r = l + w;
            b = t + h;
            _anchor = new PixelPoint(_anchorFrom.X + l - _dragFrom.X, _anchorFrom.Y + t - _dragFrom.Y); // the marker moves with the area
        }
        else
        {
            if (_drag.HasFlag(Edge.Left)) l = Math.Clamp(l + dx, left, r - MinSize);
            if (_drag.HasFlag(Edge.Right)) r = Math.Clamp(r + dx, l + MinSize, right);
            if (_drag.HasFlag(Edge.Top)) t = Math.Clamp(t + dy, top, b - MinSize);
            if (_drag.HasFlag(Edge.Bottom)) b = Math.Clamp(b + dy, t + MinSize, bottom);
        }
        _area = new PixelRect(l, t, r - l, b - t);
        _anchor = _area.Clamp(_anchor, 18);
        Place();
        UpdateSizeText();
    }

    private void End(UIElement handle)
    {
        if (_drag is Edge.None or Edge.Marker) return;
        _drag = Edge.None;
        if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
        if (!handle.IsMouseOver)
        {
            HideGrab();
            if (handle is FrameworkElement fe) Pop(fe, 1);
        }
        if (_area != _dragFrom) AreaChanged?.Invoke(_area);
    }

    private void UpdateSizeText() => _sizeText.Text = _label ?? $"{_area.Width:N0} × {_area.Height:N0}";

    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_hidden)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            return;
        }
        _scale = ScreenGeometry.MonitorAt(new Native.POINT { X = _area.CenterX, Y = _area.CenterY }).Scale;
        CaptureWindows.Place(_hwnd, _area.Inflate((int)Math.Ceiling(Reach * _scale)));
        PlaceMarker();
        Moving?.Invoke(_area);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeNoActivate(_hwnd);
        CaptureWindows.ExcludeFromCapture(_hwnd);
        _scale = ScreenGeometry.MonitorAt(new Native.POINT { X = _area.CenterX, Y = _area.CenterY }).Scale;
        Place();
    }
}

/// <summary>
/// The Longshot panel, in Amir Lap's glass: the blurred screen behind it under a frosted tint, what
/// to capture (window, scroll area, region), a live preview of the whole long image that grows (and
/// scrolls to each new part) as the capture goes on, and Auto / Extend / Follow / Undo / Done. The
/// next sensible step is always the highlighted button: Auto to begin, Done once the end is
/// reached. It stays on top of everything until it is closed, never takes focus from the app being
/// captured (so keyboard scrolling in Follow still goes there), can be moved by its title and
/// resized from its corner, and never appears in screenshots.
/// </summary>
internal sealed class CapturePanel : Window
{
    /// <summary>Width (DIPs) the session renders the whole-image preview at.</summary>
    public const double PreviewWidth = 300;
    private const double Shadow = 14;  // room around the glass for its shadow
    private const double Radius = 22;

    private readonly ScreenBackdrop _backdrop = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _seam = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false, BorderThickness = new Thickness(0, 2, 0, 0) };
    private readonly Grid _stage = new();
    private readonly Border _live = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly Ellipse _liveDot = new() { Width = 7, Height = 7, Fill = Brushes.White, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _liveText = new() { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _info = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 0) };
    private readonly TextBlock _status = new() { MinHeight = 34, Margin = new Thickness(2, 2, 0, 0) };
    private readonly TextBlock _keysHint = new();
    private readonly DispatcherTimer _keepUp = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly Dictionary<TargetKind, RadioButton> _targets = [];
    private readonly Style _plain, _accent;
    private PixelRect? _beside;
    private IntPtr _hwnd;
    private DateTime _lastBackdrop;
    private bool _finishReady;

    public CapturePanel(string? shortcut = null)
    {
        Title = "Longshot";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 316 + 2 * Shadow;
        Height = 700;
        MinWidth = 292 + 2 * Shadow;
        MinHeight = 540;
        MaxWidth = 560;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        SetResourceReference(ForegroundProperty, "Brush.Text");
        _plain = SettingsPage.Res("Lap.Button");
        _accent = SettingsPage.Res("Lap.Button.Accent");

        Auto = Make("lap-play", Lang.T("Auto", "تلقائي"), Lang.T("Scroll to the end by itself (Esc stops)", "التمرير حتى النهاية تلقائيًا (Esc للإيقاف)"), accent: true);
        Extend = Make("lap-down", Lang.T("Extend", "مدّ"), Lang.T("Scroll down once and add the new part", "مرّر مرة واحدة وأضف الجزء الجديد"));
        Follow = Make("lap-follow", Lang.T("Follow", "تتبّع"), Lang.T("You scroll; Longshot adds as you go", "أنت تمرّر واللقطة تُضاف"));
        Undo = Make("lap-undo", Lang.T("Undo", "تراجع"), Lang.T("Remove the last part that was added", "إزالة آخر جزء أُضيف"));
        Done = Make("lap-check", Lang.T("Done", "تم"), Lang.T("Save, copy to the clipboard and show the result", "حفظ ونسخ إلى الحافظة وإظهار النتيجة"));
        CloseButton = new Button { Style = SettingsPage.Res("Lap.Caption.Close"), Width = 34, Height = 30, Content = SettingsPage.Glyph("close", 12, stroke: 2.2), ToolTip = Lang.T("Cancel without saving", "إلغاء دون حفظ") };
        RefreshButton = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 34, Height = 30, Content = SettingsPage.Glyph("lap-refresh", 15, stroke: 2), ToolTip = Lang.T("Start again: back to the top, take the first part afresh", "البدء من جديد: العودة إلى الأعلى والتقاط الجزء الأول مرة أخرى"), Margin = new Thickness(0, 0, 2, 0) };
        System.Windows.Automation.AutomationProperties.SetName(RefreshButton, Lang.T("Start again", "البدء من جديد"));
        System.Windows.Automation.AutomationProperties.SetName(CloseButton, Lang.T("Cancel", "إلغاء"));
        _keysHint.Text = Lang.T("Esc stops Auto and Follow", "Esc يوقف التلقائي والتتبّع")
            + (shortcut is { Length: > 0 } ? Lang.T($". {shortcut} again saves.", $". ⁦{shortcut}⁩ مرة أخرى للحفظ.") : ".");
        Content = Build();

        _keepUp.Tick += (_, _) => KeepUp();
        LocationChanged += (_, _) =>
        {
            // While the panel is dragged, refresh the glass behind it at most ~30 times a second.
            if ((DateTime.UtcNow - _lastBackdrop).TotalMilliseconds > 33) RefreshBackdrop();
        };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(RefreshBackdrop, DispatcherPriority.Render);
        Closed += (_, _) => _keepUp.Stop();
    }

    public Button Extend { get; }
    public Button Auto { get; }
    public Button Follow { get; }
    public Button Undo { get; }
    public Button Done { get; }
    public Button CloseButton { get; }
    public Button RefreshButton { get; }

    /// <summary>The user chose what to capture (it may be the one already chosen: pick again).</summary>
    public event Action<TargetKind>? TargetChosen;

    /// <summary>The Video mode was chosen on the switch at the top.</summary>
    public event Action? VideoChosen;

    private Button Make(string icon, string text, string tip, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        // Icons keep their shape in Arabic, except Undo's arrow, which points the reading way.
        content.Children.Add(SettingsPage.Glyph(icon, 15, stroke: 2).With(g => { g.Margin = new Thickness(0, 0, 8, 0); if (icon != "lap-undo") g.FlowDirection = FlowDirection.LeftToRight; }));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = accent ? _accent : _plain, Content = content, ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>Shows a button as Stop (while Auto or Follow runs) or as itself: swaps its icon's shape and its text.</summary>
    public static void SetButton(Button button, string icon, string text)
    {
        if (button.Content is not StackPanel { Children.Count: 2 } content) return;
        if (content.Children[0] is Viewbox { Child: Canvas { Children.Count: > 0 } canvas } && canvas.Children[0] is Path path)
            path.Data = Icons.Get(icon);
        if (content.Children[1] is TextBlock label) label.Text = text;
        System.Windows.Automation.AutomationProperties.SetName(button, text);
    }

    /// <summary>For tests: the size line under the preview ("900 × 1,600 px · 4 parts").</summary>
    internal string InfoText => _info.Text;

    /// <summary>For tests: the status line ("Added 466 px.").</summary>
    internal string StatusText => _status.Text;

    /// <summary>For tests: which target is shown as chosen.</summary>
    internal TargetKind? ShownTarget => _targets.FirstOrDefault(t => t.Value.IsChecked == true) is { Value: not null } t ? t.Key : null;

    /// <summary>For tests: the target buttons.</summary>
    internal RadioButton TargetButton(TargetKind kind) => _targets[kind];

    /// <summary>For tests: whether Done is the highlighted next step.</summary>
    internal bool FinishReady => _finishReady;

    /// <summary>For tests: keeps the panel off the screen (and does not read the screen behind it).</summary>
    internal static bool KeepOffScreen { get; set; }

    /// <summary>Empties the preview (the capture starts again).</summary>
    public void ClearPreview()
    {
        _preview.Source = null;
        _info.Text = "";
        _seam.BeginAnimation(OpacityProperty, null);
        _seam.Opacity = 0;
        SetFinishReady(false);
    }

    /// <summary>Shows which target is chosen, without raising <see cref="TargetChosen"/>.</summary>
    public void SetTarget(TargetKind kind)
    {
        foreach (var (k, b) in _targets) b.IsChecked = k == kind;
    }

    /// <summary>The end was reached: Done becomes the highlighted button (and Auto plain), or back.</summary>
    public void SetFinishReady(bool ready)
    {
        bool was = _finishReady;
        _finishReady = ready;
        Done.Style = ready ? _accent : _plain;
        Auto.Style = ready ? _plain : _accent;
        if (ready && !was && !Motion.IsReduced && Done.RenderTransform is ScaleTransform s)
        {
            var bump = new DoubleAnimation(1, 1.04, TimeSpan.FromMilliseconds(180)) { AutoReverse = true, EasingFunction = Motion.Out };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, bump);
        }
    }

    /// <summary>While another area is being picked, the capture buttons wait.</summary>
    public void SetPicking(bool picking, string? status = null)
    {
        foreach (var b in new[] { Auto, Extend, Follow, Undo, Done }) b.IsEnabled = !picking && b.IsEnabled;
        if (status is not null) _status.Text = status;
    }

    private UIElement Build()
    {
        var dark = ThemeResources.Lap.IsDark;
        var root = new Grid();

        var shadow = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = dark ? 0.6 : 0.25 } };
        shadow.SetResourceReference(Border.BackgroundProperty, "Brush.Content");
        root.Children.Add(shadow);
        root.Children.Add(_backdrop);
        var tint = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius) };
        tint.SetResourceReference(Border.BackgroundProperty, "Brush.Glass");
        root.Children.Add(tint);
        root.Children.Add(new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), Background = GlassTexture.Grain(dark), Opacity = dark ? 0.45 : 0.6, IsHitTestVisible = false });
        var rim = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        rim.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        root.Children.Add(rim);
        var top = new Border { Margin = new Thickness(Shadow + 1), CornerRadius = new CornerRadius(Radius - 1), BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false };
        top.SetResourceReference(Border.BorderBrushProperty, "Brush.RimTop");
        root.Children.Add(top);

        var dock = new DockPanel { Margin = new Thickness(Shadow + 16, Shadow + 12, Shadow + 16, Shadow + 14), FlowDirection = Lang.Flow };

        // Title (drag to move).
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.SizeAll };
        DockPanel.SetDock(CloseButton, Dock.Right);
        head.Children.Add(CloseButton);
        DockPanel.SetDock(RefreshButton, Dock.Right);
        head.Children.Add(RefreshButton);
        var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 10, 0), Child = SettingsPage.Glyph("lap-longshot", 17, "Brush.Accent") };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        head.Children.Add(new TextBlock { Text = Lang.T("Longshot", "اللقطة الطويلة"), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is DependencyObject d && (IsInside(d, CloseButton) || IsInside(d, RefreshButton))) return;
            try { DragMove(); } catch (InvalidOperationException) { }
            RefreshBackdrop();
        };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        // Long screenshot or Video.
        var modes = Video.ModeSwitch.Build(video: false, v => { if (v) VideoChosen?.Invoke(); });
        DockPanel.SetDock(modes, Dock.Top);
        dock.Children.Add(modes);

        // What to capture.
        var bar = new Border { Style = SettingsPage.Res("Lap.SegmentBar"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 12) };
        var segments = new UniformGrid { Rows = 1 };
        string group = "longshot-target-" + Guid.NewGuid().ToString("N");
        foreach (var (kind, tip) in new[]
        {
            (TargetKind.Window, Lang.T("The whole window in front", "النافذة الأمامية كاملة")),
            (TargetKind.ScrollArea, Lang.T("Point at a part that scrolls (a chat, a list, a panel) and click it", "أشر إلى جزء يتمرّر (محادثة أو قائمة أو لوحة) وانقره")),
            (TargetKind.Region, Lang.T("Drag to draw any area", "اسحب لرسم أي منطقة")),
        })
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(SettingsPage.Glyph(PickHighlight.KindIcon(kind), 13, stroke: 1.9).With(g => { g.Margin = new Thickness(0, 0, 4, 0); g.FlowDirection = FlowDirection.LeftToRight; }));
            content.Children.Add(new TextBlock { Text = PickHighlight.KindText(kind), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var rb = new RadioButton { Style = SettingsPage.Res("Lap.Segment"), GroupName = group, Content = content, ToolTip = tip, Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(rb, PickHighlight.KindText(kind));
            rb.Click += (_, _) => TargetChosen?.Invoke(kind);
            _targets[kind] = rb;
            segments.Children.Add(rb);
        }
        bar.Child = segments;
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);

        // Footer hint, buttons, then the size and status line above them.
        _keysHint.Style = SettingsPage.Res("Lap.Help");
        _keysHint.Margin = new Thickness(2, 12, 18, 0);
        DockPanel.SetDock(_keysHint, Dock.Bottom);
        dock.Children.Add(_keysHint);

        var buttons = new Grid();
        for (int i = 0; i < 5; i++) buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(8) : new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++) buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(Button b, int row, int col, int span = 1)
        {
            b.Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 0);
            b.RenderTransformOrigin = new Point(0.5, 0.5);
            b.RenderTransform = new ScaleTransform(1, 1);
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Grid.SetColumnSpan(b, span);
            buttons.Children.Add(b);
        }
        Auto.Height = 42;
        Put(Auto, 0, 0, 5);
        Put(Extend, 1, 0);
        Put(Follow, 1, 2);
        Put(Undo, 1, 4);
        Done.Height = 40;
        Put(Done, 2, 0, 5);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);

        var stats = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        stats.Children.Add(SettingsPage.Ltr(_info));
        _status.Style = SettingsPage.Res("Lap.Help");
        _status.Text = Lang.T("Getting ready…", "جارٍ التجهيز…");
        stats.Children.Add(_status);
        DockPanel.SetDock(stats, Dock.Bottom);
        dock.Children.Add(stats);

        // The live preview: the whole long image, newest part at the bottom.
        _seam.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        _seam.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        _stage.Children.Add(_preview);
        _stage.Children.Add(_seam);
        _scroll.Content = _stage;
        _scroll.Style = SettingsPage.Res("Lap.Scroll");
        _scroll.SizeChanged += (_, _) => _stage.Width = Math.Max(1, _scroll.ActualWidth);

        var liveRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        liveRow.Children.Add(_liveDot);
        liveRow.Children.Add(_liveText);
        _live.Child = liveRow;
        _live.Height = 22;
        _live.Padding = new Thickness(8, 0, 10, 0);
        _live.CornerRadius = new CornerRadius(11);
        _live.Margin = new Thickness(8);
        _live.HorizontalAlignment = HorizontalAlignment.Left;
        _live.VerticalAlignment = VerticalAlignment.Top;
        _live.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x12, 0x18));

        var frame = new Grid();
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = _scroll, Padding = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        card.SizeChanged += (_, e) => _scroll.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2)), 13, 13);
        frame.Children.Add(card);
        frame.Children.Add(_live);
        dock.Children.Add(frame);
        root.Children.Add(dock);

        // Resize from the corner.
        var grip = new Thumb { Width = 22, Height = 22, Cursor = Cursors.SizeNWSE, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, Shadow + 4, Shadow + 4), ToolTip = Lang.T("Drag to resize", "اسحب لتغيير الحجم") };
        grip.Template = GripTemplate();
        grip.DragDelta += (_, e) =>
        {
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, SystemParameters.WorkArea.Height);
        };
        grip.DragCompleted += (_, _) => RefreshBackdrop();
        root.Children.Add(grip);
        return root;
    }

    private static ControlTemplate GripTemplate()
    {
        var dots = new FrameworkElementFactory(typeof(Path));
        dots.SetValue(Path.DataProperty, Geometry.Parse("M14,4 L4,14 M14,9 L9,14"));
        dots.SetValue(Shape.StrokeThicknessProperty, 1.6);
        dots.SetValue(Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        dots.SetValue(Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        dots.SetResourceReference(Shape.StrokeProperty, "Brush.Text3");
        dots.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        var host = new FrameworkElementFactory(typeof(Border));
        host.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        host.AppendChild(dots);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = host };
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x == ancestor) return true;
        return false;
    }

    /// <summary>
    /// The capture so far: the whole image (shrunk), scrolled down to the newest part, which
    /// glows for a moment so you can see what was just added.
    /// </summary>
    public void ShowUpdate(BitmapSource preview, int width, int height, int parts, string? message, int addedRows = 0)
    {
        _preview.Source = preview;
        _info.Text = $"{width:N0} × {height:N0} px  ·  " + Lang.T(parts == 1 ? "1 part" : $"{parts} parts", parts == 1 ? "جزء واحد" : $"{parts} أجزاء");
        if (message is not null) _status.Text = message;

        _scroll.UpdateLayout();
        double shown = _preview.ActualHeight;
        if (addedRows > 0 && height > 0 && shown > 0)
        {
            double top = (height - addedRows) * shown / height;
            _seam.Margin = new Thickness(0, top, 0, 0);
            _seam.Height = Math.Max(2, shown - top);
            _seam.BeginAnimation(OpacityProperty, Motion.IsReduced ? null : new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1400)) { EasingFunction = Motion.InOut });
        }
        ScrollAnimator.To(_scroll, _scroll.ScrollableHeight);
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>The "Auto" or "Follow" badge on the preview while Longshot is scrolling or following.</summary>
    public void SetLive(string? text)
    {
        _live.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        _liveText.Text = text ?? "";
        if (text is null || Motion.IsReduced) _liveDot.BeginAnimation(OpacityProperty, null);
        else _liveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>Puts the panel beside the area: right, left, below, above; inside its corner as a last resort.</summary>
    public void PlaceBeside(PixelRect area)
    {
        _beside = area;
        if (IsLoaded) PlaceAtSpot();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeNoActivate(_hwnd);
        CaptureWindows.ExcludeFromCapture(_hwnd);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PlaceAtSpot();
        RefreshBackdrop();
        _keepUp.Start();
    }

    /// <summary>Stays above every other window until closed, and keeps the glass in step with what is behind it.</summary>
    private void KeepUp()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        RefreshBackdrop();
    }

    private void RefreshBackdrop()
    {
        _lastBackdrop = DateTime.UtcNow;
        if (_hwnd != IntPtr.Zero && !KeepOffScreen) _backdrop.Refresh(_hwnd, Shadow, Radius);
    }

    private void PlaceAtSpot()
    {
        if (KeepOffScreen && _hwnd != IntPtr.Zero)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            return;
        }
        if (_beside is not { } area || _hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return;
        var spot = Spot(area, r.Width, r.Height);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, spot.X, spot.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    internal static (int X, int Y) Spot(PixelRect area, int width, int height)
    {
        var monitor = ScreenGeometry.MonitorAt(new Native.POINT { X = area.CenterX, Y = area.CenterY });
        var work = monitor.WorkArea;
        int gap = (int)(14 * monitor.Scale), edge = (int)(4 * monitor.Scale);
        var keepOut = area.Inflate(gap / 2);
        (int X, int Y)[] tries =
        [
            (area.Right + gap, area.Y), (area.X - gap - width, area.Y),
            (area.Right - width, area.Bottom + gap), (area.Right - width, area.Y - gap - height),
        ];
        int ClampX(int x) => Math.Clamp(x, work.Left + edge, Math.Max(work.Left + edge, work.Right - width - edge));
        int ClampY(int y) => Math.Clamp(y, work.Top + edge, Math.Max(work.Top + edge, work.Bottom - height - edge));
        foreach (var (x, y) in tries)
        {
            var candidate = new PixelRect(ClampX(x), ClampY(y), width, height);
            if (!candidate.Overlaps(keepOut)) return (candidate.X, candidate.Y);
        }
        // No room outside (the whole screen was chosen): inside the area's right edge. The panel is
        // left out of screenshots, so it never shows in the capture.
        return (ClampX(area.Right - width - gap), ClampY(area.Y + gap));
    }
}

/// <summary>
/// What is on the screen behind a window, blurred, so a see-through panel reads as frosted glass.
/// The window is left out of screen captures, so reading the screen shows what is under it.
/// In memory only.
/// </summary>
/// <remarks>
/// It takes no room of its own (see <see cref="MeasureOverride"/>): docs/LESSONS.md, "Glass that
/// grows the window".
/// </remarks>
internal sealed class ScreenBackdrop : Border
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
        Effect = new BlurEffect { Radius = 30, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(1.08, 1.08), // past the edges, so the blur never fades out there
    };

    public ScreenBackdrop()
    {
        IsHitTestVisible = false;
        Child = _image;
    }

    /// <summary>
    /// Takes no room of its own: the picture only fills whatever the window already is. The picture
    /// is the window's size in physical pixels (1.25× its size in DIPs at 125%); when it counted, a
    /// window that sizes to its content grew on every refresh, until it was as wide as the screen
    /// (Pulse's dock in 0.5.0, the colour window in 0.5.5).
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        _image.Measure(new Size(0, 0));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _image.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }

    /// <summary>For tests: a picture as if read from the screen.</summary>
    internal void SetPicture(BitmapSource picture) => _image.Source = picture;

    public void Refresh(IntPtr hwnd, double inset, double radius)
    {
        // With the debugging switch that shows Longshot's windows in captures, it would see itself.
        if (Environment.GetEnvironmentVariable("AMIRLAP_SHOW_IN_CAPTURES") == "1") return;
        if (!Native.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0) return;
        try
        {
            using var source = new GdiScreenSource(PixelRect.From(r));
            var frame = source.Grab();
            _image.Source = LongshotOutput.ToBitmap(frame.Width, frame.Height, frame.Pixels);
            Clip = new RectangleGeometry(new Rect(inset, inset, Math.Max(0, ActualWidth - 2 * inset), Math.Max(0, ActualHeight - 2 * inset)), radius, radius);
        }
        catch (InvalidOperationException)
        {
            // The screen could not be read (locked, secure desktop): the tint alone still works.
        }
    }
}

/// <summary>Smooth scrolling for the preview (a ScrollViewer's offset cannot be animated directly).</summary>
internal static class ScrollAnimator
{
    private static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(ScrollAnimator), new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    public static void To(ScrollViewer scroll, double offset)
    {
        if (Motion.IsReduced)
        {
            scroll.BeginAnimation(OffsetProperty, null);
            scroll.ScrollToVerticalOffset(offset);
            return;
        }
        scroll.BeginAnimation(OffsetProperty, new DoubleAnimation(scroll.VerticalOffset, offset, TimeSpan.FromMilliseconds(520)) { EasingFunction = Motion.Out });
    }
}

/// <summary>"Longshot in 3…" before a delayed capture. Never activated, never captured.</summary>
internal sealed class CountdownToast : GlassWindow
{
    private readonly TextBlock _text = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };

    public CountdownToast()
    {
        BackdropKind = Backdrop.Solid;
        NoActivate = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CloseOnEscape = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(SettingsPage.Glyph("lap-longshot", 18, "Brush.Accent").With(g => g.Margin = new Thickness(0, 0, 10, 0)));
        row.Children.Add(_text);
        var border = new Border { Child = row, Padding = new Thickness(18, 11, 20, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        Content = border;
        PlaceNear(ScreenGeometry.CursorPosition(), 28);
    }

    public void SetSeconds(int secondsLeft) => _text.Text = Lang.T($"Longshot in {secondsLeft}…  open what you need now", $"اللقطة الطويلة بعد {secondsLeft}…  افتح ما تحتاجه الآن");

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureWindows.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }
}
