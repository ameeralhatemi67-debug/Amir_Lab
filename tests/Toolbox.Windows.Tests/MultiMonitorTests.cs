using Toolbox.Windows.Interop;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Placement maths against simulated monitor layouts (physical pixels, as Windows reports them to a
/// PerMonitorV2 app). This machine has one display, so real multi-monitor behaviour is still untested.
/// </summary>
public class MultiMonitorTests
{
    private static Native.RECT R(int l, int t, int r, int b) => new() { Left = l, Top = t, Right = r, Bottom = b };

    private static Native.POINT P(int x, int y) => new() { X = x, Y = y };

    // Laptop 1920×1080 at 125% (taskbar at the bottom) with a 2560×1440 monitor at 100% to its right,
    // mounted 200 px higher, and a 1920×1080 monitor at 150% on the left (negative coordinates,
    // taskbar on its left edge).
    private static readonly MonitorArea Laptop = new(R(0, 0, 1920, 1080), R(0, 0, 1920, 1032), 1.25);
    private static readonly MonitorArea Right = new(R(1920, -200, 4480, 1240), R(1920, -200, 4480, 1240), 1.0);
    private static readonly MonitorArea Left = new(R(-1920, 0, 0, 1080), R(-1858, 0, 0, 1080), 1.5);
    private static readonly MonitorArea[] Layout = [Laptop, Right, Left];

    [Fact]
    public void Wheel_near_the_shared_edge_stays_on_the_pointer_monitor()
    {
        var pointer = P(1925, 600); // just across the edge, on the right monitor
        var monitor = ScreenGeometry.Nearest(pointer, Layout);
        Assert.Equal(Right, monitor);
        var (left, _, size) = ScreenGeometry.PlaceWheel(pointer, monitor, 380);
        Assert.Equal(380, size); // 100% scaling there
        Assert.Equal(1920, left); // pushed right instead of spanning onto the laptop
    }

    [Fact]
    public void Wheel_uses_the_scale_of_the_monitor_it_opens_on()
    {
        var (_, _, onLaptop) = ScreenGeometry.PlaceWheel(P(900, 500), Laptop, 380);
        var (_, _, onLeft) = ScreenGeometry.PlaceWheel(P(-900, 500), Left, 380);
        Assert.Equal(475, onLaptop);
        Assert.Equal(570, onLeft);
    }

    [Fact]
    public void Negative_coordinates_and_a_left_taskbar_are_respected()
    {
        var pointer = P(-1900, 20); // top-left corner of the left monitor, over its taskbar
        var (left, top, size) = ScreenGeometry.PlaceWheel(pointer, ScreenGeometry.Nearest(pointer, Layout), 380);
        Assert.Equal(-1858, left); // right of the taskbar
        Assert.Equal(0, top);
        Assert.True(left + size <= 0);
    }

    [Fact]
    public void Monitor_mounted_higher_allows_negative_top()
    {
        var pointer = P(3000, -190);
        var (_, top, _) = ScreenGeometry.PlaceWheel(pointer, ScreenGeometry.Nearest(pointer, Layout), 380);
        Assert.Equal(-200, top);
    }

    [Fact]
    public void Point_in_a_gap_of_the_desktop_uses_the_nearest_monitor()
    {
        // (2000, 1300) is 61 px below the right monitor and over 220 px from the laptop: the right one wins.
        Assert.Equal(Right, ScreenGeometry.Nearest(P(2000, 1300), Layout));
        Assert.Equal(Laptop, ScreenGeometry.Nearest(P(500, 1500), Layout));
    }

    [Fact]
    public void Wheel_stays_above_the_taskbar()
    {
        var (_, top, size) = ScreenGeometry.PlaceWheel(P(960, 1070), Laptop, 380);
        Assert.Equal(1032 - size, top);
    }

    [Fact]
    public void Editors_open_below_the_anchor_and_inside_the_work_area()
    {
        // A 700×500 px editor anchored near the bottom-right corner of the laptop.
        var (left, top) = ScreenGeometry.PlaceNear(P(1900, 1000), 700, 500, 24, Laptop);
        Assert.Equal(1920 - 700 - 10, left); // 8 DIP margin at 125% = 10 px
        Assert.Equal(1032 - 500 - 10, top);
        // Anchored on the left monitor, it stays there (not pulled onto the laptop).
        var (l2, _) = ScreenGeometry.PlaceNear(P(-20, 300), 700, 500, 24, Left);
        Assert.True(l2 + 700 <= 0);
    }
}
