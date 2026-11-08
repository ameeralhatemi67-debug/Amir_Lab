using System.Windows;
using System.Windows.Media.Animation;

namespace Toolbox.Windows.Theme;

/// <summary>
/// A CSS-style cubic-bezier curve, so the app moves with the same curves as the design
/// (quick to answer, soft to settle; springs overshoot a little). Usable from XAML.
/// </summary>
internal sealed class BezierEase : IEasingFunction
{
    public BezierEase() { }

    public BezierEase(double x1, double y1, double x2, double y2) => (X1, Y1, X2, Y2) = (x1, y1, x2, y2);

    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; } = 1;
    public double Y2 { get; set; } = 1;

    public double Ease(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        // Solve x(u) = t for the curve parameter u (Newton, then bisection if it stalls).
        double u = t;
        for (int i = 0; i < 8; i++)
        {
            double x = Bez(u, X1, X2) - t;
            if (Math.Abs(x) < 1e-5) return Bez(u, Y1, Y2);
            double d = Slope(u, X1, X2);
            if (Math.Abs(d) < 1e-6) break;
            u -= x / d;
        }
        double lo = 0, hi = 1;
        u = t;
        for (int i = 0; i < 24; i++)
        {
            double x = Bez(u, X1, X2);
            if (Math.Abs(x - t) < 1e-5) break;
            if (x < t) lo = u; else hi = u;
            u = (lo + hi) / 2;
        }
        return Bez(u, Y1, Y2);
    }

    private static double Bez(double u, double p1, double p2) => ((1 - 3 * p2 + 3 * p1) * u + (3 * p2 - 6 * p1)) * u * u + 3 * p1 * u;

    private static double Slope(double u, double p1, double p2) => 3 * (1 - 3 * p2 + 3 * p1) * u * u + 2 * (3 * p2 - 6 * p1) * u + 3 * p1;
}

internal static partial class Motion
{
    /// <summary>Hover, press, page changes: answers at once, settles softly.</summary>
    public static readonly BezierEase Out = new(.2, .8, .2, 1);

    /// <summary>Switch knob, chips: overshoots a little, like a spring.</summary>
    public static readonly BezierEase Spring = new(.34, 1.56, .64, 1);

    /// <summary>The sidebar pill: a softer spring.</summary>
    public static readonly BezierEase Settle = new(.34, 1.32, .5, 1);

    /// <summary>The idle light sweep.</summary>
    public static readonly BezierEase InOut = new(.45, 0, .25, 1);

    /// <summary>Set from the Reduce motion setting; when true every animation jumps to its end.</summary>
    public static bool IsReduced { get; set; }

    /// <summary>Animates a double property (or sets it straight away when motion is reduced).</summary>
    public static void To(IAnimatable target, DependencyProperty property, double to, int milliseconds, IEasingFunction? ease = null)
    {
        if (IsReduced || milliseconds <= 0)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease ?? Out };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Animates from a start value (page and chip entrances).</summary>
    public static void FromTo(IAnimatable target, DependencyProperty property, double from, double to, int milliseconds, IEasingFunction? ease = null)
    {
        if (IsReduced || milliseconds <= 0)
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
            return;
        }
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = ease ?? Out };
        target.BeginAnimation(property, animation);
    }
}
