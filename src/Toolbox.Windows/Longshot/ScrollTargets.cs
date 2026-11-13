using System.Runtime.InteropServices;
using System.Windows.Automation;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Windows.Longshot;

/// <summary>
/// Scrolls one area by itself, by where it is scrolled to (0–100%), without the mouse pointer and
/// without the wheel: so nothing else on the screen is touched, a page around it never scrolls by
/// accident (the wheel hands the scroll on to the page once a section reaches its end), and the end
/// is known exactly. Also reads where the scroll areas around it are, so Longshot can tell if the
/// page around moved anyway and put it back.
/// </summary>
internal interface IAreaScroller
{
    /// <summary>Where the area is scrolled to, 0 (top) to 100 (bottom); null when it can't be read.</summary>
    double? Percent { get; }

    /// <summary>How much of its content the area shows at once, as a percentage; null when unknown.</summary>
    double? ViewSize { get; }

    /// <summary>The area's height on the screen, in pixels.</summary>
    int ViewHeight { get; }

    /// <summary>Scrolls to <paramref name="percent"/>; false when the app refuses.</summary>
    bool SetPercent(double percent);

    /// <summary>Where each scroll area around this one is (innermost first).</summary>
    IReadOnlyList<double?> AncestorPercents();

    /// <summary>Puts the scroll areas around back where they were.</summary>
    void RestoreAncestors(IReadOnlyList<double?> percents);
}

/// <summary>
/// What scrolls at a point: the part of it that shows (its own box, cut by the page and window it
/// sits in), whether it is the page itself (nothing around it scrolls), how to scroll it on its own,
/// and what it is (for telling two finds apart, and for the log).
/// </summary>
internal sealed record FoundTarget(PixelRect Area, bool IsPage, IAreaScroller? Scroller, string Identity, string Name)
{
    public bool Same(FoundTarget? other) => other is not null && other.Identity == Identity;
}

/// <summary>Finds the innermost area that scrolls up and down at a point, through UI Automation.</summary>
internal static class ScrollTargets
{
    /// <summary>
    /// The innermost area at <paramref name="p"/> that scrolls up and down and is big enough to
    /// capture (a small drop-down list is skipped for what holds it), or null. Blocks while UI
    /// Automation answers: call it off the UI thread.
    /// </summary>
    /// <param name="outermost">The page instead: the outermost area there that scrolls.</param>
    public static FoundTarget? At(PixelPoint p, bool outermost = false)
    {
        if (TargetFinder.WindowAt(p) is not { } window) return null;
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(p.X, p.Y));
            // The raw view: browsers expose a scrolling <div> as a plain container, which the
            // control view leaves out.
            var walker = TreeWalker.RawViewWalker;
            var scrollers = new List<(AutomationElement Element, ScrollPattern Pattern, PixelRect Box)>();
            for (int depth = 0; element is not null && depth < 60; depth++)
            {
                var current = element.Current;
                if (current.ProcessId == Environment.ProcessId) return null;
                if (element.TryGetCurrentPattern(ScrollPattern.Pattern, out var found) && found is ScrollPattern scroll && scroll.Current.VerticallyScrollable)
                {
                    var r = current.BoundingRectangle;
                    if (!r.IsEmpty && double.IsFinite(r.Width) && double.IsFinite(r.Height))
                        scrollers.Add((element, scroll, new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height))));
                }
                if (current.NativeWindowHandle == (int)window.Hwnd) break; // reached the window itself
                element = walker.GetParent(element);
            }
            // Innermost first. What shows of each is its box, cut by every scroll area around it.
            var order = Enumerable.Range(0, scrollers.Count);
            foreach (int i in outermost ? order.Reverse() : order)
            {
                var visible = scrollers[i].Box.Intersect(window.Rect);
                for (int j = i + 1; j < scrollers.Count; j++) visible = visible.Intersect(scrollers[j].Box);
                if (visible.Width < TargetFinder.MinWidth || visible.Height < TargetFinder.MinHeight || !visible.Contains(p)) continue;
                var (target, pattern, box) = scrollers[i];
                var outer = scrollers.Skip(i + 1).Select(s => s.Pattern).ToList();
                var info = target.Current;
                string name = $"{info.ControlType.ProgrammaticName.Replace("ControlType.", "")} \"{Short(info.Name)}\"";
                string id = string.Join(".", target.GetRuntimeId());
                return new FoundTarget(visible, outer.Count == 0, new UiaAreaScroller(pattern, outer, box.Height), id, name);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException or TimeoutException)
        {
            Log.Info($"Longshot: no scroll area from UI Automation ({ex.GetType().Name}).");
        }
        return null;
    }

    private static string Short(string? s) => s is null ? "" : s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>
    /// <see cref="At"/> on a worker thread, giving up after <paramref name="timeout"/>. Browsers turn
    /// their accessibility tree on when first asked, so a first empty answer from one is asked again.
    /// </summary>
    public static async Task<FoundTarget?> AtAsync(PixelPoint p, TimeSpan timeout, bool outermost = false)
    {
        var deadline = DateTime.UtcNow + timeout;
        for (int tries = 0; tries < 2; tries++)
        {
            var left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) return null;
            var lookup = Task.Run(() => At(p, outermost));
            if (await Task.WhenAny(lookup, Task.Delay(left)).ConfigureAwait(true) != lookup) return null;
            if (lookup.Result is { } found) return found;
            if (tries == 0 && !TargetFinder.IsBrowser(p)) return null;
            await Task.Delay(180).ConfigureAwait(true);
        }
        return null;
    }
}

/// <summary>
/// <see cref="IAreaScroller"/> through UI Automation's scroll pattern: what screen readers use, and
/// what browsers, Office, Explorer and most Windows apps answer to. Each call runs on a worker thread
/// and gives up after two seconds, so a busy app can't hang a capture.
/// </summary>
internal sealed class UiaAreaScroller(ScrollPattern pattern, IReadOnlyList<ScrollPattern> around, int viewHeight) : IAreaScroller
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(2);

    public int ViewHeight => viewHeight;

    public double? Percent => Call(() => Read(pattern));

    public double? ViewSize => Call(() =>
    {
        double v = pattern.Current.VerticalViewSize;
        return v > 0 && v < 100 ? v : (double?)null;
    });

    public bool SetPercent(double percent) => Call(() =>
    {
        pattern.SetScrollPercent(ScrollPattern.NoScroll, Math.Clamp(percent, 0, 100));
        return (bool?)true;
    }) == true;

    public IReadOnlyList<double?> AncestorPercents() => around.Select(a => Call(() => Read(a))).ToList();

    public void RestoreAncestors(IReadOnlyList<double?> percents)
    {
        for (int i = 0; i < Math.Min(percents.Count, around.Count); i++)
            if (percents[i] is { } p)
            {
                var a = around[i];
                Call(() => { a.SetScrollPercent(ScrollPattern.NoScroll, p); return (bool?)true; });
            }
    }

    private static double? Read(ScrollPattern p)
    {
        double v = p.Current.VerticalScrollPercent;
        return v >= 0 && v <= 100 ? v : null; // NoScroll is -1
    }

    private static T? Call<T>(Func<T?> read) where T : struct
    {
        try
        {
            var task = Task.Run(read);
            return task.Wait(Limit) ? task.Result : null;
        }
        catch (AggregateException ex) when (ex.InnerException is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
        {
            Log.Info($"Longshot: the scroll area stopped answering ({ex.InnerException.GetType().Name}).");
            return null;
        }
    }
}
