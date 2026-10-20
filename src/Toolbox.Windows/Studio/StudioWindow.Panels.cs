using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Studio;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Studio;

internal sealed partial class StudioWindow
{
    private bool _suppressRebuild;
    private bool _rebuildingPanel => _suppressRebuild;
    private TextBox? _cropW, _cropH;
    private TextBlock? _estimate;
    private int _estimateVersion;

    /// <summary>Applies a change made by the panel itself (no panel rebuild, so sliders keep their drag).</summary>
    private void Apply(Func<EditDocument, EditDocument> change)
    {
        if (_session is null) return;
        _suppressRebuild = true;
        try { _session.Update(change); }
        finally { _suppressRebuild = false; }
        if (_step == StudioStep.Export) RefreshEstimate();
    }

    private void RebuildPanel()
    {
        if (_session is null) return;
        _panel.Content = _step switch
        {
            StudioStep.Crop => CropPanel(),
            StudioStep.Adjust => AdjustPanel(),
            StudioStep.Background => BackgroundPanel(),
            StudioStep.Annotate => AnnotatePanel(),
            StudioStep.Redact => RedactPanel(),
            _ => ExportPanel(),
        };
        if (_step == StudioStep.Export) RefreshEstimate();
    }

    // ---- Crop ---------------------------------------------------------------------------------

    private UIElement CropPanel()
    {
        var doc = _session!.Document;
        var panel = Column();
        panel.Children.Add(Heading("Aspect ratio"));
        var options = new[] { ("free", "Free"), ("original", "Original"), ("1:1", "1:1"), ("16:9", "16:9"), ("9:16", "9:16"), ("4:3", "4:3"), ("3:4", "3:4") };
        panel.Children.Add(Segments("aspect", options, doc.Crop.Aspect, value =>
        {
            var (ow, oh) = StudioRenderer.OrientedSize(_session.Source.Width, _session.Source.Height, _session.Document.Crop.Rotation);
            double? ratio = value switch
            {
                "free" => null,
                "original" => (double)ow / oh,
                _ => BackgroundPresets.Ratio(value),
            };
            Apply(d => d with { Crop = d.Crop with { Aspect = value } });
            _crop.ApplyAspect(ratio, fromWholeImage: true);
        }, wrap: true));

        panel.Children.Add(Heading("Size"));
        var size = new StackPanel { Orientation = Orientation.Horizontal };
        _cropW = NumberBox(_crop.Value.Width);
        _cropH = NumberBox(_crop.Value.Height);
        void CommitSize(TextBox box, bool width)
        {
            if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < 1) return;
            if (width) _crop.SetSize(v, null);
            else _crop.SetSize(null, v);
        }
        Hook(_cropW, () => CommitSize(_cropW, true));
        Hook(_cropH, () => CommitSize(_cropH, false));
        size.Children.Add(Label("W", 18));
        size.Children.Add(_cropW);
        size.Children.Add(Label("H", 18, new Thickness(12, 0, 0, 0)));
        size.Children.Add(_cropH);
        size.Children.Add(Label("px", 20, new Thickness(6, 0, 0, 0)));
        panel.Children.Add(size);
        _crop.Aspect = doc.Crop.Aspect switch
        {
            "free" => null,
            "original" => (double)StudioRenderer.OrientedSize(_session.Source.Width, _session.Source.Height, doc.Crop.Rotation).Width
                          / StudioRenderer.OrientedSize(_session.Source.Width, _session.Source.Height, doc.Crop.Rotation).Height,
            _ => BackgroundPresets.Ratio(doc.Crop.Aspect),
        };

        panel.Children.Add(Heading("Rotate and flip"));
        var turn = new WrapPanel();
        turn.Children.Add(SmallButton("Rotate left", () => Apply(d => d with { Crop = d.Crop with { Rotation = (d.Crop.Rotation + 270) % 360, Rect = null } })));
        turn.Children.Add(SmallButton("Rotate right", () => Apply(d => d with { Crop = d.Crop with { Rotation = (d.Crop.Rotation + 90) % 360, Rect = null } })));
        turn.Children.Add(SmallButton("Flip ↔", () => Apply(d => d with { Crop = d.Crop with { FlipHorizontal = !d.Crop.FlipHorizontal } })));
        turn.Children.Add(SmallButton("Flip ↕", () => Apply(d => d with { Crop = d.Crop with { FlipVertical = !d.Crop.FlipVertical } })));
        panel.Children.Add(turn);
        panel.Children.Add(Caption("Drag the frame or its handles; drag outside it to start a new frame. Arrow keys nudge it (Shift: 10 px). Rotating resets the frame."));
        return panel;
    }

    private void UpdateCropFields(PixelRect rect)
    {
        if (_cropW is not null && !_cropW.IsKeyboardFocused) _cropW.Text = rect.Width.ToString(CultureInfo.InvariantCulture);
        if (_cropH is not null && !_cropH.IsKeyboardFocused) _cropH.Text = rect.Height.ToString(CultureInfo.InvariantCulture);
    }

    // ---- Adjust -------------------------------------------------------------------------------

    private UIElement AdjustPanel()
    {
        var a = _session!.Document.Adjust;
        var panel = Column();
        panel.Children.Add(Heading("Light and colour"));
        panel.Children.Add(SliderRow("Brightness", -100, 100, a.Brightness, "", v => Apply(d => d with { Adjust = d.Adjust with { Brightness = v } })));
        panel.Children.Add(SliderRow("Contrast", -100, 100, a.Contrast, "", v => Apply(d => d with { Adjust = d.Adjust with { Contrast = v } })));
        panel.Children.Add(SliderRow("Saturation", -100, 100, a.Saturation, "", v => Apply(d => d with { Adjust = d.Adjust with { Saturation = v } })));
        panel.Children.Add(Heading("Detail"));
        panel.Children.Add(SliderRow("Sharpen", 0, 100, a.Sharpen, "", v => Apply(d => d with { Adjust = d.Adjust with { Sharpen = v } })));
        panel.Children.Add(Caption("Double-click a slider to reset it. Output size is set in Export."));
        return panel;
    }

    // ---- Background ---------------------------------------------------------------------------

    private UIElement BackgroundPanel()
    {
        var bg = _session!.Document.Background;
        var (cw, ch) = ContentSize();
        var panel = Column();

        var enabled = new CheckBox { Content = "Add background", IsChecked = bg.Enabled, Margin = new Thickness(0, 0, 0, 6) };
        enabled.Checked += (_, _) => Apply(d => d with { Background = d.Background with { Enabled = true } });
        enabled.Unchecked += (_, _) => Apply(d => d with { Background = d.Background with { Enabled = false } });
        panel.Children.Add(enabled);

        panel.Children.Add(Heading("Background"));
        var gradients = new WrapPanel();
        foreach (var preset in BackgroundPresets.Gradients)
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            double rad = preset.Angle * Math.PI / 180;
            brush.StartPoint = new Point(0.5 - Math.Cos(rad) / 2, 0.5 - Math.Sin(rad) / 2);
            brush.EndPoint = new Point(0.5 + Math.Cos(rad) / 2, 0.5 + Math.Sin(rad) / 2);
            foreach (var stop in preset.Stops) brush.GradientStops.Add(new System.Windows.Media.GradientStop((Color)ColorConverter.ConvertFromString(stop.Color), stop.Offset));
            gradients.Children.Add(Swatch(brush, preset.Name, bg.Fill == "preset:" + preset.Id, 36, 8,
                () => { Apply(d => d with { Background = d.Background with { Fill = "preset:" + preset.Id, Enabled = true } }); RebuildPanel(); }));
        }
        var picture = new Button { Width = 36, Height = 36, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), Content = IconPath("frame", 16), ToolTip = "Use a picture as the background" };
        picture.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.webp;*.avif;*.tif;*.tiff;*.bmp;*.gif|All files|*.*" };
            if (dialog.ShowDialog(this) == true)
            {
                Apply(d => d with { Background = d.Background with { Fill = "image:" + dialog.FileName, Enabled = true } });
                RebuildPanel();
            }
        };
        gradients.Children.Add(picture);
        panel.Children.Add(gradients);

        var solids = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        foreach (var hex in BackgroundPresets.Solids)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            solids.Children.Add(Swatch(brush, hex, bg.Fill.Equals(hex, StringComparison.OrdinalIgnoreCase), 26, 13,
                () => { Apply(d => d with { Background = d.Background with { Fill = hex, Enabled = true } }); RebuildPanel(); }));
        }
        var custom = new Button { Content = "Custom…", MinHeight = 26, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 6) };
        custom.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                var hex = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
                Apply(d => d with { Background = d.Background with { Fill = hex, Enabled = true } });
                RebuildPanel();
            }
        };
        solids.Children.Add(custom);
        panel.Children.Add(solids);
        if (bg.Fill.StartsWith("image:", StringComparison.OrdinalIgnoreCase))
        {
            panel.Children.Add(Caption("Picture: " + IOPath.GetFileName(bg.Fill[6..])));
            panel.Children.Add(SliderRow("Picture blur", 0, Math.Max(40, Math.Max(cw, ch) / 20), bg.ImageBlur, " px", v => Apply(d => d with { Background = d.Background with { ImageBlur = v } })));
        }

        int longEdge = Math.Max(cw, ch), shortEdge = Math.Min(cw, ch);
        panel.Children.Add(Heading("Framing"));
        panel.Children.Add(SliderRow("Padding", 0, Math.Max(200, longEdge / 2), bg.Padding, " px", v => Apply(d => d with { Background = d.Background with { Padding = v } })));
        panel.Children.Add(SliderRow("Corners", 0, Math.Max(10, shortEdge / 2), bg.Corner, " px", v => Apply(d => d with { Background = d.Background with { Corner = v } })));
        panel.Children.Add(SliderRow("Shadow", 0, Math.Max(100, longEdge / 10), bg.Shadow, " px", v => Apply(d => d with { Background = d.Background with { Shadow = v } })));

        panel.Children.Add(Heading("Ratio"));
        panel.Children.Add(Segments("ratio", [("auto", "Auto"), ("1:1", "1:1"), ("16:9", "16:9"), ("4:5", "4:5"), ("9:16", "9:16")], bg.Ratio,
            value => Apply(d => d with { Background = d.Background with { Ratio = value } })));
        panel.Children.Add(Caption("Decorative framing for slides, posts and reports. It does not cut the subject out of the picture."));
        return panel;
    }

    private (int Width, int Height) ContentSize()
    {
        var doc = _session!.Document;
        var (w, h) = StudioRenderer.OrientedSize(_session.Source.Width, _session.Source.Height, doc.Crop.Rotation);
        return doc.Crop.Rect is { } r ? (r.ClampTo(w, h).Width, r.ClampTo(w, h).Height) : (w, h);
    }

    // ---- Annotate -----------------------------------------------------------------------------

    private UIElement AnnotatePanel()
    {
        var doc = _session!.Document;
        var panel = Column();
        panel.Children.Add(Heading("Tool"));
        var tools = new[]
        {
            (DrawTool.Arrow, "Arrow"), (DrawTool.Rectangle, "Box"), (DrawTool.Ellipse, "Ellipse"), (DrawTool.Line, "Line"),
            (DrawTool.Pen, "Pen"), (DrawTool.Highlight, "Highlight"), (DrawTool.Text, "Text"), (DrawTool.Step, "Number"),
        };
        if (_draw.Tool >= DrawTool.RedactSolid) _draw.Tool = DrawTool.Arrow;
        panel.Children.Add(Segments("tool", tools.Select(t => (t.Item1.ToString(), t.Item2)).ToArray(), _draw.Tool.ToString(),
            value => _draw.Tool = Enum.Parse<DrawTool>(value), wrap: true));

        panel.Children.Add(Heading("Colour"));
        var colours = new WrapPanel();
        foreach (var hex in new[] { "#E5484D", "#F76B15", "#FFC53D", "#30A46C", "#0090FF", "#8E4EC6", "#111111", "#FFFFFF" })
        {
            colours.Children.Add(Swatch(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)), hex, _draw.Color == hex, 26, 13, () =>
            {
                _draw.Color = hex;
                RebuildPanel();
            }));
        }
        panel.Children.Add(colours);

        var (cw, ch) = ContentSize();
        double unit = Math.Max(1, Math.Max(cw, ch) / 1000.0);
        if (_draw.Stroke < 1) _draw.Stroke = Math.Round(4 * unit);
        panel.Children.Add(SliderRow("Line width", 1, (int)(40 * unit), (int)_draw.Stroke, " px", v => _draw.Stroke = v, recordsUndo: false));
        panel.Children.Add(SliderRow("Text size", 8, (int)(200 * unit), (int)_draw.FontSize, " px", v => _draw.FontSize = v, recordsUndo: false));

        panel.Children.Add(Heading(doc.Annotations.Count == 1 ? "1 mark" : $"{doc.Annotations.Count} marks"));
        var actions = new WrapPanel();
        actions.Children.Add(SmallButton("Remove last", () => Apply(d => d with { Annotations = d.Annotations.Take(Math.Max(0, d.Annotations.Count - 1)).ToList() }), doc.Annotations.Count > 0));
        actions.Children.Add(SmallButton("Clear all", () => Apply(d => d with { Annotations = [] }), doc.Annotations.Count > 0));
        panel.Children.Add(actions);
        panel.Children.Add(Caption("Drag on the picture to draw; click to place text or a number. Marks are drawn into the saved copy's pixels."));
        return panel;
    }

    // ---- Redact -------------------------------------------------------------------------------

    private UIElement RedactPanel()
    {
        var doc = _session!.Document;
        var panel = Column();
        if (_draw.Tool < DrawTool.RedactSolid) _draw.Tool = DrawTool.RedactSolid;
        panel.Children.Add(Heading("Method"));
        foreach (var (tool, label, note) in new[]
        {
            (DrawTool.RedactSolid, "Solid box", "Replaces the pixels with black in the saved copy."),
            (DrawTool.RedactPixelate, "Pixelate", "Obscures only — not secure removal."),
            (DrawTool.RedactBlur, "Blur", "Obscures only — not secure removal."),
        })
        {
            var rb = new RadioButton { Style = Res("Toolbox.Radio"), GroupName = "redact", IsChecked = _draw.Tool == tool, Margin = new Thickness(0, 0, 0, 6) };
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = label });
            text.Children.Add(new TextBlock { Text = note, Style = (Style)Application.Current.Resources["Text.Caption"] });
            rb.Content = text;
            rb.Checked += (_, _) => _draw.Tool = tool;
            panel.Children.Add(rb);
        }

        panel.Children.Add(Heading(doc.Redactions.Count == 1 ? "1 area" : $"{doc.Redactions.Count} areas"));
        var actions = new WrapPanel();
        actions.Children.Add(SmallButton("Remove last", () => Apply(d => d with { Redactions = d.Redactions.Take(Math.Max(0, d.Redactions.Count - 1)).ToList() }), doc.Redactions.Count > 0));
        actions.Children.Add(SmallButton("Clear all", () => Apply(d => d with { Redactions = [] }), doc.Redactions.Count > 0));
        panel.Children.Add(actions);

        var card = new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Margin = new Thickness(0, 10, 0, 0) };
        card.Child = new TextBlock
        {
            Text = _session.Source.File.Kind == FileKind.Pdf
                ? "The saved result is an image of the page with the areas covered. The original PDF is not changed and still contains its text."
                : "Only the saved copy is changed. Metadata is not included in Studio exports.",
            Style = (Style)Application.Current.Resources["Text.Caption"],
        };
        panel.Children.Add(card);
        panel.Children.Add(Caption("Drag over the areas to hide."));
        return panel;
    }

    // ---- Export -------------------------------------------------------------------------------

    private UIElement ExportPanel()
    {
        var ex = _session!.Document.Export;
        var panel = Column();
        var resolved = StudioExporter.ResolveFormat(_session.Document, _session.Source);

        if (_entry.SuffixOverride == " Compressed") panel.Children.Add(TargetSection(ex));

        panel.Children.Add(Heading("Format"));
        panel.Children.Add(Segments("format", [("auto", $"Auto ({resolved.Label()})"), ("png", "PNG"), ("jpg", "JPG"), ("webp", "WebP"), ("avif", "AVIF")], ex.Format,
            value => { Apply(d => d with { Export = d.Export with { Format = value } }); RebuildPanel(); }, wrap: true));

        bool lossy = resolved is FileKind.Jpeg or FileKind.WebP or FileKind.Avif;
        if (lossy && ex.TargetBytes is null)
            panel.Children.Add(SliderRow("Quality", 30, 100, ex.Quality, "", v => Apply(d => d with { Export = d.Export with { Quality = v } })));
        else if (!lossy)
            panel.Children.Add(Caption($"{resolved.Label()} is lossless: quality does not apply. Choose JPG, WebP or AVIF for smaller files."));

        panel.Children.Add(Heading("Size"));
        var limit = new CheckBox { Content = "Limit the longest side to", IsChecked = ex.MaxEdge is not null };
        var edge = NumberBox(ex.MaxEdge ?? 1920);
        void SetEdge()
        {
            if (int.TryParse(edge.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 16)
                Apply(d => d with { Export = d.Export with { MaxEdge = limit.IsChecked == true ? v : null } });
        }
        limit.Checked += (_, _) => SetEdge();
        limit.Unchecked += (_, _) => Apply(d => d with { Export = d.Export with { MaxEdge = null } });
        Hook(edge, SetEdge);
        var edgeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        edgeRow.Children.Add(edge);
        edgeRow.Children.Add(Label("px", 22, new Thickness(6, 0, 0, 0)));
        panel.Children.Add(limit);
        panel.Children.Add(edgeRow);
        var chips = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var preset in new[] { 1080, 1600, 1920, 2560, 3840 })
            chips.Children.Add(SmallButton(preset.ToString(CultureInfo.InvariantCulture), () => { edge.Text = preset.ToString(CultureInfo.InvariantCulture); limit.IsChecked = true; SetEdge(); }));
        panel.Children.Add(chips);

        if (_entry.SuffixOverride != " Compressed") panel.Children.Add(TargetSection(ex));

        var card = new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Margin = new Thickness(0, 12, 0, 0) };
        var info = new StackPanel();
        var (fw, fh) = StudioRenderer.FinalSize(_session.Document, _session.Source);
        info.Children.Add(new TextBlock { Text = OutputName(_session.Document, resolved), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        info.Children.Add(new TextBlock { Text = $"{fw} × {fh} px · {resolved.Label()}", Style = (Style)Application.Current.Resources["Text.Caption"] });
        _estimate = new TextBlock { Text = "Estimating size…", Style = (Style)Application.Current.Resources["Text.Caption"] };
        info.Children.Add(_estimate);
        info.Children.Add(new TextBlock { Text = "Saved next to the original, which is not changed. Camera and location metadata are not included.", Style = (Style)Application.Current.Resources["Text.Caption"], Margin = new Thickness(0, 6, 0, 0) });
        if (_session.Source.HasAlpha && resolved is FileKind.Jpeg or FileKind.Bmp)
            info.Children.Add(new TextBlock { Text = $"{resolved.Label()} has no transparency: transparent areas become white.", Style = (Style)Application.Current.Resources["Text.Caption"] });
        card.Child = info;
        panel.Children.Add(card);
        return panel;
    }

    private UIElement TargetSection(ExportSettings ex)
    {
        var section = Column();
        section.Children.Add(Heading("File size"));
        var fit = new CheckBox { Content = "Fit under", IsChecked = ex.TargetBytes is not null };
        long current = ex.TargetBytes ?? 2 * 1024 * 1024;
        bool inMb = current >= 1024 * 1024;
        var amount = NumberBox(inMb ? Math.Round(current / 1048576.0, 1) : Math.Round(current / 1024.0));
        amount.Width = 64;
        var unit = new ComboBox { Items = { "KB", "MB" }, SelectedIndex = inMb ? 1 : 0, Width = 64, Margin = new Thickness(6, 0, 0, 0) };
        void SetTarget()
        {
            if (fit.IsChecked != true) { Apply(d => d with { Export = d.Export with { TargetBytes = null } }); return; }
            if (!double.TryParse(amount.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0) return;
            long bytes = (long)(v * (unit.SelectedIndex == 1 ? 1048576 : 1024));
            Apply(d => d with { Export = d.Export with { TargetBytes = bytes } });
        }
        fit.Checked += (_, _) => { SetTarget(); RebuildPanel(); };
        fit.Unchecked += (_, _) => { SetTarget(); RebuildPanel(); };
        Hook(amount, SetTarget);
        unit.SelectionChanged += (_, _) => SetTarget();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(amount);
        row.Children.Add(unit);
        section.Children.Add(fit);
        section.Children.Add(row);
        var shrink = new CheckBox { Content = "Allow smaller dimensions if needed", IsChecked = ex.AllowDownscale, Margin = new Thickness(0, 8, 0, 0) };
        shrink.Checked += (_, _) => Apply(d => d with { Export = d.Export with { AllowDownscale = true } });
        shrink.Unchecked += (_, _) => Apply(d => d with { Export = d.Export with { AllowDownscale = false } });
        section.Children.Add(shrink);
        section.Children.Add(Caption($"Lowers quality first (not below {ex.MinQuality}), then size. A target is a request: the result reports whether it was met."));
        return section;
    }

    private async void RefreshEstimate()
    {
        if (_session is null || _estimate is null) return;
        int version = ++_estimateVersion;
        var doc = _session.Document;
        await Task.Delay(250);
        if (version != _estimateVersion || _session is null) return;
        if (doc.Export.TargetBytes is { } target)
        {
            _estimate.Text = $"Target: under {Bytes.Format(target)} — checked when saving.";
            return;
        }
        var format = StudioExporter.ResolveFormat(doc, _session.Source);
        var bytes = await PreviewEngine.EstimateBytesAsync(doc, _session.Source, format);
        if (version != _estimateVersion || _estimate is null) return;
        _estimate.Text = bytes is { } b ? $"≈ {Bytes.Format(b)} (estimate; the actual size is shown after saving)" : "Size estimate unavailable.";
    }

    // ---- small UI helpers -----------------------------------------------------------------------

    private static StackPanel Column() => new() { Margin = new Thickness(0, 2, 0, 12) };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        FontSize = 12.5,
        Margin = new Thickness(0, 12, 0, 6),
    };

    private static TextBlock Caption(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["Text.Caption"], Margin = new Thickness(0, 10, 0, 0) };

    private static TextBlock Label(string text, double width, Thickness? margin = null) => new()
    {
        Text = text,
        Width = width,
        VerticalAlignment = VerticalAlignment.Center,
        Style = (Style)Application.Current.Resources["Text.Label"],
        Margin = margin ?? new Thickness(0),
    };

    private static TextBox NumberBox(double value) => new()
    {
        Text = value.ToString(CultureInfo.InvariantCulture),
        Width = 76,
        HorizontalContentAlignment = HorizontalAlignment.Right,
    };

    private static void Hook(TextBox box, Action commit)
    {
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(); e.Handled = true; } };
        box.LostKeyboardFocus += (_, _) => commit();
    }

    private static Button SmallButton(string text, Action click, bool enabled = true)
    {
        var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3, 10, 3), MinHeight = 28, IsEnabled = enabled };
        b.Click += (_, _) => click();
        return b;
    }

    private Border Segments(string group, IReadOnlyList<(string Value, string Label)> options, string selected, Action<string> onSelect, bool wrap = false)
    {
        Panel host = wrap ? new WrapPanel() : new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (value, label) in options)
        {
            var rb = new RadioButton
            {
                Content = label,
                Style = Res("Toolbox.Segment"),
                GroupName = group + GetHashCode(),
                IsChecked = string.Equals(value, selected, StringComparison.OrdinalIgnoreCase),
            };
            rb.Checked += (_, _) => onSelect(value);
            host.Children.Add(rb);
        }
        return new Border { Style = (Style)Application.Current.Resources["Toolbox.SegmentBar"], Child = host, HorizontalAlignment = HorizontalAlignment.Left };
    }

    private FrameworkElement SliderRow(string label, int min, int max, int value, string unit, Action<int> onChange, bool recordsUndo = true)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
        var slider = new Slider { Minimum = min, Maximum = Math.Max(max, min + 1), Value = Math.Clamp(value, min, Math.Max(max, min + 1)), SmallChange = 1, LargeChange = Math.Max(1, (max - min) / 20), VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1 };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        var text = new TextBlock { Text = value + unit, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.Resources["Text.Label"] };
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            text.Text = v + unit;
            onChange(v);
        };
        if (recordsUndo)
        {
            slider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _session?.BeginTransaction()));
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => _session?.EndTransaction()));
        }
        slider.MouseDoubleClick += (_, _) => slider.Value = Math.Clamp(0, min, max);
        grid.Children.Add(name);
        Grid.SetColumn(slider, 1);
        grid.Children.Add(slider);
        Grid.SetColumn(text, 2);
        grid.Children.Add(text);
        return grid;
    }

    private static Button Swatch(Brush fill, string name, bool selected, double size, double radius, Action click)
    {
        var template = new ControlTemplate(typeof(Button));
        var outer = new FrameworkElementFactory(typeof(Border), "Ring");
        outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius + 3));
        outer.SetValue(Border.BorderThicknessProperty, new Thickness(2));
        outer.SetValue(Border.PaddingProperty, new Thickness(2));
        if (selected) outer.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        else outer.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        var inner = new FrameworkElementFactory(typeof(Border));
        inner.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        inner.SetValue(Border.BackgroundProperty, fill);
        inner.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        inner.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        outer.AppendChild(inner);
        template.VisualTree = outer;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85));
        template.Triggers.Add(hover);
        var button = new Button { Template = template, Width = size + 8, Height = size + 8, Margin = new Thickness(0, 0, 4, 4), ToolTip = name, Cursor = Cursors.Hand };
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}
