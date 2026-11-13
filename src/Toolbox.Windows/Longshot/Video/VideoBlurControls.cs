using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Processing.Studio;

namespace Toolbox.Windows.Longshot.Video;

using Toolbox.Windows.Theme;
using PixelRect = Toolbox.Processing.Studio.PixelRect;

/// <summary>A box as the editor shows it at the current moment.</summary>
internal readonly record struct ShownBox(int Id, PixelRect Box, RedactionMode Mode, string Name);

/// <summary>The colour each way of hiding is drawn in (boxes and timeline lanes).</summary>
internal static class BlurColours
{
    public static Color For(RedactionMode mode, Color accent) => mode switch
    {
        RedactionMode.Pixelate => Color.FromRgb(0x8B, 0x5C, 0xF6),
        RedactionMode.Solid => Color.FromRgb(0x47, 0x55, 0x69),
        _ => accent,
    };

    public static Color Accent(FrameworkElement e) => e.TryFindResource("Brush.Accent") is SolidColorBrush b ? b.Color : Color.FromRgb(0x25, 0x63, 0xEB);
}

/// <summary>
/// Over the video picture: the boxes on at this moment, the chosen one with handles. Drag on an
/// empty spot to draw a box; drag a box to move it, or its handles to resize it; click one to
/// choose it. Coordinates are kept in the video's pixels.
/// </summary>
internal sealed class BlurOverlay : FrameworkElement
{
    private const double Handle = 9;
    private enum Drag { None, Draw, Move, Resize }
    private Drag _drag;
    private Point _from;
    private PixelRect _start;
    private int _handle;          // 0..7 clockwise from the top-left
    private int? _dragId;
    private PixelRect? _live;     // the box as it is being drawn, moved or resized
    private bool _moved;

    public Size VideoSize { get; set; } = new(1, 1);

    /// <summary>The boxes on now (asked for on every redraw).</summary>
    public Func<IReadOnlyList<ShownBox>> Boxes { get; set; } = () => [];

    public int? SelectedId { get; set; }

    /// <summary>While following or finding: the boxes can't be changed by hand.</summary>
    public bool Locked { get; set; }

    public event Action? EditStarted;
    public event Action<PixelRect>? Drawn;
    public event Action<int?>? Chosen;
    public event Action<int, PixelRect>? Changed;

    public BlurOverlay()
    {
        Focusable = false;
        Cursor = Cursors.Cross;
    }

    /// <summary>Where the picture is drawn inside this element (fitted, centred).</summary>
    public Rect Fit()
    {
        double s = Math.Min(ActualWidth / VideoSize.Width, ActualHeight / VideoSize.Height);
        if (double.IsNaN(s) || double.IsInfinity(s) || s <= 0) return Rect.Empty;
        double w = VideoSize.Width * s, h = VideoSize.Height * s;
        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    private Rect ToScreen(PixelRect r)
    {
        var f = Fit();
        double s = f.Width / VideoSize.Width;
        return new Rect(f.X + r.X * s, f.Y + r.Y * s, r.Width * s, r.Height * s);
    }

    private Point ToVideo(Point p)
    {
        var f = Fit();
        double s = VideoSize.Width / Math.Max(1, f.Width);
        return new Point((p.X - f.X) * s, (p.Y - f.Y) * s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // takes the mouse everywhere
        var accent = BlurColours.Accent(this);
        foreach (var b in Boxes())
        {
            bool chosen = b.Id == SelectedId;
            var box = chosen && _live is { } live && _drag is Drag.Move or Drag.Resize ? live : b.Box;
            var r = ToScreen(box);
            var colour = BlurColours.For(b.Mode, accent);
            var shade = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), chosen ? 4 : 3);
            dc.DrawRectangle(null, shade, r);
            if (chosen)
            {
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x18, colour.R, colour.G, colour.B)), new Pen(new SolidColorBrush(colour), 2), r);
                foreach (var h in Handles(r))
                    dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(colour), 1.5), h, 2, 2);
            }
            else dc.DrawRectangle(null, new Pen(Brushes.White, 1.5) { DashStyle = new DashStyle([4, 3], 0) }, r);
            DrawTag(dc, r, b.Name, colour);
        }
        if (_drag == Drag.Draw && _live is { } drawing)
        {
            var r = ToScreen(drawing);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x22, accent.R, accent.G, accent.B)), new Pen(new SolidColorBrush(accent), 2) { DashStyle = new DashStyle([5, 3], 0) }, r);
        }
    }

    /// <summary>A small name tag above the box (inside it when there is no room above).</summary>
    private void DrawTag(DrawingContext dc, Rect r, string name, Color colour)
    {
        if (string.IsNullOrEmpty(name)) return;
        var text = new FormattedText(name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double w = Math.Min(text.Width + 10, Math.Max(40, r.Width + 60)), h = 17;
        text.MaxTextWidth = Math.Max(10, w - 10);
        text.Trimming = TextTrimming.CharacterEllipsis;
        text.MaxLineCount = 1;
        double y = r.Y - h - 2 >= 0 ? r.Y - h - 2 : r.Y + 2;
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xE6, colour.R, colour.G, colour.B)), null, new Rect(r.X, y, w, h), 4, 4);
        dc.DrawText(text, new Point(r.X + 5, y + 1));
    }

    private static Rect[] Handles(Rect r)
    {
        double h = Handle, x0 = r.Left - h / 2, x1 = r.Left + r.Width / 2 - h / 2, x2 = r.Right - h / 2, y0 = r.Top - h / 2, y1 = r.Top + r.Height / 2 - h / 2, y2 = r.Bottom - h / 2;
        return [new(x0, y0, h, h), new(x1, y0, h, h), new(x2, y0, h, h), new(x2, y1, h, h), new(x2, y2, h, h), new(x1, y2, h, h), new(x0, y2, h, h), new(x0, y1, h, h)];
    }

    private (Drag Kind, int Id, int Handle) HitTest(Point p)
    {
        var boxes = Boxes();
        if (SelectedId is { } sel && boxes.FirstOrDefault(b => b.Id == sel) is { Id: not 0 } chosen)
        {
            var hs = Handles(ToScreen(chosen.Box));
            for (int i = 0; i < hs.Length; i++)
                if (Rect.Inflate(hs[i], 4, 4).Contains(p)) return (Drag.Resize, chosen.Id, i);
            if (ToScreen(chosen.Box).Contains(p)) return (Drag.Move, chosen.Id, -1);
        }
        for (int i = boxes.Count - 1; i >= 0; i--)
            if (ToScreen(boxes[i].Box).Contains(p)) return (Drag.Move, boxes[i].Id, -1);
        return (Drag.Draw, 0, -1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_drag == Drag.None)
        {
            if (Locked) { Cursor = Cursors.Arrow; return; }
            var (kind, _, handle) = HitTest(p);
            Cursor = kind switch
            {
                Drag.Resize => handle is 0 or 4 ? Cursors.SizeNWSE : handle is 2 or 6 ? Cursors.SizeNESW : handle is 1 or 5 ? Cursors.SizeNS : Cursors.SizeWE,
                Drag.Move => Cursors.SizeAll,
                _ => Cursors.Cross,
            };
            return;
        }
        var a = ToVideo(_from);
        var b = ToVideo(p);
        double dx = b.X - a.X, dy = b.Y - a.Y;
        if (Math.Abs(p.X - _from.X) + Math.Abs(p.Y - _from.Y) > 3) _moved = true;
        int vw = (int)VideoSize.Width, vh = (int)VideoSize.Height;
        switch (_drag)
        {
            case Drag.Draw:
                _live = Clamp(Normal(a, b), vw, vh);
                break;
            case Drag.Move:
                _live = _start with { X = Math.Clamp(_start.X + (int)Math.Round(dx), -_start.Width / 2, vw - _start.Width / 2), Y = Math.Clamp(_start.Y + (int)Math.Round(dy), -_start.Height / 2, vh - _start.Height / 2) };
                break;
            case Drag.Resize:
                double l = _start.X, t = _start.Y, r = _start.X + _start.Width, btm = _start.Y + _start.Height;
                if (_handle is 0 or 6 or 7) l += dx;
                if (_handle is 2 or 3 or 4) r += dx;
                if (_handle is 0 or 1 or 2) t += dy;
                if (_handle is 4 or 5 or 6) btm += dy;
                _live = Clamp(Normal(new Point(l, t), new Point(r, btm)), vw, vh);
                break;
        }
        InvalidateVisual();
    }

    private static PixelRect Normal(Point a, Point b)
    {
        int x0 = (int)Math.Floor(Math.Min(a.X, b.X)), y0 = (int)Math.Floor(Math.Min(a.Y, b.Y));
        int x1 = (int)Math.Ceiling(Math.Max(a.X, b.X)), y1 = (int)Math.Ceiling(Math.Max(a.Y, b.Y));
        return new PixelRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    private static PixelRect Clamp(PixelRect r, int w, int h)
    {
        int x0 = Math.Clamp(r.X, 0, w), y0 = Math.Clamp(r.Y, 0, h);
        int x1 = Math.Clamp(r.X + r.Width, 0, w), y1 = Math.Clamp(r.Y + r.Height, 0, h);
        return new PixelRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Locked || Fit().IsEmpty) return;
        _from = e.GetPosition(this);
        var (kind, id, handle) = HitTest(_from);
        _drag = kind;
        _dragId = id == 0 ? null : id;
        _handle = handle;
        _moved = false;
        _live = null;
        if (_dragId is { } chosen && Boxes().FirstOrDefault(b => b.Id == chosen) is { Id: not 0 } box)
        {
            _start = box.Box;
            if (SelectedId != chosen) { SelectedId = chosen; Chosen?.Invoke(chosen); }
        }
        EditStarted?.Invoke();
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == Drag.None) return;
        ReleaseMouseCapture();
        var drag = _drag;
        _drag = Drag.None;
        var live = _live;
        _live = null;
        switch (drag)
        {
            case Drag.Draw when _moved && live is { Width: >= 8, Height: >= 8 } drawn:
                Drawn?.Invoke(drawn);
                break;
            case Drag.Draw:
                if (SelectedId is not null) { SelectedId = null; Chosen?.Invoke(null); }
                break;
            case Drag.Move or Drag.Resize when _moved && live is { } changed && _dragId is { } id:
                Changed?.Invoke(id, changed);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        _live = null;
        InvalidateVisual();
    }
}

/// <summary>One box's lane in the timeline.</summary>
internal readonly record struct TimelineLane(int Id, TimeSpan Start, TimeSpan End, RedactionMode Mode, string Name, IReadOnlyList<TimeSpan> Keys);

/// <summary>
/// Under the video: a strip of frames with the playhead (click or drag to move through the
/// video), and one lane per box showing when it is on, with its keys. Drag a lane's ends to change
/// when it starts and stops; click a lane to choose that box; click a key to go to it.
/// </summary>
internal sealed class BlurTimeline : FrameworkElement
{
    public const double StripHeight = 40, LaneHeight = 18, LaneGap = 4, Edge = 8;
    private enum Drag { None, Seek, Start, End }
    private Drag _drag;
    private int _dragId;
    private TimeSpan _dragStart, _dragEnd;

    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan Position { get; set; }
    public BitmapSource[] Strip { get; set; } = [];
    public IReadOnlyList<TimelineLane> Lanes { get; set; } = [];
    public int? SelectedId { get; set; }

    public event Action<TimeSpan>? Seeked;
    public event Action<int>? Chosen;
    public event Action<int, TimeSpan, TimeSpan>? SpanChanged;

    public BlurTimeline()
    {
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
    }

    private double LanesTop => StripHeight + 8;

    protected override Size MeasureOverride(Size available)
    {
        int lanes = Math.Max(1, Lanes.Count);
        double laneH = lanes > 6 ? Math.Max(10, (6 * (LaneHeight + LaneGap)) / lanes - LaneGap) : LaneHeight;
        return new Size(0, LanesTop + lanes * (laneH + LaneGap) + 2);
    }

    private double CurrentLaneHeight => Lanes.Count > 6 ? Math.Max(10, (6 * (LaneHeight + LaneGap)) / Lanes.Count - LaneGap) : LaneHeight;

    private Rect Track => new(Edge, 0, Math.Max(1, ActualWidth - 2 * Edge), ActualHeight);

    private double X(TimeSpan t) => Track.X + Track.Width * Math.Clamp(t.TotalMilliseconds / Math.Max(1, Duration.TotalMilliseconds), 0, 1);

    private TimeSpan At(double x) => TimeSpan.FromMilliseconds(Duration.TotalMilliseconds * Math.Clamp((x - Track.X) / Track.Width, 0, 1));

    private Rect LaneRect(int i)
    {
        double h = CurrentLaneHeight;
        return new Rect(Track.X, LanesTop + i * (h + LaneGap), Track.Width, h);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var accent = BlurColours.Accent(this);
        var line = TryFindResource("Brush.Line2") as Brush ?? Brushes.Gray;
        var back = TryFindResource("Brush.LapCard") as Brush ?? Brushes.DimGray;
        var text = TryFindResource("Brush.Text2") as Brush ?? Brushes.Gray;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        // The strip of frames.
        var strip = new Rect(Track.X, 0, Track.Width, StripHeight);
        var shape = new RectangleGeometry(strip, 7, 7);
        dc.DrawGeometry(back, new Pen(line, 1), shape);
        dc.PushClip(shape);
        if (Strip.Length > 0)
        {
            double w = strip.Width / Strip.Length;
            for (int i = 0; i < Strip.Length; i++)
            {
                var img = Strip[i];
                double drawW = Math.Max(w, strip.Height * img.PixelWidth / Math.Max(1, img.PixelHeight));
                dc.PushClip(new RectangleGeometry(new Rect(strip.X + i * w, 0, w + 0.5, strip.Height)));
                dc.DrawImage(img, new Rect(strip.X + i * w + (w - drawW) / 2, 0, drawW, strip.Height));
                dc.Pop();
            }
        }
        dc.Pop();

        // The lanes.
        if (Lanes.Count == 0)
        {
            var hint = new FormattedText(Lang.T("Boxes you add show here, with when they are on.", "تظهر هنا الصناديق التي تضيفها ومتى تكون ظاهرة."), CultureInfo.CurrentUICulture, Lang.Flow, new Typeface("Segoe UI"), 12, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(hint, new Point(Lang.IsArabic ? Track.Right - hint.Width : Track.X + 2, LanesTop + 1));
        }
        for (int i = 0; i < Lanes.Count; i++)
        {
            var lane = Lanes[i];
            var r = LaneRect(i);
            bool chosen = lane.Id == SelectedId;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80)), null, r, 4, 4);
            var colour = BlurColours.For(lane.Mode, accent);
            var (start, end) = _drag is Drag.Start or Drag.End && _dragId == lane.Id ? (_dragStart, _dragEnd) : (lane.Start, lane.End);
            var bar = new Rect(X(start), r.Y, Math.Max(3, X(end) - X(start)), r.Height);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(chosen ? (byte)0xF0 : (byte)0xA8, colour.R, colour.G, colour.B)), chosen ? new Pen(Brushes.White, 1.5) : null, bar, 4, 4);
            if (r.Height >= 14)
            {
                var name = new FormattedText(lane.Name, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
                { MaxTextWidth = Math.Max(10, bar.Width - 12), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                if (bar.Width > 24) dc.DrawText(name, new Point(bar.X + 6, r.Y + (r.Height - name.Height) / 2));
            }
            if (chosen)
            {
                // Grips at both ends, and the keys.
                var grip = new SolidColorBrush(Colors.White);
                dc.DrawRoundedRectangle(grip, null, new Rect(bar.X + 2, r.Y + 3, 3, r.Height - 6), 1.5, 1.5);
                dc.DrawRoundedRectangle(grip, null, new Rect(bar.Right - 5, r.Y + 3, 3, r.Height - 6), 1.5, 1.5);
                foreach (var k in lane.Keys)
                {
                    double kx = X(k), cy = r.Y + r.Height / 2, s = Math.Min(4, r.Height / 3);
                    var diamond = new StreamGeometry();
                    using (var g = diamond.Open())
                    {
                        g.BeginFigure(new Point(kx, cy - s), true, true);
                        g.LineTo(new Point(kx + s, cy), true, false);
                        g.LineTo(new Point(kx, cy + s), true, false);
                        g.LineTo(new Point(kx - s, cy), true, false);
                    }
                    dc.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 1), diamond);
                }
            }
        }

        // The playhead, across everything.
        double px = X(Position);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 4), new Point(px, 0), new Point(px, ActualHeight));
        dc.DrawLine(new Pen(Brushes.White, 2), new Point(px, 0), new Point(px, ActualHeight));
        dc.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(accent), 1.5), new Point(px, 4), 5, 5);
    }

    private int LaneAt(double y)
    {
        for (int i = 0; i < Lanes.Count; i++)
        {
            var r = LaneRect(i);
            if (y >= r.Y - LaneGap / 2 && y < r.Bottom + LaneGap / 2) return i;
        }
        return -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        switch (_drag)
        {
            case Drag.Seek:
                Position = At(p.X);
                Seeked?.Invoke(Position);
                InvalidateVisual();
                return;
            case Drag.Start:
                _dragStart = TimeSpan.FromTicks(Math.Min(At(p.X).Ticks, _dragEnd.Ticks - TimeSpan.FromMilliseconds(100).Ticks));
                if (_dragStart < TimeSpan.Zero) _dragStart = TimeSpan.Zero;
                Seeked?.Invoke(_dragStart);
                InvalidateVisual();
                return;
            case Drag.End:
                _dragEnd = TimeSpan.FromTicks(Math.Max(At(p.X).Ticks, _dragStart.Ticks + TimeSpan.FromMilliseconds(100).Ticks));
                if (_dragEnd > Duration) _dragEnd = Duration;
                Seeked?.Invoke(_dragEnd);
                InvalidateVisual();
                return;
        }
        int i = LaneAt(p.Y);
        Cursor = i >= 0 && Lanes[i].Id == SelectedId && (Math.Abs(p.X - X(Lanes[i].Start)) < 7 || Math.Abs(p.X - X(Lanes[i].End)) < 7) ? Cursors.SizeWE : Cursors.Hand;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        int i = p.Y > StripHeight + 2 ? LaneAt(p.Y) : -1;
        if (i >= 0)
        {
            var lane = Lanes[i];
            if (lane.Id == SelectedId && Math.Abs(p.X - X(lane.Start)) < 7) { Begin(Drag.Start, lane); }
            else if (lane.Id == SelectedId && Math.Abs(p.X - X(lane.End)) < 7) { Begin(Drag.End, lane); }
            else
            {
                if (lane.Id != SelectedId) { SelectedId = lane.Id; Chosen?.Invoke(lane.Id); }
                // A key under the pointer: go to it; else go where the lane was clicked, if it is on there.
                var key = lane.Keys.Where(k => Math.Abs(X(k) - p.X) < 6).Cast<TimeSpan?>().FirstOrDefault();
                var to = key ?? At(p.X);
                if (to < lane.Start) to = lane.Start;
                if (to > lane.End) to = lane.End;
                Position = to;
                Seeked?.Invoke(to);
            }
        }
        else
        {
            _drag = Drag.Seek;
            Position = At(p.X);
            Seeked?.Invoke(Position);
        }
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    private void Begin(Drag drag, TimelineLane lane)
    {
        _drag = drag;
        _dragId = lane.Id;
        _dragStart = lane.Start;
        _dragEnd = lane.End;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var drag = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        if (drag is Drag.Start or Drag.End) SpanChanged?.Invoke(_dragId, _dragStart, _dragEnd);
        InvalidateVisual();
    }
}
