using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Pdf;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Jobs;

/// <summary>
/// Asks which PDF pages to export before rendering — never silently just the first page.
/// Single PDF: thumbnails you can toggle, or a range such as "1-3, 7". Several PDFs: all pages or first page of each.
/// </summary>
internal sealed class PagePickerWindow : GlassWindow
{
    private const int MaxThumbnails = 80;

    private readonly ActionDefinition _action;
    private readonly IReadOnlyList<(ProbedFile File, int Pages)> _pdfs;
    private readonly ActionOptions _options;
    private readonly RadioButton _all = new() { Content = "All pages", IsChecked = true };
    private readonly RadioButton _some = new() { Content = "Pages" };
    private readonly RadioButton _first = new() { Content = "First page of each" };
    private readonly TextBox _range = new() { Width = 150, Margin = new Thickness(10, 0, 0, 0) };
    private readonly TextBlock _rangeError = new();
    private readonly TextBlock _summary = new();
    private readonly WrapPanel _thumbs = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly List<ToggleButton> _thumbButtons = [];
    private readonly Button _export = new() { Content = "Export", IsDefault = true, MinWidth = 96 };
    private readonly Dictionary<string, RadioButton> _dpiButtons = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _syncing;

    public PagePickerWindow(ActionDefinition action, IReadOnlyList<(ProbedFile File, int Pages)> pdfs, ActionOptions options)
    {
        _action = action;
        _pdfs = pdfs;
        _options = new ActionOptions(options);
        Title = action.Title;
        Width = pdfs.Count == 1 ? 520 : 420;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = false;
        Content = Build();
        Loaded += (_, _) =>
        {
            Keyboard.Focus(_export);
            if (_pdfs.Count == 1) _ = LoadThumbnailsAsync(_pdfs[0].File.Path, _pdfs[0].Pages, _cts.Token);
        };
        Closed += (_, _) => _cts.Cancel();
    }

    /// <summary>The chosen options (pages, dpi) when the user confirmed; null when cancelled.</summary>
    public ActionOptions? Result { get; private set; }

    private int PageCount => _pdfs[0].Pages;

    private UIElement Build()
    {
        var root = new StackPanel { Margin = new Thickness(18, 16, 18, 16) };

        var header = new StackPanel { Background = Brushes.Transparent };
        header.Children.Add(new TextBlock { Text = _action.Title, Style = Res("Text.Title") });
        var subtitle = _pdfs.Count == 1
            ? $"{_pdfs[0].File.Name} · {_pdfs[0].Pages} pages"
            : $"{_pdfs.Count} PDFs · {_pdfs.Sum(p => p.Pages)} pages in total";
        header.Children.Add(new TextBlock { Text = subtitle, Style = Res("Text.Caption"), Margin = new Thickness(0, 2, 0, 12) });
        MakeDragHandle(header);
        root.Children.Add(header);

        foreach (var rb in new[] { _all, _some, _first })
        {
            rb.Style = Res("Toolbox.Radio");
            rb.GroupName = "pages";
            rb.Margin = new Thickness(0, 0, 0, 8);
            rb.Checked += (_, _) => OnModeChanged();
        }
        root.Children.Add(_all);
        if (_pdfs.Count == 1)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            row.Children.Add(_some);
            _range.Text = $"1-{PageCount}";
            _range.ToolTip = "Page numbers and ranges, for example 1-3, 7, 10-";
            _range.TextChanged += (_, _) => { if (!_syncing) { _some.IsChecked = true; SyncFromText(); } };
            _range.GotKeyboardFocus += (_, _) => _some.IsChecked = true;
            row.Children.Add(_range);
            root.Children.Add(row);
            _rangeError.Style = Res("Text.Caption");
            _rangeError.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
            _rangeError.Margin = new Thickness(26, 2, 0, 4);
            _rangeError.Visibility = Visibility.Collapsed;
            root.Children.Add(_rangeError);

            var scroller = new ScrollViewer
            {
                Content = _thumbs,
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 6, 0, 0),
            };
            root.Children.Add(scroller);
            BuildThumbnailPlaceholders();
        }
        else
        {
            root.Children.Add(_first);
        }

        // Resolution
        var dpiRow = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = false };
        dpiRow.Children.Add(new TextBlock { Text = "Resolution", Style = Res("Text.Label"), Width = 80 });
        var bar = new Border { Style = (Style)Application.Current.Resources["Toolbox.SegmentBar"] };
        var segs = new StackPanel { Orientation = Orientation.Horizontal };
        string current = _options.GetString("dpi", "300");
        foreach (var (dpi, label) in new[] { ("150", "150 DPI · screen"), ("300", "300 DPI · print"), ("600", "600 DPI · detail") })
        {
            var rb = new RadioButton { Content = label, Style = Res("Toolbox.Segment"), GroupName = "dpi", IsChecked = dpi == current, Tag = dpi };
            rb.Checked += (_, _) => { _options["dpi"] = dpi; UpdateSummary(); };
            _dpiButtons[dpi] = rb;
            segs.Children.Add(rb);
        }
        if (!_dpiButtons.ContainsKey(current)) _dpiButtons["300"].IsChecked = true;
        bar.Child = segs;
        dpiRow.Children.Add(bar);
        root.Children.Add(dpiRow);

        _summary.Style = Res("Text.Caption");
        _summary.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(_summary);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => Close();
        _export.Style = Res("Toolbox.Button.Accent");
        _export.Click += (_, _) => Confirm();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_export);
        root.Children.Add(buttons);

        UpdateSummary();
        var border = new Border { Child = root, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private void BuildThumbnailPlaceholders()
    {
        int count = Math.Min(PageCount, MaxThumbnails);
        for (int i = 1; i <= count; i++)
        {
            int page = i;
            var image = new Image { Width = 84, Height = 108, Stretch = Stretch.Uniform };
            var stack = new StackPanel();
            stack.Children.Add(new Border { Width = 84, Height = 108, Child = image, CornerRadius = new CornerRadius(3) });
            stack.Children.Add(new TextBlock { Text = page.ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, Margin = new Thickness(0, 3, 0, 0) });
            var toggle = new ToggleButton
            {
                Content = stack,
                IsChecked = true,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(4),
                Tag = page,
                Template = ThumbTemplate(),
                Cursor = Cursors.Hand,
                ToolTip = $"Page {page}",
            };
            System.Windows.Automation.AutomationProperties.SetName(toggle, $"Page {page}");
            toggle.Checked += (_, _) => SyncFromThumbs();
            toggle.Unchecked += (_, _) => SyncFromThumbs();
            _thumbButtons.Add(toggle);
            _thumbs.Children.Add(toggle);
        }
        if (PageCount > MaxThumbnails)
            _thumbs.Children.Add(new TextBlock { Text = $"Previews show the first {MaxThumbnails} pages; use the page box for the rest.", Style = Res("Text.Caption"), Margin = new Thickness(0, 4, 0, 0) });
    }

    private static ControlTemplate ThumbTemplate()
    {
        var template = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        border.SetValue(Border.PaddingProperty, new Thickness(4));
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        template.VisualTree = border;
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        on.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("Brush.Accent"), "Bd"));
        on.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Brush.AccentSoft"), "Bd"));
        template.Triggers.Add(on);
        var off = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = false };
        off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "Bd"));
        template.Triggers.Add(off);
        return template;
    }

    private async Task LoadThumbnailsAsync(string path, int pages, CancellationToken ct)
    {
        try
        {
            await Task.Run(() =>
            {
                using var doc = PdfDocument.Open(path);
                for (int p = 1; p <= Math.Min(pages, MaxThumbnails) && !ct.IsCancellationRequested; p++)
                {
                    var size = doc.GetPageSize(p);
                    double dpi = 160.0 / Math.Max(size.Width, size.Height) * 72.0 * 1.5;
                    using var img = doc.RenderPage(p, dpi, out _);
                    var bytes = img.WriteToMemory<byte>();
                    var bitmap = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Rgb24, null, bytes, img.Width * 3);
                    bitmap.Freeze();
                    int page = p;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (page - 1 < _thumbButtons.Count && _thumbButtons[page - 1].Content is StackPanel s
                            && s.Children[0] is Border b && b.Child is Image image)
                            image.Source = bitmap;
                    });
                }
            }, ct);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ProcessingException)
        {
            // Previews are optional; the page box still works.
        }
    }

    private void OnModeChanged()
    {
        if (_all.IsChecked == true)
        {
            _syncing = true;
            foreach (var t in _thumbButtons) t.IsChecked = true;
            _range.Text = $"1-{PageCount}";
            _syncing = false;
        }
        UpdateSummary();
    }

    private void SyncFromThumbs()
    {
        if (_syncing) return;
        var selected = _thumbButtons.Where(t => t.IsChecked == true).Select(t => (int)t.Tag).ToList();
        if (PageCount > MaxThumbnails && _all.IsChecked != true)
            selected.AddRange(Enumerable.Range(MaxThumbnails + 1, PageCount - MaxThumbnails));
        _syncing = true;
        _range.Text = PageSet.ToSpec(selected);
        _syncing = false;
        if (selected.Count == PageCount) _all.IsChecked = true;
        else _some.IsChecked = true;
        UpdateSummary();
    }

    private void SyncFromText()
    {
        var pages = TryParse(out _);
        if (pages is null) { UpdateSummary(); return; }
        _syncing = true;
        var set = pages.ToHashSet();
        foreach (var t in _thumbButtons) t.IsChecked = set.Contains((int)t.Tag);
        _syncing = false;
        UpdateSummary();
    }

    private IReadOnlyList<int>? TryParse(out string? error)
    {
        error = null;
        try
        {
            return PageSet.Parse(_range.Text, PageCount);
        }
        catch (ProcessingException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private void UpdateSummary()
    {
        int files;
        string? error = null;
        if (_all.IsChecked == true) files = _pdfs.Sum(p => p.Pages);
        else if (_first.IsChecked == true) files = _pdfs.Count;
        else files = TryParse(out error)?.Count ?? 0;

        _rangeError.Text = error ?? "";
        _rangeError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        _export.IsEnabled = files > 0;
        var kind = _action.Output?.Label() ?? "image";
        _summary.Text = files == 0
            ? "No pages selected."
            : $"Creates {files} {kind} file{(files == 1 ? "" : "s")} at {_options.GetString("dpi", "300")} DPI next to the PDF{(_pdfs.Count == 1 ? "" : "s")}. The PDF is not changed.";
    }

    private void Confirm()
    {
        if (!_export.IsEnabled) return;
        var result = new ActionOptions(_options);
        if (_all.IsChecked == true) result["pages"] = "all";
        else if (_first.IsChecked == true) result["pages"] = "1";
        else result["pages"] = _range.Text;
        Result = result;
        Close();
    }

    private static Style Res(string key) => (Style)Application.Current.Resources[key];
}
