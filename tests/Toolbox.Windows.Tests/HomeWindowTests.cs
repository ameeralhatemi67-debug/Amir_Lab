using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Home;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Pulse;
using Toolbox.Core.Pulse;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Builds every page of the Amir Lap window, and Longshot's panel, result window and area chooser,
/// off-screen (nothing is shown, no tool is started, no input is sent), in light and dark, English
/// and Arabic. Set AMIRLAP_SNAPSHOT_DIR to also save each one as a PNG for visual review.
/// </summary>
[Collection(AppLanguageCollection.Name)]
public class HomeWindowTests
{
    [Fact]
    public void Every_page_and_window_builds_in_light_and_dark()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { BuildAndRender(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void BuildAndRender()
    {
        var data = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"amirlap-home-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable(Core.AppPaths.DataEnvironmentVariable);
        Environment.SetEnvironmentVariable(Core.AppPaths.DataEnvironmentVariable, data);
        // Pulse's page looks for Claude Code and Codex: point both at empty folders, never the real ones.
        var previousClaude = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var previousCodex = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", System.IO.Path.Combine(data, "claude"));
        Environment.SetEnvironmentVariable("CODEX_HOME", System.IO.Path.Combine(data, "codex"));
        var snapshots = Environment.GetEnvironmentVariable("AMIRLAP_SNAPSHOT_DIR");
        try
        {
            // Only the styles: start-up (single instance, tray, tools) must not run in the test, or it
            // would talk to an installed Amir Lap that is running.
            Environment.SetEnvironmentVariable(App.StylesOnlyVariable, "1");
            var app = new App();
            app.InitializeComponent(); // loads the shared styles (Theme/Controls.xaml)
            var host = new ToolboxHost(new DiagnosticsChannel(false));
            var dispatcher = new ActionDispatcher(host, new ToastManager(host, _ => { }));
            var fileToolbox = new FileToolboxTool(host, dispatcher, new DiagnosticsChannel(false));
            var longshot = new LongshotTool(host);
            var pulse = new PulseTool(host);
            using var registry = new ToolRegistry(host.Settings, [fileToolbox, longshot, pulse]); // never applied: nothing starts

            foreach (var (theme, lang) in new[] { ("light", "en"), ("dark", "en"), ("light", "ar"), ("dark", "ar") })
            {
                Lang.Set(lang);
                ThemeResources.Apply(app, theme);
                var window = new AmirLapWindow(host, registry);
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                // Stands in for the desktop behind the glass (the real window sits on Windows' acrylic).
                var frame = new Border { Child = content, Background = theme == "dark"
                    ? new LinearGradientBrush(Color.FromRgb(0x10, 0x16, 0x2C), Color.FromRgb(0x24, 0x10, 0x22), 30)
                    : new LinearGradientBrush(Color.FromRgb(0xD6, 0xE0, 0xF6), Color.FromRgb(0xF4, 0xE4, 0xEC), 30) };

                foreach (var page in new[] { AmirLapWindow.DashboardPage, FileToolboxTool.ToolId, LongshotTool.ToolId, PulseTool.ToolId, AmirLapWindow.GeneralPage, AmirLapWindow.AboutPage })
                {
                    window.ShowPage(page);
                    Assert.Equal(page, window.CurrentPage);
                    double height = page switch { FileToolboxTool.ToolId => 1900, LongshotTool.ToolId => 1500, PulseTool.ToolId => 2700, AmirLapWindow.GeneralPage => 1150, AmirLapWindow.DashboardPage => 1500, _ => 790 };
                    Layout(frame, 1180, height);
                    var scroll = FindScroll(content);
                    Assert.NotNull(scroll?.Content);
                    Save(snapshots, frame, 1180, height, $"{page}-{theme}-{lang}");
                }

                // Changing the language or theme rebuilds the window in place, on the same page.
                window.Rebuild(spinThemeIcon: true);
                Assert.Equal(AmirLapWindow.AboutPage, window.CurrentPage);
                Assert.Equal(lang == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, content.FlowDirection);
                window.Close();

                RenderLongshotWindows(snapshots, theme, lang);
                if (lang == "en") RenderWheel(snapshots, theme);
                RenderPulse(snapshots, theme, lang, host);
            }
            Lang.Set("en");
            ThemeResources.Apply(app, "light");
            CheckRealFrame(host, registry);
            CheckRealDock();
            CheckLongshotFlow(host, longshot);
            CheckLongshotTargets(host, longshot);
            app.Shutdown();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Core.AppPaths.DataEnvironmentVariable, previous);
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", previousClaude);
            Environment.SetEnvironmentVariable(App.StylesOnlyVariable, null);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousCodex);
            try { System.IO.Directory.Delete(data, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    /// <summary>
    /// Pulse's real dock window, shown far off-screen (a made-up screen at -30000) without taking
    /// focus, and no input sent. Its glass reads and blurs the screen behind it; that picture must
    /// never make the dock bigger (it grew on every refresh in 0.5.0 until it covered the screen).
    /// Its empty parts must let clicks through, tiles must answer clicks, and its menu must work.
    /// </summary>
    private static void CheckRealDock()
    {
        var real = Interop.ScreenGeometry.MonitorAt(new Interop.Native.POINT());
        var rect = new Interop.Native.RECT { Left = -30000, Top = -30000, Right = -30000 + 1600, Bottom = -30000 + 1000 };
        var screen = new Interop.MonitorArea(rect, rect, real.Scale);
        var settings = new PulseSettings();
        string? clicked = null;
        var dock = new DockWindow(() => settings, _ => { }) { ScreenOverride = () => screen };
        dock.TileClicked += id => clicked = id;
        bool ran = false;
        dock.MenuFor = () => [new DockMenuItem("Work", () => ran = true, Checked: true), new DockMenuItem("Pulse settings…", () => { }, Separator: true)];
        dock.Show();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            Pump(() => (dock.Tile(PulseTiles.Claude) is { ActualWidth: > 0 } && dock.Placed.Width > 0) || DateTime.UtcNow > deadline, "the dock to be placed");
            Assert.True(dock.Placed.Width > 0 && dock.Tile(PulseTiles.Claude) is { ActualWidth: > 0 },
                $"placed {dock.Placed}, loaded {dock.IsLoaded}, tiles {dock.Tiles.Count}, claude width {dock.Tile(PulseTiles.Claude)?.ActualWidth}, window {dock.ActualWidth}x{dock.ActualHeight}");
            var hwnd = new System.Windows.Interop.WindowInteropHelper(dock).Handle;
            var first = dock.Placed;
            double k = real.Scale;
            // Medium tiles: about 76 DIPs thick, plus room for the cards beside it; on the right edge, flush with it.
            Assert.InRange(first.Width, (int)(360 * k), (int)(400 * k));
            Assert.Equal(rect.Right, first.Right);
            for (int i = 0; i < 5; i++)
            {
                dock.KeepUpNow();
                var until = DateTime.UtcNow.AddMilliseconds(150);
                Pump(() => DateTime.UtcNow > until, "a moment");
            }
            Assert.True(dock.BackdropRefreshes >= 3, "the glass reads the screen behind it");
            Assert.Equal(first, dock.Placed);
            Assert.True(GetWindowRect(hwnd, out var r));
            Assert.Equal(first.Width, r.Right - r.Left);
            Assert.Equal(first.Height, r.Bottom - r.Top);

            // The tile and the empty room beside the dock: only the tile belongs to the dock.
            var tile = dock.Tile(PulseTiles.Claude)!;
            var tileCentre = tile.PointToScreen(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2));
            Assert.Equal(hwnd, WindowFromPoint(new POINT { X = (int)tileCentre.X, Y = (int)tileCentre.Y }));
            Assert.NotEqual(hwnd, WindowFromPoint(new POINT { X = r.Left + 40, Y = (r.Top + r.Bottom) / 2 }));

            // A click on a tile (the events WPF raises for one; no real input).
            tile.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            tile.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal(PulseTiles.Claude, clicked);

            // The menu opens inside the dock's own window and runs what is picked.
            dock.ShowMenu(new Point(r.Right - r.Left - 40, 100));
            Assert.NotNull(dock.OpenMenu);
            var first2 = FindButton(dock.OpenMenu!, "Work") ?? FirstButton(dock.OpenMenu!);
            Assert.NotNull(first2);
            Click(first2!);
            Assert.True(ran);
            Assert.Null(dock.OpenMenu);

            // Every edge places it flush with that edge and keeps the same thickness.
            foreach (var edge in new[] { "left", "top", "bottom", "right" })
            {
                settings.Edge = edge;
                dock.Rebuild();
                var p = dock.Placed;
                bool vertical = edge is "left" or "right";
                Assert.InRange(vertical ? p.Width : p.Height, (int)(360 * k), (int)(400 * k));
                if (edge == "left") Assert.Equal(rect.Left, p.X);
                if (edge == "top") Assert.Equal(rect.Top, p.Y);
                if (edge == "bottom") Assert.Equal(rect.Bottom, p.Y + p.Height);
            }
        }
        finally
        {
            dock.Close();
        }
    }

    private static Button? FirstButton(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b) return b;
            if (FirstButton(child) is { } found) return found;
        }
        return null;
    }

    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    /// <summary>
    /// The real window, shown far off-screen without taking focus: Windows must treat its edges as
    /// resize borders, the empty title bar as the drag area and our maximize button as its maximize
    /// button (that is what opens the Windows 11 snap layouts), and must not draw caption buttons of
    /// its own. Only hit-test questions are sent to the window; no mouse or keyboard input.
    /// </summary>
    private static void CheckRealFrame(ToolboxHost host, ToolRegistry registry)
    {
        var window = new AmirLapWindow(host, registry)
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            long style = GetWindowLongPtr(hwnd, -16).ToInt64();
            Assert.True((style & 0x00080000) == 0, "no system menu, so Windows draws no caption buttons over ours");
            Assert.True((style & 0x00C00000) == 0x00C00000, "keeps the caption style (real maximize, snap, animations)");
            Assert.True((style & 0x00040000) != 0, "resizable frame");
            Assert.True((style & 0x00030000) == 0x00030000, "minimize and maximize boxes");

            var max = FindButton(window, "Maximize");
            Assert.NotNull(max);
            Assert.Equal(9, HitTest(hwnd, max!.PointToScreen(new Point(max.ActualWidth / 2, max.ActualHeight / 2)))); // HTMAXBUTTON

            Assert.True(GetWindowRect(hwnd, out var r));
            Assert.Equal(2, HitTest(hwnd, new Point((r.Left + r.Right) / 2.0, r.Top + 20)));   // HTCAPTION: drag to move
            Assert.Equal(10, HitTest(hwnd, new Point(r.Left + 2, (r.Top + r.Bottom) / 2.0)));  // HTLEFT
            Assert.Equal(17, HitTest(hwnd, new Point(r.Right - 2, r.Bottom - 2)));             // HTBOTTOMRIGHT
            Assert.Equal(1, HitTest(hwnd, new Point((r.Left + r.Right) / 2.0, (r.Top + r.Bottom) / 2.0))); // HTCLIENT
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A whole Longshot capture through the real controller, panel, frame and session, against a
    /// simulated page and wheel (nothing real is read or scrolled; every window stays off the
    /// screen): it gets ready, Extend adds a part, pulling the bottom edge down adds the rows below
    /// without starting again, Auto turns into Stop and runs to the bottom, Refresh scrolls back to
    /// the start and begins again from one part, and the close button ends it.
    /// </summary>
    private static void CheckLongshotFlow(ToolboxHost host, LongshotTool longshot)
    {
        const int width = 320, view = 300, pageHeight = 1600;
        var page = new byte[width * pageHeight * 4];
        Array.Fill(page, (byte)255);
        var rnd = new Random(3);
        for (int y = 8; y < pageHeight - 20; y += 18)
            for (int x = rnd.Next(4, 24); x < width - 40;)
            {
                int word = rnd.Next(10, 50);
                byte shade = (byte)rnd.Next(0, 120);
                for (int yy = y; yy < y + 9; yy++)
                    for (int xx = x; xx < Math.Min(x + word, width - 4); xx++)
                    {
                        int i = (yy * width + xx) * 4;
                        page[i] = shade; page[i + 1] = (byte)(shade / 2); page[i + 2] = (byte)(255 - shade);
                    }
                x += word + rnd.Next(5, 12);
            }
        // Below the scrolling page the "screen" shows something that doesn't scroll (a status bar, say).
        var below = new byte[width * 120 * 4];
        for (int i = 0; i < below.Length; i += 4) { below[i] = 40; below[i + 1] = (byte)(i / (width * 4) * 2); below[i + 2] = 90; below[i + 3] = 255; }
        int offset = 0; // only the session's worker thread reads and moves it
        LongshotCapture.ScreenOverride = area => new PageScreen(() =>
        {
            int h = area.Height, rows = Math.Min(h, view);
            var px = new byte[width * h * 4];
            Buffer.BlockCopy(page, offset * width * 4, px, 0, rows * width * 4);
            if (h > view) Buffer.BlockCopy(below, 0, px, rows * width * 4, (h - view) * width * 4);
            return new Processing.Capture.Frame(width, h, px);
        });
        LongshotCapture.ScrollerOverride = _ => new PageScroller(by => offset = Math.Clamp(offset + by, 0, pageHeight - view));
        LongshotCapture.TimingOverride = new SessionTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(3));
        CapturePanel.KeepOffScreen = true;
        try
        {
            LongshotCapture.Start(host, longshot, new PixelRect(-30000, -30000, width, view));
            var panel = LongshotCapture.Current?.Panel;
            Assert.NotNull(panel);

            // Ready: the first part is shown and Extend can be pressed (the buttons are enabled for a
            // moment while the panel is created, before the session marks them busy).
            Pump(() => panel!.Extend.IsEnabled && panel.InfoText.Contains("1 part"), "ready (Extend enabled)");
            Assert.Contains("1 part", panel!.InfoText);

            Click(panel.Extend);
            Pump(() => panel.InfoText.Contains("2 parts"), "a second part after Extend");

            // Pull the bottom edge down 60 px: those rows are added, nothing starts again.
            LongshotCapture.Current!.ChangeArea(new PixelRect(-30000, -30000, width, view + 60));
            Pump(() => panel.StatusText.StartsWith("Added 60 px below"), "the rows below being added");
            Assert.Contains("3 parts", panel.InfoText);

            Click(panel.Auto);
            Assert.Equal("Stop", Label(panel.Auto));
            Pump(() => Label(panel.Auto) == "Auto" && panel.Extend.IsEnabled, "Auto running to the bottom");
            Assert.DoesNotContain(" 3 parts", panel.InfoText);

            Click(panel.RefreshButton);
            Pump(() => panel.InfoText.Contains("1 part") && panel.Extend.IsEnabled, "starting again after Refresh");
            Assert.Equal(0, offset); // scrolled back to where the capture started

            Click(panel.CloseButton);
            Assert.Null(LongshotCapture.Current);
        }
        finally
        {
            LongshotCapture.CancelActive();
            LongshotCapture.ScreenOverride = null;
            LongshotCapture.ScrollerOverride = null;
            LongshotCapture.TimingOverride = null;
            CapturePanel.KeepOffScreen = false;
        }
    }

    /// <summary>
    /// Targets through the real controller, panel and frame, on a simulated window whose page stays
    /// put and whose chat box scrolls (only when the wheel turns over it): the capture starts on the
    /// window and glides onto the scroll area "found" under the pointer; Auto runs to the end and
    /// Done becomes the highlighted next step; the Window button goes back to the whole window, where
    /// the first step finds the part that scrolls by itself and narrows to it; the scroll marker can
    /// be moved; pressing the shortcut again saves.
    /// </summary>
    private static void CheckLongshotTargets(ToolboxHost host, LongshotTool longshot)
    {
        const int ww = 520, wh = 380;                       // the window
        var origin = new PixelPoint(-30000, -30000);
        var box = new PixelRect(origin.X + 200, origin.Y + 70, 260, 240); // the chat box in it
        int contentHeight = 1300, offset = 0;
        var content = new byte[box.Width * contentHeight * 4];
        Array.Fill(content, (byte)255);
        var rnd = new Random(8);
        for (int y = 8; y < contentHeight - 20; y += 18)
            for (int x = rnd.Next(4, 20); x < box.Width - 30;)
            {
                int word = rnd.Next(10, 46);
                byte shade = (byte)rnd.Next(0, 120);
                for (int yy = y; yy < y + 9; yy++)
                    for (int xx = x; xx < Math.Min(x + word, box.Width - 4); xx++) { int i = (yy * box.Width + xx) * 4; content[i] = shade; content[i + 1] = (byte)(shade / 2); content[i + 2] = (byte)(255 - shade); }
                x += word + rnd.Next(5, 12);
            }
        var still = new byte[ww * wh * 4];
        for (int i = 0; i < still.Length; i += 4) { still[i] = still[i + 1] = still[i + 2] = 236; still[i + 3] = 255; }
        for (int y = 12; y < wh - 10; y += 14)                // the page around: text that never moves
            for (int x = 10; x < ww - 20; x += 46)
                for (int yy = y; yy < y + 6; yy++)
                    for (int xx = x; xx < x + 30; xx++) { int i = (yy * ww + xx) * 4; still[i] = 90; still[i + 1] = 90; still[i + 2] = 100; }
        int bx = box.X - origin.X, by = box.Y - origin.Y;
        for (int x = bx - 1; x <= bx + box.Width; x++) foreach (int y in new[] { by - 1, by + box.Height }) { int i = (y * ww + x) * 4; still[i] = still[i + 1] = still[i + 2] = 150; }
        for (int y = by - 1; y <= by + box.Height; y++) foreach (int x in new[] { bx - 1, bx + box.Width }) { int i = (y * ww + x) * 4; still[i] = still[i + 1] = still[i + 2] = 150; }
        Processing.Capture.Frame Grab(PixelRect area)
        {
            var screen = (byte[])still.Clone();
            for (int y = 0; y < box.Height; y++) Buffer.BlockCopy(content, (offset + y) * box.Width * 4, screen, ((by + y) * ww + bx) * 4, box.Width * 4);
            var px = new byte[area.Width * area.Height * 4];
            for (int y = 0; y < area.Height; y++) Buffer.BlockCopy(screen, ((area.Y - origin.Y + y) * ww + area.X - origin.X) * 4, px, y * area.Width * 4, area.Width * 4);
            return new Processing.Capture.Frame(area.Width, area.Height, px);
        }
        var window = new PixelRect(origin.X, origin.Y, ww, wh);
        var pointer = new PixelPoint(box.CenterX, box.CenterY);
        LongshotCapture.StartOverride = (window, pointer);
        LongshotCapture.ScrollAreaOverride = p => box.Contains(p) ? box : null;
        LongshotCapture.ScreenOverride = area => new PageScreen(() => Grab(area));
        LongshotCapture.ScrollerOverride = _ => new BoxScroller((at, by) => { if (box.Contains(at)) offset = Math.Clamp(offset + by, 0, contentHeight - box.Height); });
        LongshotCapture.TimingOverride = new SessionTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(3));
        CapturePanel.KeepOffScreen = true;
        try
        {
            LongshotCapture.Start(host, longshot);
            var capture = LongshotCapture.Current!;
            var panel = capture.Panel!;
            // It starts on the window, then glides onto the chat under the pointer.
            Pump(() => capture.Target.Kind == TargetKind.ScrollArea && panel.Extend.IsEnabled && panel.InfoText.Contains("1 part"), "the scroll area under the pointer");
            Assert.Equal(box, capture.Target.Area);
            Assert.Equal(TargetKind.ScrollArea, panel.ShownTarget);
            Pump(() => capture.Frame!.Area == box, "the frame gliding onto it");

            Click(panel.Auto);
            Pump(() => panel.FinishReady && Label(panel.Auto) == "Auto", "Auto to the end of the chat, Done highlighted");
            Assert.Contains($"{box.Width:N0} × {contentHeight:N0} px", panel.InfoText);

            // Back to the whole window (the chat scrolled back to its top first): the first step
            // finds that only the chat scrolls.
            offset = 0;
            panel.TargetButton(TargetKind.Window).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Pump(() => capture.Target.Kind == TargetKind.Window && panel.Extend.IsEnabled, "the whole window again");
            Assert.Equal(window, capture.Target.Area);
            Assert.False(panel.FinishReady);
            Click(panel.Extend);
            Pump(() => panel.InfoText.Contains("2 parts"), "a second part, from the chat alone");
            Assert.Equal(TargetKind.ScrollArea, capture.Target.Kind);
            Assert.Equal(box, capture.Target.Area);
            Assert.Equal(TargetKind.ScrollArea, panel.ShownTarget);
            Assert.StartsWith("Only this part scrolls", panel.StatusText);

            // The marker moves where the wheel turns.
            var spot = new PixelPoint(box.X + 30, box.Y + 40);
            capture.MoveAnchor(spot);
            Assert.Equal(spot, capture.Target.Anchor);
            Assert.Equal(spot, capture.Frame.Anchor);

            // The shortcut again saves: it starts finishing at once (cancelled straight after, so
            // nothing is written to the real Pictures folder).
            LongshotCapture.Start(host, longshot);
            Assert.StartsWith("Saving", panel.StatusText);
            Assert.False(panel.Done.IsEnabled);
        }
        finally
        {
            LongshotCapture.CancelActive();
            Pump(() => LongshotCapture.Current is null, "the capture to end", 10);
            LongshotCapture.StartOverride = null;
            LongshotCapture.ScrollAreaOverride = null;
            LongshotCapture.ScreenOverride = null;
            LongshotCapture.ScrollerOverride = null;
            LongshotCapture.TimingOverride = null;
            CapturePanel.KeepOffScreen = false;
        }
    }

    private sealed class BoxScroller(Action<PixelPoint, int> by) : IScroller
    {
        public void Scroll(PixelPoint at, int notches, bool down, int unit) => by(at, (down ? 1 : -1) * notches * 80 * unit / 120);
    }

    private sealed class PageScreen(Func<Processing.Capture.Frame> grab) : IScreenSource
    {
        public Processing.Capture.Frame Grab() => grab();
        public void Dispose() { }
    }

    private sealed class PageScroller(Action<int> by) : IScroller
    {
        public void Scroll(PixelPoint at, int notches, bool down, int unit) => by((down ? 1 : -1) * notches * 90 * unit / 120);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, button));

    private static string Label(Button button) => ((TextBlock)((StackPanel)button.Content).Children[1]).Text;

    /// <summary>Lets the UI thread handle what the capture thread sent, until <paramref name="done"/> (or fails after a while).</summary>
    private static void Pump(Func<bool> done, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Longshot never got to: " + what);
            var frame = new System.Windows.Threading.DispatcherFrame();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => frame.Continue = false);
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static Button? FindButton(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b && System.Windows.Automation.AutomationProperties.GetName(b) == name) return b;
            if (FindButton(child, name) is { } found) return found;
        }
        return null;
    }

    private static int HitTest(IntPtr hwnd, Point screen)
    {
        int x = (int)Math.Round(screen.X), y = (int)Math.Round(screen.Y);
        var lParam = new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        return SendMessage(hwnd, 0x0084, IntPtr.Zero, lParam).ToInt32(); // WM_NCHITTEST
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>
    /// File Toolbox's glass wheel over a busy background: idle, a lit tile, the tools ring and the
    /// Share bubble lit, plus hit testing of tiles, hub and bubble. Motion is reduced so each state
    /// is drawn at its end.
    /// </summary>
    private static void RenderWheel(string? snapshots, string theme)
    {
        var payload = new[] { new Core.Inputs.ProbedFile(@"C:\Users\You\Pictures\Holiday.png", Core.Inputs.FileKind.Png, 1_150_000, null) };
        var rings = Wheel.WheelModels.Build(payload, new Core.Settings.AppSettings());
        var menu = new Wheel.RadialMenu(330);
        menu.ApplyPalette(Palette.Current(theme));
        menu.SetReducedMotion(true);
        var (primary, secondary) = Wheel.WheelModels.IdleText(payload);
        menu.SetIdleText(primary, secondary);
        menu.SetShare(new Wheel.WheelSegmentModel(Shell.ShareService.ActionId, "Share", "Quick Share", "lap-share", true, "Send to a nearby phone or PC"));
        menu.SetRing(rings[0], 0, rings.Count, animate: false);

        Assert.Equal(Wheel.RadialMenu.ShareHit, menu.HitTest(menu.ShareCenter));
        Assert.Equal(Shell.ShareService.ActionId, menu.SegmentAt(Wheel.RadialMenu.ShareHit)?.ActionId);
        Assert.Equal(Wheel.RadialMenu.CenterHit, menu.HitTest(new Point(menu.Center, menu.Center)));
        for (int i = 0; i < menu.SegmentCount; i++) Assert.Equal(i, menu.HitTest(menu.SegmentCenter(i)));
        Assert.Equal(Wheel.RadialMenu.OutsideHit, menu.HitTest(new Point(menu.Center, -40)));

        // Something busy behind the glass: colour, blocks and text, as on a real desktop.
        var stage = new Grid { Width = 420, Height = 420 };
        stage.Background = theme == "dark"
            ? new LinearGradientBrush(Color.FromRgb(0x2B, 0x10, 0x4A), Color.FromRgb(0x6A, 0x12, 0x3A), 35)
            : new LinearGradientBrush(Color.FromRgb(0x8E, 0x5C, 0xE8), Color.FromRgb(0xE8, 0x5C, 0x9A), 35);
        var busy = new Canvas();
        for (int i = 0; i < 9; i++)
        {
            var block = new Border { Width = 150, Height = 26, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)), Child = new TextBlock { Text = $"Document {i + 1}.pdf", Margin = new Thickness(8, 4, 0, 0), Foreground = Brushes.Black } };
            Canvas.SetLeft(block, 20 + (i % 2) * 230);
            Canvas.SetTop(block, 30 + i * 40);
            busy.Children.Add(block);
        }
        stage.Children.Add(busy);
        // What the app does as the wheel opens: a picture of what is behind it, frosted under the glass.
        Layout(stage, 420, 420);
        var behind = new RenderTargetBitmap(420, 420, 96, 96, PixelFormats.Pbgra32);
        behind.Render(stage);
        behind.Freeze();
        double offset = (420 - menu.Diameter) / 2;
        menu.SetBackdrop(behind, new Rect(-offset, -offset, 420, 420));
        stage.Children.Add(new Border { Child = menu, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

        foreach (var (name, ring, hover) in new[] { ("idle", 0, Wheel.RadialMenu.OutsideHit), ("hover", 0, 2), ("tools", 1, 6), ("share", 0, Wheel.RadialMenu.ShareHit) })
        {
            menu.SetRing(rings[ring], ring, rings.Count, animate: false);
            menu.SetHover(hover);
            Layout(stage, 420, 420);
            Save(snapshots, stage, 420, 420, $"wheel-{name}-{theme}");
        }
    }

    /// <summary>
    /// The Longshot panel (with a fake preview: working, then at the end with Done highlighted), the
    /// frame on a scroll area with its scroll marker and the rows just added, the pick highlight,
    /// a whole scene as the user sees it (an app, the dim with its hole, the frame, the panel), and
    /// the result window, built off-screen.
    /// </summary>
    private static void RenderLongshotWindows(string? snapshots, string theme, string lang)
    {
        // A fake "page": rows of text-like blocks.
        int w = 900, h = 1600;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 250; px[i + 3] = 255; }
        for (int y = 20; y < h - 20; y += 26)
            for (int x = 40; x < w - 80; x += 70)
                for (int yy = y; yy < y + 10; yy++)
                    for (int xx = x; xx < x + 50; xx++) { int i = (yy * w + xx) * 4; px[i] = 60; px[i + 1] = 60; px[i + 2] = 70; }
        // What the session sends: the whole image so far, at the preview width.
        var preview = CaptureSession.Downscale(px, w, h, 300);
        Brush Desktop() => theme == "dark"
            ? new LinearGradientBrush(Color.FromRgb(0x10, 0x16, 0x2C), Color.FromRgb(0x24, 0x10, 0x22), 30)
            : new LinearGradientBrush(Color.FromRgb(0xD6, 0xE0, 0xF6), Color.FromRgb(0xF4, 0xE4, 0xEC), 30);

        var panel = new CapturePanel("Alt+Shift+S");
        panel.SetTarget(TargetKind.ScrollArea);
        var panelContent = (FrameworkElement)panel.Content;
        panel.Content = null;
        var panelFrame = new Border { Child = panelContent, Background = Desktop() };
        Layout(panelFrame, 344, 660);
        panel.ShowUpdate(preview, w, h, 4, lang == "ar" ? "أُضيف 466 بكسل." : "Added 466 px.", addedRows: 466);
        panel.SetLive(lang == "ar" ? "تلقائي" : "Auto");
        CapturePanel.SetButton(panel.Auto, "lap-stop", lang == "ar" ? "إيقاف" : "Stop");
        Layout(panelFrame, 344, 660);
        Assert.True(panelContent.ActualHeight > 540, "the panel should lay out its preview and buttons");
        Assert.Equal(TargetKind.ScrollArea, panel.ShownTarget);
        Save(snapshots, panelFrame, 344, 660, $"longshot-panel-{theme}-{lang}");

        panel.SetLive(null);
        CapturePanel.SetButton(panel.Auto, "lap-play", lang == "ar" ? "تلقائي" : "Auto");
        panel.SetFinishReady(true);
        panel.ShowUpdate(preview, w, h, 9, lang == "ar" ? "وصلنا إلى النهاية. اضغط «تم» للحفظ." : "Reached the end. Press Done to save.");
        Layout(panelFrame, 344, 660);
        Assert.True(panel.FinishReady);
        Save(snapshots, panelFrame, 344, 660, $"longshot-panel-done-{theme}-{lang}");
        panel.Close();

        if (lang != "en") return;

        var frame = new CaptureFrame(new PixelRect(0, 0, 600, 400), new PixelPoint(330, 230), TargetKind.ScrollArea);
        var frameContent = (FrameworkElement)frame.Content;
        frame.Content = null;
        var frameStage = new Border { Child = frameContent, Background = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)) };
        Layout(frameStage, 644, 444);
        frame.FlashAdded(120);
        Layout(frameStage, 644, 444);
        Save(snapshots, frameStage, 644, 444, $"longshot-frame-{theme}");
        frame.Close();

        var highlight = new PickHighlight();
        highlight.SetLabel(new PixelRect(0, 0, 547, 848), TargetKind.ScrollArea);
        var highlightContent = (FrameworkElement)highlight.Content;
        highlight.Content = null;
        var highlightStage = new Border { Child = highlightContent, Background = new SolidColorBrush(Color.FromRgb(0x70, 0x74, 0x7C)) };
        Layout(highlightStage, 420, 260);
        Save(snapshots, highlightStage, 420, 260, $"longshot-pick-{theme}");
        highlight.Close();

        RenderLongshotScene(snapshots, theme, preview, w, h);

        var image = LongshotOutput.ToBitmap(w, h, px);
        var result = new LongshotResultWindow(image, CaptureSession.Downscale(px, w, h, 600),
            @"C:\Users\You\Pictures\Screenshots\Longshot 2026-11-08 143012.png", copied: true, newCapture: () => { }, fileToolboxOn: true);
        var resultContent = (FrameworkElement)result.Content;
        result.Content = null;
        var resultFrame = new Border { Child = resultContent };
        resultFrame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        Layout(resultFrame, 760, 700);
        Save(snapshots, resultFrame, 760, 700, $"longshot-result-{theme}");
        result.Close();
    }

    /// <summary>
    /// What the user sees with a chat app in front: the app (a sidebar, a top bar, a conversation),
    /// the dim layer with its hole, the frame on the conversation (marker, label) and the panel.
    /// </summary>
    private static void RenderLongshotScene(string? snapshots, string theme, BitmapSource preview, int w, int h)
    {
        const double W = 1360, H = 820;
        var scene = new Canvas { Width = W, Height = H, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x55, 0x8C)) };
        void Box(double x, double y, double bw, double bh, Color c, double r = 0) =>
            scene.Children.Add(new Border { Width = bw, Height = bh, Background = new SolidColorBrush(c), CornerRadius = new CornerRadius(r) }.With(b => { Canvas.SetLeft(b, x); Canvas.SetTop(b, y); }));
        bool dark = theme == "dark";
        Color bg = dark ? Color.FromRgb(0x1E, 0x1F, 0x22) : Colors.White, side = dark ? Color.FromRgb(0x2B, 0x2D, 0x31) : Color.FromRgb(0xF2, 0xF3, 0xF5), ink = dark ? Color.FromRgb(0xC8, 0xCA, 0xD0) : Color.FromRgb(0x4A, 0x4E, 0x58);
        Box(30, 24, 960, 772, bg, 8);                    // the app window
        Box(30, 24, 960, 40, side, 8);                   // its title bar
        Box(30, 64, 230, 732, side);                     // sidebar
        for (int i = 0; i < 16; i++) Box(54, 92 + i * 40, 120 + i * 7 % 60, 12, ink, 6);
        Box(260, 64, 730, 56, side);                     // conversation header
        Box(284, 84, 220, 14, ink, 7);
        var rnd = new Random(2);
        for (double y = 140; y < 700; y += 48)          // messages
        {
            bool mine = rnd.NextDouble() < 0.35;
            double mw = rnd.Next(180, 520);
            Box(mine ? 960 - mw : 290, y, mw, 34, mine ? Color.FromRgb(0x25, 0x63, 0xEB) : (dark ? Color.FromRgb(0x2F, 0x31, 0x36) : Color.FromRgb(0xEE, 0xF0, 0xF3)), 14);
        }
        Box(290, 724, 670, 50, side, 12);                // the message box (stays put)

        // The dim layer: everything but the conversation.
        var area = new Rect(260, 120, 730, 590);
        var dim = new System.Windows.Shapes.Path
        {
            Fill = new SolidColorBrush(Color.FromArgb(88, 0x0C, 0x0F, 0x14)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(0, 0, W, H)), new RectangleGeometry(area, 6, 6)),
        };
        scene.Children.Add(dim);

        var frame = new CaptureFrame(new PixelRect(0, 0, (int)area.Width, (int)area.Height), new PixelPoint(420, 300), TargetKind.ScrollArea);
        var frameContent = (FrameworkElement)frame.Content;
        frame.Content = null;
        frameContent.Width = area.Width + 44;
        frameContent.Height = area.Height + 44;
        Canvas.SetLeft(frameContent, area.X - 22);
        Canvas.SetTop(frameContent, area.Y - 22);
        scene.Children.Add(frameContent);

        var panel = new CapturePanel("Alt+Shift+S");
        panel.SetTarget(TargetKind.ScrollArea);
        var panelContent = (FrameworkElement)panel.Content;
        panel.Content = null;
        panelContent.Width = 344;
        panelContent.Height = 660;
        Canvas.SetLeft(panelContent, 1004);
        Canvas.SetTop(panelContent, 40);
        scene.Children.Add(panelContent);
        Layout(scene, W, H);
        panel.ShowUpdate(preview, w, h, 3, "Only this part scrolls, so Longshot captures just this part. Added 412 px.", addedRows: 412);
        frame.FlashAdded(412);
        Layout(scene, W, H);
        Save(snapshots, scene, W, H, $"longshot-scene-{theme}");
        frame.Close();
        panel.Close();
    }

    /// <summary>
    /// Pulse's dock (built but never shown: its content is laid out off-screen) with every tile, in
    /// each state Claude's tile can show, on each edge, and the cards that open beside the tiles.
    /// </summary>
    private static void RenderPulse(string? snapshots, string theme, string lang, ToolboxHost host)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = new PulseSettings();
        settings.Active.Tiles = [.. PulseTiles.All];
        foreach (var (edge, state) in new[] { ("right", ClaudeActivity.Working), ("left", ClaudeActivity.Waiting), ("top", ClaudeActivity.Done), ("bottom", ClaudeActivity.Idle) })
        {
            settings.Edge = edge;
            var dock = new DockWindow(() => settings, _ => { });
            dock.Rebuild();
            Assert.Equal(PulseTiles.All.Count + 1, dock.Tiles.Count); // every tile and the "+" tile
            dock.Tile(PulseTiles.Claude)!.SetUsage(64);
            dock.Tile(PulseTiles.Claude)!.SetLabel("64%");
            dock.Tile(PulseTiles.Claude)!.SetStatus(state, state == ClaudeActivity.Waiting ? 2 : 0);
            dock.Tile(PulseTiles.Codex)!.SetUsage(76);
            dock.Tile(PulseTiles.System)!.SetUsage(93);
            dock.Tile(PulseTiles.Toolbox)!.SetDropTarget(state == ClaudeActivity.Done);
            var content = (FrameworkElement)dock.Content;
            dock.Content = null;
            bool vertical = edge is "left" or "right";
            var stage = new Border { Child = content, Background = theme == "dark"
                ? new LinearGradientBrush(Color.FromRgb(0x14, 0x1B, 0x3C), Color.FromRgb(0x3C, 0x15, 0x32), 30)
                : new LinearGradientBrush(Color.FromRgb(0xC3, 0xD2, 0xF6), Color.FromRgb(0xF2, 0xD3, 0xE2), 30) };
            double w = vertical ? 420 : 1100, h = vertical ? 1000 : 420;
            Layout(stage, w, h);
            Assert.True(dock.Tile(PulseTiles.Claude)!.ActualWidth > 40, "the tiles should lay out");
            Save(snapshots, stage, w, h, $"pulse-dock-{edge}-{theme}-{lang}");
            dock.Close();
        }

        var cards = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16) };
        cards.Children.Add(PulseCards.Card(
            PulseCards.Head("lap-claude", "Claude"),
            PulseCards.Meter("Current session", new UsageWindow(64, now.AddMinutes(72)), now),
            PulseCards.Meter("This week", new UsageWindow(91, now.AddDays(3)), now),
            PulseCards.Status(PulseTileView.Amber, "Needs you · Amir_Lab"),
            PulseCards.Note("From Claude Code on this PC.")));
        cards.Children.Add(PulseCards.Card(PulseCards.Head("lap-codex", "Codex", "Plus plan"), PulseCards.Meter("5-hour limit", new UsageWindow(21, now.AddHours(3)), now), PulseCards.Meter("This week", null, now)).With(c => c.Margin = new Thickness(16, 0, 0, 0)));
        cards.Children.Add(PulseCards.Card(PulseCards.Head("lap-toolbox", "File Toolbox"), PulseCards.Body("Drop a file on this tile."), PulseCards.DropBox("Drop a file here")).With(c => c.Margin = new Thickness(16, 0, 0, 0)));
        var preview = new PulsePreview(220);
        preview.Update(settings);
        preview.Width = 360;
        preview.Margin = new Thickness(16, 0, 0, 0);
        cards.Children.Add(preview);
        var cardStage = new Border { Child = cards };
        cardStage.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        cardStage.FlowDirection = Lang.Flow;
        Layout(cardStage, 1300, 340);
        Save(snapshots, cardStage, 1300, 340, $"pulse-cards-{theme}-{lang}");

        Assert.Equal("Claude", PulseTool.ShortName(PulseTiles.Claude));
        Assert.NotNull(host);
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Save(string? folder, FrameworkElement element, double width, double height, string name)
    {
        if (string.IsNullOrEmpty(folder)) return;
        System.IO.Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png"));
        png.Save(file);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }
}
