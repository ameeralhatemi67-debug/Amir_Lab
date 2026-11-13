using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Toolbox.Core.Pulse;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse;

/// <summary>
/// The Colour picker tile: the screen stays as it is, with a cross pointer and a magnifier beside
/// it showing the pixels around the pointer (the framed one in the middle is the colour) and its
/// HEX code. A click picks; Shift+click also asks for the Pro window. Arrow keys move the pointer
/// one pixel (ten with Shift), Enter picks, the wheel zooms, Esc or a right click cancels. The
/// magnifier keeps to whichever side of the pointer has room on its screen.
/// </summary>
internal sealed class ColourPicker : Window
{
    private const int Cells = 11;                 // pixels across the magnifier (odd: one in the middle)
    private static readonly int[] Zooms = [9, 12, 15];
    private const double Gap = 22;                // DIPs between the pointer and the magnifier

    private readonly Canvas _canvas = new();
    private readonly StackPanel _loupe = new() { IsHitTestVisible = false };
    private readonly Grid _lens = new();
    private readonly Image _pixels = new() { Stretch = Stretch.Fill };
    private readonly Rectangle _grid = new();
    private readonly Border _centre = new() { BorderThickness = new Thickness(1.5), BorderBrush = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _ring = new() { StrokeThickness = 2.5, Stroke = Brushes.White };
    private readonly Border _swatch = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(4.5), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hex = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private int _zoom = 1;
    private Rgb _current;
    private bool _done;

    /// <param name="pro">Pro mode: no "Shift+click" hint (every pick opens the window anyway).</param>
    public ColourPicker(bool pro)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // takes the clicks, invisible
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        FlowDirection = FlowDirection.LeftToRight;

        // The lens: the pixels, a faint grid, the middle pixel framed, a white ring.
        RenderOptions.SetBitmapScalingMode(_pixels, BitmapScalingMode.NearestNeighbor);
        _lens.Children.Add(_pixels);
        _lens.Children.Add(_grid);
        _centre.Child = new Border { BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)) };
        _lens.Children.Add(_centre);
        _lens.Children.Add(_ring);
        _lens.Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Direction = 270, Opacity = 0.45 };
        _loupe.Children.Add(_lens);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_swatch);
        row.Children.Add(_hex);
        var words = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        words.Children.Add(row);
        if (!pro)
        {
            var hint = new TextBlock { Text = Lang.T("Shift+click: more", "Shift+نقر: المزيد"), FontSize = 10.5, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, FlowDirection = Lang.Flow };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
            words.Children.Add(hint);
        }
        var pill = new Border { Child = words, Padding = new Thickness(10, 6, 12, 6), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        pill.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        pill.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        pill.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Direction = 270, Opacity = 0.3 };
        _hex.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _loupe.Children.Add(pill);
        _canvas.Children.Add(_loupe);
        Content = _canvas;
        SizeLens();

        MouseMove += (_, _) => Track();
        MouseLeftButtonUp += (_, _) => Pick(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        MouseRightButtonUp += (_, _) => Cancel();
        MouseWheel += (_, e) =>
        {
            _zoom = Math.Clamp(_zoom + Math.Sign(e.Delta), 0, Zooms.Length - 1);
            SizeLens();
            Track();
        };
        KeyDown += OnKey;
        Deactivated += (_, _) => Cancel();
    }

    /// <summary>A colour was picked; true when the Pro window was asked for (Shift).</summary>
    public event Action<Rgb, bool>? Picked;

    public event Action? Cancelled;

    /// <summary>Where the colour was picked (physical pixels), for placing the Pro window beside it.</summary>
    public PixelPoint PickedAt { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.ExcludeFromCapture(hwnd); // reading the screen sees what is under it
        int x = Native.GetSystemMetrics(76), y = Native.GetSystemMetrics(77), w = Native.GetSystemMetrics(78), h = Native.GetSystemMetrics(79);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_SHOWWINDOW);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Activate();
        Focus();
        Track();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        int step = shift ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape: Cancel(); break;
            case Key.Enter or Key.Space: Pick(shift); break;
            case Key.Left: Nudge(-step, 0); break;
            case Key.Right: Nudge(step, 0); break;
            case Key.Up: Nudge(0, -step); break;
            case Key.Down: Nudge(0, step); break;
            default: return;
        }
        e.Handled = true;
    }

    private void Nudge(int dx, int dy)
    {
        var p = ScreenGeometry.CursorPosition();
        Native.SetCursorPos(p.X + dx, p.Y + dy);
        Track();
    }

    private void Pick(bool pro)
    {
        if (_done) return;
        _done = true;
        PickedAt = PixelPoint.From(ScreenGeometry.CursorPosition());
        var colour = _current;
        Close();
        Picked?.Invoke(colour, pro);
    }

    private void Cancel()
    {
        if (_done) return;
        _done = true;
        Close();
        Cancelled?.Invoke();
    }

    private void SizeLens()
    {
        int z = Zooms[_zoom];
        double size = Cells * z;
        _lens.Width = _lens.Height = size;
        _lens.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        _centre.Width = _centre.Height = z + 3;
        var line = new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(0x2E, 0, 0, 0)), 0.8), new RectangleGeometry(new Rect(0, 0, z, z)));
        _grid.Fill = new DrawingBrush(line)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            Viewport = new Rect(0, 0, z, z),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, z, z),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
    }

    /// <summary>Reads the pixels around the pointer and moves the magnifier beside it.</summary>
    private void Track()
    {
        var p = ScreenGeometry.CursorPosition();
        const int half = Cells / 2;
        try
        {
            using var source = new GdiScreenSource(new PixelRect(p.X - half, p.Y - half, Cells, Cells));
            ShowPixels(source.Grab());
        }
        catch (InvalidOperationException)
        {
            // The screen could not be read (a secure desktop): keep the last colour.
        }
        Place(p);
    }

    /// <summary>Shows the pixels around the pointer (<see cref="Cells"/> square, BGRA) and the middle one's code.</summary>
    internal void ShowPixels(Toolbox.Processing.Capture.Frame frame)
    {
        const int half = Cells / 2;
        _pixels.Source = LongshotOutput.ToBitmap(frame.Width, frame.Height, frame.Pixels);
        int i = (half * frame.Width + half) * 4;
        _current = new Rgb(frame.Pixels[i + 2], frame.Pixels[i + 1], frame.Pixels[i]);
        _swatch.Background = new SolidColorBrush(Color.FromRgb(_current.R, _current.G, _current.B));
        _hex.Text = _current.Hex;
        // The ring takes the picked colour's opposite tone, so it shows on light and dark.
        _ring.Stroke = Colours.TextOn(_current) == new Rgb(255, 255, 255) ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1C));
    }

    /// <summary>For tests: the magnifier (lens and code), as drawn beside the pointer.</summary>
    internal FrameworkElement Loupe => _loupe;

    internal Rgb Current => _current;

    /// <summary>Pixels across the magnifier (an odd number: the colour is the middle one).</summary>
    public const int PixelsAcross = Cells;

    /// <summary>Beside the pointer: right and below when there is room on its screen, otherwise left or above.</summary>
    private void Place(Native.POINT p)
    {
        if (PresentationSource.FromVisual(this) is null) return;
        _loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _loupe.DesiredSize;
        var at = PointFromScreen(new Point(p.X, p.Y));
        var work = ScreenGeometry.MonitorAt(p).WorkArea;
        var topLeft = PointFromScreen(new Point(work.Left, work.Top));
        var bottomRight = PointFromScreen(new Point(work.Right, work.Bottom));
        var spot = Beside(at, size, new Rect(topLeft, bottomRight), Gap);
        Canvas.SetLeft(_loupe, spot.X);
        Canvas.SetTop(_loupe, spot.Y);
    }

    /// <summary>
    /// Where a box of <paramref name="size"/> goes beside <paramref name="at"/> inside <paramref name="area"/>:
    /// to the right unless that runs past the area's right edge (then to the left), and below unless
    /// that runs past its bottom (then above); kept inside the area if neither side fits.
    /// </summary>
    internal static Point Beside(Point at, Size size, Rect area, double gap)
    {
        double x = at.X + gap + size.Width <= area.Right ? at.X + gap
            : at.X - gap - size.Width >= area.Left ? at.X - gap - size.Width
            : Math.Clamp(at.X + gap, area.Left, Math.Max(area.Left, area.Right - size.Width));
        double y = at.Y + gap + size.Height <= area.Bottom ? at.Y + gap
            : at.Y - gap - size.Height >= area.Top ? at.Y - gap - size.Height
            : Math.Clamp(at.Y + gap, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
        return new Point(x, y);
    }
}
