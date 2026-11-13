using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Viewer;

/// <summary>A picture, fitted to the window at first; Ctrl+wheel or the buttons zoom, a double-click toggles fit and 100%, it can be turned.</summary>
internal sealed class ImageView : Grid
{
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private BitmapSource? _bitmap;
    private double _zoom = 1;
    private bool _fit = true;
    private int _turns;

    public ImageView()
    {
        FlowDirection = FlowDirection.LeftToRight;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        _scroll.Content = _image;
        _scroll.SetResourceReference(StyleProperty, "Lap.Scroll");
        Children.Add(_scroll);
        SizeChanged += (_, _) => Apply();
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
            e.Handled = true;
            Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15);
        };
        _image.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { if (_fit) SetZoom(1); else Fit(); } };
    }

    public event Action? ZoomChanged;

    public double ZoomLevel => _fit ? FitZoom() : _zoom;

    public bool Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            _bitmap = image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _bitmap = ShellThumbnail.Get(path, 1600); // AVIF, HEIC and others Windows can thumbnail
        }
        _image.Source = _bitmap;
        Apply();
        return _bitmap is not null;
    }

    public (int W, int H) PixelSize => _bitmap is null ? (0, 0) : (_bitmap.PixelWidth, _bitmap.PixelHeight);

    public void Fit() { _fit = true; Apply(); }

    public void SetZoom(double zoom) { _fit = false; _zoom = Math.Clamp(zoom, 0.05, 16); Apply(); }

    public void Zoom(double factor) => SetZoom(ZoomLevel * factor);

    public void Turn(int quarter) { _turns = (_turns + quarter + 4) & 3; Apply(); }

    private double FitZoom()
    {
        if (_bitmap is null) return 1;
        double w = (_turns & 1) == 1 ? _bitmap.PixelHeight : _bitmap.PixelWidth;
        double h = (_turns & 1) == 1 ? _bitmap.PixelWidth : _bitmap.PixelHeight;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double vw = Math.Max(50, ActualWidth - 24), vh = Math.Max(50, ActualHeight - 24);
        return Math.Min(1, Math.Min(vw / (w / dpi), vh / (h / dpi)));
    }

    private void Apply()
    {
        if (_bitmap is null) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double zoom = _fit ? FitZoom() : _zoom;
        _image.Width = _bitmap.PixelWidth / dpi * zoom;
        _image.Height = _bitmap.PixelHeight / dpi * zoom;
        _image.LayoutTransform = _turns == 0 ? Transform.Identity : new RotateTransform(90 * _turns);
        ZoomChanged?.Invoke();
    }
}

/// <summary>Text, Markdown, code, CSV, JSON or a log, read-only, in a monospaced font; the first 4 MB of very large files.</summary>
internal sealed class TextView : Grid
{
    public const long Limit = 4 * 1024 * 1024;
    private readonly TextBox _text = new()
    {
        IsReadOnly = true,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(18, 14, 18, 14),
        AcceptsReturn = true,
    };

    public TextView()
    {
        _text.SetResourceReference(Control.BackgroundProperty, "Brush.Content");
        _text.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        Children.Add(_text);
    }

    public bool Truncated { get; private set; }

    public void Load(string path)
    {
        using var stream = new FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
        Truncated = stream.Length > Limit;
        var buffer = new byte[Math.Min(stream.Length, Limit)];
        int read = stream.Read(buffer, 0, buffer.Length);
        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        _text.Text = text;
        _text.FlowDirection = Toolbox.Processing.Pdf.PdfEditor.IsRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
    }

    public bool Wrap
    {
        get => _text.TextWrapping == TextWrapping.Wrap;
        set => _text.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
    }

    /// <summary>Selects the next place the text occurs (after the selection, wrapping round); false when it is not there.</summary>
    public bool Find(string what, bool matchCase, bool backwards = false)
    {
        if (what.Length == 0) return false;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var all = _text.Text;
        int from = backwards ? _text.SelectionStart - 1 : _text.SelectionStart + _text.SelectionLength;
        int at = backwards
            ? (from >= 0 ? all.LastIndexOf(what, from, comparison) : -1)
            : all.IndexOf(what, Math.Clamp(from, 0, all.Length), comparison);
        if (at < 0) at = backwards ? all.LastIndexOf(what, comparison) : all.IndexOf(what, comparison);
        if (at < 0) return false;
        _text.Focus();
        _text.Select(at, what.Length);
        _text.ScrollToLine(_text.GetLineIndexFromCharacterIndex(at));
        return true;
    }

    public int Count(string what, bool matchCase)
    {
        if (what.Length == 0) return 0;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int n = 0;
        for (int i = _text.Text.IndexOf(what, comparison); i >= 0; i = _text.Text.IndexOf(what, i + what.Length, comparison)) n++;
        return n;
    }
}

/// <summary>A video or a sound with play/pause, a position bar and its time; Space plays and pauses.</summary>
internal sealed class MediaView : Grid
{
    private readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, Stretch = Stretch.Uniform };
    private readonly Slider _position = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 96, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
    private readonly Button _play = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _playing, _seeking;

    public MediaView()
    {
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var stage = new Border { Background = Brushes.Black, Child = _media, FlowDirection = FlowDirection.LeftToRight };
        Children.Add(stage);
        var bar = new DockPanel { Margin = new Thickness(14, 10, 14, 12) };
        Grid.SetRow(bar, 1);
        _play.Style = SettingsPage.Res("Lap.Caption");
        _play.Width = 38;
        _play.Height = 32;
        _play.Click += (_, _) => Toggle();
        DockPanel.SetDock(_play, Dock.Left);
        bar.Children.Add(_play);
        DockPanel.SetDock(_time, Dock.Right);
        _time.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        bar.Children.Add(_time);
        bar.Children.Add(_position);
        Children.Add(bar);
        _position.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _seeking = true));
        _position.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _seeking = false; _media.Position = TimeSpan.FromSeconds(_position.Value); }));
        _position.PreviewMouseLeftButtonUp += (_, _) => { if (!_seeking) _media.Position = TimeSpan.FromSeconds(_position.Value); };
        _media.MediaOpened += (_, _) => { _position.Maximum = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan.TotalSeconds : 1; Show(); };
        _media.MediaEnded += (_, _) => { _media.Stop(); _playing = false; Show(); };
        _media.MediaFailed += (_, e) => Failed?.Invoke(e.ErrorException?.Message ?? "");
        _tick.Tick += (_, _) => Show();
        Unloaded += (_, _) => { _tick.Stop(); _media.Close(); };
        Show();
    }

    public event Action<string>? Failed;

    public void Load(string path)
    {
        _media.Source = new Uri(path);
        _media.Play();
        _media.Pause();
        _tick.Start();
    }

    public void Toggle()
    {
        if (_playing) _media.Pause(); else _media.Play();
        _playing = !_playing;
        Show();
    }

    private void Show()
    {
        _play.Content = SettingsPage.Glyph(_playing ? "lap-stop" : "lap-play", 15, stroke: 2);
        if (!_seeking) _position.Value = _media.Position.TotalSeconds;
        var total = _media.NaturalDuration.HasTimeSpan ? _media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        _time.Text = $"{Format(_media.Position)} / {Format(total)}";
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
}

/// <summary>Any other file (Word, Excel, PowerPoint, archives…): its large Windows thumbnail and what it is.</summary>
internal sealed class ShellPreview : StackPanel
{
    public ShellPreview(string path, string kind, string size)
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        var thumb = ShellThumbnail.Get(path, 512);
        if (thumb is not null)
            Children.Add(new Border { Child = new Image { Source = thumb, MaxWidth = 420, MaxHeight = 420, Stretch = Stretch.Uniform }, CornerRadius = new CornerRadius(12), ClipToBounds = true, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Center });
        Children.Add(new TextBlock { Text = IOPath.GetFileName(path), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 });
        Children.Add(new TextBlock { Text = $"{kind} · {size}", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2")));
    }
}

/// <summary>The large thumbnail Windows shows for a file (Explorer's, from the file's own preview provider).</summary>
internal static partial class ShellThumbnail
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Cx, Cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_THUMBNAILONLY = 0x8;

    public static BitmapSource? Get(string path, int size, bool thumbnailOnly = false)
    {
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            int flags = SIIGBF_BIGGERSIZEOK | (thumbnailOnly ? SIIGBF_THUMBNAILONLY : 0);
            if (factory.GetImage(new SIZE { Cx = size, Cy = size }, flags, out hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or System.IO.FileNotFoundException)
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
        }
    }
}
