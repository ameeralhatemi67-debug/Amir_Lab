using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Processing.Pdf;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Viewer;

/// <summary>
/// Pages of one or more PDFs, one under the other (or a grid of small pages in Overview), in a
/// scrolling view.
/// <list type="bullet">
/// <item>Rendering: each page shows a quick low-resolution render as soon as it comes into view, and
/// a sharp one once scrolling settles, so fast scrolling never waits for rendering.</item>
/// <item>Zoom: Ctrl+wheel zooms around the pointer; fit width and fit page are available.</item>
/// <item>View: pages can be turned; a theme recolours them; search hits are highlighted.</item>
/// <item>Links: a click on a link to another page opens a peek of that place (read the footnote,
/// close it, carry on); web links open in the browser.</item>
/// <item>History: jumps go into a back/forward history.</item>
/// </list>
/// Marks drawn by the editor sit on each page's own layer, in the page's points, so they follow
/// zoom and turns by themselves.
/// </summary>
internal sealed class PdfView : Grid
{
    private const double Gap = 14, Pad = 18, LowWidth = 200;
    private const double PointsToDip = 96.0 / 72.0;

    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true, CanContentScroll = false };
    private readonly Canvas _sheet = new() { Background = Brushes.Transparent };
    private readonly List<Slot> _slots = [];
    private readonly DispatcherTimer _settle = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _linkTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly Stack<(int Index, double Y)> _back = new(), _forward = new();
    private readonly Popup _peek = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.MousePoint };
    private IReadOnlyList<PageRef> _pages = [];
    private IReadOnlyList<(PageRef? Ref, OpenPdf Pdf, int Page, IReadOnlyList<PdfBox> Boxes)> _hits = [];
    private int _currentHit = -1;
    private double _zoom = 1;
    private bool _fitWidth = true;
    private int _viewTurns;
    private bool _overview;
    private PageTheme _theme = PageTheme.Light;
    private int _generation;
    private bool _pumping;
    private DateTime _lastScroll;
    private int _current = -1;
    private Point? _linkAt;
    private (int Slot, Point At)? _pendingLink;

    public PdfView()
    {
        ClipToBounds = true;
        FlowDirection = FlowDirection.LeftToRight; // pages are pages, in any language
        _scroll.Content = _sheet;
        _scroll.SetResourceReference(StyleProperty, "Lap.Scroll");
        Children.Add(_scroll);
        _scroll.ScrollChanged += OnScrolled;
        _scroll.PreviewMouseWheel += OnWheel;
        SizeChanged += (_, _) => { if (_fitWidth) ApplyZoom(FitWidthZoom(), keepCentre: true); else Relayout(keep: true); };
        _settle.Tick += (_, _) => { _settle.Stop(); Pump(); };
        _linkTimer.Tick += (_, _) => { _linkTimer.Stop(); CheckLink(); };
        _sheet.MouseLeftButtonDown += OnSheetDown;
        _sheet.MouseMove += OnSheetMove;
        _sheet.MouseLeftButtonUp += OnSheetUp;
    }

    // ---- what is shown -----------------------------------------------------------------------------

    public IReadOnlyList<PageRef> Pages => _pages;

    /// <summary>For tests: pages showing a render (low, and sharp).</summary>
    internal (int Low, int High) Rendered => (_slots.Count(s => s.Low.Source is not null), _slots.Count(s => s.High.Source is not null));

    /// <summary>For tests: how many search highlights are drawn.</summary>
    internal int HighlightCount => _slots.Sum(s => s.Hits.Children.Count);

    /// <summary>0-based index of the page in the middle of the view.</summary>
    public int Current => Math.Max(0, _current);

    public double Zoom => _zoom;

    public bool IsFitWidth => _fitWidth;

    public bool IsOverview => _overview;

    public int ViewTurns => _viewTurns;

    /// <summary>The page in view changed (index).</summary>
    public event Action<int>? CurrentChanged;

    /// <summary>The zoom changed (by the wheel, a fit, or a size change).</summary>
    public event Action? ZoomChanged;

    /// <summary>A page's mark layer was (re)created: the editor draws its marks there (index, layer in page points).</summary>
    public event Action<int, Canvas>? LayerReady;

    /// <summary>Mouse on a page while the editor is on: index, point in the page's points (before turns), the event.</summary>
    public event Action<int, Point, MouseButtonEventArgs>? PageMouseDown;
    public event Action<int, Point, MouseEventArgs>? PageMouseMove;
    public event Action<int, Point, MouseButtonEventArgs>? PageMouseUp;

    /// <summary>When on, mouse events on pages go to the editor (and links are not followed).</summary>
    public bool Editing { get; set; }

    public Cursor? EditCursor { get; set; }

    public void SetPages(IReadOnlyList<PageRef> pages, int? keepIndex = null)
    {
        int keep = keepIndex ?? Current;
        _pages = pages;
        _generation++;
        BuildSlots();
        if (_fitWidth) _zoom = FitWidthZoom();
        Relayout(keep: false);
        if (pages.Count > 0) GoTo(Math.Clamp(keep, 0, pages.Count - 1), record: false);
    }

    /// <summary>The same pages, with one page's marks redrawn (the editor calls this).</summary>
    public Canvas? LayerOf(int index) => index >= 0 && index < _slots.Count ? _slots[index].Marks : null;

    public PageTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            _generation++;
            foreach (var s in _slots)
            {
                s.Low.Source = null;
                s.High.Source = null;
                s.HighWidth = 0;
                s.LowAsked = s.HighAsked = false;
                s.Frame.Background = new SolidColorBrush(ToColor(value.Paper));
            }
            Pump();
        }
    }

    // ---- slots and layout --------------------------------------------------------------------------

    private sealed class Slot
    {
        public required PageRef Ref;
        public required Border Frame;      // the page's box in the sheet (turned size)
        public required Grid Inner;        // unturned page, rotated by a layout transform
        public required Image Low, High;
        public required Canvas Hits, Marks;
        public double X, Y, W, H;          // in the sheet (DIPs), turned
        public int HighWidth;
        public bool LowAsked, HighAsked;
    }

    private void BuildSlots()
    {
        _sheet.Children.Clear();
        _slots.Clear();
        for (int i = 0; i < _pages.Count; i++)
        {
            var r = _pages[i];
            var low = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            var high = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(low, BitmapScalingMode.HighQuality);
            var hits = new Canvas { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            var marks = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent, ClipToBounds = true };
            var inner = new Grid();
            inner.Children.Add(low);
            inner.Children.Add(high);
            inner.Children.Add(hits);
            inner.Children.Add(marks);
            var frame = new Border
            {
                Child = inner,
                Background = new SolidColorBrush(ToColor(_theme.Paper)),
                Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 2, Direction = 270, Opacity = 0.35 },
                SnapsToDevicePixels = true,
            };
            var slot = new Slot { Ref = r, Frame = frame, Inner = inner, Low = low, High = high, Hits = hits, Marks = marks };
            _slots.Add(slot);
            _sheet.Children.Add(frame);
            int index = i;
            marks.Tag = index;
            LayerReady?.Invoke(index, marks);
        }
        ShowHits();
    }

    private double Scale => _zoom * PointsToDip;

    /// <summary>Places every page for the current zoom, turns and mode (one column, or a grid in Overview).</summary>
    private void Relayout(bool keep)
    {
        (int Index, double Fraction) anchor = keep ? Anchor() : (Current, 0);
        double scale = _overview ? OverviewScale() : Scale;
        double viewW = Math.Max(100, _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth);
        int columns = 1;
        if (_overview && _slots.Count > 0)
        {
            double cell = _slots.Max(s => Turned(s).W) * scale + Gap;
            columns = Math.Max(1, (int)((viewW - 2 * Pad + Gap) / cell));
        }
        double y = Pad, maxW = 0;
        for (int i = 0; i < _slots.Count; i += columns)
        {
            double rowH = 0, rowW = 0;
            for (int c = 0; c < columns && i + c < _slots.Count; c++)
            {
                var s = _slots[i + c];
                var (tw, th) = Turned(s);
                s.W = tw * scale;
                s.H = th * scale;
                rowH = Math.Max(rowH, s.H);
                rowW += s.W + (c > 0 ? Gap : 0);
            }
            maxW = Math.Max(maxW, rowW);
            double x0 = Math.Max(Pad, (viewW - rowW) / 2);
            double x = x0;
            for (int c = 0; c < columns && i + c < _slots.Count; c++)
            {
                var s = _slots[i + c];
                s.X = x;
                s.Y = y + (rowH - s.H) / 2;
                x += s.W + Gap;
                Place(s, scale);
            }
            y += rowH + Gap;
        }
        _sheet.Width = Math.Max(viewW, maxW + 2 * Pad);
        _sheet.Height = y + Pad - Gap;
        _generation++;
        foreach (var s in _slots) { s.LowAsked = s.HighAsked = false; if (!_overview) s.HighWidth = s.High.Source is null ? 0 : s.HighWidth; }
        ShowHits();
        if (keep) RestoreAnchor(anchor);
        Pump();
    }

    private (double W, double H) Turned(Slot s)
    {
        var size = s.Ref.Pdf.Size(s.Ref.Page);
        int turns = (s.Ref.Turns + _viewTurns) & 3;
        return (turns & 1) == 1 ? (size.Height, size.Width) : (size.Width, size.Height);
    }

    private void Place(Slot s, double scale)
    {
        var size = s.Ref.Pdf.Size(s.Ref.Page);
        s.Inner.Width = size.Width * scale;
        s.Inner.Height = size.Height * scale;
        int turns = (s.Ref.Turns + _viewTurns) & 3;
        s.Inner.LayoutTransform = turns == 0 ? Transform.Identity : new RotateTransform(90 * turns);
        foreach (var layer in new[] { s.Hits, s.Marks })
        {
            layer.Width = size.Width;
            layer.Height = size.Height;
            layer.RenderTransform = new ScaleTransform(scale, scale);
        }
        Canvas.SetLeft(s.Frame, s.X);
        Canvas.SetTop(s.Frame, s.Y);
        s.Frame.Width = s.W;
        s.Frame.Height = s.H;
    }

    private (int Index, double Fraction) Anchor()
    {
        if (_slots.Count == 0) return (0, 0);
        int i = Math.Clamp(Current, 0, _slots.Count - 1);
        var s = _slots[i];
        double frac = s.H > 0 ? (_scroll.VerticalOffset - s.Y) / s.H : 0;
        return (i, Math.Clamp(frac, -0.5, 1));
    }

    private void RestoreAnchor((int Index, double Fraction) a)
    {
        if (a.Index < 0 || a.Index >= _slots.Count) return;
        _sheet.UpdateLayout();
        _scroll.UpdateLayout();
        var s = _slots[a.Index];
        _scroll.ScrollToVerticalOffset(Math.Max(0, s.Y + a.Fraction * s.H));
    }

    // ---- zoom, turns, overview ---------------------------------------------------------------------

    public void SetZoom(double zoom) { _fitWidth = false; ApplyZoom(zoom, keepCentre: true); }

    public void ZoomBy(double factor) => SetZoom(_zoom * factor);

    public void FitWidth() { _fitWidth = true; ApplyZoom(FitWidthZoom(), keepCentre: true); }

    public void FitPage()
    {
        _fitWidth = false;
        if (_slots.Count == 0) return;
        var (w, h) = Turned(_slots[Current]);
        double vw = Math.Max(50, _scroll.ViewportWidth - 2 * Pad), vh = Math.Max(50, _scroll.ViewportHeight - 2 * Gap);
        ApplyZoom(Math.Min(vw / (w * PointsToDip), vh / (h * PointsToDip)), keepCentre: false);
        GoTo(Current, record: false);
    }

    private double FitWidthZoom()
    {
        if (_slots.Count == 0) return 1;
        double widest = _slots.Max(s => Turned(s).W);
        double vw = (_scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth) - 2 * Pad;
        return Math.Clamp(vw / (widest * PointsToDip), 0.1, 8);
    }

    private double OverviewScale()
    {
        if (_slots.Count == 0) return 0.2;
        double widest = _slots.Max(s => Turned(s).W);
        return 150 / widest; // about 150 DIPs a page
    }

    private void ApplyZoom(double zoom, bool keepCentre, Point? around = null)
    {
        zoom = Math.Clamp(zoom, 0.1, 8);
        if (_overview) { _overview = false; }
        if (around is { } p && _slots.Count > 0 && Math.Abs(zoom - _zoom) > 1e-6)
        {
            // Keep the point under the pointer where it is.
            double docX = (_scroll.HorizontalOffset + p.X) / _zoom, docY = (_scroll.VerticalOffset + p.Y) / _zoom;
            _zoom = zoom;
            Relayout(keep: false);
            _scroll.UpdateLayout();
            _scroll.ScrollToHorizontalOffset(Math.Max(0, docX * _zoom - p.X));
            _scroll.ScrollToVerticalOffset(Math.Max(0, docY * _zoom - p.Y));
        }
        else
        {
            _zoom = zoom;
            Relayout(keep: keepCentre);
        }
        ZoomChanged?.Invoke();
    }

    public void Turn(int quarter)
    {
        _viewTurns = (_viewTurns + quarter + 4) & 3;
        if (_fitWidth) _zoom = FitWidthZoom();
        Relayout(keep: true);
        ZoomChanged?.Invoke();
    }

    public void SetOverview(bool on)
    {
        if (_overview == on) return;
        int keep = Current;
        _overview = on;
        Relayout(keep: false);
        GoTo(keep, record: false);
        ZoomChanged?.Invoke();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        e.Handled = true;
        _fitWidth = false;
        ApplyZoom(_zoom * (e.Delta > 0 ? 1.15 : 1 / 1.15), keepCentre: false, around: e.GetPosition(_scroll));
    }

    // ---- moving around -----------------------------------------------------------------------------

    /// <summary>Scrolls to a page (0-based), optionally to a height on it (points from its top).</summary>
    public void GoTo(int index, double? pointsFromTop = null, bool record = true)
    {
        if (_slots.Count == 0) return;
        index = Math.Clamp(index, 0, _slots.Count - 1);
        if (record) Remember();
        var s = _slots[index];
        double y = s.Y - Gap / 2;
        if (pointsFromTop is { } py && !_overview) y = s.Y + py * Scale - 24;
        _scroll.ScrollToVerticalOffset(Math.Max(0, y));
        if (_overview) _scroll.ScrollToHorizontalOffset(0);
        _current = index;
        CurrentChanged?.Invoke(index);
    }

    public void Next() => GoTo(Current + 1, record: false);

    public void Previous() => GoTo(Current - 1, record: false);

    public void PageDown() => _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + _scroll.ViewportHeight * 0.9);

    public void PageUp() => _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - _scroll.ViewportHeight * 0.9);

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    public event Action? HistoryChanged;

    private void Remember()
    {
        if (_slots.Count == 0) return;
        _back.Push((Current, Anchor().Fraction));
        _forward.Clear();
        HistoryChanged?.Invoke();
    }

    public void Back()
    {
        if (_back.Count == 0) return;
        _forward.Push((Current, Anchor().Fraction));
        var (i, f) = _back.Pop();
        RestoreAnchor((i, f));
        HistoryChanged?.Invoke();
    }

    public void Forward()
    {
        if (_forward.Count == 0) return;
        _back.Push((Current, Anchor().Fraction));
        var (i, f) = _forward.Pop();
        RestoreAnchor((i, f));
        HistoryChanged?.Invoke();
    }

    /// <summary>Where the view is, for remembering (page index and how far down it, 0–1).</summary>
    public (int Index, double Fraction) Position => Anchor();

    public void SetPosition(int index, double fraction) => RestoreAnchor((Math.Clamp(index, 0, Math.Max(0, _slots.Count - 1)), fraction));

    private void OnScrolled(object sender, ScrollChangedEventArgs e)
    {
        _lastScroll = DateTime.UtcNow;
        UpdateCurrent();
        _settle.Stop();
        _settle.Start();
        Pump();
    }

    private void UpdateCurrent()
    {
        if (_slots.Count == 0) return;
        double probe = _scroll.VerticalOffset + _scroll.ViewportHeight * 0.35;
        int best = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].Y <= probe) best = i;
            else break;
        }
        if (best != _current)
        {
            _current = best;
            CurrentChanged?.Invoke(best);
        }
    }

    // ---- rendering ---------------------------------------------------------------------------------

    /// <summary>Renders what is in view: quick low-resolution pages first, sharp ones once scrolling settles.</summary>
    private async void Pump()
    {
        if (_pumping || _slots.Count == 0) return;
        _pumping = true;
        try
        {
            while (true)
            {
                var job = NextJob();
                if (job is null) break;
                var (slot, width, low) = job.Value;
                int generation = _generation;
                var r = slot.Ref;
                var theme = _theme;
                int turns = 0; // pages are rendered unturned; the layout transform turns them
                var size = r.Pdf.Size(r.Page);
                int pxW = Math.Max(8, width);
                int pxH = Math.Max(8, (int)Math.Round(pxW * size.Height / size.Width));
                if (low) slot.LowAsked = true; else slot.HighAsked = true;
                BitmapSource? bitmap = null;
                try
                {
                    bitmap = await Task.Run(() => Render(r, pxW, pxH, theme, turns));
                }
                catch (Exception ex) when (ex is Toolbox.Core.ProcessingException or OutOfMemoryException)
                {
                    Log.Warn($"Viewer could not render page {r.Page}: {ex.Message}");
                }
                if (generation != _generation || bitmap is null) continue;
                if (low) slot.Low.Source = bitmap;
                else
                {
                    slot.High.Source = bitmap;
                    slot.HighWidth = pxW;
                }
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private static BitmapSource Render(PageRef r, int w, int h, PageTheme theme, int turns)
    {
        var pixels = r.Pdf.Document.RenderBgra(r.Page, w, h);
        if (!theme.IsPlain)
        {
            List<(int, int, int, int)>? keep = null;
            if (theme.KeepPictures)
            {
                keep = [];
                var map = r.Pdf.Map(r.Page);
                var size = r.Pdf.Size(r.Page);
                double k = w / size.Width;
                foreach (var box in r.Pdf.Pictures(r.Page))
                {
                    var (x, y, bw, bh) = map.ToDisplay(box);
                    keep.Add(((int)Math.Floor(x * k), (int)Math.Floor(y * k), (int)Math.Ceiling(bw * k) + 1, (int)Math.Ceiling(bh * k) + 1));
                }
            }
            theme.Apply(pixels, w, h, keep);
        }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, w * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The most useful render to do next, or null: visible pages first (nearest the middle), low before sharp.</summary>
    private (Slot Slot, int Width, bool Low)? NextJob()
    {
        if (_slots.Count == 0 || !IsVisible) return null;
        double top = _scroll.VerticalOffset, bottom = top + Math.Max(1, _scroll.ViewportHeight), middle = (top + bottom) / 2;
        double margin = _scroll.ViewportHeight * (_overview ? 1.5 : 0.75);
        var visible = _slots.Where(s => s.Y + s.H >= top - margin && s.Y <= bottom + margin).OrderBy(s => Math.Abs(s.Y + s.H / 2 - middle)).ToList();
        foreach (var s in visible)
            if (s.Low.Source is null && !s.LowAsked) return (s, (int)LowWidth, true);
        bool settled = (DateTime.UtcNow - _lastScroll).TotalMilliseconds > 120;
        if (!settled || _overview) return null;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (var s in visible.Where(s => s.Y + s.H >= top && s.Y <= bottom))
        {
            var size = s.Ref.Pdf.Size(s.Ref.Page);
            int want = (int)Math.Round(size.Width * Scale * dpi);
            if (want <= LowWidth * 1.1) continue;
            if (!s.HighAsked && Math.Abs(s.HighWidth - want) > 2) return (s, Math.Min(want, 6000), false);
        }
        // Far from view: let go of sharp renders.
        foreach (var s in _slots.Where(s => s.Y + s.H < top - 3 * Math.Max(400, _scroll.ViewportHeight) || s.Y > bottom + 3 * Math.Max(400, _scroll.ViewportHeight)))
        {
            s.High.Source = null;
            s.HighWidth = 0;
            s.HighAsked = false;
        }
        return null;
    }

    // ---- search hits -------------------------------------------------------------------------------

    /// <summary>Highlights search hits (on whatever pages show them); <paramref name="current"/> is drawn stronger and scrolled to.</summary>
    public void SetHits(IReadOnlyList<(OpenPdf Pdf, int Page, IReadOnlyList<PdfBox> Boxes)> hits, int current)
    {
        _hits = hits.Select(h => ((PageRef?)null, h.Pdf, h.Page, h.Boxes)).ToList();
        _currentHit = current;
        ShowHits();
        if (current >= 0 && current < hits.Count)
        {
            var h = hits[current];
            int index = IndexOf(h.Pdf, h.Page);
            if (index >= 0)
            {
                var map = h.Pdf.Map(h.Page);
                var (_, y, _, _) = map.ToDisplay(h.Boxes[0]);
                bool turned = ((_slots[index].Ref.Turns + _viewTurns) & 3) != 0;
                GoTo(index, turned ? null : Math.Max(0, y - 60));
            }
        }
    }

    public int IndexOf(OpenPdf pdf, int page)
    {
        for (int i = 0; i < _slots.Count; i++) if (_slots[i].Ref.Pdf == pdf && _slots[i].Ref.Page == page) return i;
        return -1;
    }

    private void ShowHits()
    {
        foreach (var s in _slots) s.Hits.Children.Clear();
        for (int k = 0; k < _hits.Count; k++)
        {
            var (_, pdf, page, boxes) = _hits[k];
            int index = IndexOf(pdf, page);
            if (index < 0) continue;
            var map = pdf.Map(page);
            bool current = k == _currentHit;
            foreach (var box in boxes)
            {
                var (x, y, w, h) = map.ToDisplay(box);
                var rect = new Rectangle
                {
                    Width = Math.Max(1, w + 2),
                    Height = Math.Max(1, h + 2),
                    Fill = new SolidColorBrush(current ? Color.FromArgb(0x80, 0xFF, 0x8A, 0x00) : Color.FromArgb(0x5C, 0xFF, 0xD6, 0x00)),
                    RadiusX = 1.5,
                    RadiusY = 1.5,
                };
                Canvas.SetLeft(rect, x - 1);
                Canvas.SetTop(rect, y - 1);
                _slots[index].Hits.Children.Add(rect);
            }
        }
    }

    // ---- the mouse: the editor's tools, or links -----------------------------------------------------

    private (int Index, Point At)? Hit(MouseEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return null;
        for (var v = d; v is not null; v = v is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(v) : null)
        {
            if (v is Canvas c && c.Tag is int index && index < _slots.Count && _slots[index].Marks == c)
                return (index, e.GetPosition(c));
            if (v == _sheet) break;
        }
        return null;
    }

    private void OnSheetDown(object sender, MouseButtonEventArgs e)
    {
        var hit = Hit(e);
        if (hit is null) return;
        if (_overview)
        {
            SetOverview(false);
            GoTo(hit.Value.Index);
            e.Handled = true;
            return;
        }
        if (Editing)
        {
            PageMouseDown?.Invoke(hit.Value.Index, hit.Value.At, e);
            return;
        }
        _pendingLink = hit;
    }

    private void OnSheetMove(object sender, MouseEventArgs e)
    {
        var hit = Hit(e);
        if (Editing)
        {
            if (hit is { } h) PageMouseMove?.Invoke(h.Index, h.At, e);
            _sheet.Cursor = EditCursor;
            return;
        }
        if (hit is null) { _sheet.Cursor = null; _sheet.ToolTip = null; return; }
        _pendingLink = null;
        _linkAt = hit.Value.At;
        _linkSlot = hit.Value.Index;
        _linkTimer.Stop();
        _linkTimer.Start();
    }

    private int _linkSlot = -1;

    private void OnSheetUp(object sender, MouseButtonEventArgs e)
    {
        var hit = Hit(e);
        if (Editing)
        {
            if (hit is { } h) PageMouseUp?.Invoke(h.Index, h.At, e);
            return;
        }
        if (hit is null || _pendingLink is null || _overview) return;
        _pendingLink = null;
        var link = LinkAt(hit.Value.Index, hit.Value.At);
        if (link is null) return;
        if (link.Uri is { } uri)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto))
                Pulse.PulseLaunch.Open(u.ToString());
        }
        else if (link.Page is { } page) Peek(_slots[hit.Value.Index].Ref.Pdf, page, link.UserY);
        e.Handled = true;
    }

    private PdfLinkTarget? LinkAt(int index, Point at)
    {
        var r = _slots[index].Ref;
        var (ux, uy) = r.Pdf.Map(r.Page).ToUser(at.X, at.Y);
        try { return r.Pdf.Document.GetLinkAt(r.Page, ux, uy); }
        catch (Toolbox.Core.ProcessingException) { return null; }
    }

    private void CheckLink()
    {
        if (_linkAt is not { } at || _linkSlot < 0 || _linkSlot >= _slots.Count || Editing) return;
        var link = LinkAt(_linkSlot, at);
        _sheet.Cursor = link is null ? null : Cursors.Hand;
        _sheet.ToolTip = link switch
        {
            { Uri: { } uri } => uri,
            { Page: { } p } => Lang.T($"Page {p}: click to peek", $"الصفحة {p}: انقر لإلقاء نظرة"),
            _ => null,
        };
    }

    /// <summary>A card with the place a link leads to (a footnote, a figure), so it can be read and closed without losing the place.</summary>
    private async void Peek(OpenPdf pdf, int page, double? userY)
    {
        var size = pdf.Size(page);
        var map = pdf.Map(page);
        double top = 0;
        if (userY is { } uy) top = Math.Max(0, map.ToDisplay(map.ToUser(0, 0).X, uy).Y - 30);
        double band = Math.Min(size.Height - top, 320);
        if (band < 120) { top = Math.Max(0, size.Height - 320); band = Math.Min(size.Height, 320); }
        const int width = 620;
        int height = (int)Math.Round(width * size.Height / size.Width);
        var theme = _theme;
        BitmapSource full;
        try
        {
            full = await Task.Run(() => Render(new PageRef(pdf, page), width, height, theme, 0));
        }
        catch (Toolbox.Core.ProcessingException)
        {
            return;
        }
        double k = width / size.Width;
        var crop = new CroppedBitmap(full, new Int32Rect(0, (int)(top * k), width, Math.Max(1, Math.Min(height - (int)(top * k), (int)(band * k)))));
        var card = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(12, 10, 8, 6) };
        var go = SettingsPage.Button(Lang.T($"Go to page {page}", $"الذهاب إلى الصفحة {page}"), () =>
        {
            _peek.IsOpen = false;
            int index = IndexOf(pdf, page);
            if (index >= 0) GoTo(index, top);
        }, accent: true);
        DockPanel.SetDock(go, Dock.Right);
        head.Children.Add(go);
        head.Children.Add(new TextBlock { Text = Lang.T($"Page {page}", $"الصفحة {page}"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text")));
        card.Children.Add(head);
        card.Children.Add(new Image { Source = crop, Width = 520, Stretch = Stretch.Uniform, Margin = new Thickness(10, 0, 10, 10) });
        var border = new Border { Child = card, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.45 }, Margin = new Thickness(12) };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        _peek.Child = border;
        _peek.IsOpen = true;
    }

    private static Color ToColor(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
