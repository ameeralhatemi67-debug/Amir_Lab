using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Viewer;

/// <summary>
/// Make or choose a signature: draw it, type it in a handwriting font, or pick a picture of it.
/// Every signature made here is kept (a PNG with a see-through background in Amir Lap's data
/// folder, never uploaded) and offered again next time; a click on a kept one places it.
/// </summary>
internal sealed class SignaturePad : GlassWindow
{
    private static string Folder => Directory.CreateDirectory(IOPath.Combine(AppPaths.DataDir, "signatures")).FullName;

    private readonly InkCanvas _ink = new() { Height = 170, Background = Brushes.White };
    private readonly TextBox _typed = new() { FontSize = 16, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _typedPreview = new() { FontSize = 44, Foreground = Brushes.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 6, 16, 6) };
    private readonly WrapPanel _kept = new();
    private readonly ContentControl _body = new();
    private string _mode = "draw";
    private string _font = "Segoe Script";
    private Color _colour = Color.FromRgb(0x10, 0x24, 0x6E);

    public SignaturePad()
    {
        Title = Lang.T("Signature", "التوقيع");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = Lang.Flow;
        _ink.DefaultDrawingAttributes = new DrawingAttributes { Color = _colour, Width = 2.6, Height = 2.6, FitToCurve = true };
        _typed.Style = SettingsPage.Res("Lap.TextBox");
        _typed.TextChanged += (_, _) => _typedPreview.Text = _typed.Text;
        Content = Build();
        ShowKept();
        Show("draw");
    }

    /// <summary>The chosen signature as PNG bytes (see-through background, cropped to the ink), or null when cancelled.</summary>
    public byte[]? Result { get; private set; }

    private UIElement Build()
    {
        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 12), Background = Brushes.Transparent };
        var close = new Button { Style = SettingsPage.Res("Lap.Caption.Close"), Width = 34, Height = 30, Content = SettingsPage.Glyph("close", 12, stroke: 2.2) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = Lang.T("Signature", "التوقيع"), FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        MakeDragHandle(head);
        root.Children.Add(head);

        var keptTitle = new TextBlock { Text = Lang.T("Your signatures (click one to place it)", "توقيعاتك (انقر أحدها لوضعه)"), Style = SettingsPage.Res("Lap.Help"), Margin = new Thickness(0, 0, 0, 6) };
        root.Children.Add(keptTitle);
        root.Children.Add(_kept);

        var bar = new Border { Style = SettingsPage.Res("Lap.SegmentBar"), Margin = new Thickness(0, 12, 0, 10) };
        var grid = new UniformGrid { Rows = 1 };
        foreach (var (mode, text) in new[] { ("draw", Lang.T("Draw", "رسم")), ("type", Lang.T("Type", "كتابة")), ("picture", Lang.T("Picture", "صورة")) })
        {
            var rb = new RadioButton { Style = SettingsPage.Res("Lap.Segment"), Content = text, GroupName = "sig-" + GetHashCode(), IsChecked = mode == "draw" };
            rb.Checked += (_, _) => Show(mode);
            grid.Children.Add(rb);
        }
        bar.Child = grid;
        root.Children.Add(bar);
        root.Children.Add(_body);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var clear = SettingsPage.Button(Lang.T("Clear", "مسح"), () => { _ink.Strokes.Clear(); _typed.Text = ""; }, ghost: true);
        clear.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(clear);
        buttons.Children.Add(SettingsPage.Button(Lang.T("Use and keep", "استخدام وحفظ"), UseNew, accent: true));
        root.Children.Add(buttons);
        return root;
    }

    private void Show(string mode)
    {
        _mode = mode;
        switch (mode)
        {
            case "draw":
            {
                var colours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
                foreach (var c in new[] { Color.FromRgb(0x10, 0x24, 0x6E), Color.FromRgb(0x14, 0x14, 0x18), Color.FromRgb(0x0B, 0x5D, 0x3A) })
                {
                    var swatch = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(c), Margin = new Thickness(0, 0, 8, 0), Cursor = System.Windows.Input.Cursors.Hand };
                    swatch.MouseLeftButtonUp += (_, _) => { _colour = c; _ink.DefaultDrawingAttributes.Color = c; foreach (var s in _ink.Strokes) s.DrawingAttributes.Color = c; };
                    colours.Children.Add(swatch);
                }
                var panel = new StackPanel();
                panel.Children.Add(new Border { Child = _ink, CornerRadius = new CornerRadius(10), ClipToBounds = true, BorderThickness = new Thickness(1) }.With(b => b.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2")));
                panel.Children.Add(colours);
                Detach(_ink);
                _body.Content = panel;
                break;
            }
            case "type":
            {
                var panel = new StackPanel();
                Detach(_typed);
                Detach(_typedPreview);
                panel.Children.Add(_typed);
                var fonts = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
                foreach (var f in new[] { "Segoe Script", "Ink Free", "Lucida Handwriting", "Segoe UI" })
                {
                    var b = new Button { Style = SettingsPage.Res("Lap.Chip"), Content = new TextBlock { Text = f, FontFamily = new FontFamily(f) }, Margin = new Thickness(0, 0, 6, 6) };
                    b.Click += (_, _) => { _font = f; _typedPreview.FontFamily = new FontFamily(f); };
                    fonts.Children.Add(b);
                }
                panel.Children.Add(fonts);
                _typedPreview.FontFamily = new FontFamily(_font);
                _typedPreview.Foreground = new SolidColorBrush(_colour);
                panel.Children.Add(new Border { Child = _typedPreview, Background = Brushes.White, CornerRadius = new CornerRadius(10), MinHeight = 110, BorderThickness = new Thickness(1) }.With(b => b.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2")));
                _body.Content = panel;
                _typed.Focus();
                break;
            }
            default:
            {
                var choose = SettingsPage.Button(Lang.T("Choose a picture…", "اختيار صورة…"), ChoosePicture, accent: false);
                _body.Content = new StackPanel { Children = { new TextBlock { Text = Lang.T("A photo or scan of your signature. A white background is made see-through.", "صورة أو مسح لتوقيعك. تصبح الخلفية البيضاء شفافة."), Style = SettingsPage.Res("Lap.Help"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) }, choose } };
                break;
            }
        }
    }

    private static void Detach(FrameworkElement e)
    {
        if (e.Parent is Panel p) p.Children.Remove(e);
        else if (e.Parent is Border b) b.Child = null;
        else if (e.Parent is ContentControl c) c.Content = null;
    }

    private void ShowKept()
    {
        _kept.Children.Clear();
        foreach (var file in Directory.GetFiles(Folder, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).Take(8))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            var image = new Image { Source = Load(bytes), Height = 52, MaxWidth = 150, Stretch = Stretch.Uniform };
            var remove = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 24, Height = 24, Content = SettingsPage.Glyph("lap-x", 10, stroke: 2.2), ToolTip = Lang.T("Forget this signature", "حذف هذا التوقيع"), VerticalAlignment = VerticalAlignment.Top };
            var path = file;
            remove.Click += (_, e) => { try { File.Delete(path); } catch (IOException) { } ShowKept(); e.Handled = true; };
            var row = new DockPanel();
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(image);
            var card = new Border { Child = row, Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(8, 4, 2, 4), Margin = new Thickness(0, 0, 8, 8), Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
            card.MouseLeftButtonUp += (_, _) => { Result = bytes; File.SetLastWriteTimeUtc(path, DateTime.UtcNow); Close(); };
            _kept.Children.Add(card);
        }
        if (_kept.Children.Count == 0) _kept.Children.Add(new TextBlock { Text = Lang.T("None yet.", "لا يوجد بعد."), Style = SettingsPage.Res("Lap.Help") });
    }

    private void UseNew()
    {
        byte[]? png = _mode switch
        {
            "draw" when _ink.Strokes.Count > 0 => RenderInk(),
            "type" when _typed.Text.Trim().Length > 0 => RenderTyped(),
            _ => null,
        };
        if (png is null) return;
        Keep(png);
        Result = png;
        Close();
    }

    private void ChoosePicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Lang.T("Pictures", "صور") + "|*.png;*.jpg;*.jpeg;*.webp;*.bmp", Title = Lang.T("Choose a picture of your signature", "اختر صورة توقيعك") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(dialog.FileName), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var png = Transparent(new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0));
            Keep(png);
            Result = png;
            Close();
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException)
        {
            MessageBox.Show(this, ex.Message, Title);
        }
    }

    private static void Keep(byte[] png)
    {
        try { File.WriteAllBytes(IOPath.Combine(Folder, $"signature-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png"), png); }
        catch (IOException) { }
    }

    private byte[] RenderInk()
    {
        var bounds = _ink.Strokes.GetBounds();
        bounds.Inflate(6, 6);
        const double k = 3;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(k, k));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            _ink.Strokes.Draw(dc);
        }
        var target = new RenderTargetBitmap((int)Math.Ceiling(bounds.Width * k), (int)Math.Ceiling(bounds.Height * k), 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return Png(target);
    }

    private byte[] RenderTyped()
    {
        var text = new TextBlock { Text = _typed.Text.Trim(), FontFamily = new FontFamily(_font), FontSize = 120, Foreground = new SolidColorBrush(_colour), Padding = new Thickness(10), FlowDirection = Toolbox.Processing.Pdf.PdfEditor.IsRightToLeft(_typed.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight };
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        text.Arrange(new Rect(text.DesiredSize));
        var target = new RenderTargetBitmap((int)Math.Ceiling(text.DesiredSize.Width), (int)Math.Ceiling(text.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        target.Render(text);
        return Png(target);
    }

    /// <summary>White and near-white become see-through (a photo or scan of a signature on paper).</summary>
    private static byte[] Transparent(BitmapSource source)
    {
        int w = source.PixelWidth, h = source.PixelHeight;
        var px = new byte[w * h * 4];
        source.CopyPixels(px, w * 4, 0);
        for (int i = 0; i < px.Length; i += 4)
        {
            int lum = (px[i] + px[i + 1] + px[i + 2]) / 3;
            if (lum > 225) px[i + 3] = 0;
            else if (lum > 170) px[i + 3] = (byte)(px[i + 3] * (225 - lum) / 55);
        }
        return Png(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4));
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static BitmapImage Load(byte[] bytes)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = new MemoryStream(bytes);
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
