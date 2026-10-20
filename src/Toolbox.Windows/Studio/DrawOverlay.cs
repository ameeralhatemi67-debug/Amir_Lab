using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Processing.Studio;

namespace Toolbox.Windows.Studio;

internal enum DrawTool
{
    Arrow,
    Rectangle,
    Ellipse,
    Line,
    Pen,
    Highlight,
    Text,
    Step,
    RedactSolid,
    RedactPixelate,
    RedactBlur,
}

/// <summary>
/// Captures drawing gestures over the content preview and turns them into annotations or
/// redaction rectangles in full-resolution content pixels. Only the in-progress shape is drawn
/// here; finished marks come back rendered by the real renderer, so what you see is what is saved.
/// </summary>
internal sealed class DrawOverlay : Canvas
{
    private Shape? _ghost;
    private Point _start;
    private readonly List<Point> _penPoints = [];
    private TextBox? _textEditor;

    public DrawOverlay()
    {
        Background = Brushes.Transparent;
        Cursor = Cursors.Cross;
        Focusable = true;
        FocusVisualStyle = null;
    }

    public Rect ImageRect { get; private set; }
    public double FullWidth { get; private set; } = 1;
    public double FullHeight { get; private set; } = 1;
    public DrawTool Tool { get; set; } = DrawTool.Arrow;
    public string Color { get; set; } = "#E5484D";
    public double Stroke { get; set; } = 6;
    public double FontSize { get; set; } = 48;

    public event Action<Annotation>? AnnotationAdded;
    public event Action<Redaction>? RedactionAdded;

    public void SetLayout(Rect imageRect, double fullWidth, double fullHeight)
    {
        ImageRect = imageRect;
        FullWidth = Math.Max(1, fullWidth);
        FullHeight = Math.Max(1, fullHeight);
    }

    private double Scale => ImageRect.Width / FullWidth;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CommitText();
        var p = e.GetPosition(this);
        if (!ImageRect.Contains(p)) return;
        Focus();
        _start = p;

        if (Tool == DrawTool.Step)
        {
            var full = ToFull(p);
            AnnotationAdded?.Invoke(new Annotation { Kind = AnnotationKind.Step, Points = [full.X, full.Y], Color = Color, FontSize = Math.Max(24, FontSize * 0.75) });
            return;
        }
        if (Tool == DrawTool.Text)
        {
            OpenTextEditor(p);
            e.Handled = true;
            return;
        }

        var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(IsRedact ? "#000000" : Color));
        _ghost = Tool switch
        {
            DrawTool.Ellipse => new Ellipse { Stroke = brush, StrokeThickness = Math.Max(1, Stroke * Scale) },
            DrawTool.Line or DrawTool.Arrow => new Line { Stroke = brush, StrokeThickness = Math.Max(1, Stroke * Scale), StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y },
            DrawTool.Pen => new Polyline { Stroke = brush, StrokeThickness = Math.Max(1, Stroke * Scale), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
            DrawTool.Highlight => new Rectangle { Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x66, brush.Color.R, brush.Color.G, brush.Color.B)) },
            _ when IsRedact => new Rectangle { Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(Tool == DrawTool.RedactSolid ? (byte)0xE0 : (byte)0x70, 0, 0, 0)), Stroke = Brushes.White, StrokeDashArray = [3, 2], StrokeThickness = 1 },
            _ => new Rectangle { Stroke = brush, StrokeThickness = Math.Max(1, Stroke * Scale), RadiusX = Stroke * Scale, RadiusY = Stroke * Scale },
        };
        _ghost.IsHitTestVisible = false;
        _penPoints.Clear();
        _penPoints.Add(p);
        if (_ghost is Polyline pl) pl.Points.Add(p);
        Children.Add(_ghost);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_ghost is null || !IsMouseCaptured) return;
        var p = Clamp(e.GetPosition(this));
        switch (_ghost)
        {
            case Line line:
                line.X2 = p.X;
                line.Y2 = p.Y;
                break;
            case Polyline poly:
                poly.Points.Add(p);
                _penPoints.Add(p);
                break;
            default:
                var r = new Rect(_start, p);
                SetLeft(_ghost, r.X);
                SetTop(_ghost, r.Y);
                _ghost.Width = r.Width;
                _ghost.Height = r.Height;
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_ghost is null) return;
        ReleaseMouseCapture();
        var end = Clamp(e.GetPosition(this));
        var a = ToFull(_start);
        var b = ToFull(end);
        bool tiny = Math.Abs(end.X - _start.X) < 3 && Math.Abs(end.Y - _start.Y) < 3;

        if (!tiny || Tool == DrawTool.Pen)
        {
            if (IsRedact)
            {
                var rect = new Rect(a, b);
                var mode = Tool switch { DrawTool.RedactPixelate => RedactionMode.Pixelate, DrawTool.RedactBlur => RedactionMode.Blur, _ => RedactionMode.Solid };
                RedactionAdded?.Invoke(new Redaction(new PixelRect((int)Math.Floor(rect.X), (int)Math.Floor(rect.Y), (int)Math.Ceiling(rect.Width), (int)Math.Ceiling(rect.Height)), mode));
            }
            else
            {
                var kind = Tool switch
                {
                    DrawTool.Ellipse => AnnotationKind.Ellipse,
                    DrawTool.Line => AnnotationKind.Line,
                    DrawTool.Arrow => AnnotationKind.Arrow,
                    DrawTool.Pen => AnnotationKind.Pen,
                    DrawTool.Highlight => AnnotationKind.Highlight,
                    _ => AnnotationKind.Rectangle,
                };
                var points = Tool == DrawTool.Pen
                    ? _penPoints.SelectMany(pt => { var f = ToFull(pt); return new[] { f.X, f.Y }; }).ToList()
                    : [a.X, a.Y, b.X, b.Y];
                if (points.Count >= 4)
                    AnnotationAdded?.Invoke(new Annotation { Kind = kind, Points = points, Color = Color, Stroke = Stroke });
            }
        }
        // Keep the ghost until the rendered frame arrives (see ClearGhosts) so there is no flicker.
    }

    /// <summary>Removes in-progress shapes once the renderer has drawn the real marks.</summary>
    public void ClearGhosts()
    {
        if (IsMouseCaptured) return;
        foreach (var shape in Children.OfType<Shape>().ToList()) Children.Remove(shape);
        _ghost = null;
    }

    private bool IsRedact => Tool is DrawTool.RedactSolid or DrawTool.RedactPixelate or DrawTool.RedactBlur;

    private void OpenTextEditor(Point p)
    {
        _textEditor = new TextBox
        {
            MinWidth = 160,
            FontSize = Math.Max(12, FontSize * Scale),
            AcceptsReturn = true,
            FlowDirection = FlowDirection.LeftToRight,
            Tag = ToFull(p),
        };
        SetLeft(_textEditor, p.X);
        SetTop(_textEditor, p.Y);
        Children.Add(_textEditor);
        _textEditor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { CommitText(); e.Handled = true; }
            if (e.Key == Key.Escape) { Children.Remove(_textEditor); _textEditor = null; e.Handled = true; }
        };
        _textEditor.LostKeyboardFocus += (_, _) => CommitText();
        Dispatcher.BeginInvoke(() => _textEditor?.Focus());
    }

    private void CommitText()
    {
        if (_textEditor is null) return;
        var editor = _textEditor;
        _textEditor = null;
        Children.Remove(editor);
        var text = editor.Text.Trim();
        if (text.Length == 0 || editor.Tag is not Point full) return;
        // Text sits with its top-left at the click point (the renderer adds the ascent itself).
        AnnotationAdded?.Invoke(new Annotation { Kind = AnnotationKind.Text, Points = [full.X, full.Y], Text = text, Color = Color, FontSize = FontSize });
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, ImageRect.Left, ImageRect.Right), Math.Clamp(p.Y, ImageRect.Top, ImageRect.Bottom));

    private Point ToFull(Point screen) => new(
        (screen.X - ImageRect.X) / ImageRect.Width * FullWidth,
        (screen.Y - ImageRect.Y) / ImageRect.Height * FullHeight);
}
