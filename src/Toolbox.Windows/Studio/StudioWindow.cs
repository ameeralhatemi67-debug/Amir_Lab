using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Output;
using Toolbox.Processing.Studio;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Studio;

internal enum StudioStep
{
    Crop,
    Adjust,
    Background,
    Annotate,
    Redact,
    Export,
}

/// <summary>How the Studio was opened: which step comes first and what the window is called.</summary>
internal sealed record StudioEntry(StudioStep Start, string Title, IReadOnlyList<StudioStep> Steps, string? SuffixOverride = null)
{
    /// <summary>Open the Blur step and look for personal information straight away (Longshot's "Hide personal info").</summary>
    public bool FindPersonalInfo { get; init; }
}

/// <summary>
/// Focused, non-destructive editor. Each step keeps its parameters; switching steps never writes
/// files; Save renders once from the original-quality source through the normal verified job pipeline.
/// </summary>
internal sealed partial class StudioWindow : GlassWindow
{
    private readonly StudioEntry _entry;
    private readonly Action<EditDocument, string?, IReadOnlyList<string>> _save;
    private readonly HashSet<string> _variants;
    private StudioSession? _session;
    private PreviewEngine? _engine;
    private StudioStep _step;
    private PreviewFrame? _frame;
    private bool _comparing;
    private DateTime _escapeArmedUntil;

    // Layout parts
    private readonly TextBlock _title = new();
    private readonly TextBlock _subtitle = new();
    private readonly StackPanel _stepBar = new() { Orientation = Orientation.Horizontal };
    private readonly Dictionary<StudioStep, RadioButton> _stepButtons = new();
    private readonly Canvas _surface = new() { ClipToBounds = true };
    private readonly Rectangle _checker = new() { IsHitTestVisible = false };
    private readonly Image _preview = new() { IsHitTestVisible = false, Stretch = Stretch.Fill };
    private readonly CropOverlay _crop = new();
    private readonly DrawOverlay _draw = new();
    private readonly TextBlock _busy = new() { Text = "Opening…", IsHitTestVisible = false };
    private readonly ContentControl _panel = new();
    private readonly TextBlock _info = new();
    private readonly TextBlock _hint = new();
    private readonly Button _undo = new() { ToolTip = "Undo (Ctrl+Z)" };
    private readonly Button _redo = new() { ToolTip = "Redo (Ctrl+Y)" };
    private readonly Button _saveButton = new() { Content = "Save", MinWidth = 96, IsDefault = false };
    private readonly ComboBox _pagePicker = new() { MinWidth = 120, Visibility = Visibility.Collapsed };
    private BitmapSource? _originalPreview;

    public StudioWindow(StudioEntry entry, string fileName, Native.POINT? anchor, Action<EditDocument, string?, IReadOnlyList<string>> save, IReadOnlyList<string>? variants = null)
    {
        _entry = entry;
        _save = save;
        _variants = new HashSet<string>(variants ?? []);
        _step = entry.Start;
        Title = entry.Title;
        Resizable = true;
        MinWidth = 760;
        MinHeight = 520;
        var monitor = ScreenGeometry.MonitorAt(anchor ?? ScreenGeometry.CursorPosition());
        double workW = monitor.WorkArea.Width / monitor.Scale, workH = monitor.WorkArea.Height / monitor.Scale;
        Width = Math.Min(1120, workW * 0.9);
        Height = Math.Min(760, workH * 0.9);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _title.Text = entry.Title;
        _subtitle.Text = fileName;
        Content = BuildLayout();
        CloseOnEscape = false;
        PreviewKeyDown += OnKeys;
        SizeChanged += (_, _) => LayoutStage();
        Closed += (_, _) => _session?.Source.Dispose();
    }

    /// <summary>Attaches the loaded source (loading happens off the UI thread before this).</summary>
    public void Attach(StudioSession session)
    {
        _session = session;
        _engine = new PreviewEngine(Dispatcher, () => session.Source);
        _engine.Rendered += OnFrame;
        _engine.Failed += message => _busy.Text = "Preview failed: " + message;
        session.Changed += _ =>
        {
            UpdateChrome();
            RequestPreview();
            if (!_rebuildingPanel) RebuildPanel();
        };
        if (session.Source.PageCount > 1)
        {
            _pagePicker.Items.Clear();
            for (int p = 1; p <= session.Source.PageCount; p++) _pagePicker.Items.Add($"Page {p} of {session.Source.PageCount}");
            _pagePicker.SelectedIndex = session.Source.Page - 1;
            _pagePicker.Visibility = Visibility.Visible;
            _pagePicker.SelectionChanged += async (_, _) => await SwitchPage(_pagePicker.SelectedIndex + 1);
        }
        _saveButton.IsEnabled = true;
        ShowStep(_step);
        UpdateChrome();
        _ = PrepareOriginalPreviewAsync(session);
    }

    public void ShowLoadError(string message)
    {
        _busy.Text = message;
        _busy.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
    }

    // ---- layout ------------------------------------------------------------------------------

    private UIElement BuildLayout()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new DockPanel { Margin = new Thickness(18, 12, 10, 6), Background = Brushes.Transparent };
        var close = IconButton("close", "Close (Esc)");
        close.Click += (_, _) => TryClose();
        _undo.Content = IconPath("undo", 15);
        _redo.Content = IconPath("repeat", 15);
        _undo.Style = Res("Toolbox.Button.Icon");
        _redo.Style = Res("Toolbox.Button.Icon");
        _undo.Click += (_, _) => _session?.Undo();
        _redo.Click += (_, _) => _session?.Redo();
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _pagePicker.Margin = new Thickness(0, 0, 10, 0);
        right.Children.Add(_pagePicker);
        right.Children.Add(_undo);
        right.Children.Add(_redo);
        right.Children.Add(new Border { Width = 8 });
        right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title.Style = Res("Text.Title");
        _title.FontSize = 16;
        _subtitle.Style = Res("Text.Caption");
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);
        MakeDragHandle(header);
        root.Children.Add(header);

        // Step strip
        var stepHost = new Border { Style = (Style)Application.Current.Resources["Toolbox.SegmentBar"], HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(18, 4, 18, 10) };
        foreach (var step in _entry.Steps)
        {
            var rb = new RadioButton { Content = StepLabel(step), Style = Res("Toolbox.Segment"), GroupName = "steps", Padding = new Thickness(14, 5, 14, 5), Tag = step };
            rb.Checked += (_, _) => { if (_session is not null && _step != step) ShowStep(step); };
            _stepButtons[step] = rb;
            _stepBar.Children.Add(rb);
        }
        stepHost.Child = _stepBar;
        stepHost.Visibility = _entry.Steps.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(stepHost, 1);
        root.Children.Add(stepHost);

        // Body: stage + panel
        var body = new Grid { Margin = new Thickness(18, 0, 18, 0) };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        var stage = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true };
        stage.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        _checker.Fill = CheckerBrush();
        _surface.Children.Add(_checker);
        _surface.Children.Add(_preview);
        _surface.Children.Add(_crop);
        _surface.Children.Add(_draw);
        _busy.Style = Res("Text.Caption");
        Canvas.SetLeft(_busy, 14);
        Canvas.SetTop(_busy, 12);
        _surface.Children.Add(_busy);
        _surface.SizeChanged += (_, _) => LayoutStage();
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        stage.Child = _surface;
        body.Children.Add(stage);

        var panelScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _panel, Margin = new Thickness(14, 0, 0, 0), Padding = new Thickness(0, 0, 6, 0) };
        Grid.SetColumn(panelScroll, 1);
        body.Children.Add(panelScroll);
        Grid.SetRow(body, 2);
        root.Children.Add(body);

        // Footer
        var footer = new DockPanel { Margin = new Thickness(18, 12, 18, 14) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var compare = new Button { Content = "Hold to compare", Margin = new Thickness(0, 0, 8, 0), ToolTip = "Shows the original while pressed" };
        compare.PreviewMouseLeftButtonDown += (_, _) => SetCompare(true);
        compare.PreviewMouseLeftButtonUp += (_, _) => SetCompare(false);
        compare.MouseLeave += (_, _) => SetCompare(false);
        var reset = new Button { Content = "Reset", Margin = new Thickness(0, 0, 8, 0), ToolTip = "Reset this step" };
        reset.Click += (_, _) => ResetStep();
        _saveButton.Style = Res("Toolbox.Button.Accent");
        _saveButton.IsEnabled = false;
        _saveButton.ToolTip = "Save a new copy (Ctrl+S). The original is never changed.";
        _saveButton.Click += (_, _) => Save();
        buttons.Children.Add(compare);
        buttons.Children.Add(reset);
        buttons.Children.Add(_saveButton);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _info.Style = Res("Text.Caption");
        _info.TextWrapping = TextWrapping.NoWrap;
        _info.TextTrimming = TextTrimming.CharacterEllipsis;
        _hint.Style = Res("Text.Caption");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
        _hint.Visibility = Visibility.Collapsed;
        infoStack.Children.Add(_info);
        infoStack.Children.Add(_hint);
        footer.Children.Add(infoStack);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        _crop.Committed += rect => _session?.Update(d => d with { Crop = d.Crop with { Rect = IsWhole(rect) ? null : rect } });
        _crop.Changing += rect => UpdateCropFields(rect);
        _draw.AnnotationAdded += a => _session?.Update(d => d with { Annotations = [.. d.Annotations, a] });
        _draw.RedactionsAdded += list => _session?.Update(d => d with { Redactions = [.. d.Redactions, .. list] });

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private static string StepLabel(StudioStep step) => step switch
    {
        StudioStep.Background => "Background",
        StudioStep.Export => "Export",
        StudioStep.Redact => "Blur",
        _ => step.ToString(),
    };

    // ---- step switching and preview -----------------------------------------------------------

    private void ShowStep(StudioStep step)
    {
        _step = step;
        if (_stepButtons.TryGetValue(step, out var rb) && rb.IsChecked != true) rb.IsChecked = true;
        _crop.Visibility = step == StudioStep.Crop ? Visibility.Visible : Visibility.Collapsed;
        _draw.Visibility = step is StudioStep.Annotate or StudioStep.Redact ? Visibility.Visible : Visibility.Collapsed;
        if (step == StudioStep.Background && _session is { } s && !s.Document.Background.Enabled)
            s.Update(d => d with { Background = d.Background with { Enabled = true } });
        RebuildPanel();
        RequestPreview();
        if (step == StudioStep.Crop) _crop.Focus();
        if (step == StudioStep.Redact) ReadText();
    }

    private RenderStage StageFor(StudioStep step) => step switch
    {
        StudioStep.Crop => RenderStage.Uncropped,
        StudioStep.Annotate or StudioStep.Redact => RenderStage.Content,
        _ => RenderStage.Final,
    };

    private void RequestPreview()
    {
        if (_session is null || _engine is null) return;
        _engine.Request(_session.Document, StageFor(_step));
    }

    private void OnFrame(PreviewFrame frame)
    {
        if (_session is null) return;
        if (frame.Stage != StageFor(_step)) return; // a frame for a step we already left
        _frame = frame;
        _busy.Visibility = Visibility.Collapsed;
        if (!_comparing) _preview.Source = frame.Bitmap;
        LayoutStage();
        _draw.ClearGhosts();
        UpdateInfo();
    }

    private void LayoutStage()
    {
        if (_frame is null) return;
        double availW = Math.Max(10, _surface.ActualWidth - 40), availH = Math.Max(10, _surface.ActualHeight - 40);
        double d = Math.Min(availW / _frame.LogicalWidth, availH / _frame.LogicalHeight);
        double w = _frame.LogicalWidth * d, h = _frame.LogicalHeight * d;
        var rect = new Rect((_surface.ActualWidth - w) / 2, (_surface.ActualHeight - h) / 2, w, h);

        Canvas.SetLeft(_preview, rect.X);
        Canvas.SetTop(_preview, rect.Y);
        _preview.Width = rect.Width;
        _preview.Height = rect.Height;
        Canvas.SetLeft(_checker, rect.X);
        Canvas.SetTop(_checker, rect.Y);
        _checker.Width = rect.Width;
        _checker.Height = rect.Height;

        foreach (var overlay in new FrameworkElement[] { _crop, _draw })
        {
            overlay.Width = _surface.ActualWidth;
            overlay.Height = _surface.ActualHeight;
            Canvas.SetLeft(overlay, 0);
            Canvas.SetTop(overlay, 0);
        }
        if (_step == StudioStep.Crop)
        {
            _crop.SetLayout(rect, _frame.LogicalWidth, _frame.LogicalHeight);
            if (_session is { } s) _crop.SetValue(s.Document.Crop.Rect);
        }
        else
        {
            _draw.SetLayout(rect, _frame.LogicalWidth, _frame.LogicalHeight);
        }
    }

    private void UpdateChrome()
    {
        if (_session is null) return;
        var doc = _session.Document;
        _undo.IsEnabled = _session.CanUndo;
        _redo.IsEnabled = _session.CanRedo;
        foreach (var (step, button) in _stepButtons)
        {
            bool changed = step switch
            {
                StudioStep.Crop => !doc.Crop.IsIdentity,
                StudioStep.Adjust => !doc.Adjust.IsIdentity,
                StudioStep.Background => doc.Background.Enabled,
                StudioStep.Annotate => doc.Annotations.Count > 0,
                StudioStep.Redact => doc.Redactions.Count > 0,
                _ => doc.Export.TargetBytes is not null || doc.Export.MaxEdge is not null || doc.Export.Format != "auto",
            };
            button.Content = changed ? StepLabel(step) + " •" : StepLabel(step);
        }
        var src = _session.Source;
        _subtitle.Text = $"{src.File.Name} · {src.Width} × {src.Height} px{(src.PageCount > 1 ? $" · page {src.Page}" : "")}";
    }

    private void UpdateInfo()
    {
        if (_session is null) return;
        var doc = _session.Document;
        var src = _session.Source;
        var (ow, oh) = StudioRenderer.OrientedSize(src.Width, src.Height, doc.Crop.Rotation);
        var (fw, fh) = StudioRenderer.FinalSize(doc, src);
        var format = StudioExporter.ResolveFormat(doc, src);
        _info.Text = $"{ow} × {oh} px  →  {fw} × {fh} px · {format.Label()} · saves as \"{OutputName(doc, format)}\"";
    }

    private string OutputName(EditDocument doc, FileKind format)
    {
        var src = _session!.Source;
        var suffix = _entry.SuffixOverride ?? doc.Suffix();
        return OutputNamer.Compose(App.Host.Settings.Current.NamingPattern,
            new NameParts(src.File.BaseName, suffix, format.Extension(), src.PageCount > 1 ? src.Page : null, src.PageCount));
    }

    private void SetCompare(bool on)
    {
        _comparing = on && _originalPreview is not null;
        _preview.Source = _comparing ? _originalPreview : _frame?.Bitmap;
        if (_comparing && _frame is not null && _session is not null)
        {
            // The original has the source's oriented size; show it in the same box for a fair comparison.
            _preview.Stretch = Stretch.Uniform;
        }
        else
        {
            _preview.Stretch = Stretch.Fill;
        }
    }

    private async Task PrepareOriginalPreviewAsync(StudioSession session)
    {
        var original = new EditDocument { SourcePath = session.Initial.SourcePath, Page = session.Source.Page, Dpi = session.Source.Dpi };
        var frame = await Task.Run(() => StudioRenderer.RenderPreview(original, session.Source, RenderStage.Final));
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null, frame.Bgra, frame.Width * 4);
        bitmap.Freeze();
        _originalPreview = bitmap;
    }

    private async Task SwitchPage(int page)
    {
        if (_session is null || page == _session.Source.Page) return;
        _busy.Text = $"Rendering page {page}…";
        _busy.Visibility = Visibility.Visible;
        var doc = _session.Document;
        try
        {
            var source = await Task.Run(() => StudioSource.Open(doc.SourcePath, page, doc.Dpi));
            _session.ReplaceSource(source, doc with { Page = page, Crop = new CropSettings(), Annotations = [], Redactions = [] });
            _ = PrepareOriginalPreviewAsync(_session);
        }
        catch (ProcessingException ex)
        {
            ShowLoadError(ex.Message);
        }
    }

    // ---- actions --------------------------------------------------------------------------------

    private void Save()
    {
        if (_session is null) return;
        _save(_session.Document, _entry.SuffixOverride, _variants.ToList());
        Close();
    }

    private void TryClose()
    {
        if (_session?.Document is { } doc && (doc.HasEdits || doc != _session.Initial) && DateTime.Now > _escapeArmedUntil)
        {
            _escapeArmedUntil = DateTime.Now.AddSeconds(3);
            _hint.Text = "Unsaved changes — press Esc (or close) again to discard them.";
            _hint.Visibility = Visibility.Visible;
            return;
        }
        Close();
    }

    private void ResetStep()
    {
        if (_session is null) return;
        _session.Update(d => _step switch
        {
            StudioStep.Crop => d with { Crop = new CropSettings() },
            StudioStep.Adjust => d with { Adjust = new AdjustSettings() },
            StudioStep.Background => d with { Background = new BackgroundSettings { Enabled = true } },
            StudioStep.Annotate => d with { Annotations = [] },
            StudioStep.Redact => d with { Redactions = [] },
            _ => d with { Export = new ExportSettings() },
        });
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (Keyboard.FocusedElement is TextBox) return;
            TryClose();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control || _session is null) return;
        switch (e.Key)
        {
            case Key.Z: _session.Undo(); e.Handled = true; break;
            case Key.Y: _session.Redo(); e.Handled = true; break;
            case Key.S: Save(); e.Handled = true; break;
        }
    }

    /// <summary>A frame covering the whole oriented image means "no crop".</summary>
    private bool IsWhole(PixelRect r)
    {
        if (_session is null) return false;
        var (w, h) = StudioRenderer.OrientedSize(_session.Source.Width, _session.Source.Height, _session.Document.Crop.Rotation);
        return r.X <= 0 && r.Y <= 0 && r.Width >= w && r.Height >= h;
    }

    private static Brush CheckerBrush()
    {
        var group = new DrawingGroup();
        var light = (Brush)Application.Current.Resources["Brush.Checker"];
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        return new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
    }

    internal static Style Res(string key) => (Style)Application.Current.Resources[key];

    internal static Path IconPath(string key, double size)
    {
        var p = new Path { Data = Icons.Get(key), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        p.SetResourceReference(Shape.StrokeProperty, "Brush.Text");
        return p;
    }

    private static Button IconButton(string icon, string tip) => new() { Style = Res("Toolbox.Button.Icon"), Content = IconPath(icon, 13), ToolTip = tip };
}
