using System.Windows;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Pulse;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Where the colour picker's magnifier and Pro window go: beside the picked point, on the side that
/// has room. Picked near the bottom: above; near the top: below; near the right: to the left; near
/// the left: to the right; and both at once in the corners.
/// </summary>
public class ColourPlacementTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040); // a 1080p screen above its taskbar
    private const int W = 400, H = 700, Gap = 15;

    [Theory]
    // x, y of the pick → expected side: +1 right/below, -1 left/above
    [InlineData(960, 200, 1, 1)]     // middle, near the top: right and below
    [InlineData(960, 1000, 1, -1)]   // near the bottom: above
    [InlineData(1880, 200, -1, 1)]   // near the right: to the left
    [InlineData(40, 900, 1, -1)]     // bottom left: right and above
    [InlineData(1900, 1020, -1, -1)] // bottom right corner: left and above
    [InlineData(10, 10, 1, 1)]       // top left corner: right and below
    public void The_window_goes_where_there_is_room(int x, int y, int sideX, int sideY)
    {
        var (left, top) = ColourWindow.Spot(new PixelPoint(x, y), W, H, Work, Gap);
        Assert.Equal(sideX, left >= x ? 1 : -1);
        Assert.Equal(sideY, top >= y ? 1 : -1);
        // On the screen, and never covering the picked pixel.
        Assert.InRange(left, Work.X, Work.Right - W);
        Assert.InRange(top, Work.Y, Work.Bottom - H);
        Assert.False(x >= left && x < left + W && y >= top && y < top + H, "the window must not cover the picked point");
    }

    [Fact]
    public void The_shadow_margin_may_run_past_the_screen_but_the_glass_keeps_its_gap()
    {
        const int margin = 18;
        var (left, top) = ColourWindow.Spot(new PixelPoint(100, 100), W, H, Work, Gap, margin);
        Assert.Equal(100 + Gap - margin, left); // the glass starts exactly Gap px from the point
        Assert.Equal(100 + Gap - margin, top);
        (left, top) = ColourWindow.Spot(new PixelPoint(1900, 1030), W, H, Work, Gap, margin);
        Assert.Equal(1900 - Gap - (W - 2 * margin) - margin, left);
        Assert.Equal(1030 - Gap - (H - 2 * margin) - margin, top);
    }

    [Fact]
    public void The_magnifier_flips_the_same_way()
    {
        var area = new Rect(0, 0, 1536, 832);
        var size = new Size(140, 180);
        Assert.Equal(new Point(122, 122), ColourPicker.Beside(new Point(100, 100), size, area, 22));
        Assert.Equal(new Point(1500 - 22 - 140, 800 - 22 - 180), ColourPicker.Beside(new Point(1500, 800), size, area, 22));
        // Too little room on either side (a tiny screen): kept inside it.
        var tiny = new Rect(0, 0, 200, 200);
        var p = ColourPicker.Beside(new Point(100, 100), size, tiny, 22);
        Assert.InRange(p.X, 0, 60);
        Assert.InRange(p.Y, 0, 20);
    }
}
