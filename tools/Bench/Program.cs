using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NetVips;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Processing;
using Toolbox.Processing.Imaging;
using Toolbox.Processing.Studio;
using Toolbox.Windows.Ocr;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

// Usage:
//   Bench [--out <dir>] [--app <AmirLap.exe>] [--skip-idle]
//   Bench --op <name> <fixtures dir>      (internal: one operation, prints one JSON line)
Console.OutputEncoding = Encoding.UTF8;
var inv = CultureInfo.InvariantCulture;

if (args.Length >= 3 && args[0] == "--op") return RunOp(args[1], args[2]);

string Arg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var outDir = Path.GetFullPath(Arg("--out", Path.Combine("artifacts", "bench")));
var fixtures = Path.Combine(outDir, "fixtures");
Directory.CreateDirectory(fixtures);
Environment.SetEnvironmentVariable(AppPaths.DataEnvironmentVariable, Path.Combine(outDir, "data"));
Fixtures.Create(fixtures);

var rows = new List<string>();
foreach (var op in Ops.Names)
{
    var psi = new ProcessStartInfo(Environment.ProcessPath!, $"--op {op} \"{fixtures}\"") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    psi.Environment[AppPaths.DataEnvironmentVariable] = Path.Combine(outDir, "data");
    using var child = Process.Start(psi)!;
    var stdout = child.StandardOutput.ReadToEnd();
    var stderr = child.StandardError.ReadToEnd();
    child.WaitForExit();
    var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(l => l.TrimStart().StartsWith('{'));
    if (line is null)
    {
        Console.WriteLine($"{op}: failed {stderr.Trim()}");
        rows.Add($"| {op} | failed | | |");
        continue;
    }
    var r = JsonSerializer.Deserialize<OpResult>(line)!;
    Console.WriteLine($"{op,-34} {r.Ms,8:0} ms  peak {r.PeakMb,6:0} MB  {r.Detail}");
    rows.Add(string.Format(inv, "| {0} | {1:0.0} s | {2:0} MB | {3} |", r.Op, r.Ms / 1000, r.PeakMb, r.Detail));
}

var report = new StringBuilder();
report.AppendLine($"Operations (each in a fresh process; peak = peak working set of that process; {Environment.ProcessorCount} logical CPUs):");
report.AppendLine();
report.AppendLine("| Operation | Time | Peak memory | Detail |");
report.AppendLine("|---|---|---|---|");
foreach (var row in rows) report.AppendLine(row);

if (!args.Contains("--skip-idle"))
{
    report.AppendLine();
    report.Append(Idle.Measure(Arg("--app", Idle.FindApp()), Path.Combine(outDir, "appdata")));
}
File.WriteAllText(Path.Combine(outDir, "results.md"), report.ToString(), new UTF8Encoding(false));
Console.WriteLine();
Console.WriteLine(report);
return 0;

static int RunOp(string op, string fixtures)
{
    ProcessingEngine.Initialize();
    WindowsOcrEngine.Register();
    var sw = Stopwatch.StartNew();
    var detail = Ops.Run(op, fixtures);
    sw.Stop();
    var self = Process.GetCurrentProcess();
    self.Refresh();
    Console.WriteLine(JsonSerializer.Serialize(new OpResult(op, sw.Elapsed.TotalMilliseconds, self.PeakWorkingSet64 / 1048576.0, detail)));
    return 0;
}

internal sealed record OpResult(string Op, double Ms, double PeakMb, string Detail);

internal static class Ops
{
    public static readonly string[] Names =
    [
        "image-48mp-png-to-webp",
        "image-24mp-jpeg-to-png",
        "pdf-60-pages-to-jpg-300dpi",
        "pdf-60-pages-text",
        "studio-open-and-preview-24mp",
        "studio-export-24mp-with-background",
        "ocr-text-a4-scan-300dpi",
        "ocr-searchable-pdf-5-pages",
    ];

    public static string Run(string op, string dir)
    {
        string F(string name) => Path.Combine(dir, name);
        switch (op)
        {
            case "image-48mp-png-to-webp": return Job(Ids.ImageToWebp, F("big 8000x6000.png"));
            case "image-24mp-jpeg-to-png": return Job(Ids.ImageToPng, F("photo 6000x4000.jpg"));
            case "pdf-60-pages-to-jpg-300dpi": return Job(Ids.PdfToJpg, F("report 60 pages.pdf"), ("pages", "all"), ("dpi", "300"));
            case "pdf-60-pages-text": return Job(Ids.PdfToTxt, F("report 60 pages.pdf"));
            case "ocr-text-a4-scan-300dpi": return Job(Ids.OcrImageText, F("scan page.png"));
            case "ocr-searchable-pdf-5-pages": return Job(Ids.OcrSearchablePdf, F("scan 5 pages.pdf"));
            case "studio-open-and-preview-24mp":
            {
                var open = Stopwatch.StartNew();
                using var source = StudioSource.Open(F("photo 6000x4000.jpg"));
                open.Stop();
                var doc = new EditDocument { SourcePath = F("photo 6000x4000.jpg") };
                var first = Stopwatch.StartNew();
                StudioRenderer.RenderPreview(doc, source, RenderStage.Final);
                first.Stop();
                var times = new List<double>();
                for (int i = 1; i <= 12; i++)
                {
                    var edit = doc with
                    {
                        Adjust = new AdjustSettings { Brightness = i * 3, Contrast = i },
                        Background = new BackgroundSettings { Enabled = i % 2 == 0 },
                    };
                    var t = Stopwatch.StartNew();
                    StudioRenderer.RenderPreview(edit, source, RenderStage.Final);
                    times.Add(t.Elapsed.TotalMilliseconds);
                }
                times.Sort();
                return string.Format(CultureInfo.InvariantCulture, "open {0:0} ms, first preview {1:0} ms, then median {2:0} ms / max {3:0} ms per edit (≤1600 px proxy)",
                    open.Elapsed.TotalMilliseconds, first.Elapsed.TotalMilliseconds, times[times.Count / 2], times[^1]);
            }
            case "studio-export-24mp-with-background":
            {
                using var source = StudioSource.Open(F("photo 6000x4000.jpg"));
                var doc = new EditDocument { SourcePath = F("photo 6000x4000.jpg"), Background = new BackgroundSettings { Enabled = true } };
                using var image = StudioRenderer.Render(doc, source, RenderStage.Final, null, out _);
                var path = Path.Combine(Path.GetTempPath(), $"bench-export-{Guid.NewGuid():N}.png");
                ImageEncoder.Save(image, Toolbox.Core.Inputs.FileKind.Png, path, new EncodeSettings());
                var size = new FileInfo(path).Length;
                File.Delete(path);
                return $"{image.Width}×{image.Height} PNG, {Bytes.Format(size)}";
            }
            default: throw new ArgumentException(op);
        }
    }

    private static string Job(string actionId, string path, params (string, string)[] options)
    {
        var dir = Path.Combine(Path.GetDirectoryName(path)!, "out-" + actionId);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var outputs = new OutputManager(() => new OutputPolicy { Location = OutputLocation.Folder, Folder = dir, FallbackFolder = dir },
            new TempJournal(Path.Combine(dir + "-journal.txt")));
        var runner = ProcessingEngine.CreateRunner(outputs);
        var opts = new ActionOptions();
        foreach (var (k, v) in options) opts[k] = v;
        var job = runner.Start(new JobRequest(actionId, [path], opts));
        job.Completion.Wait();
        var item = job.Items[0];
        if (item.State != JobItemState.Completed) return $"{item.State}: {item.Error}";
        return $"{item.Outputs.Count} file(s), {Bytes.Format(item.Outputs.Sum(o => o.SizeBytes))}";
    }
}

internal static class Fixtures
{
    public static void Create(string dir)
    {
        ProcessingEngine.Initialize();
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        string F(string name) => Path.Combine(dir, name);

        if (!File.Exists(F("big 8000x6000.png"))) Photo(8000, 6000).Pngsave(F("big 8000x6000.png"), compression: 3);
        if (!File.Exists(F("photo 6000x4000.jpg"))) Photo(6000, 4000).Jpegsave(F("photo 6000x4000.jpg"), q: 90);
        if (!File.Exists(F("report 60 pages.pdf"))) Report(F("report 60 pages.pdf"), 60);
        if (!File.Exists(F("scan 5 pages.pdf")) || !File.Exists(F("scan page.png"))) Scans(dir);
    }

    /// <summary>A photo-like image: smooth gradients plus noise, so encoders do real work.</summary>
    private static Image Photo(int w, int h)
    {
        using var xy = Image.Xyz(w, h);
        using var r = xy[0] * (200.0 / w) + 30;
        using var g = xy[1] * (180.0 / h) + 40;
        using var b = (xy[0] + xy[1]) * (120.0 / (w + h)) + 60;
        using var noise = Image.Gaussnoise(w, h, sigma: 18);
        using var rgb = r.Bandjoin(g, b) + noise;
        return rgb.Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
    }

    private static void Report(string path, int pages)
    {
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var title = new XFont("Arial", 20, XFontStyleEx.Bold);
        var body = new XFont("Times New Roman", 11);
        for (int p = 1; p <= pages; p++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Quarterly report — section {p}", title, XBrushes.Black, 60, 80);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 40, 110, 180)), 60, 100, 480, 140);
            for (int line = 0; line < 38; line++)
                gfx.DrawString($"Line {line + 1}: revenue across all regions increased steadily during the period under review.", body, XBrushes.Black, 60, 270 + line * 13.5);
        }
        doc.Save(path);
    }

    /// <summary>"Scanned" pages: a text PDF rendered to 300 DPI pictures, then put back into a PDF of images.</summary>
    private static void Scans(string dir)
    {
        var digital = Path.Combine(dir, "scan source.pdf");
        Report(digital, 5);
        using var pdf = Toolbox.Processing.Pdf.PdfDocument.Open(digital);
        using var doc = new PdfSharp.Pdf.PdfDocument();
        for (int p = 1; p <= pdf.PageCount; p++)
        {
            using var image = pdf.RenderPage(p, 300, out _);
            using var gray = image.Colourspace(Enums.Interpretation.Bw);
            var jpeg = gray.JpegsaveBuffer(q: 80);
            if (p == 1) image.Pngsave(Path.Combine(dir, "scan page.png"));
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var x = XImage.FromStream(new MemoryStream(jpeg));
            gfx.DrawImage(x, 0, 0, page.Width.Point, page.Height.Point);
        }
        doc.Save(Path.Combine(dir, "scan 5 pages.pdf"));
        File.Delete(digital);
    }
}

internal static class Idle
{
    public static string FindApp()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var exe = Path.Combine(d.FullName, "src", "Toolbox.Windows", "bin", "Release", "net9.0-windows10.0.19041.0", "AmirLap.exe");
            if (File.Exists(exe)) return exe;
            exe = Path.Combine(d.FullName, "src", "Toolbox.Windows", "bin", "Debug", "net9.0-windows10.0.19041.0", "AmirLap.exe");
            if (File.Exists(exe)) return exe;
        }
        return "AmirLap.exe";
    }

    public static string Measure(string exe, string dataDir)
    {
        var inv = CultureInfo.InvariantCulture;
        var psi = new ProcessStartInfo(exe, "--background") { UseShellExecute = false };
        psi.Environment[AppPaths.DataEnvironmentVariable] = dataDir;
        var started = Stopwatch.StartNew();
        using var app = Process.Start(psi)!;
        Thread.Sleep(15000); // start-up and warm-up
        app.Refresh();
        var cpu0 = app.TotalProcessorTime;
        long ws0 = app.WorkingSet64, private0 = app.PrivateMemorySize64;
        var modules0 = Modules(app);
        Thread.Sleep(60000);
        app.Refresh();
        var cpu1 = app.TotalProcessorTime;
        double cpuPercent = (cpu1 - cpu0).TotalMilliseconds / 60000.0 * 100;
        long ws1 = app.WorkingSet64, private1 = app.PrivateMemorySize64;

        var quit = Stopwatch.StartNew();
        using (var q = Process.Start(new ProcessStartInfo(exe, "--quit") { UseShellExecute = false }.WithData(dataDir))) q?.WaitForExit(10000);
        bool exited = app.WaitForExit(15000);
        quit.Stop();
        if (!exited) app.Kill();

        var loaded = new[] { "libvips-42.dll", "pdfium.dll", "libSkiaSharp.dll", "Microsoft.Windows.SDK.NET.dll" }
            .Select(m => $"{m}: {(modules0.Contains(m, StringComparer.OrdinalIgnoreCase) ? "loaded" : "not loaded")}");
        var sb = new StringBuilder();
        sb.AppendLine($"Idle desktop app (`{Path.GetFileName(Path.GetDirectoryName(exe))}` build, `--background`, no input for 60 s after a 15 s warm-up):");
        sb.AppendLine();
        sb.AppendLine("| Measure | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(string.Format(inv, "| CPU over 60 s idle | {0:0.00}% of one core |", cpuPercent));
        sb.AppendLine(string.Format(inv, "| Working set | {0:0} MB after warm-up, {1:0} MB after 60 s |", ws0 / 1048576.0, ws1 / 1048576.0));
        sb.AppendLine(string.Format(inv, "| Private bytes | {0:0} MB after warm-up, {1:0} MB after 60 s |", private0 / 1048576.0, private1 / 1048576.0));
        sb.AppendLine($"| Native engines at idle | {string.Join("; ", loaded)} |");
        sb.AppendLine(string.Format(inv, "| `--quit` to process exit | {0} |", exited ? $"{quit.Elapsed.TotalMilliseconds:0} ms" : "did not exit (killed)"));
        return sb.ToString();
    }

    private static ProcessStartInfo WithData(this ProcessStartInfo psi, string dataDir)
    {
        psi.Environment[AppPaths.DataEnvironmentVariable] = dataDir;
        return psi;
    }

    private static List<string> Modules(Process p)
    {
        try
        {
            return p.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
