using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Frame = Toolbox.Processing.Capture.Frame;
using Toolbox.Windows.Interop;

namespace Toolbox.Windows.Longshot;

/// <summary>
/// Step 1 of a capture: a frozen, dimmed snapshot of every monitor. Drag a box (it shows bright),
/// click for the whole monitor under the pointer, Esc or right-click to cancel. Coordinates are
/// converted to physical pixels for the capture.
/// </summary>
internal sealed class SelectionOverlay : Window
{
    public const int MinimumSize = 60;

    private readonly PixelRect _virtual;
    private readonly Canvas _canvas = new();
    private readonly Path _shade = new() { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)) };
    private readonly Rectangle _frame = new() { StrokeThickness = 2, Visibility = Visibility.Collapsed };
    private readonly Border _sizeTag = new() { Background = new SolidColorBrush(Color.FromArgb(0xD0, 0, 0, 0)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 3), Visibility = Visibility.Collapsed };
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly Border _hint = new() { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 9, 16, 10), BorderThickness = new Thickness(1) };
    private readonly TextBlock _hintText = new() { FontSize = 13.5, Text = "Drag over the area to capture   ·   Click for the whole screen   ·   Esc to cancel" };
    private Point? _start;
    private Point _current;
    private bool _done;

    public SelectionOverlay(Frame screenshot, PixelRect virtualScreen)
    {
        _virtual = virtualScreen;
        Title = "Longshot: choose an area";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Cursor = Cursors.Cross;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;

        var bitmap = BitmapSource.Create(screenshot.Width, screenshot.Height, 96, 96, PixelFormats.Bgr32, null, screenshot.Pixels, screenshot.Stride);
        bitmap.Freeze();
        var root = new Grid();
        root.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Fill });
        _frame.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        _sizeTag.Child = _sizeText;
        _hint.Child = _hintText;
        _hint.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        _hint.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        _hintText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _canvas.Children.Add(_shade);
        _canvas.Children.Add(_frame);
        _canvas.Children.Add(_sizeTag);
        _canvas.Children.Add(_hint);
        root.Children.Add(_canvas);
        Content = root;

        _canvas.SizeChanged += (_, _) => { Redraw(); PlaceHint(); };
        MouseLeftButtonDown += OnDown;
        MouseMove += OnMove;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Cancel();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel(); };
        Deactivated += (_, _) => { if (!_done && IsLoaded) Cancel(); }; // Alt+Tab away cancels
    }

    public event Action<PixelRect>? Selected;
    public event Action? Cancelled;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, _virtual.X, _virtual.Y, _virtual.Width, _virtual.Height, Native.SWP_SHOWWINDOW);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Activate();
        Focus();
        Keyboard.Focus(this);
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private int ToPhysicalX(double dip) => _virtual.X + (int)Math.Round(dip * Scale);
    private int ToPhysicalY(double dip) => _virtual.Y + (int)Math.Round(dip * Scale);

    private void PlaceHint()
    {
        var mouse = ScreenGeometry.CursorPosition();
        var monitor = ScreenGeometry.MonitorAt(mouse).Bounds;
        _hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double centerX = ((monitor.Left + monitor.Right) / 2.0 - _virtual.X) / Scale;
        Canvas.SetLeft(_hint, centerX - _hint.DesiredSize.Width / 2);
        Canvas.SetTop(_hint, (monitor.Top - _virtual.Y) / Scale + 28);
    }

    private Rect Box()
    {
        var s = _start ?? _current;
        return new Rect(new Point(Math.Min(s.X, _current.X), Math.Min(s.Y, _current.Y)),
                        new Point(Math.Max(s.X, _current.X), Math.Max(s.Y, _current.Y)));
    }

    private void Redraw()
    {
        var all = new RectangleGeometry(new Rect(0, 0, _canvas.ActualWidth, _canvas.ActualHeight));
        if (_start is null)
        {
            _shade.Data = all;
            _frame.Visibility = _sizeTag.Visibility = Visibility.Collapsed;
            return;
        }
        var box = Box();
        _shade.Data = new GeometryGroup { FillRule = FillRule.EvenOdd, Children = { all, new RectangleGeometry(box) } };
        _frame.Visibility = _sizeTag.Visibility = Visibility.Visible;
        Canvas.SetLeft(_frame, box.X - 1);
        Canvas.SetTop(_frame, box.Y - 1);
        _frame.Width = box.Width + 2;
        _frame.Height = box.Height + 2;
        _sizeText.Text = $"{(int)Math.Round(box.Width * Scale)} × {(int)Math.Round(box.Height * Scale)}";
        Canvas.SetLeft(_sizeTag, box.X);
        Canvas.SetTop(_sizeTag, box.Y > 30 ? box.Y - 26 : box.Y + 6);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _start = _current = e.GetPosition(_canvas);
        _hint.Visibility = Visibility.Collapsed;
        CaptureMouse();
        Redraw();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (_start is null) return;
        _current = e.GetPosition(_canvas);
        Redraw();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_start is null) return;
        _current = e.GetPosition(_canvas);
        ReleaseMouseCapture();
        var box = Box();
        PixelRect area;
        if (box.Width < 6 && box.Height < 6)
        {
            // A click: the whole monitor under the pointer.
            var monitor = ScreenGeometry.MonitorAt(new Native.POINT { X = ToPhysicalX(_current.X), Y = ToPhysicalY(_current.Y) });
            area = PixelRect.From(monitor.Bounds);
        }
        else
        {
            area = new PixelRect(ToPhysicalX(box.X), ToPhysicalY(box.Y), (int)Math.Round(box.Width * Scale), (int)Math.Round(box.Height * Scale));
            if (area.Width < MinimumSize || area.Height < MinimumSize)
            {
                _start = null;
                Redraw();
                _hintText.Text = "That's a bit small. Drag a bigger area (Esc to cancel)";
                _hint.Visibility = Visibility.Visible;
                PlaceHint();
                return;
            }
        }
        _done = true;
        Close();
        Selected?.Invoke(area);
    }

    private void Cancel()
    {
        if (_done) return;
        _done = true;
        Close();
        Cancelled?.Invoke();
    }
}
