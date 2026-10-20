using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Processing.Studio;

namespace Toolbox.Windows.Studio;

/// <summary>
/// Direct-manipulation crop frame over the displayed image: eight handles, drag inside to move,
/// drag outside to draw a new frame, optional aspect lock, shaded outside area, rule-of-thirds
/// guides while dragging, arrow keys to nudge (Shift = 10 px). Works in full-resolution pixels.
/// </summary>
internal sealed class CropOverlay : Canvas
{
    private const double HandleSize = 11;
    private const double MinSize = 8;

    private readonly Path _shade = new() { IsHitTestVisible = false };
    private readonly Rectangle _frame = new() { StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Rectangle[] _handles = new Rectangle[8];
    private readonly Line[] _thirds = new Line[4];

    private Rect _value; // full-res pixels
    private int _dragHandle = -2; // -2 none, -1 move, 8 new selection, 0..7 handles
    private Point _dragStart;
    private Rect _startRect;

    public CropOverlay()
    {
        Background = Brushes.Transparent;
        Focusable = true;
        FocusVisualStyle = null;
        _shade.SetResourceReference(Shape.FillProperty, "Brush.Shade");
        _frame.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        Children.Add(_shade);
        for (int i = 0; i < 4; i++)
        {
            _thirds[i] = new Line { Stroke = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)), StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            Children.Add(_thirds[i]);
        }
        Children.Add(_frame);
        for (int i = 0; i < 8; i++)
        {
            _handles[i] = new Rectangle { Width = HandleSize, Height = HandleSize, RadiusX = 2, RadiusY = 2, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
            _handles[i].SetResourceReference(Shape.FillProperty, "Brush.Accent");
            Children.Add(_handles[i]);
        }
        System.Windows.Automation.AutomationProperties.SetName(this, "Crop area. Drag to move or resize; arrow keys nudge.");
    }

    /// <summary>Where the full image is drawn, in this element's coordinates (DIPs).</summary>
    public Rect ImageRect { get; private set; }

    public double FullWidth { get; private set; } = 1;
    public double FullHeight { get; private set; } = 1;

    /// <summary>Width/height to keep, or null for free.</summary>
    public double? Aspect { get; set; }

    public PixelRect Value => new((int)Math.Round(_value.X), (int)Math.Round(_value.Y), (int)Math.Max(1, Math.Round(_value.Width)), (int)Math.Max(1, Math.Round(_value.Height)));

    /// <summary>Raised continuously while dragging.</summary>
    public event Action<PixelRect>? Changing;

    /// <summary>Raised when a drag or nudge finishes.</summary>
    public event Action<PixelRect>? Committed;

    public void SetLayout(Rect imageRect, double fullWidth, double fullHeight)
    {
        ImageRect = imageRect;
        FullWidth = Math.Max(1, fullWidth);
        FullHeight = Math.Max(1, fullHeight);
        Redraw();
    }

    public void SetValue(PixelRect? rect)
    {
        _value = rect is { } r ? new Rect(r.X, r.Y, r.Width, r.Height) : new Rect(0, 0, FullWidth, FullHeight);
        _value = Clamp(_value);
        Redraw();
    }

    /// <summary>Largest centred rectangle with the given aspect that fits in the current one (or the image).</summary>
    public void ApplyAspect(double? aspect, bool fromWholeImage)
    {
        Aspect = aspect;
        if (aspect is not { } a) return;
        var basis = fromWholeImage ? new Rect(0, 0, FullWidth, FullHeight) : _value;
        double w = basis.Width, h = basis.Height;
        if (w / h > a) w = h * a;
        else h = w / a;
        var c = new Point(basis.X + basis.Width / 2, basis.Y + basis.Height / 2);
        _value = Clamp(new Rect(c.X - w / 2, c.Y - h / 2, w, h));
        Redraw();
        Committed?.Invoke(Value);
    }

    /// <summary>Sets the size from typed values, keeping the centre (and the aspect when locked).</summary>
    public void SetSize(double? width, double? height)
    {
        double w = width ?? _value.Width, h = height ?? _value.Height;
        if (Aspect is { } a)
        {
            if (width is not null) h = w / a;
            else if (height is not null) w = h * a;
        }
        w = Math.Clamp(w, MinSize, FullWidth);
        h = Math.Clamp(h, MinSize, FullHeight);
        var c = new Point(_value.X + _value.Width / 2, _value.Y + _value.Height / 2);
        _value = Clamp(new Rect(c.X - w / 2, c.Y - h / 2, w, h));
        Redraw();
        Committed?.Invoke(Value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        _dragHandle = HitHandle(p);
        if (_dragHandle == -2)
        {
            if (!ImageRect.Contains(p)) return;
            _dragHandle = 8;
        }
        _dragStart = ToFull(p);
        _startRect = _value;
        CaptureMouse();
        ShowThirds(true);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragHandle == -2 || !IsMouseCaptured)
        {
            Cursor = CursorFor(HitHandle(p));
            return;
        }
        var full = ToFull(p);
        _value = _dragHandle switch
        {
            -1 => Clamp(new Rect(_startRect.X + full.X - _dragStart.X, _startRect.Y + full.Y - _dragStart.Y, _startRect.Width, _startRect.Height)),
            8 => FromAnchor(_dragStart, full),
            _ => ResizeWithHandle(_dragHandle, full),
        };
        Redraw();
        Changing?.Invoke(Value);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        ShowThirds(false);
        _dragHandle = -2;
        Committed?.Invoke(Value);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => (Vector?)null,
        };
        if (delta is not { } d) return;
        _value = Clamp(new Rect(_value.X + d.X, _value.Y + d.Y, _value.Width, _value.Height));
        Redraw();
        Committed?.Invoke(Value);
        e.Handled = true;
    }

    private Rect ResizeWithHandle(int handle, Point p)
    {
        var r = _startRect;
        bool left = handle is 0 or 6 or 7, right = handle is 2 or 3 or 4, top = handle is 0 or 1 or 2, bottom = handle is 4 or 5 or 6;
        bool corner = handle is 0 or 2 or 4 or 6;
        if (corner)
        {
            var anchor = new Point(left ? r.Right : r.Left, top ? r.Bottom : r.Top);
            return FromAnchor(anchor, p);
        }

        // Edge handles: move one side; with an aspect lock the other dimension follows, centred.
        double x1 = r.Left, x2 = r.Right, y1 = r.Top, y2 = r.Bottom;
        if (left) x1 = Math.Min(p.X, x2 - MinSize);
        if (right) x2 = Math.Max(p.X, x1 + MinSize);
        if (top) y1 = Math.Min(p.Y, y2 - MinSize);
        if (bottom) y2 = Math.Max(p.Y, y1 + MinSize);
        x1 = Math.Max(0, x1); y1 = Math.Max(0, y1); x2 = Math.Min(FullWidth, x2); y2 = Math.Min(FullHeight, y2);
        var result = new Rect(x1, y1, x2 - x1, y2 - y1);
        if (Aspect is { } a)
        {
            if (left || right)
            {
                double h = result.Width / a, cy = r.Y + r.Height / 2;
                result = new Rect(result.X, cy - h / 2, result.Width, h);
            }
            else
            {
                double w = result.Height * a, cx = r.X + r.Width / 2;
                result = new Rect(cx - w / 2, result.Y, w, result.Height);
            }
            result = FitInside(result);
        }
        return result;
    }

    /// <summary>Rectangle from a fixed anchor to the pointer, honouring the aspect and image bounds.</summary>
    private Rect FromAnchor(Point anchor, Point p)
    {
        p = new Point(Math.Clamp(p.X, 0, FullWidth), Math.Clamp(p.Y, 0, FullHeight));
        double w = Math.Max(MinSize, Math.Abs(p.X - anchor.X)), h = Math.Max(MinSize, Math.Abs(p.Y - anchor.Y));
        double sx = p.X >= anchor.X ? 1 : -1, sy = p.Y >= anchor.Y ? 1 : -1;
        if (Aspect is { } a)
        {
            if (w / h > a) w = h * a;
            else h = w / a;
            // Keep inside the image on the side we grow towards.
            double maxW = sx > 0 ? FullWidth - anchor.X : anchor.X;
            double maxH = sy > 0 ? FullHeight - anchor.Y : anchor.Y;
            if (w > maxW) { w = maxW; h = w / a; }
            if (h > maxH) { h = maxH; w = h * a; }
        }
        double x = sx > 0 ? anchor.X : anchor.X - w;
        double y = sy > 0 ? anchor.Y : anchor.Y - h;
        return Clamp(new Rect(x, y, Math.Max(MinSize, w), Math.Max(MinSize, h)));
    }

    private Rect FitInside(Rect r)
    {
        double scale = Math.Min(1, Math.Min(FullWidth / r.Width, FullHeight / r.Height));
        r = new Rect(r.X, r.Y, r.Width * scale, r.Height * scale);
        return Clamp(r);
    }

    private Rect Clamp(Rect r)
    {
        double w = Math.Clamp(r.Width, Math.Min(MinSize, FullWidth), FullWidth);
        double h = Math.Clamp(r.Height, Math.Min(MinSize, FullHeight), FullHeight);
        double x = Math.Clamp(r.X, 0, FullWidth - w);
        double y = Math.Clamp(r.Y, 0, FullHeight - h);
        return new Rect(x, y, w, h);
    }

    private Point ToFull(Point screen) => new(
        (screen.X - ImageRect.X) / ImageRect.Width * FullWidth,
        (screen.Y - ImageRect.Y) / ImageRect.Height * FullHeight);

    private Rect ToScreen(Rect full) => new(
        ImageRect.X + full.X / FullWidth * ImageRect.Width,
        ImageRect.Y + full.Y / FullHeight * ImageRect.Height,
        full.Width / FullWidth * ImageRect.Width,
        full.Height / FullHeight * ImageRect.Height);

    private int HitHandle(Point p)
    {
        var s = ToScreen(_value);
        var points = HandlePoints(s);
        for (int i = 0; i < 8; i++)
            if (Math.Abs(p.X - points[i].X) <= HandleSize && Math.Abs(p.Y - points[i].Y) <= HandleSize) return i;
        return s.Contains(p) ? -1 : -2;
    }

    private static Point[] HandlePoints(Rect s) =>
    [
        new(s.Left, s.Top), new(s.Left + s.Width / 2, s.Top), new(s.Right, s.Top), new(s.Right, s.Top + s.Height / 2),
        new(s.Right, s.Bottom), new(s.Left + s.Width / 2, s.Bottom), new(s.Left, s.Bottom), new(s.Left, s.Top + s.Height / 2),
    ];

    private static Cursor CursorFor(int handle) => handle switch
    {
        0 or 4 => Cursors.SizeNWSE,
        2 or 6 => Cursors.SizeNESW,
        1 or 5 => Cursors.SizeNS,
        3 or 7 => Cursors.SizeWE,
        -1 => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private void ShowThirds(bool show)
    {
        foreach (var line in _thirds) line.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Redraw()
    {
        if (ImageRect.Width <= 0) return;
        var s = ToScreen(_value);
        _shade.Data = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(ImageRect), new RectangleGeometry(s) },
        };
        SetLeft(_frame, s.X);
        SetTop(_frame, s.Y);
        _frame.Width = s.Width;
        _frame.Height = s.Height;
        var points = HandlePoints(s);
        for (int i = 0; i < 8; i++)
        {
            SetLeft(_handles[i], points[i].X - HandleSize / 2);
            SetTop(_handles[i], points[i].Y - HandleSize / 2);
        }
        for (int i = 0; i < 2; i++)
        {
            double x = s.X + s.Width * (i + 1) / 3, y = s.Y + s.Height * (i + 1) / 3;
            (_thirds[i].X1, _thirds[i].Y1, _thirds[i].X2, _thirds[i].Y2) = (x, s.Top, x, s.Bottom);
            (_thirds[i + 2].X1, _thirds[i + 2].Y1, _thirds[i + 2].X2, _thirds[i + 2].Y2) = (s.Left, y, s.Right, y);
        }
    }
}
