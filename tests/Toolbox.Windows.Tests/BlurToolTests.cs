using System.Windows;
using Toolbox.Windows.Studio;

namespace Toolbox.Windows.Tests;

/// <summary>The Blur step's drag across text: it snaps to the words it covers, one area per line.</summary>
public class BlurToolTests
{
    // Two lines of three words, 20 px tall.
    private static readonly (Rect Box, int Line)[] Words =
    [
        (new Rect(10, 10, 60, 20), 0), (new Rect(80, 10, 50, 20), 0), (new Rect(140, 10, 70, 20), 0),
        (new Rect(10, 50, 40, 20), 1), (new Rect(60, 50, 90, 20), 1), (new Rect(160, 50, 30, 20), 1),
    ];

    private static Rect Pad(Rect r)
    {
        double pad = Math.Max(2, r.Height * 0.18);
        r.Inflate(pad, pad);
        return r;
    }

    [Fact]
    public void A_drag_over_one_line_hides_the_words_it_covers()
    {
        var areas = DrawOverlay.SnapToWords(new Rect(15, 5, 120, 30), Words);
        var area = Assert.Single(areas);
        Assert.Equal(Pad(new Rect(10, 10, 120, 20)), area); // the first two words, end to end
    }

    [Fact]
    public void A_drag_over_two_lines_gives_one_area_per_line()
    {
        var areas = DrawOverlay.SnapToWords(new Rect(0, 0, 220, 80), Words);
        Assert.Equal(2, areas.Count);
        Assert.Contains(Pad(new Rect(10, 10, 200, 20)), areas);
        Assert.Contains(Pad(new Rect(10, 50, 180, 20)), areas);
    }

    [Fact]
    public void Brushing_the_edge_of_a_word_does_not_take_it()
    {
        // Covers all of word 2 and only a sliver of word 3.
        var area = Assert.Single(DrawOverlay.SnapToWords(new Rect(75, 5, 70, 30), Words));
        Assert.Equal(Pad(new Rect(80, 10, 50, 20)), area);
    }

    [Fact]
    public void A_drag_away_from_text_is_the_area_itself()
    {
        var drag = new Rect(300, 300, 40, 40);
        Assert.Equal(drag, Assert.Single(DrawOverlay.SnapToWords(drag, Words)));
        Assert.Equal(drag, Assert.Single(DrawOverlay.SnapToWords(drag, [])));
    }
}
