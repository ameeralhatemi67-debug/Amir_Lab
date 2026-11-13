using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Toolbox.Processing.Capture;
using Toolbox.Windows.Longshot;
using Xunit.Abstractions;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Longshot on the real screen, with the real wheel, in a real browser. Off unless
/// AMIRLAP_LIVE_LONGSHOT=1, because it opens Microsoft Edge (in a throwaway profile, on pages
/// written to a temp folder) and moves the mouse pointer for a moment at each step; keep hands off
/// the mouse while it runs. Every row on the test pages carries a barcode of its number, so the
/// stitched result can be read back: each row must appear exactly once, in order.
/// <code>set AMIRLAP_LIVE_LONGSHOT=1 &amp;&amp; dotnet test tests/Toolbox.Windows.Tests --filter LiveLongshotTests</code>
/// Set AMIRLAP_SNAPSHOT_DIR to keep the captures as PNGs.
/// </summary>
[Collection(AppLanguageCollection.Name)]
public sealed class LiveLongshotTests(ITestOutputHelper output)
{
    private const int Rows = 120;

    private static bool On
    {
        get
        {
            if (Environment.GetEnvironmentVariable("AMIRLAP_LIVE_LONGSHOT") != "1") return false;
            // Amir Lap is per-monitor DPI aware (its manifest says so); the test host isn't, and Windows
            // would scale its pointer moves and screen reads. Run these tests on their own.
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2 (fails harmlessly if already set)
            Assert.True(AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), new IntPtr(-4)),
                "The test process isn't per-monitor DPI aware: run the live tests on their own (--filter LiveLongshotTests).");
            return true;
        }
    }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr a, IntPtr b);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Interop.Native.RECT value, int size);

    // ---- pages -----------------------------------------------------------------------------------

    private const string Style = """
        <style>
        html,body{margin:0;font:15px "Segoe UI",sans-serif;background:#f4f5f7;color:#222}
        .row{height:32px;display:flex;align-items:center;padding:0 12px}
        .g{width:6px;height:12px;background:#ff0000;margin-right:8px;flex:none}
        .blue .g{background:#0000ff}.green .g{background:#00c000}
        .b{width:10px;height:12px;flex:none}
        .t{margin-left:12px;white-space:nowrap;overflow:hidden}
        .top{position:fixed;left:0;right:0;top:0;height:52px;background:#2a3f8f;color:#fff;display:flex;align-items:center;padding:0 18px;z-index:2}
        .side{position:fixed;left:0;top:52px;bottom:0;width:230px;background:#eceef2;overflow:hidden}
        .side p{margin:16px 18px;height:12px;background:#8e95a3;border-radius:6px}
        </style>
        """;

    private const string Script = """
        <script>
        function fill(box, rows){
          const words = ["the","quick","brown","fox","jumps","over","a","lazy","dog","today"];
          for (let i = 0; i < (rows || ROWS); i++) {
            const row = document.createElement("div"); row.className = "row";
            const g = document.createElement("span"); g.className = "g"; row.appendChild(g);
            for (let b = 9; b >= 0; b--) { const s = document.createElement("span"); s.className = "b"; s.style.background = (i >> b) & 1 ? "#000" : "#d0d0d0"; row.appendChild(s); }
            const t = document.createElement("span"); t.className = "t";
            let text = "Message " + String(i).padStart(3, "0") + " ";
            for (let k = 0; k < 3 + (i * 7) % 9; k++) text += words[(i * 3 + k) % words.length] + " ";
            t.textContent = text; row.appendChild(t);
            box.appendChild(row);
          }
        }
        </script>
        """;

    /// <summary>The owner's case: the page itself can't scroll, the chat in it can.</summary>
    private static string ChatPage() => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Longshot live: chat</title>{{Style}}
        <style>html,body{height:100%;overflow:hidden}
        #chat{position:absolute;left:246px;top:68px;right:18px;bottom:92px;overflow-y:auto;background:#fff;border:1px solid #c4c8d0;border-radius:8px}
        .input{position:fixed;left:246px;right:18px;bottom:18px;height:56px;background:#e3e6eb;border-radius:12px}</style></head>
        <body><div class="top">Chat app: the page stays put, the conversation scrolls</div>
        <div class="side">{{string.Concat(Enumerable.Repeat("<p></p>", 18))}}</div>
        <div id="chat"></div><div class="input"></div>
        {{Script.Replace("ROWS", Rows.ToString())}}<script>fill(document.getElementById("chat"))</script></body></html>
        """;

    /// <summary>A documentation site: the page scrolls under a fixed top bar and beside a fixed sidebar.</summary>
    private static string DocsPage() => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Longshot live: docs</title>{{Style}}
        <style>#doc{margin:52px 0 0 230px;padding:16px 24px;background:#fff;min-height:100vh}</style></head>
        <body><div class="top">Docs site: fixed top bar and sidebar</div>
        <div class="side">{{string.Concat(Enumerable.Repeat("<p></p>", 18))}}</div>
        <div id="doc"></div>
        {{Script.Replace("ROWS", Rows.ToString())}}<script>fill(document.getElementById("doc"))</script></body></html>
        """;

    private const int PageRows = 70;

    /// <summary>
    /// The owner's page (the Salla order editor, 13 Nov 2026): the page scrolls, and two sections in
    /// it scroll on their own (A: red rows, B: green rows); below them, the page's own rows (blue).
    /// </summary>
    private static string NestedPage() => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Longshot live: nested</title>{{Style}}
        <style>body{padding:0 18px}.head{height:70px;background:#2a3f8f;color:#fff;display:flex;align-items:center;padding:0 16px;margin-bottom:12px}
        .cols{display:flex;gap:18px}#a,#b{height:300px;overflow-y:auto;background:#fff;border:1px solid #c4c8d0;border-radius:8px}#a{flex:3}#b{flex:2}
        #page{margin-top:14px;background:#fff}</style></head>
        <body><div class="head">Order editor: the page scrolls, and so do the two lists</div>
        <div class="cols"><div id="a"></div><div id="b" class="green"></div></div><div id="page" class="blue"></div>
        {{Script.Replace("ROWS", Rows.ToString())}}<script>fill(document.getElementById("a")); fill(document.getElementById("b"), 90); fill(document.getElementById("page"), {{PageRows}})</script></body></html>
        """;

    /// <summary>A plain long page: everything scrolls (what worked before must still work).</summary>
    private static string PlainPage() => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><title>Longshot live: plain</title>{{Style}}
        <style>#doc{padding:16px 24px;background:#fff}</style></head>
        <body><div id="doc"></div>
        {{Script.Replace("ROWS", Rows.ToString())}}<script>fill(document.getElementById("doc"))</script></body></html>
        """;

    // ---- browser -----------------------------------------------------------------------------------

    private sealed class Browser : IDisposable
    {
        private readonly Process _process;
        private readonly string _profile;

        public Browser(string file, string profile)
        {
            _profile = profile;
            string edge = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            }.First(System.IO.File.Exists);
            _process = Process.Start(new ProcessStartInfo(edge,
                // InPrivate: Edge would otherwise sign the throwaway profile in with the Windows account and
                // cover the page with a "now syncing" prompt.
                $"--inprivate --app=\"{new Uri(file).AbsoluteUri}\" --user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check --disable-sync --disable-features=msEdgeSidebarV2,Translate,msImplicitSignin --window-position=60,40 --window-size=1000,640")
            { UseShellExecute = false })!;
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (DateTime.UtcNow < deadline)
            {
                _process.Refresh();
                Hwnd = FindWindow(_process.Id);
                if (Hwnd != IntPtr.Zero) break;
                Thread.Sleep(200);
            }
            if (Hwnd == IntPtr.Zero) throw new InvalidOperationException("Edge's window didn't appear.");
            Thread.Sleep(2500); // let the page load and paint
            ShowWindow(Hwnd, 9);
            SetForegroundWindow(Hwnd);
            // On top of everything for the test (Windows may refuse to bring it to the front otherwise).
            TopmostSet = SetWindowPos(Hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
            TopmostError = Marshal.GetLastWin32Error();
            Thread.Sleep(300);
            // Newer Edge shows the app in another top-level window than the one with the title: use
            // the Edge window that is actually at the middle of it.
            var middle = Rect.Center;
            var shown = GetAncestorRaw(WindowFromPointRaw(new Interop.Native.POINT { X = middle.X, Y = middle.Y }), 2);
            GetWindowThreadProcessId(shown, out uint shownPid);
            if (shown != IntPtr.Zero && shown != Hwnd && Process.GetProcessById((int)shownPid).ProcessName == "msedge")
            {
                Hwnd = shown;
                SetWindowPos(Hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
                Thread.Sleep(200);
            }
            Thread.Sleep(500);
        }

        public IntPtr Hwnd { get; private set; }

        public bool TopmostSet { get; }
        public int TopmostError { get; }

        /// <summary>The window as Longshot sees it: its visible frame, kept inside the work area.</summary>
        public PixelRect Rect
        {
            get
            {
                DwmGetWindowAttribute(Hwnd, 9, out var r, Marshal.SizeOf<Interop.Native.RECT>());
                var raw = PixelRect.From(r);
                var work = TargetFinder.WorkAreaAt(raw.Center);
                return raw.Intersect(work);
            }
        }

        /// <summary>
        /// Stops the test unless this window is the one at every point the wheel may turn: if another
        /// window covers it (Edge didn't come to the front), the wheel would scroll that window instead.
        /// </summary>
        public void MustBeUnder(params PixelPoint[] points)
        {
            foreach (var p in points)
            {
                var at = TargetFinder.WindowAt(p);
                if (at is not { } w || w.Hwnd != Hwnd)
                {
                    var raw = WindowFromPointRaw(new Interop.Native.POINT { X = p.X, Y = p.Y });
                    var title = new System.Text.StringBuilder(256);
                    GetWindowText(GetAncestorRaw(raw, 2), title, 256);
                    GetWindowThreadProcessId(GetAncestorRaw(raw, 2), out uint pid);
                    string who = $"'{title}' (process {Process.GetProcessById((int)pid).ProcessName}), usable: {at?.Hwnd.ToString() ?? "no"}";
                    throw new Xunit.Sdk.XunitException($"Edge ({Hwnd}) isn't the window at {p}: {who} is. Bring nothing to the front while the live tests run.");
                }
            }
        }

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr param);
        [DllImport("user32.dll", EntryPoint = "WindowFromPoint")] private static extern IntPtr WindowFromPointRaw(Interop.Native.POINT p);
        [DllImport("user32.dll", EntryPoint = "GetAncestor")] private static extern IntPtr GetAncestorRaw(IntPtr h, uint f);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);
        private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

        /// <summary>The visible window titled "Longshot live…" (Edge may hand the page to another of its processes).</summary>
        private static IntPtr FindWindow(int _)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var sb = new System.Text.StringBuilder(256);
                GetWindowText(h, sb, 256);
                if (sb.ToString().StartsWith("Longshot live", StringComparison.Ordinal)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        public void Dispose()
        {
            try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            Thread.Sleep(800);
            try { System.IO.Directory.Delete(_profile, recursive: true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- capture and check -------------------------------------------------------------------------

    private SessionFinished Capture(PixelRect area, PixelPoint anchor, bool keepRows, List<SessionEvent> seen, IAreaScroller? byItself = null)
    {
        var events = new BlockingCollection<SessionEvent>();
        using var session = new CaptureSession(area, a => new GdiScreenSource(a), new WheelScroller(), 0.55, 300, e => { lock (seen) seen.Add(e); events.Add(e); })
        {
            Anchor = anchor,
            KeepRows = keepRows,
            RewindToTop = true, // start from the top, whatever the page was left at
            EscapePressed = () => false,
            AreaScroller = byItself,
            WholeAreaOnly = byItself is not null,
        };
        session.Start();
        T Next<T>(Func<T, bool>? where = null) where T : SessionEvent
        {
            var deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline)
            {
                if (!events.TryTake(out var e, TimeSpan.FromSeconds(1))) continue;
                if (e is SessionFailed f) throw new Xunit.Sdk.XunitException("Session failed: " + f.Message);
                if (e is SessionUpdate u && u.Message is { } m) output.WriteLine("  " + m);
                if (e is T t && (where is null || where(t))) return t;
            }
            throw new TimeoutException($"No {typeof(T).Name}.");
        }
        Next<SessionReady>();
        session.StartMode(SessionCommand.Auto);
        Next<SessionBusy>(b => b.Command == SessionCommand.Auto);
        Next<SessionBusy>(b => b.Command is null);
        session.Enqueue(SessionCommand.Finish);
        return Next<SessionFinished>();
    }

    /// <summary>Reads the row numbers off the barcodes, top to bottom.</summary>
    internal static List<int> Decode(byte[] px, int w, int h, char guard = 'r')
    {
        bool Red(int x, int y)
        {
            int i = (y * w + x) * 4;
            return guard switch
            {
                'b' => px[i] > 200 && px[i + 1] < 60 && px[i + 2] < 60,
                'g' => px[i + 1] > 150 && px[i] < 60 && px[i + 2] < 60,
                _ => px[i + 2] > 200 && px[i + 1] < 60 && px[i] < 60,
            };
        }
        bool White(int x, int y) { int i = (y * w + x) * 4; return px[i] > 235 && px[i + 1] > 235 && px[i + 2] > 235; }
        var found = new List<int>();
        for (int y = 0; y < h; y++)
        {
            int gx = -1;
            for (int x = 0; x < Math.Min(w, guard == 'r' ? 200 : w); x++) if (Red(x, y)) { gx = x; break; }
            if (gx < 0) continue;
            int gw = 0;
            while (gx + gw < w && Red(gx + gw, y)) gw++;
            int cx = gx + gw / 2, gh = 0;
            while (y + gh < h && Red(cx, y + gh)) gh++;       // measured down the guard's middle
            int cy = y + gh / 2;
            // The ten bits: the strip of grey and black after the guard, measured where it is.
            int bs = gx + gw;
            while (bs < w && White(bs, cy)) bs++;
            int be = bs, run = 0;
            while (be < w && run < 5) { run = White(be, cy) ? run + 1 : 0; be++; }
            be -= run;
            double bit = (be - bs) / 10.0;
            int value = 0;
            for (int k = 0; k < 10; k++)
            {
                int i = (cy * w + (int)(bs + (k + 0.5) * bit)) * 4;
                value = value * 2 + (px[i] + px[i + 1] + px[i + 2] < 3 * 100 ? 1 : 0);
            }
            if (gh >= 6 && bit >= 6) found.Add(value); // a guard cut to a sliver at an edge can't be read
            y += Math.Max(0, gh - 1);
        }
        return found;
    }

    private void Check(SessionFinished done, string name, int first = 0, char guard = 'r', int rows = Rows)
    {
        var snapshots = Environment.GetEnvironmentVariable("AMIRLAP_SNAPSHOT_DIR");
        if (snapshots is not null)
        {
            System.IO.Directory.CreateDirectory(snapshots);
            var bitmap = LongshotOutput.ToBitmap(done.Width, done.Height, done.Pixels);
            var file = System.IO.Path.Combine(snapshots, $"live-{name}.png");
            System.IO.File.Delete(file);
            LongshotOutput.Save(bitmap, file);
        }
        var read = Decode(done.Pixels, done.Width, done.Height, guard);
        output.WriteLine($"{name}: {done.Width}x{done.Height}, rows read: {read.Count} ({(read.Count > 0 ? $"{read[0]}..{read[^1]}" : "none")})");
        var expected = Enumerable.Range(first, rows - first).ToList();
        Assert.True(read.SequenceEqual(expected), $"{name}: rows read {string.Join(",", read)}");
    }

    private static PixelPoint Point(PixelRect r, double fx, double fy) => new(r.X + (int)(r.Width * fx), r.Y + (int)(r.Height * fy));

    [Fact]
    public async Task A_chat_in_a_page_that_cannot_scroll_is_captured_whole()
    {
        if (!On) return;
        var dir = System.IO.Directory.CreateTempSubdirectory("amirlap-live-");
        var page = System.IO.Path.Combine(dir.FullName, "chat.html");
        System.IO.File.WriteAllText(page, ChatPage());
        using var edge = new Browser(page, System.IO.Path.Combine(dir.FullName, "profile"));
        var window = edge.Rect;
        output.WriteLine($"Edge window {window}");
        var overChat = Point(window, 0.55, 0.45);
        // Every spot the wheel may turn at (where the pointer is, and the spots tried when nothing moves there).
        edge.MustBeUnder(overChat, Point(window, 0.08, 0.5), window.Center, Point(window, 0.25, 0.5), Point(window, 0.75, 0.5), Point(window, 0.5, 0.75));

        // 1. What UI Automation says is under the pointer.
        var scrollArea = await TargetFinder.ScrollAreaAtAsync(overChat, TimeSpan.FromSeconds(4));
        output.WriteLine($"UI Automation scroll area at {overChat}: {scrollArea?.ToString() ?? "none"}");

        // 2. The whole window, the wheel where the pointer was (over the chat): it must narrow to the chat.
        var seen = new List<SessionEvent>();
        var done = Capture(window, overChat, keepRows: false, seen);
        output.WriteLine("narrowed to: " + string.Join(", ", seen.OfType<SessionAreaChanged>().Select(a => a.Area.ToString())));
        Check(done, "chat-window");

        // 3. The window, but the wheel starts over the still sidebar: other spots are tried.
        await BackToTop(overChat);
        seen.Clear();
        done = Capture(window, Point(window, 0.08, 0.5), keepRows: false, seen);
        output.WriteLine("scroll point moved to: " + string.Join(", ", seen.OfType<SessionAnchorMoved>().Select(a => a.At.ToString())));
        Check(done, "chat-probe");

        // 4. The scroll area UI Automation found (its own scrollbar is inside it).
        Assert.NotNull(scrollArea);
        await BackToTop(overChat);
        seen.Clear();
        done = Capture(scrollArea!.Value, overChat, keepRows: true, seen);
        output.WriteLine("narrowed to: " + string.Join(", ", seen.OfType<SessionAreaChanged>().Select(a => a.Area.ToString())));
        Check(done, "chat-scrollarea");
    }

    /// <summary>The last capture left the chat at its end: back to the top (as a user would scroll).</summary>
    private static async Task BackToTop(PixelPoint at)
    {
        for (int i = 0; i < 4; i++)
        {
            new WheelScroller().Scroll(at, 15, down: false, 120);
            await Task.Delay(250);
        }
        await Task.Delay(600);
    }

    [Fact]
    public void A_docs_page_beside_a_fixed_sidebar_is_captured_whole()
    {
        if (!On) return;
        var dir = System.IO.Directory.CreateTempSubdirectory("amirlap-live-");
        var page = System.IO.Path.Combine(dir.FullName, "docs.html");
        System.IO.File.WriteAllText(page, DocsPage());
        using var edge = new Browser(page, System.IO.Path.Combine(dir.FullName, "profile"));
        var window = edge.Rect;
        edge.MustBeUnder(Point(window, 0.6, 0.5), window.Center);
        var seen = new List<SessionEvent>();
        var done = Capture(window, Point(window, 0.6, 0.5), keepRows: false, seen);
        output.WriteLine("narrowed to: " + string.Join(", ", seen.OfType<SessionAreaChanged>().Select(a => a.Area.ToString())));
        Check(done, "docs-window");
    }

    [Fact]
    public void A_plain_long_page_still_works()
    {
        if (!On) return;
        var dir = System.IO.Directory.CreateTempSubdirectory("amirlap-live-");
        var page = System.IO.Path.Combine(dir.FullName, "plain.html");
        System.IO.File.WriteAllText(page, PlainPage());
        using var edge = new Browser(page, System.IO.Path.Combine(dir.FullName, "profile"));
        var window = edge.Rect;
        edge.MustBeUnder(Point(window, 0.5, 0.5));
        var seen = new List<SessionEvent>();
        var done = Capture(window, Point(window, 0.5, 0.5), keepRows: false, seen);
        output.WriteLine("narrowed to: " + string.Join(", ", seen.OfType<SessionAreaChanged>().Select(a => a.Area.ToString())));
        Check(done, "plain-window");
    }

    /// <summary>
    /// The owner's case, live in Edge: with the scroll point over section A, just A is captured,
    /// scrolled by itself (UI Automation, no pointer, no wheel), and the page doesn't move; over the
    /// page's own rows, the page is captured and A stays where it was.
    /// </summary>
    [Fact]
    public async Task Sections_in_a_page_that_scrolls_are_captured_on_their_own_and_the_page_whole()
    {
        if (!On) return;
        var dir = System.IO.Directory.CreateTempSubdirectory("amirlap-live-");
        var file = System.IO.Path.Combine(dir.FullName, "nested.html");
        System.IO.File.WriteAllText(file, NestedPage());
        using var edge = new Browser(file, System.IO.Path.Combine(dir.FullName, "profile"));
        var window = edge.Rect;
        output.WriteLine($"Edge window {edge.Hwnd} at {window}, topmost set: {edge.TopmostSet} (error {edge.TopmostError})");
        var overA = Point(window, 0.25, 0.42);
        var overPage = Point(window, 0.5, 0.88);
        edge.MustBeUnder(overA, overPage);

        // 1. What scrolls where: A under A, the page further down.
        var a = await ScrollTargets.AtAsync(overA, TimeSpan.FromSeconds(4));
        var page = await ScrollTargets.AtAsync(overPage, TimeSpan.FromSeconds(4));
        output.WriteLine($"over A: {a?.Name} {a?.Area} page={a?.IsPage}; over the page: {page?.Name} {page?.Area} page={page?.IsPage}");
        Assert.NotNull(a);
        Assert.NotNull(page);
        Assert.False(a!.IsPage);
        Assert.True(page!.IsPage);
        Assert.NotNull(a.Scroller);

        // 2. Section A, scrolled by itself: every red row once, and the page left where it was.
        double? pageBefore = page.Scroller!.Percent;
        var pointerBefore = ScreenGeometryPointer();
        var seen = new List<SessionEvent>();
        var done = Capture(a.Area, overA, keepRows: true, seen, a.Scroller);
        Check(done, "nested-section-a", guard: 'r');
        Assert.Equal(pageBefore, page.Scroller.Percent);
        Assert.Equal(pointerBefore, ScreenGeometryPointer()); // the pointer was never borrowed
        // At most its own scrollbar was trimmed off the side; it never moved to another part.
        Assert.All(seen.OfType<SessionAreaChanged>(), c => Assert.True(c.Area.Width >= a.Area.Width - 48 && c.Area.X >= a.Area.X && c.Area.Height >= a.Area.Height * 0.6, $"narrowed to {c.Area}"));

        // 3. The page, scrolled by itself: every blue row once; A stays at its end.
        double? aBefore = a.Scroller.Percent;
        var whole = await ScrollTargets.AtAsync(overPage, TimeSpan.FromSeconds(4));
        seen.Clear();
        done = Capture(whole!.Area, overPage, keepRows: true, seen, whole.Scroller);
        Check(done, "nested-page", guard: 'b', rows: PageRows);
        Assert.Equal(aBefore, a.Scroller.Percent);
    }

    private static PixelPoint ScreenGeometryPointer() => PixelPoint.From(Interop.ScreenGeometry.CursorPosition());
}
