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
        yield return ("multipage-pdf-asks-for-pages", MultipagePdfAsksForPages);
        if (File.Exists(Path.Combine(_folder, Fixtures.HeicName)))
            yield return ("heic-to-jpg-via-windows-codec", () => DropOnSegment([Fixtures.HeicName], ringSwitches: 0, "image.to-jpg"));
        yield return ("studio-crop-background-save", StudioCropBackgroundSave);
        yield return ("palette-from-second-launch", PaletteFromSecondLaunch);
        yield return ("recipe-ring-website-images", () => DropOnSegment([Fixtures.SecondImageName], ringSwitches: 2, "recipe:builtin-web"));
        yield return ("escape-cancels", EscapeCancels);
        yield return ("leave-wheel-closes", LeaveWheelCloses);
        yield return ("no-wheel-without-file-drag", NoWheelWithoutFileDrag);
        if (includeDesktop) yield return ("desktop-source", DesktopSource);
        if (IncludeQa) yield return ("qa-windows", QaWindows);
    }

    /// <summary>Visual QA only (--qa): opens the palette, Recipes and Settings windows and screenshots them.</summary>
    public static bool IncludeQa { get; set; }

    private ScenarioResult QaWindows()
    {
        var launch = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_app.ExePath) { UseShellExecute = false });
        launch?.WaitForExit(8000);
        var palette = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, _app.ProcessId),
            new PropertyCondition(AutomationElement.NameProperty, "File Toolbox"))), TimeSpan.FromSeconds(6));
        if (palette is null) return Fail("Palette did not open.");
        Thread.Sleep(800);
        Snapshots.Capture("qa-palette-empty");
        foreach (var (button, title, tab) in new[] { ("Recipes", "File Toolbox recipes", "Brand styles"), ("Settings", "File Toolbox settings", (string?)null) })
        {
            if (!Select(palette, button)) return Fail($"{button} button not found.");
            var window = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, title)), TimeSpan.FromSeconds(5));
            if (window is null) return Fail($"{title} did not open.");
            Thread.Sleep(1200);
            Snapshots.Capture("qa-" + button);
            if (tab is not null && Select(window, tab))
            {
                Thread.Sleep(800);
                var create = window.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.NameProperty, "New style")));
                if (create is null) return Fail("\"New style\" button not found in the Brand styles tab.");
                var r = create.Current.BoundingRectangle;
                RealInput.MoveTo(r.Left + r.Width / 2, r.Top + r.Height / 2);
                Thread.Sleep(100);
                RealInput.Click();
                Thread.Sleep(2000);
                Snapshots.Capture("qa-" + button + "-brand");
            }
        }
        return new ScenarioResult("", true, "Screenshots captured for visual review.", null);
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

        if (expectEditor)
        {
            // The editor opens; close it so it does not cover Explorer for later scenarios.
            var editor = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, _app.ProcessId),
                new PropertyCondition(AutomationElement.NameProperty, "Crop Image"))), TimeSpan.FromSeconds(6));
            if (editor is null) return Fail("The editor did not open.");
            Thread.Sleep(1200);
            Snapshots.Capture("editor-opened");
            CloseOurWindows();
        }

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
        Thread.Sleep(350);
        Snapshots.Capture($"after-drop {actionId}");
        return new ScenarioResult("", true, $"Dropped on {actionId} (Copy), payload exact, sources unchanged.{outputs} {note}".Trim(), latency);
    }

    private ScenarioResult MultipagePdfAsksForPages()
    {
        const string name = "Handbook 3 pages.pdf";
        var before = Snapshot();
        int jobsBefore = _app.ReadJobs().Count;
        var wheel = StartDragAndOpenWheel([name], out var note);
        if (wheel is null) return Fail($"Wheel did not appear. {note}");
        var segment = wheel.Segments.First(s => s.ActionId == "pdf.render-jpg");
        RealInput.Glide(segment.X, segment.Y, 240, 14);
        Thread.Sleep(200);
        RealInput.LeftUp();

        // The page picker must appear instead of silently exporting only page 1.
        var picker = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.NameProperty, "Render pages as JPG")), TimeSpan.FromSeconds(4));
        if (picker is null) return Fail("No page picker appeared for a 3-page PDF.");
        Thread.Sleep(600);
        Snapshots.Capture("page-picker");
        // Confirm with Enter (Export is the default button; "All pages" is preselected).
        var hwnd = new IntPtr(picker.Current.NativeWindowHandle);
        RealInput.SetForegroundWindow(hwnd);
        Thread.Sleep(150);
        RealInput.Tap(0x0D);

        var job = WaitFor(() => _app.ReadJobs().Skip(jobsBefore).FirstOrDefault(), TimeSpan.FromSeconds(30));
        if (job is null) return Fail("No job ran after confirming the picker.");
        if (job.State != "Completed" || job.Outputs.Count != 3)
            return Fail($"Expected 3 page images, got {job.Outputs.Count} ({job.State} {job.Error}).");
        var integrity = CheckSourcesUnchanged(before, allowNewFiles: true);
        Thread.Sleep(300);
        Snapshots.Capture("after-picker-job");
        return integrity is null
            ? new ScenarioResult("", true, $"Picker appeared; all 3 pages exported: {string.Join(", ", job.Outputs.Select(Path.GetFileName))}.", wheel.RevealLatencyMs)
            : Fail(integrity);
    }

    private ScenarioResult StudioCropBackgroundSave()
    {
        var before = Snapshot();
        int jobsBefore = _app.ReadJobs().Count;
        var wheel = StartDragAndOpenWheel([Fixtures.ImageName], out var note);
        if (wheel is null) return Fail($"Wheel did not appear. {note}");
        RealInput.Tap(RealInput.VK_LSHIFT);
        wheel = _app.WaitForWheel(w => w.Visible && w.Ring == "Tools", TimeSpan.FromSeconds(2));
        if (wheel is null) { Abort(); return Fail("Tools ring did not appear."); }
        var crop = wheel.Segments.First(s => s.ActionId == "tool.crop");
        RealInput.Glide(crop.X, crop.Y, 240, 14);
        Thread.Sleep(200);
        RealInput.LeftUp();

        var studio = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.NameProperty, "Crop Image")), TimeSpan.FromSeconds(6));
        if (studio is null) return Fail("The Crop Image editor did not open.");
        Thread.Sleep(1800);
        Snapshots.Capture("studio-crop");

        if (!Select(studio, "1:1")) return Fail("Aspect 1:1 not found.");
        Thread.Sleep(900);
        Snapshots.Capture("studio-crop-1x1");
        if (!Select(studio, "Background")) return Fail("Background step not found.");
        Thread.Sleep(1600);
        Snapshots.Capture("studio-background");
        if (!Select(studio, "Annotate")) return Fail("Annotate step not found.");
        Thread.Sleep(900);
        // Draw an arrow across the picture with the real mouse.
        var bounds = studio.Current.BoundingRectangle;
        RealInput.MoveTo(bounds.Left + bounds.Width * 0.25, bounds.Top + bounds.Height * 0.35);
        Thread.Sleep(100);
        RealInput.LeftDown();
        RealInput.Glide(bounds.Left + bounds.Width * 0.45, bounds.Top + bounds.Height * 0.55, 250, 12);
        RealInput.LeftUp();
        Thread.Sleep(1200);
        Snapshots.Capture("studio-annotate");
        if (!Select(studio, "Export")) return Fail("Export step not found.");
        Thread.Sleep(1200);
        Snapshots.Capture("studio-export");

        var save = studio.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, "Save")));
        if (save is null || !save.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return Fail("Save button not found.");
        ((InvokePattern)invoke).Invoke();

        var job = WaitFor(() => _app.ReadJobs().Skip(jobsBefore).FirstOrDefault(), TimeSpan.FromSeconds(40));
        if (job is null) return Fail("No export job ran.");
        if (job.State != "Completed" || job.Outputs.Count != 1) return Fail($"Export ended as {job.State}: {job.Error}");
        var name = Path.GetFileName(job.Outputs[0]);
        if (name != "صورة اختبار Cropped Annotated With Background.png") return Fail($"Unexpected output name {name}.");
        using var img = NetVips.Image.NewFromFile(job.Outputs[0]);
        // 1200×800 source → 1:1 crop of 800×800 → +120 px padding on each side.
        if (img.Width != 1040 || img.Height != 1040) return Fail($"Unexpected size {img.Width}×{img.Height}.");
        Thread.Sleep(400);
        Snapshots.Capture("studio-saved");
        var integrity = CheckSourcesUnchanged(before, allowNewFiles: true);
        return integrity is null
            ? new ScenarioResult("", true, $"Studio: 1:1 crop + arrow + background saved as \"{name}\" (1040×1040).", wheel.RevealLatencyMs)
            : Fail(integrity);
    }

    /// <summary>
    /// Keyboard path: a second launch with --open (what Explorer's menu and "Send to" do) hands the file to the
    /// running app, which opens the palette; typing "webp" and Enter runs the conversion.
    /// </summary>
    private ScenarioResult PaletteFromSecondLaunch()
    {
        var before = Snapshot();
        int jobsBefore = _app.ReadJobs().Count;
        var source = Path.Combine(_folder, Fixtures.SecondImageName);
        var second = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_app.ExePath, $"--open \"{source}\"") { UseShellExecute = false });
        second?.WaitForExit(8000);
        if (second is { HasExited: false }) return Fail("The second launch did not exit (single-instance hand-off failed).");

        var palette = WaitFor(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, _app.ProcessId),
            new PropertyCondition(AutomationElement.NameProperty, "File Toolbox"))), TimeSpan.FromSeconds(6));
        if (palette is null) return Fail("The palette did not open for the forwarded file.");
        Thread.Sleep(700);
        var search = palette.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Search actions"));
        if (search is null || !search.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return Fail("Search box not found.");
        ((ValuePattern)value).SetValue("webp");
        Thread.Sleep(400);
        Snapshots.Capture("palette");
        search.SetFocus();
        Thread.Sleep(150);
        RealInput.Tap(0x0D);

        var job = WaitFor(() => _app.ReadJobs().Skip(jobsBefore).FirstOrDefault(), TimeSpan.FromSeconds(20));
        if (job is null) return Fail("No job ran from the palette.");
        if (job.State != "Completed" || job.Outputs.Count != 1 || !job.Outputs[0].EndsWith(".webp")) return Fail($"Unexpected result: {job.State} {string.Join(",", job.Outputs)} {job.Error}");
        var integrity = CheckSourcesUnchanged(before, allowNewFiles: true);
        return integrity is null
            ? new ScenarioResult("", true, $"Second launch forwarded the file; palette ran WebP → {Path.GetFileName(job.Outputs[0])}.", null)
            : Fail(integrity);
    }

    /// <summary>Closes editors, pickers and toasts left by a scenario.</summary>
    public void Cleanup() => CloseOurWindows();

    /// <summary>Closes every visible top-level window of the app (editors, pickers, toasts) except the wheel overlay.</summary>
    private void CloseOurWindows()
    {
        foreach (var hwnd in RealInput.TopLevelWindows(_app.ProcessId))
        {
            var title = RealInput.WindowTitle(hwnd);
            if (title == "File Toolbox wheel" || !RealInput.IsVisible(hwnd)) continue;
            RealInput.PostClose(hwnd);
        }
        Thread.Sleep(500);
    }

    /// <summary>Selects a radio/segment (or invokes a button) by its visible name inside a window.</summary>
    private static bool Select(AutomationElement window, string name)
    {
        // Prefer real controls (buttons, radios, tabs) over the text elements inside them.
        AutomationElement? element = null;
        foreach (var type in new[] { ControlType.Button, ControlType.RadioButton, ControlType.TabItem, ControlType.ListItem })
        {
            element = window.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, type),
                new OrCondition(new PropertyCondition(AutomationElement.NameProperty, name), new PropertyCondition(AutomationElement.NameProperty, name + " •"))));
            if (element is not null) break;
        }
        element ??= window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name));
        if (element is null) return false;
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) { ((SelectionItemPattern)sel).Select(); return true; }
        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var inv)) { ((InvokePattern)inv).Invoke(); return true; }
        return false;
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
        // Rubber-band selection in an empty folder: mouse button down + Shift, but no OLE file drag.
        var empty = Path.Combine(Path.GetDirectoryName(_folder)!, "Empty folder");
        Directory.CreateDirectory(empty);
        using var explorer = new ExplorerDriver(empty);
        explorer.Open(TimeSpan.FromSeconds(15));
        explorer.BringToFront();
        var bounds = explorer.Bounds;
        var start = new Point(bounds.Left + bounds.Width * 0.7, bounds.Top + bounds.Height * 0.6);
        RealInput.MoveTo(start.X, start.Y);
        Thread.Sleep(150);
        RealInput.LeftDown();
        RealInput.Glide(start.X - 80, start.Y - 50, 160, 8);
        RealInput.Tap(RealInput.VK_LSHIFT);
        var shown = _app.WaitForWheel(w => w.Visible, TimeSpan.FromSeconds(1.6));
        RealInput.LeftUp();
        Thread.Sleep(300);
        return shown is null
            ? new ScenarioResult("", true, "No wheel appeared for a non-file drag (rubber band in an empty folder).", null)
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
