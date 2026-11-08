using System.Collections.Concurrent;
using Toolbox.Processing.Capture;
using Toolbox.Windows.Longshot;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Runs the whole capture loop (scroll, settle, match, retry, undo, follow, finish) against a
/// simulated page and scroller: no real screen, mouse or wheel is touched.
/// </summary>
public class CaptureSessionTests
{
    private const int W = 300;
    private const int View = 240;
    private const int HeaderH = 30, FooterH = 20;

    /// <summary>A tall page of word-like blocks and boxes (deterministic), with sticky bars.</summary>
    private sealed class FakePage
    {
        public FakePage(int height, int seed = 1)
        {
            Height = height;
            Pixels = new byte[W * height * 4];
            Array.Fill(Pixels, (byte)255);
            var rnd = new Random(seed);
            for (int y = 6; y < height - 20;)
            {
                if (rnd.NextDouble() < 0.15)
                {
                    int h = rnd.Next(20, 60);
                    Fill(rnd.Next(5, 80), y, rnd.Next(120, 280), h, (byte)rnd.Next(60, 200));
                    y += h + 8;
                    continue;
                }
                for (int x = rnd.Next(5, 30); x < W - 40;)
                {
                    int word = rnd.Next(10, 45);
                    Fill(x, y, Math.Min(word, W - 5 - x), 9, (byte)rnd.Next(0, 90));
                    x += word + rnd.Next(5, 12);
                }
                y += 18;
            }
            Header = Bar(HeaderH, 70);
            Footer = Bar(FooterH, 40);
        }

        public int Height { get; }
        public byte[] Pixels { get; }
        public byte[] Header { get; }
        public byte[] Footer { get; }
        public int MaxOffset => Height - View;

        private void Fill(int x0, int y0, int w, int h, byte v)
        {
            for (int y = y0; y < Math.Min(y0 + h, Height); y++)
                for (int x = x0; x < Math.Min(x0 + w, W); x++)
                {
                    int i = (y * W + x) * 4;
                    Pixels[i] = v; Pixels[i + 1] = (byte)(v / 2); Pixels[i + 2] = (byte)(255 - v); Pixels[i + 3] = 255;
                }
        }

        private static byte[] Bar(int h, byte v)
        {
            var bar = new byte[W * h * 4];
            for (int i = 0; i < bar.Length; i += 4) { bar[i] = v; bar[i + 1] = v; bar[i + 2] = (byte)(v + 40); bar[i + 3] = 255; }
            for (int x = 10; x < 60; x++) bar[((h / 2) * W + x) * 4] = 255; // a "label"
            return bar;
        }

        public Frame At(int offset) => new(W, HeaderH + View + FooterH,
            [.. Header, .. Pixels.AsSpan(offset * W * 4, View * W * 4).ToArray(), .. Footer]);

        public byte[] Expected(int lastOffset) =>
            [.. Header, .. Pixels.AsSpan(0, (lastOffset + View) * W * 4).ToArray(), .. Footer];
    }

    private sealed class FakeScreen(FakePage page) : IScreenSource
    {
        public volatile int Offset;
        public FakePage Page => page;
        public Frame Grab() => page.At(Offset);
        public void Dispose() { }
    }

    /// <summary>Like a browser: one 120-unit notch moves <c>pxPerNotch</c>, proportionally less for smaller deltas.</summary>
    private sealed class FakeScroller(FakeScreen screen, FakePage page, int pxPerNotch) : IScroller
    {
        public readonly ConcurrentQueue<(int Notches, bool Down, int Unit)> Calls = new();

        public void Scroll(int notches, bool down, int unit)
        {
            Calls.Enqueue((notches, down, unit));
            int px = notches * pxPerNotch * unit / 120;
            screen.Offset = Math.Clamp(screen.Offset + (down ? px : -px), 0, page.MaxOffset);
        }
    }

    private static readonly SessionTiming Fast = new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(3));

    private sealed class Run : IDisposable
    {
        public readonly BlockingCollection<SessionEvent> Events = new();
        public readonly FakePage Page;
        public readonly FakeScreen Screen;
        public readonly FakeScroller Scroller;
        public readonly CaptureSession Session;

        public Run(int pageHeight, int pxPerNotch, double stepFraction = 0.55, int areaHeight = HeaderH + View + FooterH,
            FakeScreen? screen = null, long rewindDelta = 0, double? knownPxPerDelta = null)
        {
            Page = screen is null ? new FakePage(pageHeight) : screen.Page;
            Screen = screen ?? new FakeScreen(Page);
            Scroller = new FakeScroller(Screen, Page, pxPerNotch);
            Session = new CaptureSession(() => Screen, Scroller, areaHeight, stepFraction, 120, 90, Events.Add, Fast)
            {
                EscapePressed = () => false,
                RewindDelta = rewindDelta,
                KnownPxPerDelta = knownPxPerDelta,
            };
            Session.Start();
            Next<SessionReady>();
        }

        /// <summary>The next event of type T (skipping others); fails after a timeout.</summary>
        public T Next<T>(Func<T, bool>? where = null) where T : SessionEvent
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                if (!Events.TryTake(out var e, TimeSpan.FromSeconds(1))) continue;
                if (e is SessionFailed failed) throw new Xunit.Sdk.XunitException("Session failed: " + failed.Message);
                if (e is T t && (where is null || where(t))) return t;
            }
            throw new TimeoutException($"No {typeof(T).Name} arrived.");
        }

        /// <summary>Runs a command and returns the last update it produced.</summary>
        public SessionUpdate Do(SessionCommand command, bool mode = false)
        {
            if (mode) Session.StartMode(command); else Session.Enqueue(command);
            Next<SessionBusy>(b => b.Command == command);
            SessionUpdate? last = null;
            while (true)
            {
                var e = Events.Take();
                if (e is SessionUpdate u) last = u;
                if (e is SessionBusy { Command: null }) return last!;
                if (e is SessionFailed f) throw new Xunit.Sdk.XunitException(f.Message);
            }
        }

        public SessionFinished Finish()
        {
            Session.Enqueue(SessionCommand.Finish);
            return Next<SessionFinished>();
        }

        public void Dispose() => Session.Dispose();
    }

    [Fact]
    public void Auto_reaches_the_bottom_and_the_result_is_pixel_identical()
    {
        using var run = new Run(pageHeight: 2400, pxPerNotch: 100);
        var last = run.Do(SessionCommand.Auto, mode: true);
        Assert.Contains("Reached the bottom", last.Message);
        var done = run.Finish();
        Assert.Equal(run.Page.Expected(run.Page.MaxOffset), done.Pixels);
        Assert.Equal(HeaderH + run.Page.Height + FooterH, done.Height);
    }

    [Fact]
    public void Extend_starts_with_one_notch_then_takes_bigger_steps()
    {
        using var run = new Run(pageHeight: 3000, pxPerNotch: 40);
        run.Do(SessionCommand.Extend);
        run.Do(SessionCommand.Extend);
        var calls = run.Scroller.Calls.ToArray();
        Assert.Equal(1, calls[0].Notches);
        Assert.True(calls[1].Notches > 1, $"second step used {calls[1].Notches} notch(es)");
        var done = run.Finish();
        Assert.Equal(run.Page.Expected(run.Screen.Offset), done.Pixels);
    }

    [Fact]
    public void One_notch_that_jumps_past_the_area_is_retried_with_a_smaller_wheel_step()
    {
        // A short area (under 300 px, so it starts at half a notch) where even that scrolls further
        // than the area can show: the session must step back and halve the wheel delta again.
        using var run = new Run(pageHeight: 2000, pxPerNotch: 500);
        var update = run.Do(SessionCommand.Extend);
        Assert.StartsWith("Added", update.Message);
        Assert.Contains(run.Scroller.Calls, c => c.Unit == 30); // fell back to a quarter notch
        var done = run.Finish();
        Assert.Equal(run.Page.Expected(run.Screen.Offset), done.Pixels);
    }

    [Fact]
    public void Undo_removes_the_last_part_and_scrolls_back()
    {
        using var run = new Run(pageHeight: 3000, pxPerNotch: 90);
        var first = run.Do(SessionCommand.Extend);
        int offsetAfterFirst = run.Screen.Offset;
        var second = run.Do(SessionCommand.Extend);
        Assert.True(second.Height > first.Height);
        var undone = run.Do(SessionCommand.Undo);
        Assert.Equal(first.Height, undone.Height);
        Assert.Equal(offsetAfterFirst, run.Screen.Offset);
        run.Do(SessionCommand.Extend); // carries on from there
        var done = run.Finish();
        Assert.Equal(run.Page.Expected(run.Screen.Offset), done.Pixels);
    }

    [Fact]
    public void Follow_adds_as_the_user_scrolls()
    {
        using var run = new Run(pageHeight: 2200, pxPerNotch: 100);
        run.Session.StartMode(SessionCommand.Follow);
        run.Next<SessionBusy>(b => b.Command == SessionCommand.Follow);
        var rnd = new Random(4);
        while (run.Screen.Offset < run.Page.MaxOffset) // the "user" scrolls in uneven steps, with pauses
        {
            run.Screen.Offset = Math.Min(run.Screen.Offset + rnd.Next(30, 170), run.Page.MaxOffset);
            run.Next<SessionUpdate>(u => u.Message?.StartsWith("Added") == true);
        }
        run.Session.Stop();
        run.Next<SessionBusy>(b => b.Command is null);
        var done = run.Finish();
        Assert.Equal(run.Page.Expected(run.Page.MaxOffset), done.Pixels);
    }

    [Fact]
    public void Nothing_moving_reports_the_end_without_adding_anything()
    {
        using var run = new Run(pageHeight: View, pxPerNotch: 100); // the page is exactly one screen tall
        var update = run.Do(SessionCommand.Extend);
        Assert.Contains("Reached the bottom", update.Message);
        Assert.Equal(1, update.Parts);
    }

    [Fact]
    public void Refresh_goes_back_to_where_the_capture_started_and_keeps_the_step_size()
    {
        long scrolled;
        double? perNotch;
        // A capture that starts 400 px down the page, takes three steps, then Refresh.
        var page = new FakePage(3000);
        var screen = new FakeScreen(page) { Offset = 400 };
        using (var run = new Run(0, pxPerNotch: 90, screen: screen))
        {
            run.Do(SessionCommand.Extend);
            run.Do(SessionCommand.Extend);
            run.Do(SessionCommand.Extend);
            Assert.True(screen.Offset > 400);
            scrolled = run.Session.ScrolledDelta;
            perNotch = run.Session.PxPerDelta;
        }
        Assert.True(scrolled > 0);
        Assert.NotNull(perNotch);

        using var again = new Run(0, pxPerNotch: 90, screen: screen, rewindDelta: scrolled, knownPxPerDelta: perNotch);
        Assert.Equal(400, screen.Offset); // back at the start
        again.Do(SessionCommand.Extend);
        // A new capture starts cautiously with half a notch; this one already knows how far a notch goes.
        Assert.Equal(120, again.Scroller.Calls.First(c => c.Down).Unit);
        var done = again.Finish();
        Assert.Equal(page.At(400).Height + (screen.Offset - 400), done.Height);
    }

    [Fact]
    public void Same_ignores_a_blinking_caret_but_not_a_scroll()
    {
        var page = new FakePage(1200);
        var a = page.At(100);
        var caret = new Frame(a.Width, a.Height, (byte[])a.Pixels.Clone());
        for (int y = 80; y < 96; y++) caret.Pixels[(y * W + 150) * 4] ^= 0xFF; // a 1-px text cursor
        Assert.True(CaptureSession.Same(a, caret));
        Assert.False(CaptureSession.Same(a, page.At(103)));
    }
}

public class ScreenCaptureTests
{
    [Fact]
    public void Gdi_capture_reads_a_small_screen_rectangle()
    {
        // Read-only: copies 64×64 pixels of the screen, touches nothing.
        using var source = new GdiScreenSource(new PixelRect(0, 0, 64, 64));
        var frame = source.Grab();
        Assert.Equal((64, 64, 64 * 64 * 4), (frame.Width, frame.Height, frame.Pixels.Length));
    }

    [Fact]
    public void Virtual_screen_covers_the_primary_monitor()
    {
        var all = GdiScreenSource.VirtualScreen;
        var primary = Interop.ScreenGeometry.MonitorAt(new Interop.Native.POINT { X = 0, Y = 0 }).Bounds;
        Assert.True(all.X <= primary.Left && all.Y <= primary.Top && all.Right >= primary.Right && all.Bottom >= primary.Bottom);
    }

    [Fact]
    public void Panel_goes_beside_the_area_and_inside_it_only_when_there_is_no_room()
    {
        var work = Interop.ScreenGeometry.MonitorAt(new Interop.Native.POINT { X = 0, Y = 0 }).WorkArea;
        var middle = new PixelRect(work.Left + work.Width / 4, work.Top + 100, work.Width / 3, work.Height / 2);
        var (x, y) = CapturePanel.Spot(middle, 280, 400);
        Assert.False(new PixelRect(x, y, 280, 400).Overlaps(middle));

        var whole = PixelRect.From(work);
        var (ix, iy) = CapturePanel.Spot(whole, 280, 400);
        Assert.True(ix >= work.Left && iy >= work.Top && ix + 280 <= work.Right && iy + 400 <= work.Bottom);
    }
}
