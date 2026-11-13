using Toolbox.Windows.Longshot;
using Toolbox.Windows.Tools;
using Frame = Toolbox.Processing.Capture.Frame;

namespace Toolbox.Windows.Tests;

/// <summary>
/// The owner's page (13 Nov 2026 video): a page C that scrolls, with a section A in it that scrolls
/// on its own. Through the real controller, panel, frame and session, on a simulated window:
/// started with the pointer over A, just A is captured, scrolled by itself, and C never moves; the
/// marker dropped on C switches the capture to the whole page. The simulated wheel behaves like a
/// browser's (once A is at its end it scrolls C), and must never be used.
/// </summary>
public partial class HomeWindowTests
{
    private sealed class NestedPage
    {
        public const int WW = 640, WH = 440;                    // the window
        public static readonly PixelPoint Origin = new(-30000, -30000);
        public const int PageH = 1500, BoxX = 330, BoxY = 120, BoxW = 270, BoxH = 230, BoxContentH = 1300;
        public readonly byte[] Page, Box;
        public volatile int PageOffset, BoxOffset;
        public int WheelTurns;

        public NestedPage()
        {
            Page = Text(WW, PageH, 31, 236);
            Box = Text(BoxW, BoxContentH, 32, 255);
        }

        private static byte[] Text(int w, int h, int seed, byte paper)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = paper; px[i + 3] = 255; }
            var rnd = new Random(seed);
            for (int y = 8; y < h - 16; y += 18)
                for (int x = rnd.Next(4, 24); x < w - 30;)
                {
                    int word = rnd.Next(10, 46);
                    byte shade = (byte)rnd.Next(0, 130);
                    for (int yy = y; yy < y + 9; yy++)
                        for (int xx = x; xx < Math.Min(x + word, w - 4); xx++) { int i = (yy * w + xx) * 4; px[i] = shade; px[i + 1] = (byte)(shade / 2); px[i + 2] = (byte)(255 - shade); }
                    x += word + rnd.Next(5, 12);
                }
            return px;
        }

        public int PageMax => PageH - WH;
        public int BoxMax => BoxContentH - BoxH;

        /// <summary>Where A shows on the screen now (it moves with the page), cut by the window.</summary>
        public PixelRect BoxOnScreen => new PixelRect(Origin.X + BoxX, Origin.Y + BoxY - PageOffset, BoxW, BoxH).Intersect(Window);

        public static PixelRect Window => new(Origin.X, Origin.Y, WW, WH);

        public Frame Grab(PixelRect area)
        {
            int po = PageOffset, bo = BoxOffset;
            var screen = new byte[WW * WH * 4];
            Buffer.BlockCopy(Page, po * WW * 4, screen, 0, WH * WW * 4);
            for (int y = 0; y < BoxH; y++)
            {
                int sy = BoxY - po + y;
                if (sy < 0 || sy >= WH) continue;
                Buffer.BlockCopy(Box, (bo + y) * BoxW * 4, screen, (sy * WW + BoxX) * 4, BoxW * 4);
            }
            var px = new byte[area.Width * area.Height * 4];
            for (int y = 0; y < area.Height; y++)
                Buffer.BlockCopy(screen, ((area.Y - Origin.Y + y) * WW + area.X - Origin.X) * 4, px, y * area.Width * 4, area.Width * 4);
            return new Frame(area.Width, area.Height, px);
        }

        /// <summary>A browser's wheel: it scrolls A when over A until A is at its end, then the page.</summary>
        public void Wheel(PixelPoint at, int px)
        {
            WheelTurns++;
            if (BoxOnScreen.Contains(at) && BoxOffset + px <= BoxMax) BoxOffset = Math.Max(0, BoxOffset + px);
            else PageOffset = Math.Clamp(PageOffset + px, 0, PageMax);
        }
    }

    private sealed class NestedScreen(NestedPage page, PixelRect area) : IScreenSource
    {
        public Frame Grab() => page.Grab(area);
        public void Dispose() { }
    }

    private sealed class NestedWheel(NestedPage page) : IScroller
    {
        public void Scroll(PixelPoint at, int notches, bool down, int unit) => page.Wheel(at, (down ? 1 : -1) * notches * 60 * unit / 120);
    }

    /// <summary>A or C as UI Automation would see them: scrolled by percent; A also reads where C is.</summary>
    private sealed class NestedArea(NestedPage page, bool box) : IAreaScroller
    {
        public double? Percent => box ? page.BoxOffset * 100.0 / page.BoxMax : page.PageOffset * 100.0 / page.PageMax;
        public double? ViewSize => box ? NestedPage.BoxH * 100.0 / NestedPage.BoxContentH : NestedPage.WH * 100.0 / NestedPage.PageH;
        public int ViewHeight => box ? NestedPage.BoxH : NestedPage.WH;

        public bool SetPercent(double percent)
        {
            if (box) page.BoxOffset = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100 * page.BoxMax);
            else page.PageOffset = (int)Math.Round(Math.Clamp(percent, 0, 100) / 100 * page.PageMax);
            return true;
        }

        public IReadOnlyList<double?> AncestorPercents() => box ? [page.PageOffset * 100.0 / page.PageMax] : [];

        public void RestoreAncestors(IReadOnlyList<double?> percents)
        {
            if (box && percents.Count > 0 && percents[0] is { } p) page.PageOffset = (int)Math.Round(p / 100 * page.PageMax);
        }
    }

    private static void CheckLongshotNested(ToolboxHost host, LongshotTool longshot)
    {
        var page = new NestedPage();
        var overBox = new PixelPoint(NestedPage.Origin.X + NestedPage.BoxX + 100, NestedPage.Origin.Y + NestedPage.BoxY + 80);
        var overPage = new PixelPoint(NestedPage.Origin.X + 120, NestedPage.Origin.Y + 300);
        var section = new NestedArea(page, box: true);
        var whole = new NestedArea(page, box: false);
        LongshotCapture.StartOverride = (NestedPage.Window, overBox);
        LongshotCapture.TargetOverride = (p, outermost) =>
            !outermost && page.BoxOnScreen.Contains(p)
                ? new FoundTarget(page.BoxOnScreen, false, section, "A", "section A")
                : NestedPage.Window.Contains(p) ? new FoundTarget(NestedPage.Window, true, whole, "C", "page C") : null;
        LongshotCapture.ScreenOverride = area => new NestedScreen(page, area);
        LongshotCapture.ScrollerOverride = _ => new NestedWheel(page);
        LongshotCapture.TimingOverride = new SessionTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(3));
        CapturePanel.KeepOffScreen = true;
        try
        {
            // 1. The pointer over A: just A, scrolled by itself; C stays still.
            LongshotCapture.Start(host, longshot);
            var capture = LongshotCapture.Current!;
            Pump(() => capture.Found?.Identity == "A" && capture.Panel!.Extend.IsEnabled, "the capture to settle on section A");
            Assert.Equal(page.BoxOnScreen, capture.Target.Area);
            Pump(() => capture.Frame!.Area == page.BoxOnScreen, "the frame to glide onto section A", 5);
            Click(capture.Panel!.Auto);
            Pump(() => Label(capture.Panel.Auto) == "Auto" && capture.Panel.FinishReady, "Auto through section A");
            Assert.Equal(0, page.PageOffset);   // the page never moved
            Assert.Equal(page.BoxMax, page.BoxOffset);
            Assert.Equal(0, page.WheelTurns);   // no wheel, no pointer
            Assert.Contains("Reached the end", capture.Panel.StatusText);

            // 2. The marker dropped on the page: the whole page is captured instead, A moving with it.
            capture.MoveAnchor(overPage);
            Pump(() => capture.Found?.Identity == "C" && capture.Panel.Extend.IsEnabled, "the capture to switch to the page");
            Assert.Equal(NestedPage.Window, capture.Target.Area);
            int boxAt = page.BoxOffset;
            Click(capture.Panel.Auto);
            Pump(() => Label(capture.Panel.Auto) == "Auto" && capture.Panel.FinishReady, "Auto through the page");
            Assert.Equal(page.PageMax, page.PageOffset);
            Assert.Equal(boxAt, page.BoxOffset); // A was left as it was
            Assert.Equal(0, page.WheelTurns);
            Assert.Contains($"{NestedPage.WW} × {NestedPage.PageH:N0} px", capture.Panel.InfoText);

            Click(capture.Panel.CloseButton);
            Assert.Null(LongshotCapture.Current);
        }
        finally
        {
            LongshotCapture.CancelActive();
            Pump(() => LongshotCapture.Current is null, "the capture to end", 10);
            LongshotCapture.StartOverride = null;
            LongshotCapture.TargetOverride = null;
            LongshotCapture.ScreenOverride = null;
            LongshotCapture.ScrollerOverride = null;
            LongshotCapture.TimingOverride = null;
            CapturePanel.KeepOffScreen = false;
        }
    }
}
