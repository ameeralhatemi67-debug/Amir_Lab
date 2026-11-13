using Toolbox.Windows.Longshot;

namespace Toolbox.Windows.Tests;

/// <summary>
/// A section scrolled by itself (as through UI Automation), on the simulated window whose box
/// scrolls: no wheel at all, the exact result, the end known, the page around kept still (or put
/// back if it moves), and Undo and Refresh by percent.
/// </summary>
public partial class CaptureSessionTests
{
    /// <summary>The desk's box as UI Automation would see it: where it is scrolled to, in percent.</summary>
    private sealed class BoxByPercent(Desk desk) : IAreaScroller
    {
        public double PagePercent = 40;            // the page around (a scroll area holding the box)
        public Func<double, bool>? MovesPageAt;    // simulate the page scrolling too, at these percents
        public bool Moves = true;                  // false: it reports scrolling that doesn't happen
        private double _reported;
        public int Calls;

        public double? Percent => Moves ? desk.Offset * 100.0 / desk.MaxOffset : _reported;
        public double? ViewSize => Desk.Box.Height * 100.0 / desk.Content.Height;
        public int ViewHeight => Desk.Box.Height;

        public bool SetPercent(double percent)
        {
            Calls++;
            if (Moves) desk.Offset = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100 * desk.MaxOffset);
            else _reported = percent;
            if (MovesPageAt?.Invoke(percent) == true) PagePercent += 7;
            return true;
        }

        public IReadOnlyList<double?> AncestorPercents() => [PagePercent];

        public void RestoreAncestors(IReadOnlyList<double?> percents) => PagePercent = percents[0] ?? PagePercent;
    }

    [Fact]
    public void A_section_scrolled_by_itself_is_captured_exactly_without_the_wheel()
    {
        var desk = new Desk(1700, seed: 21);
        var box = new BoxByPercent(desk);
        using var run = new DeskRun(desk, anchor: new PixelPoint(Desk.Box.CenterX, Desk.Box.CenterY), area: Desk.Box, byItself: box);
        var last = run.Do(SessionCommand.Auto, mode: true);
        Assert.True(last.AtEnd, last.Message);
        Assert.Contains("Reached the end", last.Message);
        Assert.Empty(run.Wheel.At);                 // the pointer and the wheel were never used
        Assert.Equal(40, box.PagePercent);          // the page around never moved
        Assert.Empty(run.All<SessionAreaChanged>()); // the target stays the one chosen
        Assert.Equal(desk.Expected(desk.MaxOffset), run.Finish().Pixels);
    }

    [Fact]
    public void If_the_page_around_moves_it_is_put_back_and_the_capture_stops_there()
    {
        var desk = new Desk(1700, seed: 22);
        var box = new BoxByPercent(desk) { MovesPageAt = p => p > 50 };
        using var run = new DeskRun(desk, anchor: new PixelPoint(Desk.Box.CenterX, Desk.Box.CenterY), area: Desk.Box, byItself: box);
        var last = run.Do(SessionCommand.Auto, mode: true);
        Assert.Contains("page around this part started to move", last.Message);
        Assert.Equal(40, box.PagePercent);          // put back
        Assert.True(desk.Offset < desk.MaxOffset);  // stopped before the end
        var done = run.Finish();
        Assert.Equal(desk.Expected(desk.Offset), done.Pixels); // what was captured is still exact
    }

    [Fact]
    public void Undo_and_refresh_go_back_by_percent()
    {
        var desk = new Desk(1900, seed: 23);
        var box = new BoxByPercent(desk);
        using (var run = new DeskRun(desk, anchor: new PixelPoint(Desk.Box.CenterX, Desk.Box.CenterY), area: Desk.Box, byItself: box))
        {
            var first = run.Do(SessionCommand.Extend);
            int afterFirst = desk.Offset;
            run.Do(SessionCommand.Extend);
            var undone = run.Do(SessionCommand.Undo);
            Assert.Equal(first.Height, undone.Height);
            Assert.Equal(afterFirst, desk.Offset);
            Assert.Equal(0, run.Session.StartPercent);
        }
        // Refresh: a new session goes back to where the first one started.
        using var again = new DeskRun(desk, anchor: new PixelPoint(Desk.Box.CenterX, Desk.Box.CenterY), area: Desk.Box, byItself: box, rewindToPercent: 0);
        Assert.Equal(0, desk.Offset);
        again.Do(SessionCommand.Auto, mode: true);
        Assert.Equal(desk.Expected(desk.MaxOffset), again.Finish().Pixels);
    }

    [Fact]
    public void A_section_that_only_says_it_scrolled_falls_back_to_the_wheel()
    {
        var desk = new Desk(1500, seed: 24);
        var box = new BoxByPercent(desk) { Moves = false };
        using var run = new DeskRun(desk, anchor: new PixelPoint(Desk.Box.CenterX, Desk.Box.CenterY), area: Desk.Box, byItself: box);
        run.Do(SessionCommand.Auto, mode: true);
        Assert.False(run.Session.ScrollsByItself);
        Assert.NotEmpty(run.Wheel.At);
        Assert.Equal(desk.Expected(desk.MaxOffset), run.Finish().Pixels);
    }
}
