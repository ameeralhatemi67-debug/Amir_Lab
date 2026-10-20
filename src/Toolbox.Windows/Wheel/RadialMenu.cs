using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Toolbox.Core.Actions;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Wheel;

internal sealed record WheelSegmentModel(
    string ActionId,
    string Label,
    string Title,
    string Icon,
    bool Enabled,
    string? Detail);

internal sealed record WheelRingModel(ActionGroup Group, string Name, IReadOnlyList<WheelSegmentModel> Segments, bool ShowIcons);

/// <summary>
/// The contextual wheel: rounded annular tiles around a hub with a centre pill.
/// Angles are measured clockwise from 12 o'clock. Hit testing is by angle and radius,
/// so it is exact regardless of the visual rounding.
/// </summary>
internal sealed class RadialMenu : Canvas
{
    public const int CenterHit = -1;
    public const int OutsideHit = -2;

    private const double OuterPadding = 10;
    private const double TileGap = 5;
    private const double CornerRadius = 9;
    private const double HubRatio = 0.40;

    private readonly Ellipse _disc = new();
    private readonly Ellipse _hub = new();
    private readonly Canvas _segmentLayer = new();
    private readonly Border _pill = new();
    private readonly TextBlock _pillPrimary = new();
    private readonly TextBlock _pillSecondary = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal };
    private readonly List<SegmentVisual> _segments = [];

    private Palette _palette = Palette.Current();
    private WheelRingModel? _ring;
    private int _hover = OutsideHit;
    private bool _reducedMotion;
    private string _idlePrimary = "";
    private string _idleSecondary = "";
    private int _ringIndex;
    private int _ringCount = 2;

    public RadialMenu(double diameter)
    {
        Diameter = diameter;
        Width = diameter;
        Height = diameter;
        ClipToBounds = false;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        Children.Add(_disc);
        Children.Add(_segmentLayer);
        Children.Add(_hub);

        _pillPrimary.FontSize = 13;
        _pillPrimary.FontWeight = FontWeights.SemiBold;
        _pillPrimary.TextAlignment = TextAlignment.Center;
        _pillPrimary.TextTrimming = TextTrimming.CharacterEllipsis;
        _pillSecondary.FontSize = 10.5;
        _pillSecondary.TextAlignment = TextAlignment.Center;
        _pillSecondary.TextTrimming = TextTrimming.CharacterEllipsis;
        _pillSecondary.Margin = new Thickness(0, 1, 0, 0);
        var pillStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        pillStack.Children.Add(_pillPrimary);
        pillStack.Children.Add(_pillSecondary);
        _pill.Child = pillStack;
        _pill.Padding = new Thickness(12, 6, 12, 6);
        _pill.CornerRadius = new CornerRadius(16);
        Children.Add(_pill);
        Children.Add(_dots);

        ApplyPalette(_palette);
        Layout();
    }

    public double Diameter { get; }
    public double Center => Diameter / 2;
    public double OuterRadius => Center - OuterPadding;
    public double InnerRadius => OuterRadius * HubRatio;
    public WheelRingModel? Ring => _ring;
    public int HoverIndex => _hover;
    public int SegmentCount => _ring?.Segments.Count ?? 0;

    public void ApplyPalette(Palette palette)
    {
        _palette = palette;
        _disc.Fill = new SolidColorBrush(palette.Disc);
        _disc.Stroke = new SolidColorBrush(palette.DiscRim);
        _disc.StrokeThickness = 1;
        _disc.Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = palette.IsDark ? 0.55 : 0.28, Color = Colors.Black };
        _hub.Fill = new SolidColorBrush(palette.Hub);
        _pill.Background = new SolidColorBrush(palette.Pill);
        _pill.Effect = new DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = palette.IsDark ? 0.5 : 0.18, Color = Colors.Black };
        _pillPrimary.Foreground = new SolidColorBrush(palette.PillText);
        _pillSecondary.Foreground = new SolidColorBrush(Color.FromArgb(0xA0, palette.PillText.R, palette.PillText.G, palette.PillText.B));
        if (_ring is not null) BuildSegments(_ring, animate: false);
    }

    public void SetReducedMotion(bool reduced) => _reducedMotion = reduced;

    /// <summary>Text shown in the pill while no segment is hovered (e.g. "1.2 MB").</summary>
    public void SetIdleText(string primary, string secondary = "")
    {
        _idlePrimary = primary;
        _idleSecondary = secondary;
        if (_hover < 0) ShowPill(primary, secondary);
    }

    public void SetRing(WheelRingModel ring, int ringIndex, int ringCount, bool animate)
    {
        _ring = ring;
        _ringIndex = ringIndex;
        _ringCount = ringCount;
        _hover = OutsideHit;
        BuildSegments(ring, animate);
        BuildDots();
        ShowPill(_idlePrimary, _idleSecondary);
    }

    /// <summary>Returns the segment index under a point, <see cref="CenterHit"/> or <see cref="OutsideHit"/>.</summary>
    public int HitTest(Point p)
    {
        double dx = p.X - Center, dy = p.Y - Center;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist <= InnerRadius) return CenterHit;
        if (dist > OuterRadius + OuterPadding || SegmentCount == 0) return OutsideHit;
        double angle = Math.Atan2(dx, -dy);
        if (angle < 0) angle += 2 * Math.PI;
        double sweep = 2 * Math.PI / SegmentCount;
        // Segments are centred on their mid-angle, starting with the first at 12 o'clock.
        int index = (int)Math.Floor(((angle + sweep / 2) % (2 * Math.PI)) / sweep);
        return Math.Clamp(index, 0, SegmentCount - 1);
    }

    public WheelSegmentModel? SegmentAt(int index) =>
        _ring is not null && index >= 0 && index < _ring.Segments.Count ? _ring.Segments[index] : null;

    /// <summary>Screen-independent centre point of a segment (in this control's coordinates).</summary>
    public Point SegmentCenter(int index)
    {
        double sweep = 2 * Math.PI / Math.Max(1, SegmentCount);
        double mid = index * sweep;
        double r = (OuterRadius + InnerRadius) / 2;
        return Polar(mid, r);
    }

    public void SetHover(int index)
    {
        if (index == _hover) return;
        if (_hover >= 0 && _hover < _segments.Count) Highlight(_segments[_hover], false);
        _hover = index;
        if (index >= 0 && index < _segments.Count)
        {
            var seg = _segments[index];
            Highlight(seg, true);
            var model = seg.Model;
            ShowPill(model.Enabled ? model.Title : model.Label, model.Detail ?? "");
        }
        else
        {
            ShowPill(_idlePrimary, _idleSecondary);
        }
    }

    /// <summary>Brief confirmation pulse on the chosen tile.</summary>
    public void Confirm(int index)
    {
        if (index < 0 || index >= _segments.Count || _reducedMotion) return;
        var seg = _segments[index];
        var anim = new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(70)) { AutoReverse = true };
        seg.Tile.BeginAnimation(OpacityProperty, anim);
    }

    private void Layout()
    {
        double d = Diameter - OuterPadding * 2;
        _disc.Width = d;
        _disc.Height = d;
        SetLeft(_disc, OuterPadding);
        SetTop(_disc, OuterPadding);

        double hub = InnerRadius * 2 - TileGap * 2;
        _hub.Width = hub;
        _hub.Height = hub;
        SetLeft(_hub, Center - hub / 2);
        SetTop(_hub, Center - hub / 2);

        _pill.MaxWidth = InnerRadius * 2 - 18;
        _pill.MinWidth = Math.Min(86, _pill.MaxWidth);
        _pill.SizeChanged += (_, _) => PositionPill();
        _dots.SizeChanged += (_, _) => PositionDots();
    }

    private void PositionPill()
    {
        SetLeft(_pill, Center - _pill.ActualWidth / 2);
        SetTop(_pill, Center - _pill.ActualHeight / 2);
    }

    private void PositionDots()
    {
        SetLeft(_dots, Center - _dots.ActualWidth / 2);
        SetTop(_dots, Center + InnerRadius * 0.58);
    }

    private void ShowPill(string primary, string secondary)
    {
        _pillPrimary.Text = primary;
        _pillSecondary.Text = secondary;
        _pillSecondary.Visibility = string.IsNullOrEmpty(secondary) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildDots()
    {
        _dots.Children.Clear();
        if (_ringCount < 2) return;
        for (int i = 0; i < _ringCount; i++)
        {
            bool active = i == _ringIndex;
            _dots.Children.Add(new Border
            {
                Width = active ? 14 : 5,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(active ? _palette.Accent : _palette.TextMuted),
            });
        }
    }

    private void BuildSegments(WheelRingModel ring, bool animate)
    {
        var old = _segmentLayer.Children.Cast<UIElement>().ToList();
        var layer = new Canvas();
        _segments.Clear();

        int n = ring.Segments.Count;
        for (int i = 0; i < n; i++)
        {
            var visual = CreateSegment(ring, ring.Segments[i], i, n);
            _segments.Add(visual);
            layer.Children.Add(visual.Tile);
            layer.Children.Add(visual.Content);
        }

        if (animate && !_reducedMotion && old.Count > 0)
        {
            foreach (var element in old)
            {
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
                fade.Completed += (_, _) => _segmentLayer.Children.Remove(element);
                element.BeginAnimation(OpacityProperty, fade);
            }
            var rotate = new RotateTransform(-14, Center, Center);
            layer.RenderTransform = rotate;
            layer.Opacity = 0;
            _segmentLayer.Children.Add(layer);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
            layer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }
        else
        {
            _segmentLayer.Children.Clear();
            _segmentLayer.Children.Add(layer);
        }
    }

    private SegmentVisual CreateSegment(WheelRingModel ring, WheelSegmentModel model, int index, int count)
    {
        double sweep = 2 * Math.PI / count;
        double mid = index * sweep;
        double start = mid - sweep / 2;
        double end = mid + sweep / 2;

        var tileBrush = new SolidColorBrush(model.Enabled ? _palette.Tile : _palette.TileDisabled);
        var tile = new Path
        {
            Data = TileGeometry(start, end, count),
            Fill = tileBrush,
            Stroke = tileBrush,
            StrokeThickness = CornerRadius * 2,
            StrokeLineJoin = PenLineJoin.Round,
            SnapsToDevicePixels = false,
        };
        var offset = new TranslateTransform();
        tile.RenderTransform = offset;

        var fg = new SolidColorBrush(model.Enabled ? _palette.Text : _palette.TextMuted);
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        if (ring.ShowIcons)
        {
            stack.Children.Add(new Path
            {
                Data = Icons.Get(model.Icon),
                Stroke = fg,
                StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 3),
            });
        }
        var label = new TextBlock
        {
            Text = model.Label.ToUpperInvariant(),
            FontSize = ring.ShowIcons ? 10.5 : (count > 6 ? 14 : 15.5),
            FontWeight = ring.ShowIcons ? FontWeights.SemiBold : FontWeights.Bold,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        stack.Children.Add(label);
        stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var c = Polar(mid, (OuterRadius + InnerRadius) / 2 + 2);
        SetLeft(stack, c.X - stack.DesiredSize.Width / 2);
        SetTop(stack, c.Y - stack.DesiredSize.Height / 2);
        stack.RenderTransform = offset;

        return new SegmentVisual(model, tile, tileBrush, stack, fg, offset, mid);
    }

    private Geometry TileGeometry(double start, double end, int count)
    {
        // Inset the tile by the corner radius, then stroke it with 2×radius and round joins:
        // the outline grows back to full size with evenly rounded corners.
        double ro = OuterRadius - CornerRadius;
        double ri = InnerRadius + CornerRadius;
        double halfGap = TileGap / 2 + CornerRadius;
        double outerDelta = Math.Asin(Math.Min(0.9, halfGap / ro));
        double innerDelta = Math.Asin(Math.Min(0.9, halfGap / ri));
        if (count == 1) { outerDelta = 0; innerDelta = 0; }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (count == 1)
            {
                ctx.BeginFigure(Polar(0, ro), true, true);
                ctx.ArcTo(Polar(Math.PI, ro), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, true);
                ctx.ArcTo(Polar(2 * Math.PI - 0.0001, ro), new Size(ro, ro), 0, false, SweepDirection.Clockwise, true, true);
            }
            else
            {
                var os = Polar(start + outerDelta, ro);
                var oe = Polar(end - outerDelta, ro);
                var ie = Polar(end - innerDelta, ri);
                var @is = Polar(start + innerDelta, ri);
                bool largeOuter = (end - start - 2 * outerDelta) > Math.PI;
                bool largeInner = (end - start - 2 * innerDelta) > Math.PI;
                ctx.BeginFigure(os, true, true);
                ctx.ArcTo(oe, new Size(ro, ro), 0, largeOuter, SweepDirection.Clockwise, true, true);
                ctx.LineTo(ie, true, true);
                ctx.ArcTo(@is, new Size(ri, ri), 0, largeInner, SweepDirection.Counterclockwise, true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void Highlight(SegmentVisual seg, bool on)
    {
        var duration = TimeSpan.FromMilliseconds(_reducedMotion ? 0 : 90);
        Color tileTarget = on && seg.Model.Enabled ? _palette.Accent : (seg.Model.Enabled ? _palette.Tile : _palette.TileDisabled);
        if (on && !seg.Model.Enabled) tileTarget = _palette.TileDisabled;
        Color textTarget = on && seg.Model.Enabled ? _palette.AccentText : (seg.Model.Enabled ? _palette.Text : _palette.TextMuted);

        seg.TileBrush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(tileTarget, duration));
        seg.Foreground.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(textTarget, duration));

        double push = on && seg.Model.Enabled && !_reducedMotion ? 4 : 0;
        var target = Polar(seg.MidAngle, push) - new Vector(Center, Center);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        seg.Offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(target.X, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
        seg.Offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(target.Y, TimeSpan.FromMilliseconds(120)) { EasingFunction = ease });
    }

    private Point Polar(double angle, double radius) =>
        new(Center + Math.Sin(angle) * radius, Center - Math.Cos(angle) * radius);

    private sealed record SegmentVisual(
        WheelSegmentModel Model,
        Path Tile,
        SolidColorBrush TileBrush,
        FrameworkElement Content,
        SolidColorBrush Foreground,
        TranslateTransform Offset,
        double MidAngle);
}
