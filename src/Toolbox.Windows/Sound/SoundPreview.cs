using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Sound;

/// <summary>
/// Imp Studio's card on the Dashboard: three apps with their own volumes, their level meters
/// bouncing, and the middle one easing down and back (each app has its own level).
/// </summary>
internal sealed class SoundPreview : StackPanel, IIdleMotion
{
    private static readonly (Color Colour, char Letter, double Volume)[] Apps =
    [
        (Color.FromRgb(0xE5, 0x48, 0x4D), 'V', 0.35),
        (Color.FromRgb(0x58, 0x65, 0xF2), 'D', 1.0),
        (Color.FromRgb(0x1D, 0xB9, 0x54), 'S', 0.6),
    ];

    private readonly List<ScaleTransform> _meters = [];
    private readonly ScaleTransform _ease = new(1, 1);

    public SoundPreview()
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Center;
        Width = 220;
        FlowDirection = FlowDirection.LeftToRight;
        for (int i = 0; i < Apps.Length; i++)
        {
            var (colour, letter, volume) = Apps[i];
            var row = new Grid { Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(colour), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = letter.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            });
            var bars = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var track = new Grid { Height = 6 };
            var back = new Border { CornerRadius = new CornerRadius(3) };
            back.SetResourceReference(Border.BackgroundProperty, "Brush.LapTrack");
            var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 186 * volume, RenderTransformOrigin = new Point(0, 0.5) };
            fill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
            if (i == 0) fill.RenderTransform = _ease;
            track.Children.Add(back);
            track.Children.Add(fill);
            bars.Children.Add(track);
            var meter = new Rectangle { Height = 3, RadiusX = 1.5, RadiusY = 1.5, Width = 186 * volume, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 0), Fill = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)), RenderTransformOrigin = new Point(0, 0.5) };
            var scale = new ScaleTransform(0.6, 1);
            meter.RenderTransform = scale;
            _meters.Add(scale);
            bars.Children.Add(meter);
            Grid.SetColumn(bars, 1);
            row.Children.Add(bars);
            Children.Add(row);
        }
    }

    public void SetRunning(bool running)
    {
        for (int i = 0; i < _meters.Count; i++)
        {
            if (!running || Motion.IsReduced)
            {
                _meters[i].BeginAnimation(ScaleTransform.ScaleXProperty, null);
                continue;
            }
            var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(1.6 + i * 0.35), RepeatBehavior = RepeatBehavior.Forever };
            double[] levels = [0.35, 0.9, 0.5, 0.75, 0.3, 0.95, 0.45];
            for (int k = 0; k < levels.Length; k++)
                bounce.KeyFrames.Add(new LinearDoubleKeyFrame(levels[(k + i * 2) % levels.Length], KeyTime.FromPercent(k / (double)(levels.Length - 1))));
            Timeline.SetDesiredFrameRate(bounce, 30);
            _meters[i].BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        }
        if (!running || Motion.IsReduced)
        {
            _ease.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            return;
        }
        var ease = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(5), RepeatBehavior = RepeatBehavior.Forever };
        ease.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        ease.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.4)));
        ease.KeyFrames.Add(new EasingDoubleKeyFrame(1.9, KeyTime.FromPercent(0.55), Motion.Out));
        ease.KeyFrames.Add(new EasingDoubleKeyFrame(1.9, KeyTime.FromPercent(0.8)));
        ease.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.95), Motion.Out));
        Timeline.SetDesiredFrameRate(ease, 30);
        _ease.BeginAnimation(ScaleTransform.ScaleXProperty, ease);
    }
}
