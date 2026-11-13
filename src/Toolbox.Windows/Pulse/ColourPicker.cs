using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Toolbox.Core.Pulse;
using Frame = Toolbox.Processing.Capture.Frame;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse;

/// <summary>
/// The Colour picker tile. The screen is photographed once as picking starts and shown, frozen, in
/// an ordinary (not see-through) window over every monitor, with a cross pointer and a magnifier
/// beside it: the pixels around the pointer (the framed one in the middle is the colour) and its
/// HEX code. Each frame of the screen's refresh the magnifier is moved to the pointer and filled
/// from the photo in memory, so it follows smoothly, the colours are exact (nothing of the picker
/// is in the photo) and hover effects underneath can't change what is picked.
/// A click picks; Shift+click also asks for the Pro window. Arrow keys move the pointer one pixel
/// (ten with Shift), Enter picks, the wheel zooms, Esc or a right click cancels.
/// </summary>
/// <remarks>
/// Before 0.5.6 the picker was a see-through (layered) window the size of the whole screen with the
/// magnifier inside it, and it read the screen on every mouse move. Windows redraws a layered
/// window completely whenever anything in it changes, so each move pushed a full-screen picture:
/// the magnifier lagged behind the pointer and left ghosts (the owner's video, 13 Nov 2026).
/// </remarks>
internal sealed class ColourPicker : Window
{
    private const int Cells = 11;                 // pixels across the magnifier (odd: one in the middle)
    private static readonly int[] Zooms = [9, 12, 15];
    private const double Gap = 18;                // DIPs between the pointer and the magnifier

    private readonly Frame _shot;
    private readonly PixelRect _bounds;
    private readonly Canvas _canvas = new();
    private readonly StackPanel _loupe = new() { IsHitTestVisible = false };
    private readonly TranslateTransform _at = new();
    private readonly Grid _lens = new();
    private readonly Image _pixels = new() { Stretch = Stretch.Fill };
    private readonly WriteableBitmap _cells = new(Cells, Cells, 96, 96, PixelFormats.Bgr32, null);
    private readonly byte[] _cellBytes = new byte[Cells * Cells * 4];
    private readonly Rectangle _grid = new();
    private readonly Border _centre = new() { BorderThickness = new Thickness(1.5), BorderBrush = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Ellipse _ring = new() { StrokeThickness = 2.5, Stroke = Brushes.White };
    private readonly Border _swatch = new() { Width = 16, Height = 16, CornerRadius = new CornerRadius(4.5), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hex = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
    private Native.POINT _last = new() { X = int.MinValue, Y = int.MinValue };
    private int _zoom = 1;
    private Rgb _current;
    private bool _done;
    private bool _following;

    /// <param name="pro">Pro mode: no "Shift+click" hint (every pick opens the window anyway).</param>
    public ColourPicker(bool pro) : this(pro, GdiScreenSource.CaptureVirtualScreen())
    {
    }

    /// <summary>With a given photo of the screen (and where it lies), for tests.</summary>
    internal ColourPicker(bool pro, (Frame Frame, PixelRect Bounds) shot)
    {
        _shot = shot.Frame;
        _bounds = shot.Bounds;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        Cursor = Cursors.Cross;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        FlowDirection = FlowDirection.LeftToRight;
        // The frozen screen is the window's own background, so its first frame already shows it.
        var photo = new ImageBrush(LongshotOutput.ToBitmap(_shot.Width, _shot.Height, _shot.Pixels)) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(photo, BitmapScalingMode.NearestNeighbor);
        photo.Freeze();
        Background = photo;

        // The lens: the pixels, a faint grid, the middle pixel framed, a ring.
        _pixels.Source = _cells;
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
        _loupe.RenderTransform = _at;
        _loupe.Visibility = Visibility.Hidden; // until it knows where the pointer is
        _canvas.Children.Add(_loupe);
        Content = _canvas;
        SizeLens();

        MouseLeftButtonUp += (_, _) => Pick(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        MouseRightButtonUp += (_, _) => Cancel();
        MouseWheel += (_, e) =>
        {
            _zoom = Math.Clamp(_zoom + Math.Sign(e.Delta), 0, Zooms.Length - 1);
            SizeLens();
            _last = new Native.POINT { X = int.MinValue, Y = int.MinValue }; // place it again at the new size
        };
        KeyDown += OnKey;
        Deactivated += (_, _) => Cancel();
        Closed += (_, _) => Follow(false);
    }

    /// <summary>A colour was picked; true when the Pro window was asked for (Shift).</summary>
    public event Action<Rgb, bool>? Picked;

    public event Action? Cancelled;

    /// <summary>Where the colour was picked (physical pixels), for placing the Pro window beside it.</summary>
    public PixelPoint PickedAt { get; private set; }

    /// <summary>For tests: the magnifier (lens and code), as drawn beside the pointer.</summary>
    internal FrameworkElement Loupe => _loupe;

    internal Rgb Current => _current;

    /// <summary>Pixels across the magnifier (an odd number: the colour is the middle one).</summary>
    public const int PixelsAcross = Cells;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.ExcludeFromCapture(hwnd);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, Native.SWP_SHOWWINDOW);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Activate();
        Focus();
        Follow(true);
    }

    /// <summary>Moves the magnifier with the pointer once per frame the screen draws (and only when it moved).</summary>
    private void Follow(bool on)
    {
        if (on == _following) return;
        _following = on;
        if (on) CompositionTarget.Rendering += OnFrame;
        else CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_done) return;
        var p = ScreenGeometry.CursorPosition();
        if (p.X == _last.X && p.Y == _last.Y) return;
        _last = p;
        ShowAt(new PixelPoint(p.X, p.Y));
        Place(p);
        _loupe.Visibility = Visibility.Visible;
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

    private static void Nudge(int dx, int dy)
    {
        var p = ScreenGeometry.CursorPosition();
        Native.SetCursorPos(p.X + dx, p.Y + dy); // the next frame follows it
    }

    private void Pick(bool pro)
    {
        if (_done) return;
        var p = ScreenGeometry.CursorPosition();
        ShowAt(new PixelPoint(p.X, p.Y)); // exactly where it was clicked, even between frames
        _done = true;
        PickedAt = new PixelPoint(p.X, p.Y);
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

    /// <summary>
    /// Fills the magnifier from the photo around <paramref name="p"/> (physical pixels) and shows the
    /// middle pixel's code. Pixels off the photo (between monitors of different sizes) stay black.
    /// </summary>
    internal void ShowAt(PixelPoint p)
    {
        const int half = Cells / 2;
        Array.Clear(_cellBytes);
        for (int y = 0; y < Cells; y++)
        {
            int sy = p.Y - half + y - _bounds.Y;
            if (sy < 0 || sy >= _shot.Height) continue;
            for (int x = 0; x < Cells; x++)
            {
                int sx = p.X - half + x - _bounds.X;
                if (sx < 0 || sx >= _shot.Width) continue;
                Buffer.BlockCopy(_shot.Pixels, (sy * _shot.Width + sx) * 4, _cellBytes, (y * Cells + x) * 4, 4);
            }
        }
        _cells.WritePixels(new Int32Rect(0, 0, Cells, Cells), _cellBytes, Cells * 4, 0);
        int i = (half * Cells + half) * 4;
        var c = new Rgb(_cellBytes[i + 2], _cellBytes[i + 1], _cellBytes[i]);
        if (c == _current && _hex.Text.Length > 0) return;
        _current = c;
        _swatch.Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        _hex.Text = c.Hex;
        // The ring takes the colour's opposite tone, so it shows on light and dark.
        _ring.Stroke = Colours.TextOn(c) == new Rgb(255, 255, 255) ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x15, 0x17, 0x1C));
    }

    /// <summary>Beside the pointer: right and below when there is room on its screen, otherwise left or above.</summary>
    private void Place(Native.POINT p)
    {
        // The window starts at the virtual screen's corner and draws one DIP per (pixel / scale).
        double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _loupe.DesiredSize;
        var work = ScreenGeometry.MonitorAt(p).WorkArea;
        var area = new Rect((work.Left - _bounds.X) / k, (work.Top - _bounds.Y) / k, work.Width / k, work.Height / k);
        var at = new Point((p.X - _bounds.X) / k, (p.Y - _bounds.Y) / k);
        var spot = Beside(at, size, area, Gap);
        _at.X = Math.Round(spot.X);
        _at.Y = Math.Round(spot.Y);
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
