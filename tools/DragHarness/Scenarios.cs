using System.Windows;
using System.Windows.Automation;

namespace DragHarness;

/// <summary>The real-drag scenarios. Each one restores input state and checks the source is untouched.</summary>
internal sealed class Scenarios
{
    private readonly AppDriver _app;
    private readonly ExplorerDriver _explorer;
    private readonly string _folder;
    private readonly bool _expectOutputs;

    public Scenarios(AppDriver app, ExplorerDriver explorer, string folder, bool expectOutputs)
    {
        _app = app;
        _explorer = explorer;
        _folder = folder;
        _expectOutputs = expectOutputs;
    }

    public IEnumerable<(string Name, Func<ScenarioResult> Run)> All(bool includeDesktop)
    {
        yield return ("pdf-to-jpg", () => DropOnSegment([Fixtures.PdfName], ringSwitches: 0, "pdf.render-jpg"));
        yield return ("image-tools-ring", () => DropOnSegment([Fixtures.ImageName], ringSwitches: 1, "tool.crop", expectEditor: true));
        yield return ("image-to-webp-arabic-name", () => DropOnSegment([Fixtures.ImageName], ringSwitches: 0, "image.to-webp"));
        yield return ("two-images-to-pdf", () => DropOnSegment([Fixtures.ImageName, Fixtures.SecondImageName], ringSwitches: 0, "image.to-pdf"));
        yield return ("escape-cancels", EscapeCancels);
        yield return ("leave-wheel-closes", LeaveWheelCloses);
        yield return ("no-wheel-without-file-drag", NoWheelWithoutFileDrag);
        if (includeDesktop) yield return ("desktop-source", DesktopSource);
    }

    // ---------------------------------------------------------------------------------------

    private ScenarioResult DropOnSegment(string[] names, int ringSwitches, string actionId, bool expectEditor = false)
    {
        var sources = names.Select(n => Path.Combine(_folder, n)).ToList();
        var before = Snapshot();
        int dropsBefore = _app.ReadDrops().Count;
        int jobsBefore = _app.ReadJobs().Count;

        var wheel = StartDragAndOpenWheel(names, out var note);
        if (wheel is null) return Fail($"Wheel did not appear. {note}");
        double latency = wheel.RevealLatencyMs;

        for (int i = 0; i < ringSwitches; i++)
        {
            int ring = wheel!.RingIndex;
            RealInput.Tap(RealInput.VK_LSHIFT);
            wheel = _app.WaitForWheel(w => w.Visible && w.RingIndex != ring, TimeSpan.FromSeconds(2));
            if (wheel is null) { Abort(); return Fail("Ring did not switch on the second Shift tap."); }
            Thread.Sleep(250);
            Snapshots.Capture($"ring {wheel.Ring}");
        }

        var segment = wheel!.Segments.FirstOrDefault(s => s.ActionId == actionId);
        if (segment is null) { Abort(); return Fail($"Segment '{actionId}' not on ring {wheel.Ring} ({string.Join(", ", wheel.Segments.Select(s => s.ActionId))})."); }
        if (!segment.Enabled) { Abort(); return Fail($"Segment '{actionId}' is disabled."); }

        RealInput.Glide(segment.X, segment.Y, 260, 16);
        Thread.Sleep(220);
        RealInput.LeftUp();

        var drop = WaitFor(() => _app.ReadDrops().Skip(dropsBefore).FirstOrDefault(), TimeSpan.FromSeconds(3));
        if (drop is null) return Fail("No drop was recorded by the app.");
        if (!drop.Accepted) return Fail($"Drop was not accepted (action {drop.ActionId}).");
        if (drop.ActionId != actionId) return Fail($"Dropped on '{drop.ActionId}', expected '{actionId}'.");
        if (drop.Effect != "Copy") return Fail($"Drop answered '{drop.Effect}', expected Copy.");
        if (!sources.OrderBy(s => s).SequenceEqual(drop.Paths.OrderBy(s => s), StringComparer.OrdinalIgnoreCase))
            return Fail($"Payload mismatch: got [{string.Join(" | ", drop.Paths)}].");

        string outputs = "";
        if (_expectOutputs && !expectEditor)
        {
            var job = WaitFor(() => _app.ReadJobs().Skip(jobsBefore).FirstOrDefault(), TimeSpan.FromSeconds(30));
            if (job is null) return Fail("No job result was recorded.");
            if (job.State != "Completed") return Fail($"Job ended as {job.State}: {job.Error}");
            var missing = job.Outputs.Where(o => !File.Exists(o)).ToList();
            if (job.Outputs.Count == 0 || missing.Count > 0) return Fail($"Outputs missing: {string.Join(", ", missing)}");
            outputs = $" Outputs: {string.Join(", ", job.Outputs.Select(Path.GetFileName))}.";
        }

        var integrity = CheckSourcesUnchanged(before, allowNewFiles: _expectOutputs);
        if (integrity is not null) return Fail(integrity);

        WaitFor(() => _app.ReadWheel() is { Visible: false } ? "" : null, TimeSpan.FromSeconds(2));
        return new ScenarioResult("", true, $"Dropped on {actionId} (Copy), payload exact, sources unchanged.{outputs} {note}".Trim(), latency);
    }

    private ScenarioResult EscapeCancels()
    {
        var before = Snapshot();
        int dropsBefore = _app.ReadDrops().Count;
        var wheel = StartDragAndOpenWheel([Fixtures.PdfName], out var note);
        if (wheel is null) return Fail($"Wheel did not appear. {note}");

        RealInput.Tap(RealInput.VK_ESCAPE);
        var hidden = _app.WaitForWheel(w => !w.Visible, TimeSpan.FromSeconds(2));
        RealInput.LeftUp();
        Thread.Sleep(400);

        if (hidden is null) return Fail("Wheel stayed open after Escape.");
        if (_app.ReadDrops().Count != dropsBefore) return Fail("A drop was recorded after Escape.");
        var integrity = CheckSourcesUnchanged(before, allowNewFiles: false);
        return integrity is null
            ? new ScenarioResult("", true, "Escape closed the wheel; no drop; folder unchanged.", wheel.RevealLatencyMs)
            : Fail(integrity);
    }

    private ScenarioResult LeaveWheelCloses()
    {
        var before = Snapshot();
        int dropsBefore = _app.ReadDrops().Count;
        var wheel = StartDragAndOpenWheel([Fixtures.ImageName], out var note);
        if (wheel is null) return Fail($"Wheel did not appear. {note}");

        // Move to a point clearly outside the wheel that is not a drop target (Explorer's status bar),
        // and wait past the grace period.
        var b = _explorer.Bounds;
        var candidates = new[]
        {
            new Point(b.Left + b.Width * 0.25, b.Bottom - 14),
            new Point(b.Right - 60, b.Bottom - 14),
            new Point(b.Left + 120, b.Top + 20),
        };
        var away = candidates.FirstOrDefault(p => Math.Abs(p.X - wheel.CenterX) + Math.Abs(p.Y - wheel.CenterY) > 420 &&
                                                  Math.Sqrt(Math.Pow(p.X - wheel.CenterX, 2) + Math.Pow(p.Y - wheel.CenterY, 2)) > 320);
        if (away == default) away = new Point(wheel.CenterX - 380, wheel.CenterY);
        RealInput.Glide(away.X, away.Y, 260, 14);
        var hidden = _app.WaitForWheel(w => !w.Visible, TimeSpan.FromSeconds(2));
        Snapshots.Capture("after-leaving-wheel");
        // Cancel the drag before releasing so nothing is dropped anywhere.
        RealInput.Tap(RealInput.VK_ESCAPE);
        RealInput.LeftUp();
        Thread.Sleep(500);

        if (hidden is null) return Fail("Wheel stayed open after the pointer left it.");
        if (_app.ReadDrops().Count != dropsBefore) return Fail("A drop was recorded outside the wheel.");
        var integrity = CheckSourcesUnchanged(before, allowNewFiles: false);
        return integrity is null
            ? new ScenarioResult("", true, "Leaving the wheel closed it; nothing dropped; folder unchanged.", wheel.RevealLatencyMs)
            : Fail(integrity);
    }

    private ScenarioResult NoWheelWithoutFileDrag()
    {
        // Rubber-band selection in empty space: mouse button down + Shift, but no OLE file drag.
        _explorer.BringToFront();
        var bounds = _explorer.Bounds;
        var start = new Point(bounds.Right - 120, bounds.Bottom - 110);
        RealInput.MoveTo(start.X, start.Y);
        Thread.Sleep(150);
        RealInput.LeftDown();
        RealInput.Glide(start.X - 60, start.Y - 30, 160, 8);
        RealInput.Tap(RealInput.VK_LSHIFT);
        var shown = _app.WaitForWheel(w => w.Visible, TimeSpan.FromSeconds(1.6));
        RealInput.LeftUp();
        Thread.Sleep(300);
        // Click empty space again to clear the selection the rubber band may have made.
        return shown is null
            ? new ScenarioResult("", true, "No wheel appeared for a non-file drag.", null)
            : Fail("The wheel appeared although no file was being dragged.");
    }

    private ScenarioResult DesktopSource()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var name = "FTB harness desktop test.png";
        var path = Path.Combine(desktop, name);
        File.Copy(Path.Combine(_folder, Fixtures.SecondImageName), path, overwrite: false);
        var hash = Fixtures.Sha256(path);
        try
        {
            RealInput.KeyDown(RealInput.VK_LWIN);
            RealInput.Tap(RealInput.VK_D);
            RealInput.KeyUp(RealInput.VK_LWIN);
            Thread.Sleep(1500);

            var list = AutomationElement.RootElement.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "SysListView32"),
                new PropertyCondition(AutomationElement.NameProperty, "Desktop")));
            if (list is null) return Fail("Desktop icon list not found.");
            AutomationElement? item = null;
            for (int i = 0; i < 20 && item is null; i++)
            {
                item = list.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, Path.GetFileNameWithoutExtension(name)))
                       ?? list.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, name));
                if (item is null) Thread.Sleep(200);
            }
            if (item is null) return Fail("Desktop item not found.");
            var rect = item.Current.BoundingRectangle;
            int dropsBefore = _app.ReadDrops().Count;

            var grab = new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 3);
            RealInput.MoveTo(grab.X, grab.Y);
            Thread.Sleep(200);
            RealInput.LeftDown();
            RealInput.Glide(grab.X + 40, grab.Y + 20, 150, 8);
            var screen = SystemParameters.WorkArea;
            RealInput.Glide(Math.Min(grab.X + 420, 1400), Math.Max(400, grab.Y), 300, 16);
            Thread.Sleep(150);
            RealInput.Tap(RealInput.VK_LSHIFT);
            var wheel = _app.WaitForWheel(w => w.Visible, TimeSpan.FromSeconds(2));
            if (wheel is null)
            {
                RealInput.Glide(RealInput.Cursor.X + 3, RealInput.Cursor.Y + 2, 60, 3);
                wheel = _app.WaitForWheel(w => w.Visible, TimeSpan.FromSeconds(2));
            }
            if (wheel is null) { Abort(); return Fail("Wheel did not appear for a desktop drag."); }
            var seg = wheel.Segments.First(s => s.ActionId == "image.to-jpg");
            RealInput.Glide(seg.X, seg.Y, 240, 14);
            Thread.Sleep(200);
            RealInput.LeftUp();
            var drop = WaitFor(() => _app.ReadDrops().Skip(dropsBefore).FirstOrDefault(), TimeSpan.FromSeconds(3));
            Thread.Sleep(500);

            if (drop is not { Accepted: true, Effect: "Copy" }) return Fail($"Desktop drop not accepted as Copy ({drop?.Effect}).");
            if (!File.Exists(path) || Fixtures.Sha256(path) != hash) return Fail("Desktop source was moved or changed.");
            return new ScenarioResult("", true, "Desktop drag delivered the real file; source unchanged.", wheel.RevealLatencyMs);
        }
        finally
        {
            RealInput.KeyDown(RealInput.VK_LWIN);
            RealInput.Tap(RealInput.VK_D);
            RealInput.KeyUp(RealInput.VK_LWIN);
            Thread.Sleep(600);
            foreach (var f in Directory.GetFiles(desktop, "FTB harness desktop test*")) File.Delete(f);
        }
    }

    // ---------------------------------------------------------------------------------------

    /// <summary>Selects the items, starts a real drag, moves to a free spot and taps Shift.</summary>
    private WheelState? StartDragAndOpenWheel(string[] names, out string note)
    {
        note = "";
        _explorer.BringToFront();
        var rects = names.Select(n => _explorer.FindItem(n, TimeSpan.FromSeconds(5))).ToList();

        // Select: click the first item, Ctrl+click the rest.
        var first = ExplorerDriver.GrabPoint(rects[0]);
        RealInput.MoveTo(first.X, first.Y);
        Thread.Sleep(120);
        RealInput.Click();
        Thread.Sleep(250);
        foreach (var rect in rects.Skip(1))
        {
            var p = ExplorerDriver.GrabPoint(rect);
            RealInput.MoveTo(p.X, p.Y);
            Thread.Sleep(100);
            RealInput.KeyDown(RealInput.VK_CONTROL);
            RealInput.Click();
            RealInput.KeyUp(RealInput.VK_CONTROL);
            Thread.Sleep(200);
        }

        var grab = ExplorerDriver.GrabPoint(rects.Last());
        RealInput.MoveTo(grab.X, grab.Y);
        Thread.Sleep(150);
        RealInput.LeftDown();
        Thread.Sleep(60);
        RealInput.Glide(grab.X + 24, grab.Y + 12, 120, 8);

        var target = DragTarget();
        RealInput.Glide(target.X, target.Y, 320, 18);
        Thread.Sleep(180);

        RealInput.Tap(RealInput.VK_LSHIFT);
        var wheel = _app.WaitForWheel(w => w.Visible, TimeSpan.FromSeconds(1.2));
        if (wheel is null)
        {
            Snapshots.Capture("no-wheel-after-shift");
            // Without the nudge working, the next real movement must reveal it.
            RealInput.Glide(target.X + 4, target.Y + 3, 60, 4);
            wheel = _app.WaitForWheel(w => w.Visible, TimeSpan.FromSeconds(1.5));
            note = wheel is null ? "" : "(needed a small pointer movement after Shift)";
        }
        if (wheel is null)
        {
            Snapshots.Capture("no-wheel-final");
            Abort();
        }
        else
        {
            Thread.Sleep(200); // let the reveal animation finish for the QA screenshot
            Snapshots.Capture($"wheel {string.Join("+", names.Select(Path.GetFileNameWithoutExtension))}");
        }
        return wheel;
    }

    /// <summary>A point with room for the wheel: right of centre of the Explorer window, inside the work area.</summary>
    private Point DragTarget()
    {
        var b = _explorer.Bounds;
        var work = SystemParameters.WorkArea; // DIPs of the primary monitor
        double scale = PrimaryScale();
        double x = Math.Clamp(b.Left + b.Width * 0.62, 260 * scale, work.Width * scale - 260 * scale);
        double y = Math.Clamp(b.Top + b.Height * 0.55, 260 * scale, work.Height * scale - 260 * scale);
        return new Point(x, y);
    }

    private static double PrimaryScale()
    {
        using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        return g.DpiX / 96.0;
    }

    private static void Abort()
    {
        RealInput.Tap(RealInput.VK_ESCAPE);
        RealInput.LeftUp();
        Thread.Sleep(300);
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(_folder).ToDictionary(f => f, Fixtures.Sha256, StringComparer.OrdinalIgnoreCase);

    private string? CheckSourcesUnchanged(Dictionary<string, string> before, bool allowNewFiles)
    {
        foreach (var (path, hash) in before)
        {
            if (!File.Exists(path)) return $"Source was moved or deleted: {Path.GetFileName(path)}";
            if (Fixtures.Sha256(path) != hash) return $"Source content changed: {Path.GetFileName(path)}";
        }
        if (!allowNewFiles)
        {
            var extra = Directory.GetFiles(_folder).Where(f => !before.ContainsKey(f)).ToList();
            if (extra.Count > 0) return $"Unexpected new files: {string.Join(", ", extra.Select(Path.GetFileName))}";
        }
        return null;
    }

    private static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var value = probe();
            if (value is not null) return value;
            Thread.Sleep(50);
        }
        return null;
    }

    private static ScenarioResult Fail(string detail) => new("", false, detail, null);
}
