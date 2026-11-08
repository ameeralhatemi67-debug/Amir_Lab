using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Home;

/// <summary>Something that moves on its own while the window is in front (paused otherwise, and with Reduce motion).</summary>
internal interface IIdleMotion
{
    void SetRunning(bool running);
}

/// <summary>
/// On/off switch: the knob springs across and stretches while it is held, the track fills with the
/// accent. A right-to-left layout mirrors it on its own.
/// </summary>
internal sealed class GelSwitch : ToggleButton
{
    private const double Travel = 20;
    private static readonly Brush KnobFill = Frozen(new LinearGradientBrush(Colors.White, Color.FromRgb(0xEE, 0xF0, 0xF3), 90));

    private readonly Border _fill;
    private readonly Border _knob;
    private readonly TranslateTransform _move = new();

    public GelSwitch()
    {
        Width = 48;
        Height = 28;
        Cursor = Cursors.Hand;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        Template = (ControlTemplate)Application.Current.Resources["Lap.SwitchTemplate"];
        FocusVisualStyle = (Style)Application.Current.Resources["Lap.Focus"];

        var track = new Border { CornerRadius = new CornerRadius(14) };
        track.SetResourceReference(Border.BackgroundProperty, "Brush.LapTrack");
        _fill = new Border { CornerRadius = new CornerRadius(14), Opacity = 0 };
        _fill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        var inset = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0, 0, 0)) };
        _knob = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            Background = KnobFill,
            RenderTransform = _move,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 2, Direction = 270, Opacity = 0.28, Color = Colors.Black },
        };
        Content = new Grid { Children = { track, _fill, inset, _knob } };

        Checked += (_, _) => Update(animate: true);
        Unchecked += (_, _) => Update(animate: true);
        Loaded += (_, _) => Update(animate: false);
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.45;
    }

    private void Update(bool animate)
    {
        animate &= IsLoaded; // set straight away before the switch is first shown
        bool on = IsChecked == true;
        Motion.To(_fill, OpacityProperty, on ? 1 : 0, animate ? 260 : 0);
        Motion.To(_move, TranslateTransform.XProperty, on ? Travel : 0, animate ? 450 : 0, Motion.Spring);
    }

    protected override void OnIsPressedChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsPressedChanged(e);
        // Held: the knob stretches toward the middle, like a drop of gel.
        Motion.To(_knob, WidthProperty, IsPressed ? 28 : 22, 200);
        if (IsChecked == true) Motion.To(_move, TranslateTransform.XProperty, IsPressed ? Travel - 6 : Travel, 200);
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}

/// <summary>Cards: a solid surface with a 1 px line and a soft shadow underneath (the shadow is its own layer, so text stays crisp).</summary>
internal static class LapCard
{
    public static Grid Create(UIElement child, Thickness padding, bool lift = false)
    {
        var t = ThemeResources.Lap;
        var effect = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Color = t.Shadow, Opacity = t.ShadowOpacity };
        var shadow = new Border { CornerRadius = new CornerRadius(16), Margin = new Thickness(2, 3, 2, 0), Effect = effect };
        shadow.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        var card = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = padding, Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        var grid = new Grid { Children = { shadow, card } };
        if (lift)
        {
            Fx.SetLift(grid, 2);
            grid.MouseEnter += (_, _) =>
            {
                card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
                Motion.To(effect, DropShadowEffect.OpacityProperty, Math.Min(0.6, t.ShadowOpacity * 1.9), 320);
                Motion.To(effect, DropShadowEffect.ShadowDepthProperty, 12, 320);
            };
            grid.MouseLeave += (_, _) =>
            {
                card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
                Motion.To(effect, DropShadowEffect.OpacityProperty, t.ShadowOpacity, 320);
                Motion.To(effect, DropShadowEffect.ShadowDepthProperty, 6, 320);
            };
        }
        return grid;
    }
}

/// <summary>
/// The File Toolbox card's preview: the wheel's six tiles with the highlight stepping round,
/// and a size pill in the middle, as when a file is dragged and Shift tapped.
/// </summary>
internal sealed class WheelPreview : Grid, IIdleMotion
{
    private readonly RotateTransform _turn = new(0, 60, 60);

    public WheelPreview(double size = 112)
    {
        Width = size;
        Height = size;
        var canvas = new Canvas { Width = 120, Height = 120 };
        for (int i = 0; i < 6; i++)
        {
            var tile = new Path { Data = Sector(i * 60 - 90 + 2.5, i * 60 - 30 - 2.5) };
            tile.SetResourceReference(Shape.FillProperty, "Brush.Line2");
            canvas.Children.Add(tile);
        }
        var highlight = new Path { Data = Sector(-90 + 2.5, -30 - 2.5), RenderTransform = _turn };
        highlight.SetResourceReference(Shape.FillProperty, "Brush.Accent");
        canvas.Children.Add(highlight);
        var hub = new Ellipse { Width = 36, Height = 36 };
        hub.SetResourceReference(Shape.FillProperty, "Brush.LapCard");
        Canvas.SetLeft(hub, 42);
        Canvas.SetTop(hub, 42);
        canvas.Children.Add(hub);
        var text = new TextBlock { Text = "1.1 MB", FontSize = 9.5, FontWeight = FontWeights.Bold, Width = 36, TextAlignment = TextAlignment.Center, FlowDirection = FlowDirection.LeftToRight };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        Canvas.SetLeft(text, 42);
        Canvas.SetTop(text, 53);
        canvas.Children.Add(text);
        Children.Add(new Viewbox { Child = canvas });
    }

    /// <summary>A ring tile between two angles (degrees, 0 = 3 o'clock, clockwise), inner radius 21, outer 47.</summary>
    private static Geometry Sector(double from, double to)
    {
        static Point At(double deg, double r) => new(60 + r * Math.Cos(deg * Math.PI / 180), 60 + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(At(from, 47), true, true);
            c.ArcTo(At(to, 47), new Size(47, 47), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(At(to, 21), true, false);
            c.ArcTo(At(from, 21), new Size(21, 21), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    public void SetRunning(bool running)
    {
        if (!running || Motion.IsReduced)
        {
            _turn.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }
        // Rests on a tile, then springs to the next one; after six steps it is back where it began.
        var steps = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(6), RepeatBehavior = RepeatBehavior.Forever };
        steps.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        for (int k = 1; k <= 6; k++)
        {
            steps.KeyFrames.Add(new DiscreteDoubleKeyFrame((k - 1) * 60, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(k) - TimeSpan.FromMilliseconds(320))));
            steps.KeyFrames.Add(new EasingDoubleKeyFrame(k * 60, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(k)), Motion.Spring));
        }
        Timeline.SetDesiredFrameRate(steps, 40);
        _turn.BeginAnimation(RotateTransform.AngleProperty, steps);
    }
}

/// <summary>
/// The Longshot card's preview: a page scrolling inside an accent box while the long image
/// beside it grows downward.
/// </summary>
internal sealed class LongshotPreview : StackPanel, IIdleMotion
{
    private readonly TranslateTransform _scroll = new();
    private readonly Border _grown;

    public LongshotPreview()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;

        var page = new Canvas { RenderTransform = _scroll };
        for (int i = 0; i < 12; i++)
        {
            page.Children.Add(Bar(10, i * 26, 112, 6, "Brush.Line2"));
            page.Children.Add(Bar(10, i * 26 + 14, 76 - (i % 3) * 14, 4, "Brush.Line"));
        }
        var viewport = new Border { Width = 132, Height = 96, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(2), ClipToBounds = true, Child = page };
        viewport.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        viewport.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        Children.Add(viewport);

        var strip = new Canvas();
        for (int i = 0; i < 15; i++) strip.Children.Add(Bar(6, i * 7 + 4, 30, 3, "Brush.Line2"));
        _grown = new Border { Height = 20, VerticalAlignment = VerticalAlignment.Top, ClipToBounds = true, Child = strip };
        var column = new Border { Width = 44, Height = 104, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(22, 0, 0, 0), Child = _grown };
        column.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        column.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        Children.Add(column);
    }

    private static Rectangle Bar(double x, double y, double w, double h, string brush)
    {
        var r = new Rectangle { Width = w, Height = h, RadiusX = h / 2, RadiusY = h / 2 };
        r.SetResourceReference(Shape.FillProperty, brush);
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    public void SetRunning(bool running)
    {
        if (!running || Motion.IsReduced)
        {
            _scroll.BeginAnimation(TranslateTransform.YProperty, null);
            _grown.BeginAnimation(HeightProperty, null);
            if (Motion.IsReduced) _grown.Height = 70;
            return;
        }
        var scroll = new DoubleAnimation(0, -104, TimeSpan.FromSeconds(4)) { RepeatBehavior = RepeatBehavior.Forever };
        var grow = new DoubleAnimation(20, 102, TimeSpan.FromSeconds(4)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Motion.Out };
        Timeline.SetDesiredFrameRate(scroll, 40);
        Timeline.SetDesiredFrameRate(grow, 40);
        _scroll.BeginAnimation(TranslateTransform.YProperty, scroll);
        _grown.BeginAnimation(HeightProperty, grow);
    }
}

/// <summary>
/// A light band that sweeps across a rounded area every few seconds (the sidebar's glare, the
/// logo's glint). Starts and ends fully outside the area, so it never jumps or leaves a sliver.
/// </summary>
internal sealed class GlareSweep : Canvas, IIdleMotion
{
    private readonly Rectangle _band;
    private readonly TranslateTransform _move = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private readonly double _radius;
    private readonly int _sweepMs;

    public GlareSweep(Color glare, Color soft, double radius, double bandWidth = 110, double periodSeconds = 9, int sweepMs = 3240)
    {
        _radius = radius;
        _sweepMs = sweepMs;
        IsHitTestVisible = false;
        var fill = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        fill.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        fill.GradientStops.Add(new GradientStop(soft, 0.28));
        fill.GradientStops.Add(new GradientStop(glare, 0.5));
        fill.GradientStops.Add(new GradientStop(soft, 0.72));
        fill.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        fill.Freeze();
        _band = new Rectangle { Width = bandWidth, Fill = fill, RenderTransformOrigin = new Point(0.5, 0.5) };
        _band.RenderTransform = new TransformGroup { Children = { new RotateTransform(18), _move } };
        Children.Add(_band);
        SizeChanged += (_, e) =>
        {
            Clip = new RectangleGeometry(new Rect(e.NewSize), _radius, _radius);
            _band.Height = e.NewSize.Height * 2;
            SetTop(_band, -e.NewSize.Height / 2);
            _move.X = Start;
        };
        _move.X = -10000;
        _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(periodSeconds) };
        _timer.Tick += (_, _) => Sweep();
    }

    // Far enough out that the tilted band is clear of the area at both ends, whatever its height.
    private double Start => -_band.Width - ActualHeight * 0.36 - 40;
    private double End => ActualWidth + ActualHeight * 0.36 + 40;

    private void Sweep()
    {
        if (ActualWidth <= 0) return;
        var a = new DoubleAnimation(Start, End, TimeSpan.FromMilliseconds(_sweepMs)) { EasingFunction = Motion.InOut };
        _move.BeginAnimation(TranslateTransform.XProperty, a);
    }

    public void SetRunning(bool running)
    {
        if (running && !Motion.IsReduced)
        {
            if (_timer.IsEnabled) return;
            _timer.Start();
            var first = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
            first.Tick += (_, _) => { first.Stop(); if (_timer.IsEnabled) Sweep(); };
            first.Start();
        }
        else
        {
            _timer.Stop();
            _move.BeginAnimation(TranslateTransform.XProperty, null);
            _move.X = ActualWidth > 0 ? Start : -10000;
        }
    }
}

/// <summary>Soft coloured light drifting slowly behind the glass (no blur needed: radial gradients).</summary>
internal sealed class AmbientLight : Canvas, IIdleMotion
{
    private readonly (Ellipse Orb, TranslateTransform Move, double Dx, double Dy, double Seconds)[] _orbs;

    public AmbientLight(LapTokens t)
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        _orbs =
        [
            (Orb(t.Orb1, 440), new TranslateTransform(), 60, -30, 30),
            (Orb(t.Orb2, 400), new TranslateTransform(), -40, 40, 36),
        ];
        SetLeft(_orbs[0].Orb, -150);
        SetTop(_orbs[0].Orb, -170);
        SetLeft(_orbs[1].Orb, -40);
        SetBottom(_orbs[1].Orb, -190);
        foreach (var o in _orbs)
        {
            o.Orb.RenderTransform = o.Move;
            Children.Add(o.Orb);
        }
    }

    private static Ellipse Orb(Color c, double size)
    {
        var fill = new RadialGradientBrush();
        fill.GradientStops.Add(new GradientStop(c, 0));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(c.A / 2), c.R, c.G, c.B), 0.45));
        fill.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        fill.Freeze();
        return new Ellipse { Width = size, Height = size, Fill = fill };
    }

    public void SetRunning(bool running)
    {
        foreach (var (_, move, dx, dy, seconds) in _orbs)
        {
            if (!running || Motion.IsReduced)
            {
                move.BeginAnimation(TranslateTransform.XProperty, null);
                move.BeginAnimation(TranslateTransform.YProperty, null);
                continue;
            }
            var x = new DoubleAnimation(0, dx, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            var y = new DoubleAnimation(0, dy, TimeSpan.FromSeconds(seconds * 0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            Timeline.SetDesiredFrameRate(x, 20);
            Timeline.SetDesiredFrameRate(y, 20);
            move.BeginAnimation(TranslateTransform.XProperty, x);
            move.BeginAnimation(TranslateTransform.YProperty, y);
        }
    }
}
