using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
/// The contextual wheel, in Amir Lap's glass style: a see-through frosted disc with a fine ice
/// texture, light caught at its edges, glass tiles around a lens-like hub, and a Share bubble just
/// outside. The tile under the pointer turns into tinted, lit glass and nudges out a little (its
/// light stays inside the wheel); dropping makes a ripple. Angles are measured clockwise from 12 o'clock. Hit testing is by angle
/// and radius, so it is exact regardless of the visual rounding.
/// </summary>
internal sealed class RadialMenu : Canvas
{
    public const int CenterHit = -1;
    public const int OutsideHit = -2;
    public const int ShareHit = -3;

    private const double OuterPadding = 10;
    private const double TileGap = 5;
    private const double CornerRadius = 10;
    private const double HubRatio = 0.40;
    private const double BubbleRadius = 27;
    private const double BubbleAngle = 3 * Math.PI / 4; // bottom-right, in the corner of the window

    private readonly Canvas _backdrop = new() { IsHitTestVisible = false };
    private readonly Canvas _bubbleBackdrop = new() { IsHitTestVisible = false };
    private readonly Canvas _base = new() { IsHitTestVisible = false };
    private readonly Path _glow = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly Canvas _segmentLayer = new();
    private readonly Canvas _fx = new() { IsHitTestVisible = false };
    private readonly Grid _hub = new() { IsHitTestVisible = false };
    private readonly Ellipse _hubRim = new() { StrokeThickness = 1.2 };
    private readonly TextBlock _pillPrimary = new();
    private readonly TextBlock _pillSecondary = new();
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
    private readonly Grid _bubble = new() { IsHitTestVisible = false };
    private readonly Ellipse _bubbleLight = new() { Opacity = 0 };
    private readonly TextBlock _bubbleLabel = new();
    private readonly Path _bubbleIcon = new();
    private readonly ScaleTransform _bubbleScale = new(1, 1);
    private readonly List<SegmentVisual> _segments = [];

    private Palette _palette = Palette.Current();
    private Glass _glass = Glass.For(Palette.Current());
    private WheelRingModel? _ring;
    private WheelSegmentModel? _share;
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

        _glow.Effect = new BlurEffect { Radius = 18, RenderingBias = RenderingBias.Performance };
        // The glow lights the glass around a lit tile, but never the desktop outside the wheel.
        var glowLayer = new Canvas { IsHitTestVisible = false };
        glowLayer.Children.Add(_glow);
        glowLayer.Clip = Geometry.Combine(
            new EllipseGeometry(new Point(Center, Center), OuterRadius - 0.5, OuterRadius - 0.5),
            new EllipseGeometry(ShareCenter, BubbleRadius, BubbleRadius),
            GeometryCombineMode.Union, null);
        Children.Add(_backdrop); // the blurred screen sits under the glass
        Children.Add(_base);
        Children.Add(glowLayer);
        Children.Add(_segmentLayer);
        Children.Add(_hub);
        Children.Add(_dots);
        Children.Add(_bubbleBackdrop);
        Children.Add(_bubble);
        Children.Add(_fx);

        _pillPrimary.FontSize = 13;
        _pillPrimary.FontWeight = FontWeights.SemiBold;
        _pillPrimary.TextAlignment = TextAlignment.Center;
        _pillPrimary.TextTrimming = TextTrimming.CharacterEllipsis;
        _pillSecondary.FontSize = 10.5;
        _pillSecondary.TextAlignment = TextAlignment.Center;
        _pillSecondary.TextTrimming = TextTrimming.CharacterEllipsis;
        _pillSecondary.Margin = new Thickness(0, 1, 0, 0);
        _dots.SizeChanged += (_, _) => PositionDots();

        ApplyPalette(_palette);
    }

    public double Diameter { get; }
    public double Center => Diameter / 2;
    public double OuterRadius => Center - OuterPadding;
    public double InnerRadius => OuterRadius * HubRatio;
    public WheelRingModel? Ring => _ring;
    public int HoverIndex => _hover;
    public int SegmentCount => _ring?.Segments.Count ?? 0;

    /// <summary>Centre of the Share bubble (in this control's coordinates).</summary>
    public Point ShareCenter => Polar(BubbleAngle, OuterRadius + OuterPadding + BubbleRadius + 6);

    public void ApplyPalette(Palette palette)
    {
        _palette = palette;
        _glass = Glass.For(palette);
        BuildBase();
        BuildHub();
        BuildBubble();
        _glow.Fill = new SolidColorBrush(palette.Accent);
        if (_ring is not null) BuildSegments(_ring, animate: false);
        BuildDots();
    }

    public void SetReducedMotion(bool reduced) => _reducedMotion = reduced;

    /// <summary>
    /// What is on the screen behind the wheel (captured as it opens), shown blurred under the glass
    /// so it reads as frosted glass rather than a see-through ghost, and magnified a touch, like a
    /// lens. <paramref name="where"/> is the picture's place in this control's coordinates. Null
    /// clears it. Kept in memory only, for as long as the wheel is open.
    /// </summary>
    public void SetBackdrop(BitmapSource? picture, Rect where)
    {
        _backdrop.Children.Clear();
        _bubbleBackdrop.Children.Clear();
        if (picture is null) return;
        _backdrop.Clip = new EllipseGeometry(new Point(Center, Center), OuterRadius - 0.5, OuterRadius - 0.5);
        _backdrop.Children.Add(Frosted(picture, where, lens: 1.06, new Point(Center, Center)));
        _backdrop.Children.Add(new Ellipse { Width = OuterRadius * 2, Height = OuterRadius * 2, Fill = _glass.BackdropTint }.At(Center - OuterRadius, Center - OuterRadius));
        var at = ShareCenter;
        _bubbleBackdrop.Clip = new EllipseGeometry(at, BubbleRadius - 0.5, BubbleRadius - 0.5);
        _bubbleBackdrop.Children.Add(Frosted(picture, where, lens: 1.0, at));
        _bubbleBackdrop.Width = Diameter; // a size, so the origin below is where the bubble is
        _bubbleBackdrop.Height = Diameter;
        _bubbleBackdrop.RenderTransform = _bubbleScale;
        _bubbleBackdrop.RenderTransformOrigin = new Point(at.X / Diameter, at.Y / Diameter);
    }

    private static Image Frosted(BitmapSource picture, Rect where, double lens, Point lensCenter)
    {
        var image = new Image
        {
            Source = picture,
            Width = where.Width,
            Height = where.Height,
            Stretch = Stretch.Fill,
            Effect = new BlurEffect { Radius = 26, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        };
        SetLeft(image, where.X);
        SetTop(image, where.Y);
        if (lens != 1) image.RenderTransform = new ScaleTransform(lens, lens, lensCenter.X - where.X, lensCenter.Y - where.Y);
        return image;
    }

    /// <summary>Text shown in the hub while no segment is hovered (e.g. "1.2 MB").</summary>
    public void SetIdleText(string primary, string secondary = "")
    {
        _idlePrimary = primary;
        _idleSecondary = secondary;
        if (_hover is OutsideHit or CenterHit) ShowPill(primary, secondary);
    }

    /// <summary>The Share bubble's action (null hides it).</summary>
    public void SetShare(WheelSegmentModel? share)
    {
        _share = share;
        _bubble.Visibility = share is null ? Visibility.Collapsed : Visibility.Visible;
        if (share is not null) _bubbleLabel.Text = share.Label.ToUpperInvariant();
    }

    public void SetRing(WheelRingModel ring, int ringIndex, int ringCount, bool animate)
    {
        _ring = ring;
        _ringIndex = ringIndex;
        _ringCount = ringCount;
        if (_hover == ShareHit) LightBubble(false);
        _hover = OutsideHit;
        Animate(_glow, OpacityProperty, 0, 120);
        BuildSegments(ring, animate);
        BuildDots();
        ShowPill(_idlePrimary, _idleSecondary);
    }

    /// <summary>Returns the segment index under a point, <see cref="CenterHit"/>, <see cref="ShareHit"/> or <see cref="OutsideHit"/>.</summary>
    public int HitTest(Point p)
    {
        if (_share is not null && (p - ShareCenter).Length <= BubbleRadius + 4) return ShareHit;
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
        index == ShareHit ? _share
        : _ring is not null && index >= 0 && index < _ring.Segments.Count ? _ring.Segments[index] : null;

    /// <summary>Screen-independent centre point of a segment (in this control's coordinates).</summary>
    public Point SegmentCenter(int index)
    {
        if (index == ShareHit) return ShareCenter;
        double sweep = 2 * Math.PI / Math.Max(1, SegmentCount);
        return Polar(index * sweep, (OuterRadius + InnerRadius) / 2);
    }

    public void SetHover(int index)
    {
        if (index == _hover) return;
        if (_hover >= 0 && _hover < _segments.Count) Highlight(_segments[_hover], false);
        if (_hover == ShareHit) LightBubble(false);
        _hover = index;

        WheelSegmentModel? model = null;
        if (index >= 0 && index < _segments.Count)
        {
            Highlight(_segments[index], true);
            model = _segments[index].Model;
        }
        else if (index == ShareHit && _share is not null)
        {
            LightBubble(true);
            model = _share;
        }
        else
        {
            Animate(_glow, OpacityProperty, 0, 200);
        }

        if (model is not null) ShowPill(model.Enabled ? model.Title : model.Label, model.Detail ?? "");
        else ShowPill(_idlePrimary, _idleSecondary);
        var rim = model is { Enabled: true } ? _glass.HubRimActive : _glass.HubRim;
        AnimateColor((SolidColorBrush)_hubRim.Stroke, rim, 160);
    }

    /// <summary>Confirms a drop: the tile flashes and a ripple spreads from it.</summary>
    public void Confirm(int index)
    {
        if (_reducedMotion) return;
        Point at;
        TranslateTransform? offset = null;
        if (index == ShareHit) at = ShareCenter;
        else if (index >= 0 && index < _segments.Count)
        {
            var seg = _segments[index];
            at = Polar(seg.MidAngle, (OuterRadius + InnerRadius) / 2);
            offset = seg.Offset;
            seg.Light.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 1, TimeSpan.FromMilliseconds(400)));
        }
        else return;

        if (offset is not null) at += new Vector(offset.X, offset.Y);
        var ripple = new Ellipse { Width = 20, Height = 20, StrokeThickness = 2.5, Stroke = new SolidColorBrush(_glass.Ripple), RenderTransformOrigin = new Point(0.5, 0.5) };
        var grow = new ScaleTransform(1, 1);
        ripple.RenderTransform = grow;
        SetLeft(ripple, at.X - 10);
        SetTop(ripple, at.Y - 10);
        _fx.Children.Add(ripple);
        var duration = TimeSpan.FromMilliseconds(460);
        var ease = Motion.Out;
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, 7, duration) { EasingFunction = ease });
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 7, duration) { EasingFunction = ease });
        var fade = new DoubleAnimation(0.95, 0, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => _fx.Children.Remove(ripple);
        ripple.BeginAnimation(OpacityProperty, fade);
        _glow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, _glass.GlowOpacity, TimeSpan.FromMilliseconds(380)));
    }

    /// <summary>The opening flourish: tiles fly out from the hub one after another, the Share bubble pops in.</summary>
    public void PlayIntro()
    {
        if (_reducedMotion) return;
        for (int i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            var begin = TimeSpan.FromMilliseconds(18 * i);
            var inward = Polar(seg.MidAngle, -12) - new Vector(Center, Center);
            seg.Offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(inward.X, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = Motion.Spring });
            seg.Offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(inward.Y, 0, TimeSpan.FromMilliseconds(320)) { BeginTime = begin, EasingFunction = Motion.Spring });
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = begin, FillBehavior = FillBehavior.Stop };
            seg.Tile.BeginAnimation(OpacityProperty, fade);
            seg.Content.BeginAnimation(OpacityProperty, fade);
        }
        var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = TimeSpan.FromMilliseconds(120), EasingFunction = Motion.Spring };
        _bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        _bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(120), FillBehavior = FillBehavior.Stop });
    }

    // ---- the glass ---------------------------------------------------------------------------

    /// <summary>Halo shadow, frosted disc, ice texture and streaks, light along the edge, rim and highlight.</summary>
    private void BuildBase()
    {
        _base.Children.Clear();
        double d = Diameter - OuterPadding * 2;
        Ellipse Disc(double inset) => new() { Width = d - inset * 2, Height = d - inset * 2 };
        void Place(FrameworkElement e, double inset) { SetLeft(e, OuterPadding + inset); SetTop(e, OuterPadding + inset); _base.Children.Add(e); }

        // A soft shadow just around the edge (a ring, so it never greys the glass itself).
        var halo = Disc(-2);
        halo.Stroke = new SolidColorBrush(Color.FromArgb(_glass.HaloAlpha, 0, 0, 0));
        halo.StrokeThickness = 7;
        halo.Effect = new BlurEffect { Radius = 14, RenderingBias = RenderingBias.Performance };
        halo.RenderTransform = new TranslateTransform(0, 3);
        Place(halo, -2);

        var disc = Disc(0);
        disc.Fill = _glass.Disc;
        Place(disc, 0);
        var frost = Disc(0);
        frost.Fill = _glass.Texture;
        frost.Opacity = _glass.TextureOpacity;
        Place(frost, 0);
        var ice = Disc(0);
        ice.Fill = _glass.Ice;
        Place(ice, 0);

        // Light caught just inside the edge, strongest at two corners.
        var inner = Disc(4);
        inner.Stroke = _glass.EdgeLight;
        inner.StrokeThickness = 8;
        inner.Effect = new BlurEffect { Radius = 9, RenderingBias = RenderingBias.Performance };
        Place(inner, 4);
        var rim = Disc(0);
        rim.Stroke = _glass.Rim;
        rim.StrokeThickness = 1.3;
        Place(rim, 0);

        // A specular highlight along the top-left of the edge.
        double r = OuterRadius - 6;
        var arc = new StreamGeometry();
        using (var c = arc.Open())
        {
            c.BeginFigure(Polar(-1.25, r), false, false);
            c.ArcTo(Polar(-0.2, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        var spec = new LinearGradientBrush { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) };
        spec.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
        spec.GradientStops.Add(new GradientStop(Color.FromArgb(_glass.SpecularAlpha, 255, 255, 255), 0.55));
        spec.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        _base.Children.Add(new Path { Data = arc, Stroke = spec, StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Effect = new BlurEffect { Radius = 1.2 } });
    }

    /// <summary>The hub: a glass lens with the file's size (or the hovered action) written on it.</summary>
    private void BuildHub()
    {
        _hub.Children.Clear();
        double size = InnerRadius * 2 - TileGap * 2;
        _hub.Width = size;
        _hub.Height = size;
        SetLeft(_hub, Center - size / 2);
        SetTop(_hub, Center - size / 2);
        _hub.Children.Add(new Ellipse { Fill = _glass.Hub });
        _hub.Children.Add(new Ellipse { Fill = _glass.Texture, Opacity = _glass.TextureOpacity * 0.6 });
        _hubRim.Stroke = new SolidColorBrush(_glass.HubRim);
        _hub.Children.Add(_hubRim);
        var shine = new Ellipse { Margin = new Thickness(size * 0.16, 3, size * 0.16, size * 0.55), Fill = new LinearGradientBrush(Color.FromArgb(_glass.HubShineAlpha, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90) };
        _hub.Children.Add(shine);

        _pillPrimary.Foreground = new SolidColorBrush(_glass.Text);
        _pillSecondary.Foreground = new SolidColorBrush(_glass.TextMuted);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = size - 22, Margin = new Thickness(0, 0, 0, 6) };
        if (_pillPrimary.Parent is Panel old) old.Children.Remove(_pillPrimary);
        if (_pillSecondary.Parent is Panel old2) old2.Children.Remove(_pillSecondary);
        stack.Children.Add(_pillPrimary);
        stack.Children.Add(_pillSecondary);
        _hub.Children.Add(stack);
    }

    /// <summary>The Share bubble, just outside the wheel.</summary>
    private void BuildBubble()
    {
        _bubble.Children.Clear();
        double size = BubbleRadius * 2;
        _bubble.Width = size;
        _bubble.Height = size;
        _bubble.RenderTransformOrigin = new Point(0.5, 0.5);
        _bubble.RenderTransform = _bubbleScale;
        var at = ShareCenter;
        SetLeft(_bubble, at.X - BubbleRadius);
        SetTop(_bubble, at.Y - BubbleRadius);

        var shadow = new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(_glass.HaloAlpha, 0, 0, 0)), Margin = new Thickness(3, 6, 3, 0), Effect = new BlurEffect { Radius = 10, RenderingBias = RenderingBias.Performance } };
        _bubble.Children.Add(shadow);
        _bubble.Children.Add(new Ellipse { Fill = _glass.Hub });
        _bubble.Children.Add(new Ellipse { Fill = _glass.Texture, Opacity = _glass.TextureOpacity });
        _bubbleLight.Fill = _glass.LitFill(new Point(BubbleRadius, 0), new Point(BubbleRadius, size));
        _bubbleLight.Stroke = _glass.LitRim(new Point(BubbleRadius, 0), new Point(BubbleRadius, size));
        _bubbleLight.StrokeThickness = 1.3;
        _bubble.Children.Add(_bubbleLight);
        _bubble.Children.Add(new Ellipse { Stroke = _glass.Rim, StrokeThickness = 1.2 });

        var fg = new SolidColorBrush(_glass.Text);
        _bubbleIcon.Data = Icons.Get("lap-share");
        _bubbleIcon.Stroke = fg;
        _bubbleIcon.StrokeThickness = 1.8;
        _bubbleIcon.StrokeLineJoin = PenLineJoin.Round;
        _bubbleIcon.StrokeStartLineCap = PenLineCap.Round;
        _bubbleIcon.StrokeEndLineCap = PenLineCap.Round;
        _bubbleIcon.Width = 17;
        _bubbleIcon.Height = 17;
        _bubbleIcon.Stretch = Stretch.Uniform;
        _bubbleIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _bubbleLabel.FontSize = 8.5;
        _bubbleLabel.FontWeight = FontWeights.Bold;
        _bubbleLabel.Foreground = fg;
        _bubbleLabel.HorizontalAlignment = HorizontalAlignment.Center;
        _bubbleLabel.Margin = new Thickness(0, 2, 0, 0);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        if (_bubbleIcon.Parent is Panel p1) p1.Children.Remove(_bubbleIcon);
        if (_bubbleLabel.Parent is Panel p2) p2.Children.Remove(_bubbleLabel);
        stack.Children.Add(_bubbleIcon);
        stack.Children.Add(_bubbleLabel);
        _bubble.Children.Add(stack);
        _bubble.Visibility = _share is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void LightBubble(bool on)
    {
        Animate(_bubbleLight, OpacityProperty, on ? 1 : 0, on ? 160 : 220);
        var fg = on ? Colors.White : _glass.Text;
        AnimateColor((SolidColorBrush)_bubbleIcon.Stroke, fg, 140);
        Animate(_bubbleScale, ScaleTransform.ScaleXProperty, on ? 1.1 : 1, 360, Motion.Spring);
        Animate(_bubbleScale, ScaleTransform.ScaleYProperty, on ? 1.1 : 1, 360, Motion.Spring);
        if (on)
        {
            _glow.Data = new EllipseGeometry(ShareCenter, BubbleRadius, BubbleRadius);
            _glow.RenderTransform = Transform.Identity;
            Animate(_glow, OpacityProperty, _glass.GlowOpacity, 180);
        }
    }

    // ---- hub text and ring dots --------------------------------------------------------------

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
            var dot = new Border
            {
                Width = 5,
                Height = 5,
                CornerRadius = new CornerRadius(2.5),
                Margin = new Thickness(2, 0, 2, 0),
                Background = active ? _glass.AccentFill : new SolidColorBrush(_glass.TextMuted),
            };
            _dots.Children.Add(dot);
            if (active) Animate(dot, WidthProperty, 16, 380, Motion.Spring, from: 5);
        }
    }

    // ---- tiles -------------------------------------------------------------------------------

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
            layer.Children.Add(visual.Light);
            layer.Children.Add(visual.Content);
        }

        if (animate && !_reducedMotion && old.Count > 0)
        {
            foreach (var element in old)
            {
                var spin = new RotateTransform(0, Center, Center);
                element.RenderTransform = spin;
                spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(12, TimeSpan.FromMilliseconds(150)) { EasingFunction = Motion.Out });
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130));
                fade.Completed += (_, _) => _segmentLayer.Children.Remove(element);
                element.BeginAnimation(OpacityProperty, fade);
            }
            var rotate = new RotateTransform(-18, Center, Center);
            layer.RenderTransform = rotate;
            layer.Opacity = 0;
            _segmentLayer.Children.Add(layer);
            rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = Motion.Spring });
            layer.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(170)));
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
        var geometry = TileGeometry(mid - sweep / 2, mid + sweep / 2, count);
        var offset = new TranslateTransform();

        var tile = new Path
        {
            Data = geometry,
            Fill = model.Enabled ? _glass.Tile : _glass.TileOff,
            Stroke = _glass.TileRim,
            StrokeThickness = 1,
            SnapsToDevicePixels = false,
            RenderTransform = offset,
        };
        double r = (OuterRadius + InnerRadius) / 2;
        var light = LitGlass(geometry, Polar(mid, OuterRadius), Polar(mid, InnerRadius));
        light.RenderTransform = offset;

        var fg = new SolidColorBrush(model.Enabled ? _glass.Text : _glass.TextMuted);
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
        stack.Children.Add(new TextBlock
        {
            Text = model.Label.ToUpperInvariant(),
            FontSize = ring.ShowIcons ? 10.5 : (count > 6 ? 14 : 15.5),
            FontWeight = ring.ShowIcons ? FontWeights.SemiBold : FontWeights.Bold,
            Foreground = fg,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        });
        stack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var c = Polar(mid, r + 2);
        SetLeft(stack, c.X - stack.DesiredSize.Width / 2);
        SetTop(stack, c.Y - stack.DesiredSize.Height / 2);
        stack.RenderTransform = offset;

        return new SegmentVisual(model, geometry, tile, light, stack, fg, offset, mid);
    }

    /// <summary>
    /// A lit tile, kept as glass: the accent tints it (deeper at the outer edge, where the light
    /// comes from), the frost still shows through, light glows just inside its edges, and a bright
    /// rim runs along the outer edge. Nothing is drawn outside the tile's own shape.
    /// </summary>
    private Canvas LitGlass(Geometry shape, Point outer, Point inner)
    {
        var lit = new Canvas { Opacity = 0, IsHitTestVisible = false };
        lit.Children.Add(new Path { Data = shape, Fill = _glass.LitFill(outer, inner) });
        lit.Children.Add(new Path { Data = shape, Fill = _glass.Texture, Opacity = _glass.TextureOpacity * (_glass.Dark ? 0.7 : 0.4) });
        // The inner glow: a soft bright stroke, cut to the tile so it never spills.
        var glow = new Canvas { Clip = shape };
        glow.Children.Add(new Path
        {
            Data = shape,
            Stroke = new SolidColorBrush(_glass.LitEdge),
            StrokeThickness = 9,
            Opacity = 0.55,
            Effect = new BlurEffect { Radius = 9, RenderingBias = RenderingBias.Performance },
        });
        lit.Children.Add(glow);
        lit.Children.Add(new Path { Data = shape, Stroke = _glass.LitRim(outer, inner), StrokeThickness = 1.3 });
        return lit;
    }

    /// <summary>
    /// An annular tile with evenly rounded corners: the shape is inset by the corner radius, then
    /// grown back by a round-joined outline of twice that radius (a true outline, so the see-through
    /// fill is never doubled at the edges).
    /// </summary>
    private Geometry TileGeometry(double start, double end, int count)
    {
        double ro = OuterRadius - CornerRadius;
        double ri = InnerRadius + CornerRadius;
        double halfGap = TileGap / 2 + CornerRadius;
        double outerDelta = Math.Asin(Math.Min(0.9, halfGap / ro));
        double innerDelta = Math.Asin(Math.Min(0.9, halfGap / ri));

        var inset = new StreamGeometry();
        using (var ctx = inset.Open())
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
        var outline = inset.GetWidenedPathGeometry(new Pen(Brushes.Black, CornerRadius * 2) { LineJoin = PenLineJoin.Round }, 0.05, ToleranceType.Absolute);
        var rounded = Geometry.Combine(inset, outline, GeometryCombineMode.Union, null, 0.05, ToleranceType.Absolute);
        rounded.Freeze();
        return rounded;
    }

    private void Highlight(SegmentVisual seg, bool on)
    {
        bool lit = on && seg.Model.Enabled;
        Animate(seg.Light, OpacityProperty, lit ? 1 : 0, lit ? 160 : 220);
        var text = lit ? Colors.White : (seg.Model.Enabled ? _glass.Text : _glass.TextMuted);
        AnimateColor(seg.Foreground, text, 130);

        double push = lit ? 3 : on ? 1.5 : 0;
        var target = Polar(seg.MidAngle, push) - new Vector(Center, Center);
        Animate(seg.Offset, TranslateTransform.XProperty, target.X, on ? 340 : 240, on ? Motion.Spring : Motion.Out);
        Animate(seg.Offset, TranslateTransform.YProperty, target.Y, on ? 340 : 240, on ? Motion.Spring : Motion.Out);

        if (lit)
        {
            _glow.Data = seg.Geometry;
            _glow.RenderTransform = seg.Offset;
            Animate(_glow, OpacityProperty, _glass.GlowOpacity, 180);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private void Animate(IAnimatable target, DependencyProperty property, double to, int ms, IEasingFunction? ease = null, double? from = null)
    {
        if (_reducedMotion || ms <= 0)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        var animation = from is { } f
            ? new DoubleAnimation(f, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Motion.Out }
            : new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? Motion.Out };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AnimateColor(SolidColorBrush brush, Color to, int ms)
    {
        if (_reducedMotion)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)));
    }

    private Point Polar(double angle, double radius) =>
        new(Center + Math.Sin(angle) * radius, Center - Math.Cos(angle) * radius);

    private sealed record SegmentVisual(
        WheelSegmentModel Model,
        Geometry Geometry,
        Path Tile,
        UIElement Light,
        FrameworkElement Content,
        SolidColorBrush Foreground,
        TranslateTransform Offset,
        double MidAngle);

    /// <summary>The wheel's glass, in light or dark, around the user's accent colour.</summary>
    private sealed record Glass(
        bool Dark,
        Brush Disc,
        Brush Tile,
        Brush TileOff,
        Brush TileRim,
        Brush Hub,
        Brush Rim,
        Brush EdgeLight,
        Brush Ice,
        Brush BackdropTint,
        ImageBrush Texture,
        double TextureOpacity,
        Brush AccentFill,
        Color Accent,
        Color Text,
        Color TextMuted,
        Color HubRim,
        Color HubRimActive,
        Color LitEdge,
        Color Ripple,
        byte HaloAlpha,
        byte SpecularAlpha,
        byte HubShineAlpha,
        double GlowOpacity)
    {
        public static Glass For(Palette p)
        {
            bool dark = p.IsDark;
            static Color W(byte a) => Color.FromArgb(a, 255, 255, 255);
            var a = p.Accent;
            return new Glass(
                Dark: dark,
                // Dark: the same glass as the Longshot panel (Brush.Glass), so the desktop's colour
                // comes through. Light was already at least as see-through as the panel.
                Disc: dark
                    ? Vertical(Color.FromArgb(0x94, 0x2C, 0x2D, 0x34), Color.FromArgb(0x61, 0x16, 0x17, 0x1C))
                    : Diagonal(Color.FromArgb(0x7A, 0xF8, 0xFA, 0xFF), Color.FromArgb(0x4D, 0xEE, 0xF1, 0xF8)),
                Tile: Vertical(dark ? W(0x1F) : W(0x8C), dark ? W(0x0A) : W(0x40)),
                TileOff: Vertical(dark ? W(0x0D) : W(0x40), dark ? W(0x05) : W(0x1F)),
                TileRim: Diagonal(dark ? W(0x33) : W(0xF0), dark ? W(0x0D) : W(0x66)),
                Hub: RadialFill(dark ? Color.FromArgb(0xEB, 0x30, 0x32, 0x3C) : W(0xF0), dark ? Color.FromArgb(0xD9, 0x18, 0x19, 0x1F) : Color.FromArgb(0xB8, 0xF4, 0xF6, 0xFB)),
                Rim: Corners(dark ? W(0x5C) : W(0xFA), dark ? W(0x0A) : W(0x40)),
                EdgeLight: Corners(dark ? W(0x40) : W(0xE6), Color.FromArgb(0, 255, 255, 255)),
                Ice: IceStreaks(dark ? (byte)0x10 : (byte)0x26),
                BackdropTint: Frozen(new SolidColorBrush(dark ? Color.FromArgb(0x1A, 0x00, 0x00, 0x00) : Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF))),
                Texture: Grain(dark),
                TextureOpacity: dark ? 0.45 : 0.7,
                AccentFill: Frozen(new LinearGradientBrush(ThemeResources.Mix(a, Colors.White, 0.35), a, 0)),
                Accent: a,
                Text: dark ? Color.FromRgb(0xF2, 0xF3, 0xF6) : Color.FromRgb(0x15, 0x17, 0x1C),
                TextMuted: dark ? Color.FromArgb(0x73, 0xF2, 0xF3, 0xF6) : Color.FromArgb(0x7A, 0x15, 0x17, 0x1C),
                HubRim: dark ? W(0x29) : W(0xE6),
                HubRimActive: Color.FromArgb(0xB3, a.R, a.G, a.B),
                LitEdge: ThemeResources.Mix(a, Colors.White, 0.6),
                Ripple: ThemeResources.Mix(a, Colors.White, 0.45),
                HaloAlpha: dark ? (byte)0x8C : (byte)0x3D,
                SpecularAlpha: dark ? (byte)0x8C : (byte)0xF2,
                HubShineAlpha: dark ? (byte)0x1F : (byte)0x8C,
                GlowOpacity: dark ? 0.5 : 0.55);
        }

        /// <summary>
        /// The tint of a lit tile, from its outer edge (brighter, more of the accent) to the hub
        /// (deeper, more see-through). Light glass needs more colour so white text still reads.
        /// </summary>
        public Brush LitFill(Point outer, Point inner)
        {
            bool dark = Dark;
            var b = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = outer, EndPoint = inner };
            b.GradientStops.Add(new GradientStop(WithAlpha(ThemeResources.Mix(Accent, Color.FromRgb(0xB8, 0xE6, 0xFF), 0.22), dark ? (byte)0xD9 : (byte)0xF0), 0));
            b.GradientStops.Add(new GradientStop(WithAlpha(Accent, dark ? (byte)0xA6 : (byte)0xD6), 0.5));
            b.GradientStops.Add(new GradientStop(WithAlpha(ThemeResources.Mix(Accent, Color.FromRgb(0x2A, 0x1A, 0x8A), 0.3), dark ? (byte)0x73 : (byte)0xB8), 1));
            return Frozen(b);
        }

        /// <summary>The lit tile's rim: bright along the outer edge, fading towards the hub.</summary>
        public Brush LitRim(Point outer, Point inner)
        {
            var b = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = outer, EndPoint = inner };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, 255, 255, 255), 0));
            b.GradientStops.Add(new GradientStop(WithAlpha(LitEdge, 0x8C), 0.45));
            b.GradientStops.Add(new GradientStop(WithAlpha(LitEdge, 0x33), 1));
            return Frozen(b);
        }

        private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        private static Brush Vertical(Color top, Color bottom) => Frozen(new LinearGradientBrush(top, bottom, 90));

        private static Brush Diagonal(Color from, Color to) => Frozen(new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1)));

        private static Brush RadialFill(Color center, Color edge)
        {
            var b = new RadialGradientBrush(center, edge) { GradientOrigin = new Point(0.4, 0.3), Center = new Point(0.45, 0.4), RadiusX = 0.65, RadiusY = 0.65 };
            return Frozen(b);
        }

        /// <summary>Bright at the top-left and bottom-right, fading in between: light caught by the edge.</summary>
        private static Brush Corners(Color bright, Color faint)
        {
            var b = new LinearGradientBrush { StartPoint = new Point(0.05, 0.05), EndPoint = new Point(0.95, 0.95) };
            b.GradientStops.Add(new GradientStop(bright, 0));
            b.GradientStops.Add(new GradientStop(faint, 0.36));
            b.GradientStops.Add(new GradientStop(faint, 0.64));
            b.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(bright.A * 0.6), bright.R, bright.G, bright.B), 1));
            return Frozen(b);
        }

        /// <summary>A few soft diagonal light bands, like the inside of a block of ice.</summary>
        private static Brush IceStreaks(byte alpha)
        {
            var clear = Color.FromArgb(0, 255, 255, 255);
            Color Band(double k) => Color.FromArgb((byte)(alpha * k), 255, 255, 255);
            var b = new LinearGradientBrush { StartPoint = new Point(0, 0.15), EndPoint = new Point(1, 0.85) };
            foreach (var (offset, color) in new[]
            {
                (0.0, clear), (0.17, clear), (0.21, Band(1)), (0.25, clear), (0.43, clear), (0.47, Band(0.6)), (0.49, clear),
                (0.66, clear), (0.71, Band(0.9)), (0.77, clear), (1.0, clear),
            })
                b.GradientStops.Add(new GradientStop(color, offset));
            return Frozen(b);
        }

        /// <summary>Fine frosted grain, so the glass reads as a material, not a ghost.</summary>
        private static ImageBrush Grain(bool dark) => GlassTexture.Grain(dark);

        private static T Frozen<T>(T brush) where T : Freezable
        {
            brush.Freeze();
            return brush;
        }
    }
}

internal static class CanvasPlacement
{
    public static T At<T>(this T element, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        return element;
    }
}
