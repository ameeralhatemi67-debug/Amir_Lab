using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using NetVips;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;

namespace Toolbox.Processing.Tests;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ftb-tests-data");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable(AppPaths.DataEnvironmentVariable, dir);
        // Fixture PDFs draw text; the product itself never needs fonts for its PDF output.
        GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        ProcessingEngine.Initialize();
    }
}

/// <summary>A temp folder with generated fixtures and a job runner writing into it.</summary>
public sealed class Workspace : IDisposable
{
    public Workspace()
    {
        Root = Directory.CreateTempSubdirectory("ftb-proc-").FullName;
        Fallback = Path.Combine(Root, "_fallback");
        Journal = new TempJournal(Path.Combine(Root, "_journal.txt"));
        Outputs = new OutputManager(() => new OutputPolicy { FallbackFolder = Fallback }, Journal);
        Runner = ProcessingEngine.CreateRunner(Outputs, maxConcurrency: 2);
    }

    public string Root { get; }
    public string Fallback { get; }
    public TempJournal Journal { get; }
    public OutputManager Outputs { get; }
    public JobRunner Runner { get; }

    public string PathOf(string name) => Path.Combine(Root, name);

    public async Task<Job> Run(string actionId, IEnumerable<string> files, params (string Key, string Value)[] options)
    {
        var opts = new ActionOptions();
        foreach (var (k, v) in options) opts[k] = v;
        var job = Runner.Start(new JobRequest(actionId, files.ToList(), opts));
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        return job;
    }

    public Task<Job> Run(string actionId, string file, params (string Key, string Value)[] options) => Run(actionId, [file], options);

    /// <summary>A gradient image; optionally with a fully transparent top-left square.</summary>
    public string Png(string name, int width = 640, int height = 400, bool transparentCorner = false)
    {
        using var x = Image.Xyz(width, height);
        using var r = x[0] * (255.0 / width);
        using var g = x[1] * (255.0 / height);
        using var b = (r + g) / 2;
        using var rgb = r.Bandjoin(g, b).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb);
        var path = PathOf(name);
        if (transparentCorner)
        {
            using var alpha = Image.Black(width, height).Linear([1.0], [255.0]).Cast(Enums.BandFormat.Uchar);
            using var hole = alpha.Mutate(m => m.DrawRect([0.0], 0, 0, width / 4, height / 4, fill: true));
            using var rgba = rgb.Bandjoin(hole);
            rgba.Pngsave(path);
        }
        else
        {
            rgb.Pngsave(path);
        }
        return path;
    }

    /// <summary>A JPEG whose pixels are landscape but whose EXIF orientation says "rotate 90°".</summary>
    public string RotatedJpeg(string name, int width = 300, int height = 200)
    {
        using var img = Image.Black(width, height, bands: 3).Linear([1, 1, 1], [200, 60, 60]).Cast(Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        using var tagged = img.Mutate(m => m.Set(GValue.GIntType, "orientation", 6));
        var path = PathOf(name);
        tagged.Jpegsave(path, q: 90, keep: Enums.ForeignKeep.All);
        return path;
    }

    public string Pdf(string name, int pages, bool withText = true, double width = 612, double height = 792)
    {
        var path = PathOf(name);
        using var doc = new PdfSharp.Pdf.PdfDocument();
        var font = new XFont("Arial", 22);
        for (int i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb(255, 30 * i % 255, 120, 200)), 50, 50, 200, 80);
            if (withText) gfx.DrawString($"Marker for page {i}", font, XBrushes.Black, 60, 200);
        }
        doc.Save(path);
        return path;
    }

    public static string Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    public string[] TempLeftovers() => Directory.GetFiles(Root, "*" + TempJournal.TempExtension, SearchOption.AllDirectories);

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
