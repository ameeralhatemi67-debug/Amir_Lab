using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Toolbox.Core.Pulse;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse;

/// <summary>A ring that fills clockwise from the top with the share used.</summary>
internal sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(UsageRing), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush Fill { get; set; } = Brushes.Green;
    public Brush Track { get; set; } = Brushes.Gray;
    public double Thickness { get; set; } = 4;

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - Thickness / 2;
        dc.DrawEllipse(null, new Pen(Track, Thickness), center, r, r);
        double v = Math.Clamp(Value, 0, 100);
        if (v <= 0) return;
        var pen = new Pen(Fill, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 99.9)
        {
            dc.DrawEllipse(null, pen, center, r, r);
            return;
        }
        double angle = v / 100 * 2 * Math.PI;
        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X + r * Math.Sin(angle), center.Y - r * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 50, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>
/// One tile of the dock: a usage ring or a glass square with the tile's mark, a short label under
/// it, and for Claude a light around the ring (circling while it works, pulsing amber while it
/// waits for you, green when done) with a badge. Springs up on hover and dips when pressed.
/// </summary>
internal sealed class PulseTileView : Grid
{
    public static readonly Color Green = Color.FromRgb(0x22, 0xC5, 0x5E);
    public static readonly Color Amber = Color.FromRgb(0xF5, 0x9E, 0x0B);
    public static readonly Color Red = Color.FromRgb(0xEF, 0x44, 0x44);

    private readonly Border _hover = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly Grid _face = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ScaleTransform _faceScale = new(1, 1);
    private readonly ScaleTransform _press = new(1, 1);
    private readonly UsageRing? _ring;
    private readonly Border? _plain;
    private readonly FrameworkElement _glyph;
    private readonly Grid _halo = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly RotateTransform _haloSpin = new();
    private readonly Border _badge = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock _badgeText = new() { Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new() { FontSize = 11, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _stack = new() { VerticalAlignment = VerticalAlignment.Center };
    private string _status = ClaudeActivity.Idle;
    private double _scale = 1;
    private bool _showLabel = true;

    public PulseTileView(string id, string icon)
    {
        Id = id;
        Background = Brushes.Transparent; // the whole tile takes the pointer, not only its drawing
        Cursor = Cursors.Hand;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _press;
        _hover.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
        Children.Add(_hover);

        if (PulseTiles.HasRing(id) || id == PulseTiles.Timer)
        {
            _ring = new UsageRing();
            _ring.Track = Application.Current?.TryFindResource("Brush.LapTrack") as Brush
                ?? new SolidColorBrush(ThemeResources.Lap.IsDark ? Color.FromRgb(0x2B, 0x2C, 0x33) : Color.FromRgb(0xDA, 0xDD, 0xE3));
            _face.Children.Add(_ring);
        }
        else
        {
            _plain = new Border { BorderThickness = new Thickness(1) };
            _plain.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
            _plain.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
            _face.Children.Add(_plain);
        }
        _glyph = SettingsPage.Glyph(icon, 19, "Brush.Text", stroke: id == PulseTiles.Claude ? 2 : 1.8);
        _face.Children.Add(_glyph);
        _halo.RenderTransform = _haloSpin;
        _halo.RenderTransformOrigin = new Point(0.5, 0.5);
        _face.Children.Insert(0, _halo);
        _badge.Child = _badgeText;
        _face.Children.Add(_badge);
        _face.RenderTransformOrigin = new Point(0.5, 0.5);
        _face.RenderTransform = _faceScale;

        _label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        _stack.Children.Add(_face);
        _stack.Children.Add(_label);
        Children.Add(_stack);
        ApplyScale();

        MouseEnter += (_, _) =>
        {
            Motion.To(_hover, OpacityProperty, 1, 160);
            Motion.To(_faceScale, ScaleTransform.ScaleXProperty, 1.08, 350, Motion.Spring);
            Motion.To(_faceScale, ScaleTransform.ScaleYProperty, 1.08, 350, Motion.Spring);
        };
        MouseLeave += (_, _) =>
        {
            Motion.To(_hover, OpacityProperty, 0, 220);
            Motion.To(_faceScale, ScaleTransform.ScaleXProperty, 1, 300, Motion.Out);
            Motion.To(_faceScale, ScaleTransform.ScaleYProperty, 1, 300, Motion.Out);
            Press(false);
        };
        PreviewMouseLeftButtonDown += (_, _) => Press(true);
        PreviewMouseLeftButtonUp += (_, _) => Press(false);
    }

    public string Id { get; }

    /// <summary>Size factor (Small 0.8, Medium 1, Large 1.25). Small hides the label.</summary>
    public double TileScale
    {
        get => _scale;
        set
        {
            _scale = value;
            ApplyScale();
        }
    }

    public bool ShowLabel
    {
        get => _showLabel;
        set
        {
            _showLabel = value;
            ApplyScale();
        }
    }

    private void ApplyScale()
    {
        double k = _scale;
        Width = 52 * k;
        bool label = _showLabel && _label.Text.Length > 0; // the "+" tile has none: no room kept for it
        Height = (label ? 66 : 56) * k;
        _hover.CornerRadius = new CornerRadius(16 * k);
        double face = 44 * k;
        _face.Width = face;
        _face.Height = face;
        if (_ring is not null) _ring.Thickness = 4 * k;
        if (_plain is not null) _plain.CornerRadius = new CornerRadius(14 * k);
        _glyph.Width = _glyph.Height = (_ring is not null ? 19 : 19) * k;
        _badge.Width = _badge.Height = 16;
        _badge.Margin = new Thickness(0, -3, -3, 0);
        _label.Visibility = label ? Visibility.Visible : Visibility.Collapsed;
        _label.FontSize = 11 * Math.Max(0.9, Math.Min(k, 1.1));
        _label.MaxWidth = 52 * k;
        _label.Margin = new Thickness(0, 3 * k, 0, 0);
        _halo.Margin = new Thickness(-6 * k);
        BuildHalo();
    }

    public void SetLabel(string text, string brushKey = "Brush.Text2")
    {
        bool had = _label.Text.Length > 0;
        _label.Text = text;
        if (had != (text.Length > 0)) ApplyScale();
        _label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>Fills the ring (0 to 100) in the colour of how much is used.</summary>
    public void SetUsage(double? used, Color? colour = null)
    {
        if (_ring is null) return;
        double v = used ?? 0;
        var c = colour ?? Usage.LevelOf(v) switch { UsageLevel.Hot => Red, UsageLevel.Warn => Amber, _ => Green };
        _ring.Fill = new SolidColorBrush(c);
        if (Motion.IsReduced || !IsLoaded) _ring.Value = v;
        else
        {
            var a = new DoubleAnimation(v, TimeSpan.FromMilliseconds(600)) { EasingFunction = Motion.Out };
            _ring.BeginAnimation(UsageRing.ValueProperty, a);
        }
        _ring.Opacity = used is null ? 0.55 : 1;
    }

    /// <summary>Claude's light and badge: idle, working, waiting (with how many) or done.</summary>
    public void SetStatus(string state, int waiting = 0)
    {
        if (state == _status && _badgeText.Text == BadgeText(state, waiting)) return;
        _status = state;
        BuildHalo();
        var text = BadgeText(state, waiting);
        _badge.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _badgeText.Text = text;
        _badge.Background = new SolidColorBrush(state == ClaudeActivity.Done ? Green : Amber);
        _badge.CornerRadius = new CornerRadius(8);
        _badge.BorderThickness = new Thickness(1.5);
        _badge.SetResourceReference(Border.BorderBrushProperty, "Brush.Card");
    }

    private static string BadgeText(string state, int waiting) => state switch
    {
        ClaudeActivity.Waiting => waiting > 1 ? waiting.ToString(System.Globalization.CultureInfo.InvariantCulture) : "!",
        ClaudeActivity.Done => "✓",
        _ => "",
    };

    private void BuildHalo()
    {
        _halo.Children.Clear();
        _halo.BeginAnimation(OpacityProperty, null);
        _haloSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        _halo.RenderTransform = _haloSpin;
        if (_status is ClaudeActivity.Idle)
        {
            _halo.Visibility = Visibility.Collapsed;
            return;
        }
        _halo.Visibility = Visibility.Visible;
        double k = _scale, size = 56 * k, r = size / 2 - 1.5 * k;
        if (_status == ClaudeActivity.Working)
        {
            var accent = ThemeResources.Lap.Accent;
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 0.15));
            brush.GradientStops.Add(new GradientStop(accent, 1));
            var arc = new Path
            {
                Stroke = brush,
                StrokeThickness = 3 * k,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse(FormattableString.Invariant($"M{size / 2},{size / 2 - r} A{r},{r} 0 0 1 {size / 2 + r},{size / 2}")),
            };
            _halo.Children.Add(new Canvas { Width = size, Height = size, Children = { arc } });
            if (!Motion.IsReduced)
                _haloSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(1300)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            var colour = _status == ClaudeActivity.Waiting ? Amber : Green;
            _halo.Children.Add(new Ellipse { Width = size, Height = size, StrokeThickness = 2.5 * k, Stroke = new SolidColorBrush(Color.FromArgb(0xC0, colour.R, colour.G, colour.B)) });
            if (_status == ClaudeActivity.Waiting && !Motion.IsReduced)
                _halo.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(550)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.InOut });
        }
    }

    private void Press(bool down)
    {
        Motion.To(_press, ScaleTransform.ScaleXProperty, down ? 0.92 : 1, down ? 90 : 260, down ? Motion.Out : Motion.Spring);
        Motion.To(_press, ScaleTransform.ScaleYProperty, down ? 0.92 : 1, down ? 90 : 260, down ? Motion.Out : Motion.Spring);
    }

    /// <summary>Lights the tile up while a file is dragged over it (File Toolbox).</summary>
    public void SetDropTarget(bool on)
    {
        if (_plain is null) return;
        if (on)
        {
            var a = ThemeResources.Lap.Accent;
            _plain.Background = new RadialGradientBrush(ThemeResources.Lap.AccentLight, ThemeResources.Lap.AccentDeep) { Center = new Point(0.5, 1.1), GradientOrigin = new Point(0.5, 1.1), RadiusX = 1, RadiusY = 1 };
            _plain.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            if (_glyph is Viewbox { Child: Canvas { Children: [Path p] } }) p.Stroke = Brushes.White;
            _label.Foreground = new SolidColorBrush(a);
        }
        else
        {
            _plain.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
            _plain.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
            if (_glyph is Viewbox { Child: Canvas { Children: [Path p] } }) p.SetResourceReference(Shape.StrokeProperty, "Brush.Text");
            _label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        }
    }
}
