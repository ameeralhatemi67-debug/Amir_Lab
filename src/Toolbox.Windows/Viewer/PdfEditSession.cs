using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Toolbox.Processing.Pdf;

namespace Toolbox.Windows.Viewer;

/// <summary>One page of the document being edited: where it comes from, extra turns, what was added.</summary>
internal sealed class EditPage
{
    public required OpenPdf Pdf { get; init; }
    public required int Page { get; init; }
    public int Turns { get; set; }
    public List<PdfMark> Marks { get; init; } = [];

    public EditPage Copy() => new() { Pdf = Pdf, Page = Page, Turns = Turns, Marks = [.. Marks] };
}

/// <summary>
/// The PDF editor's document: its pages (from the opened PDF and any PDFs added), their order and
/// turns, and what was added on each, with undo and redo. Nothing is written until Save, which
/// hands <see cref="ToPlan"/> to the job pipeline (a new file beside the original).
/// </summary>
internal sealed class PdfEditSession
{
    private readonly Stack<List<EditPage>> _undo = new(), _redo = new();

    public PdfEditSession(OpenPdf main)
    {
        Sources.Add(main);
        for (int p = 1; p <= main.PageCount; p++) Pages.Add(new EditPage { Pdf = main, Page = p });
    }

    public List<OpenPdf> Sources { get; } = [];
    public List<EditPage> Pages { get; private set; } = [];
    public OpenPdf Main => Sources[0];
    public bool Dirty { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Something changed: true when pages were added, removed, moved or turned (the view lays out again).</summary>
    public event Action<bool>? Changed;

    public IReadOnlyList<PageRef> Refs => Pages.Select(p => new PageRef(p.Pdf, p.Page, p.Turns)).ToList();

    /// <summary>Makes a change that can be undone.</summary>
    public void Do(Action<List<EditPage>> change, bool structural)
    {
        _undo.Push(Pages.Select(p => p.Copy()).ToList());
        _redo.Clear();
        change(Pages);
        Dirty = true;
        Changed?.Invoke(structural);
    }

    /// <summary>The current pages were handed to Save (leaving no longer asks about them).</summary>
    public void MarkSaved() => Dirty = false;

    public void Undo() => Swap(_undo, _redo);

    public void Redo() => Swap(_redo, _undo);

    private void Swap(Stack<List<EditPage>> from, Stack<List<EditPage>> to)
    {
        if (from.Count == 0) return;
        var before = Pages.Select(p => p.Copy()).ToList();
        bool structural = !SameStructure(before, from.Peek());
        to.Push(before);
        Pages = from.Pop();
        Dirty = true;
        Changed?.Invoke(structural);
    }

    private static bool SameStructure(List<EditPage> a, List<EditPage> b) =>
        a.Count == b.Count && a.Zip(b).All(x => x.First.Pdf == x.Second.Pdf && x.First.Page == x.Second.Page && x.First.Turns == x.Second.Turns);

    public void AddSource(OpenPdf pdf, int at)
    {
        if (!Sources.Contains(pdf)) Sources.Add(pdf);
        Do(pages => pages.InsertRange(Math.Clamp(at, 0, pages.Count), Enumerable.Range(1, pdf.PageCount).Select(p => new EditPage { Pdf = pdf, Page = p })), structural: true);
    }

    public PdfEditPlan ToPlan() => new(
        Sources.Select(s => s.Path).ToList(),
        Pages.Select(p => new PdfPagePlan(Sources.IndexOf(p.Pdf), p.Page, p.Turns, [.. p.Marks])).ToList());

    /// <summary>Has nothing changed since the PDF was opened (same pages, nothing added)?</summary>
    public bool IsUnchanged => Pages.Count == Main.PageCount && Pages.Select((p, i) => p.Pdf == Main && p.Page == i + 1 && p.Turns == 0 && p.Marks.Count == 0).All(x => x);
}

internal enum EditTool
{
    Select,
    Text,
    Pen,
    Highlight,
    Cover,
    Picture,
}

/// <summary>
/// The editor's tools on a <see cref="PdfView"/>: draws each page's additions on its layer (in
/// page points, so they follow zoom and turns) and turns mouse input into additions: text boxes,
/// pen and highlighter strokes, white cover, pictures and signatures; Select moves, resizes
/// (bottom-right handle) and deletes them, and a double-click edits text.
/// </summary>
internal sealed class PdfEditController
{
    private readonly PdfView _view;
    private readonly PdfEditSession _session;
    private readonly Dictionary<int, List<(PdfMark Mark, FrameworkElement Visual)>> _visuals = [];
    private EditTool _tool = EditTool.Select;
    private (int Page, PdfMark Mark)? _selected;
    private Drag? _drag;
    private TextBox? _editor;

    private sealed class Drag
    {
        public required int Page;
        public required Point Start;
        public PdfMark? Mark;            // being moved or resized
        public bool Resize;
        public List<PdfPoint>? Stroke;   // being drawn
        public Shape? Preview;
    }

    public PdfEditController(PdfView view, PdfEditSession session)
    {
        _view = view;
        _session = session;
        _view.LayerReady += (index, _) => Draw(index);
        _view.PageMouseDown += OnDown;
        _view.PageMouseMove += OnMove;
        _view.PageMouseUp += OnUp;
        _session.Changed += structural =>
        {
            CommitText();
            _selected = null;
            if (structural) _view.SetPages(_session.Refs);
            else for (int i = 0; i < _session.Pages.Count; i++) Draw(i);
        };
    }

    public Color Colour { get; set; } = Color.FromRgb(0x14, 0x14, 0x18);
    public double PenWidth { get; set; } = 2.2;
    public double FontSize { get; set; } = 14;

    /// <summary>A picture or signature waiting to be placed with the next click (PNG or JPEG bytes).</summary>
    public byte[]? PendingPicture { get; set; }

    public EditTool Tool
    {
        get => _tool;
        set
        {
            CommitText();
            _tool = value;
            if (value != EditTool.Select) Select(null);
            _view.EditCursor = value switch
            {
                EditTool.Text => Cursors.IBeam,
                EditTool.Pen or EditTool.Highlight or EditTool.Cover or EditTool.Picture => Cursors.Cross,
                _ => null,
            };
        }
    }

    public bool HasSelection => _selected is not null;

    // ---- drawing the additions ---------------------------------------------------------------------

    public void Draw(int index)
    {
        var layer = _view.LayerOf(index);
        if (layer is null || index >= _session.Pages.Count) return;
        layer.Children.Clear();
        var list = new List<(PdfMark, FrameworkElement)>();
        foreach (var mark in _session.Pages[index].Marks)
        {
            var visual = Visual(mark);
            layer.Children.Add(visual);
            list.Add((mark, visual));
        }
        _visuals[index] = list;
        if (_selected is { } s && s.Page == index) DrawSelection(index);
    }

    private static Color ColourOf(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static uint Argb(Color c, byte alpha = 255) => ((uint)alpha << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    internal static FrameworkElement Visual(PdfMark mark)
    {
        FrameworkElement e;
        switch (mark)
        {
            case PdfRectMark r:
                e = new Rectangle { Width = r.Width, Height = r.Height, Fill = new SolidColorBrush(ColourOf(r.Argb)) };
                Canvas.SetLeft(e, r.X);
                Canvas.SetTop(e, r.Y);
                break;
            case PdfImageMark m:
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.StreamSource = new MemoryStream(m.Image);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                e = new Image { Source = bitmap, Width = m.Width, Height = m.Height, Stretch = Stretch.Fill };
                Canvas.SetLeft(e, m.X);
                Canvas.SetTop(e, m.Y);
                break;
            }
            case PdfTextMark t:
                e = new TextBlock
                {
                    Text = t.Text,
                    Width = t.Width,
                    FontSize = t.Size,
                    FontFamily = new FontFamily(t.Font),
                    FontWeight = t.Bold ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(ColourOf(t.Argb)),
                    TextWrapping = TextWrapping.Wrap,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    LineHeight = t.Size * 1.3,
                    FlowDirection = PdfEditor.IsRightToLeft(t.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                };
                Canvas.SetLeft(e, t.X);
                Canvas.SetTop(e, t.Y);
                break;
            case PdfInkMark ink:
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    foreach (var s in ink.Strokes)
                    {
                        if (s.Count == 0) continue;
                        g.BeginFigure(new Point(s[0].X, s[0].Y), false, false);
                        if (s.Count == 1) g.LineTo(new Point(s[0].X + 0.01, s[0].Y), true, true);
                        for (int i = 1; i < s.Count - 1; i++)
                            g.QuadraticBezierTo(new Point(s[i].X, s[i].Y), new Point((s[i].X + s[i + 1].X) / 2, (s[i].Y + s[i + 1].Y) / 2), true, true);
                        if (s.Count > 1) g.LineTo(new Point(s[^1].X, s[^1].Y), true, true);
                    }
                }
                geometry.Freeze();
                var c = ColourOf(ink.Argb);
                e = new Path
                {
                    Data = geometry,
                    Stroke = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)),
                    StrokeThickness = ink.Width,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Opacity = c.A / 255.0,
                };
                break;
            }
            default:
                e = new Border();
                break;
        }
        e.IsHitTestVisible = false;
        return e;
    }

    /// <summary>The box a mark covers, in page points.</summary>
    internal static Rect Bounds(PdfMark mark, FrameworkElement? visual = null) => mark switch
    {
        PdfRectMark r => new Rect(r.X, r.Y, r.Width, r.Height),
        PdfImageMark m => new Rect(m.X, m.Y, m.Width, m.Height),
        PdfTextMark t => new Rect(t.X, t.Y, t.Width, Math.Max(t.Size * 1.3, visual?.ActualHeight > 0 ? visual.ActualHeight : t.Size * 1.3 * Math.Max(1, t.Text.Split('\n').Length))),
        PdfInkMark ink => InkBounds(ink),
        _ => Rect.Empty,
    };

    private static Rect InkBounds(PdfInkMark ink)
    {
        var pts = ink.Strokes.SelectMany(s => s).ToList();
        if (pts.Count == 0) return Rect.Empty;
        double pad = ink.Width / 2;
        return new Rect(new Point(pts.Min(p => p.X) - pad, pts.Min(p => p.Y) - pad), new Point(pts.Max(p => p.X) + pad, pts.Max(p => p.Y) + pad));
    }

    internal static PdfMark Moved(PdfMark mark, double dx, double dy) => mark switch
    {
        PdfRectMark r => r with { X = r.X + dx, Y = r.Y + dy },
        PdfImageMark m => m with { X = m.X + dx, Y = m.Y + dy },
        PdfTextMark t => t with { X = t.X + dx, Y = t.Y + dy },
        PdfInkMark ink => ink with { Strokes = ink.Strokes.Select(s => (IReadOnlyList<PdfPoint>)s.Select(p => new PdfPoint(p.X + dx, p.Y + dy)).ToList()).ToList() },
        _ => mark,
    };

    /// <summary>A mark with its bottom-right corner dragged by (dx, dy); pictures keep their shape.</summary>
    internal static PdfMark Resized(PdfMark mark, double dx, double dy) => mark switch
    {
        PdfRectMark r => r with { Width = Math.Max(4, r.Width + dx), Height = Math.Max(4, r.Height + dy) },
        PdfImageMark m => m with { Width = Math.Max(8, m.Width + dx), Height = Math.Max(8, (m.Width + dx) * m.Height / m.Width) },
        PdfTextMark t => t with { Width = Math.Max(30, t.Width + dx) },
        _ => mark,
    };

    // ---- selection ---------------------------------------------------------------------------------

    private void Select((int Page, PdfMark Mark)? selection)
    {
        int? old = _selected?.Page;
        _selected = selection;
        if (old is { } o) Draw(o);
        if (selection is { } s && s.Page != old) Draw(s.Page);
    }

    private void DrawSelection(int index)
    {
        var layer = _view.LayerOf(index);
        if (layer is null || _selected is not { } s) return;
        var visual = _visuals.GetValueOrDefault(index)?.FirstOrDefault(v => ReferenceEquals(v.Mark, s.Mark)).Visual;
        var box = Bounds(s.Mark, visual);
        if (box.IsEmpty) return;
        var accent = (Brush)Application.Current.Resources["Brush.Accent"];
        var frame = new Rectangle { Width = box.Width + 6, Height = box.Height + 6, Stroke = accent, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false };
        Canvas.SetLeft(frame, box.X - 3);
        Canvas.SetTop(frame, box.Y - 3);
        layer.Children.Add(frame);
        if (s.Mark is not PdfInkMark)
        {
            var handle = new Rectangle { Width = 8, Height = 8, Fill = Brushes.White, Stroke = accent, StrokeThickness = 1.5, IsHitTestVisible = false };
            Canvas.SetLeft(handle, box.Right - 1);
            Canvas.SetTop(handle, box.Bottom - 1);
            layer.Children.Add(handle);
        }
    }

    public void DeleteSelected()
    {
        if (_selected is not { } s) return;
        _selected = null;
        _session.Do(pages => pages[s.Page].Marks.Remove(s.Mark), structural: false);
    }

    // ---- mouse -------------------------------------------------------------------------------------

    private void OnDown(int index, Point at, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var layer = _view.LayerOf(index);
        if (layer is null) return;
        CommitText();
        switch (_tool)
        {
            case EditTool.Pen or EditTool.Highlight:
            {
                bool highlight = _tool == EditTool.Highlight;
                var stroke = new List<PdfPoint> { new(at.X, at.Y) };
                var preview = new Polyline
                {
                    Points = [at],
                    Stroke = new SolidColorBrush(highlight ? Color.FromRgb(0xFF, 0xD6, 0x00) : Colour),
                    StrokeThickness = highlight ? 12 : PenWidth,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    Opacity = highlight ? 0.4 : 1,
                    IsHitTestVisible = false,
                };
                layer.Children.Add(preview);
                _drag = new Drag { Page = index, Start = at, Stroke = stroke, Preview = preview };
                layer.CaptureMouse();
                break;
            }
            case EditTool.Cover:
            {
                var preview = new Rectangle { Fill = Brushes.White, Stroke = (Brush)Application.Current.Resources["Brush.Accent"], StrokeThickness = 0.8, IsHitTestVisible = false };
                Canvas.SetLeft(preview, at.X);
                Canvas.SetTop(preview, at.Y);
                layer.Children.Add(preview);
                _drag = new Drag { Page = index, Start = at, Preview = preview };
                layer.CaptureMouse();
                break;
            }
            case EditTool.Text:
                StartText(index, new PdfTextMark(at.X, at.Y - FontSize * 0.2, 240, "", FontSize, Argb(Colour)), isNew: true);
                e.Handled = true;
                break;
            case EditTool.Picture when PendingPicture is { } bytes:
            {
                var (w, h) = PictureSize(bytes);
                var mark = new PdfImageMark(at.X - w / 2, at.Y - h / 2, w, h, bytes);
                _session.Do(pages => pages[index].Marks.Add(mark), structural: false);
                PendingPicture = null;
                Tool = EditTool.Select;
                Select((index, mark));
                ToolChanged?.Invoke();
                break;
            }
            case EditTool.Select:
            {
                var hit = HitMark(index, at);
                if (hit is { } m)
                {
                    if (e.ClickCount == 2 && m is PdfTextMark text)
                    {
                        _session.Do(pages => pages[index].Marks.Remove(m), structural: false);
                        StartText(index, text, isNew: false);
                        return;
                    }
                    var visual = _visuals.GetValueOrDefault(index)?.FirstOrDefault(v => ReferenceEquals(v.Mark, m)).Visual;
                    var box = Bounds(m, visual);
                    bool resize = m is not PdfInkMark && (at - box.BottomRight).Length < 10;
                    Select((index, m));
                    _drag = new Drag { Page = index, Start = at, Mark = m, Resize = resize };
                    layer.CaptureMouse();
                }
                else Select(null);
                break;
            }
        }
    }

    /// <summary>The tool went back to Select by itself (after placing a picture).</summary>
    public event Action? ToolChanged;

    private void OnMove(int index, Point at, MouseEventArgs e)
    {
        if (_drag is not { } d || e.LeftButton != MouseButtonState.Pressed) return;
        var layer = _view.LayerOf(d.Page);
        if (layer is null) return;
        var p = Mouse.GetPosition(layer);
        p = new Point(Math.Clamp(p.X, 0, layer.Width), Math.Clamp(p.Y, 0, layer.Height));
        if (d.Stroke is { } stroke && d.Preview is Polyline line)
        {
            var last = stroke[^1];
            if (Math.Abs(last.X - p.X) + Math.Abs(last.Y - p.Y) < 0.6) return;
            stroke.Add(new PdfPoint(p.X, p.Y));
            line.Points.Add(p);
        }
        else if (d.Preview is Rectangle rect)
        {
            Canvas.SetLeft(rect, Math.Min(d.Start.X, p.X));
            Canvas.SetTop(rect, Math.Min(d.Start.Y, p.Y));
            rect.Width = Math.Abs(p.X - d.Start.X);
            rect.Height = Math.Abs(p.Y - d.Start.Y);
        }
        else if (d.Mark is { } mark)
        {
            // Preview the move or resize by redrawing that one mark.
            var changed = d.Resize ? Resized(mark, p.X - d.Start.X, p.Y - d.Start.Y) : Moved(mark, p.X - d.Start.X, p.Y - d.Start.Y);
            var entry = _visuals.GetValueOrDefault(d.Page)?.FirstOrDefault(v => ReferenceEquals(v.Mark, mark));
            if (entry?.Visual is { } old)
            {
                layer.Children.Remove(old);
                var fresh = Visual(changed);
                fresh.Opacity = 0.75;
                layer.Children.Add(fresh);
                var list = _visuals[d.Page];
                list[list.FindIndex(v => ReferenceEquals(v.Mark, mark))] = (mark, fresh);
            }
        }
    }

    private void OnUp(int index, Point at, MouseButtonEventArgs e)
    {
        if (_drag is not { } d) return;
        _drag = null;
        var layer = _view.LayerOf(d.Page);
        layer?.ReleaseMouseCapture();
        var p = layer is null ? at : Mouse.GetPosition(layer);
        if (d.Stroke is { } stroke)
        {
            bool highlight = _tool == EditTool.Highlight;
            var mark = new PdfInkMark([stroke], highlight ? Argb(Color.FromRgb(0xFF, 0xD6, 0x00), 0x66) : Argb(Colour), highlight ? 12 : PenWidth);
            _session.Do(pages => pages[d.Page].Marks.Add(mark), structural: false);
        }
        else if (d.Preview is Rectangle)
        {
            double x = Math.Min(d.Start.X, p.X), y = Math.Min(d.Start.Y, p.Y), w = Math.Abs(p.X - d.Start.X), h = Math.Abs(p.Y - d.Start.Y);
            if (w < 3 || h < 3) { Draw(d.Page); return; }
            var mark = new PdfRectMark(x, y, w, h, 0xFFFFFFFF);
            _session.Do(pages => pages[d.Page].Marks.Add(mark), structural: false);
        }
        else if (d.Mark is { } mark)
        {
            double dx = p.X - d.Start.X, dy = p.Y - d.Start.Y;
            if (Math.Abs(dx) + Math.Abs(dy) < 0.5) { Draw(d.Page); return; }
            var changed = d.Resize ? Resized(mark, dx, dy) : Moved(mark, dx, dy);
            _session.Do(pages =>
            {
                var marks = pages[d.Page].Marks;
                int i = marks.FindIndex(m => ReferenceEquals(m, mark));
                if (i >= 0) marks[i] = changed;
            }, structural: false);
            Select((d.Page, changed));
        }
    }

    private PdfMark? HitMark(int index, Point at)
    {
        if (!_visuals.TryGetValue(index, out var list)) return null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var box = Bounds(list[i].Mark, list[i].Visual);
            box.Inflate(4, 4);
            if (box.Contains(at)) return list[i].Mark;
        }
        return null;
    }

    private static (double W, double H) PictureSize(byte[] bytes)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var f = decoder.Frames[0];
            double w = 170, h = w * f.PixelHeight / Math.Max(1, f.PixelWidth);
            if (h > 220) { h = 220; w = h * f.PixelWidth / Math.Max(1, f.PixelHeight); }
            return (w, h);
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            return (170, 90);
        }
    }

    // ---- typing ------------------------------------------------------------------------------------

    private (int Page, PdfTextMark Mark)? _editing;

    private void StartText(int index, PdfTextMark mark, bool isNew)
    {
        var layer = _view.LayerOf(index);
        if (layer is null) return;
        _editing = (index, mark);
        _editor = new TextBox
        {
            Text = mark.Text,
            Width = mark.Width,
            MinHeight = mark.Size * 1.4,
            FontSize = mark.Size,
            FontFamily = new FontFamily(mark.Font),
            Foreground = new SolidColorBrush(ColourOf(mark.Argb)),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x46, 0x82, 0xFF)),
            BorderThickness = new Thickness(0.8),
            BorderBrush = (Brush)Application.Current.Resources["Brush.Accent"],
            Padding = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = PdfEditor.IsRightToLeft(mark.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
        };
        _editor.TextChanged += (_, _) => { if (_editor is { } t) t.FlowDirection = PdfEditor.IsRightToLeft(t.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight; };
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))) { CommitText(); e.Handled = true; }
        };
        _editor.LostKeyboardFocus += (_, _) => CommitText();
        Canvas.SetLeft(_editor, mark.X);
        Canvas.SetTop(_editor, mark.Y);
        layer.Children.Add(_editor);
        _editor.Dispatcher.BeginInvoke(() => { _editor?.Focus(); _editor?.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Finishes typing: the text becomes an addition (empty text is dropped).</summary>
    public void CommitText()
    {
        if (_editor is not { } box || _editing is not { } editing) return;
        _editor = null;
        _editing = null;
        var layer = box.Parent as Canvas;
        layer?.Children.Remove(box);
        var text = box.Text.TrimEnd();
        if (text.Length == 0) { Draw(editing.Page); return; }
        var mark = editing.Mark with { Text = text, Width = box.ActualWidth > 0 ? box.ActualWidth : editing.Mark.Width };
        _session.Do(pages => pages[editing.Page].Marks.Add(mark), structural: false);
    }
}
