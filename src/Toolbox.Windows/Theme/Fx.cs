using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Toolbox.Windows.Theme;

/// <summary>
/// The window's reactions, as attached properties that styles switch on:
/// <list type="bullet">
/// <item><c>Lift</c>: rises a pixel or two on hover.</item>
/// <item><c>Press</c>: shrinks to 96% while pressed and springs back; a soft light spreads from the
/// pointer when the template has a <c>PART_Glow</c> border.</item>
/// <item><c>Nudge</c>: moves a child (the icon of a sidebar item) a little toward the text on hover.</item>
/// </list>
/// Everything jumps straight to its end state when Reduce motion is on.
/// </summary>
internal static class Fx
{
    public static readonly DependencyProperty LiftProperty =
        DependencyProperty.RegisterAttached("Lift", typeof(double), typeof(Fx), new PropertyMetadata(0.0, OnLiftChanged));

    public static readonly DependencyProperty PressProperty =
        DependencyProperty.RegisterAttached("Press", typeof(bool), typeof(Fx), new PropertyMetadata(false, OnPressChanged));

    public static readonly DependencyProperty NudgeTargetProperty =
        DependencyProperty.RegisterAttached("NudgeTarget", typeof(UIElement), typeof(Fx), new PropertyMetadata(null, OnNudgeChanged));

    public static double GetLift(DependencyObject d) => (double)d.GetValue(LiftProperty);
    public static void SetLift(DependencyObject d, double value) => d.SetValue(LiftProperty, value);
    public static bool GetPress(DependencyObject d) => (bool)d.GetValue(PressProperty);
    public static void SetPress(DependencyObject d, bool value) => d.SetValue(PressProperty, value);
    public static UIElement? GetNudgeTarget(DependencyObject d) => (UIElement?)d.GetValue(NudgeTargetProperty);
    public static void SetNudgeTarget(DependencyObject d, UIElement? value) => d.SetValue(NudgeTargetProperty, value);

    /// <summary>The element's scale and offset (created on first use).</summary>
    public static (ScaleTransform Scale, TranslateTransform Move) Transforms(UIElement element)
    {
        if (element.RenderTransform is TransformGroup { Children.Count: 2 } g && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
            return (s, t);
        s = new ScaleTransform();
        t = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = { s, t } };
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        return (s, t);
    }

    private static void OnLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || e.OldValue is double old && old != 0) return; // hook once
        element.MouseEnter += (_, _) =>
        {
            if (element is UIElement { IsEnabled: false }) return;
            Motion.To(Transforms(element).Move, TranslateTransform.YProperty, -GetLift(element), 180);
        };
        element.MouseLeave += (_, _) => Motion.To(Transforms(element).Move, TranslateTransform.YProperty, 0, 240);
    }

    private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element || e.NewValue is not true || e.OldValue is true) return;
        element.PreviewMouseLeftButtonDown += (_, args) =>
        {
            Motion.To(Transforms(element).Scale, ScaleTransform.ScaleXProperty, 0.96, 120);
            Motion.To(Transforms(element).Scale, ScaleTransform.ScaleYProperty, 0.96, 120);
            Glow(element, args.GetPosition(element));
        };
        element.PreviewMouseLeftButtonUp += (_, _) => Release(element);
        element.MouseLeave += (_, _) => Release(element);
        element.LostMouseCapture += (_, _) => Release(element);
    }

    private static void Release(UIElement element)
    {
        var (scale, _) = Transforms(element);
        if (scale.ScaleX == 1 && !scale.HasAnimatedProperties) return;
        Motion.To(scale, ScaleTransform.ScaleXProperty, 1, 380, Motion.Spring);
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, 380, Motion.Spring);
    }

    /// <summary>A soft light from where the pointer pressed, fading out.</summary>
    private static void Glow(UIElement element, Point at)
    {
        if (element is not Control control || control.Template?.FindName("PART_Glow", control) is not Border glow) return;
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = at,
            GradientOrigin = at,
            RadiusX = 90,
            RadiusY = 90,
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        glow.Background = brush;
        glow.BeginAnimation(UIElement.OpacityProperty, null);
        glow.Opacity = 1;
        Motion.To(glow, UIElement.OpacityProperty, 0, 650);
    }

    private static void OnNudgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement host || e.OldValue is not null || e.NewValue is not UIElement target) return;
        // Positive X is "toward the text" in both directions: a right-to-left layout mirrors it.
        host.MouseEnter += (_, _) => Motion.To(Transforms(target).Move, TranslateTransform.XProperty, 2, 350, Motion.Spring);
        host.MouseLeave += (_, _) => Motion.To(Transforms(target).Move, TranslateTransform.XProperty, 0, 300);
    }
}
