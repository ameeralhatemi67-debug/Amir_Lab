using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Processing.Pdf;

namespace Toolbox.Windows.Viewer;

/// <summary>
/// Present mode: one page at a time, as large as the screen allows, on black. Right, Down, Space,
/// Page Down or a click go on; Left, Up, Backspace or Page Up go back; Home and End jump; Esc ends.
/// The next page is prepared while the current one shows.
/// </summary>
internal sealed class PresentWindow : Window
{
    private readonly IReadOnlyList<PageRef> _pages;
    private readonly PageTheme _theme;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _counter = new() { Foreground = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), FontSize = 13, Margin = new Thickness(0, 0, 18, 14), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Dictionary<int, BitmapSource> _ready = [];
    private int _index;

    public PresentWindow(IReadOnlyList<PageRef> pages, int start, PageTheme theme)
    {
        _pages = pages;
        _theme = theme;
        _index = Math.Clamp(start, 0, Math.Max(0, pages.Count - 1));
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.None;
        RenderRotateIfNeeded();
        var root = new Grid();
        root.Children.Add(_image);
        root.Children.Add(_counter);
        Content = root;
        KeyDown += OnKey;
        MouseLeftButtonUp += (_, _) => Go(_index + 1);
        MouseRightButtonUp += (_, _) => Go(_index - 1);
        MouseMove += (_, _) => Cursor = null;
        Loaded += (_, _) => Go(_index);
    }

    private void RenderRotateIfNeeded() => RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right or Key.Down or Key.Space or Key.PageDown or Key.Enter: Go(_index + 1); break;
            case Key.Left or Key.Up or Key.Back or Key.PageUp: Go(_index - 1); break;
            case Key.Home: Go(0); break;
            case Key.End: Go(_pages.Count - 1); break;
            case Key.Escape: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void Go(int index)
    {
        if (_pages.Count == 0) return;
        _index = Math.Clamp(index, 0, _pages.Count - 1);
        _counter.Text = $"{_index + 1} / {_pages.Count}";
        _image.Source = await Prepare(_index);
        if (_index + 1 < _pages.Count) _ = Prepare(_index + 1);
    }

    private async Task<BitmapSource?> Prepare(int index)
    {
        if (_ready.TryGetValue(index, out var done)) return done;
        var r = _pages[index];
        var size = r.Pdf.Size(r.Page);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double screenW = ActualWidth * dpi, screenH = ActualHeight * dpi;
        bool turned = (r.Turns & 1) == 1;
        double pw = turned ? size.Height : size.Width, ph = turned ? size.Width : size.Height;
        double k = Math.Min(screenW / pw, screenH / ph);
        int w = Math.Max(8, (int)(size.Width * k)), h = Math.Max(8, (int)(size.Height * k));
        var theme = _theme;
        try
        {
            var bitmap = await Task.Run(() =>
            {
                var px = r.Pdf.Document.RenderBgra(r.Page, w, h);
                theme.Apply(px, w, h);
                BitmapSource b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
                if (r.Turns != 0) b = new TransformedBitmap(b, new RotateTransform(90 * r.Turns));
                b.Freeze();
                return b;
            });
            _ready[index] = bitmap;
            foreach (var old in _ready.Keys.Where(k2 => Math.Abs(k2 - _index) > 2).ToList()) _ready.Remove(old);
            return bitmap;
        }
        catch (Toolbox.Core.ProcessingException)
        {
            return null;
        }
    }
}
