using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NetVips;
using OcrEval;
using Toolbox.Processing;
using Toolbox.Processing.Ocr;
using Toolbox.Windows.Ocr;

// Usage: OcrEval [--out <dir>] [--samples <dir>] [--only <id-prefix>] [--no-upscale-pass]
//   --samples: a folder of real scans/photos, each with a same-named .txt holding the exact text (UTF-8).
// WPF text rendering needs a single-threaded apartment.
int exitCode = 1;
var worker = new Thread(() => exitCode = Run(args));
worker.SetApartmentState(ApartmentState.STA);
worker.Start();
worker.Join();
return exitCode;

static int Run(string[] args)
{
    string Arg(string name, string fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }

    var outDir = Path.GetFullPath(Arg("--out", Path.Combine("artifacts", "ocr-eval")));
    var samplesDir = Arg("--samples", "");
    var only = Arg("--only", "");
    bool upscalePass = !args.Contains("--no-upscale-pass");
    Directory.CreateDirectory(Path.Combine(outDir, "images"));
    Directory.CreateDirectory(Path.Combine(outDir, "text"));
    Console.OutputEncoding = Encoding.UTF8;

    ProcessingEngine.Initialize();
    WindowsOcrEngine.Register();
    if (OcrService.Engine is null)
    {
        Console.Error.WriteLine("Windows OCR is not available on this computer.");
        return 2;
    }
    Console.WriteLine($"OCR languages: {string.Join(", ", OcrService.Engine.Languages)}");

    var cases = new List<(string Id, string Script, string Category, string Truth, Func<Image> Load)>();
    foreach (var spec in Corpus.Build().Where(s => s.Id.StartsWith(only, StringComparison.OrdinalIgnoreCase)))
    {
        var path = Path.Combine(outDir, "images", spec.Id + ".png");
        using (var image = PageRenderer.Render(spec)) image.WriteToFile(path);
        File.WriteAllText(Path.Combine(outDir, "images", spec.Id + ".txt"), spec.Truth, new UTF8Encoding(false));
        cases.Add((spec.Id, spec.Script, spec.Category, spec.Truth, () => Image.NewFromFile(path, access: Enums.Access.Random)));
    }
    if (samplesDir.Length > 0 && Directory.Exists(samplesDir))
    {
        foreach (var file in Directory.EnumerateFiles(samplesDir).Where(f => new[] { ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
        {
            var truthPath = Path.ChangeExtension(file, ".txt");
            if (!File.Exists(truthPath)) continue;
            var truth = File.ReadAllText(truthPath);
            string script = OcrService.ArabicRatio(truth) > 0.7 ? "ar" : OcrService.ArabicRatio(truth) > 0.05 ? "mixed" : "en";
            var f = file;
            cases.Add(("user-" + Path.GetFileNameWithoutExtension(file), script, "Your samples", truth, () => Image.NewFromFile(f, access: Enums.Access.Random).Autorot()));
        }
    }

    int Default = OcrService.UpscaleTarget;
    var strategies = new (string Name, string Languages, MixedStrategy Mixed, int Upscale)[]
    {
        ("eng only", "eng", MixedStrategy.EnglishOnly, Default),
        ("ara only", "ara", MixedStrategy.ArabicOnly, Default),
        ("ara+eng per-line", "ara+eng", MixedStrategy.PerLine, Default),
        ("ara+eng per-word (app default)", "ara+eng", MixedStrategy.PerWord, Default),
    }.ToList();
    if (upscalePass)
    {
        foreach (int target in new[] { 0, 1600, 2400, 3200 }.Where(t => t != Default))
            strategies.Add(($"per-word, upscale {(target == 0 ? "off" : target + " px")}", "ara+eng", MixedStrategy.PerWord, target));
    }

    var dump = Arg("--dump", "");
    if (dump.Length > 0)
    {
        var c = cases.First(x => x.Id == dump);
        using var image = c.Load();
        foreach (var tag in OcrService.Engine.Languages.Where(l => l.StartsWith("ar") || l == "en-US"))
        {
            Console.WriteLine($"== {tag}");
            foreach (var l in OcrService.RecognizeRaw(image, tag).Lines)
                Console.WriteLine("  " + string.Join(" | ", l.Words.Select(w => $"{w.Text}@{w.X:0},{w.Y:0} {w.Width:0}x{w.Height:0}")));
        }
        return 0;
    }

    var results = new List<Result>();
    foreach (var c in cases)
    {
        using var image = c.Load();
        foreach (var s in strategies)
        {
            OcrService.UpscaleTarget = s.Upscale;
            var sw = Stopwatch.StartNew();
            string text;
            try
            {
                text = OcrService.Recognize(image, s.Languages, s.Mixed).ToText();
            }
            catch (Exception ex)
            {
                text = "";
                Console.Error.WriteLine($"{c.Id} [{s.Name}] failed: {ex.Message}");
            }
            sw.Stop();
            File.WriteAllText(Path.Combine(outDir, "text", $"{c.Id}.{s.Name.Replace(' ', '_').Replace("(", "").Replace(")", "")}.txt"), text, new UTF8Encoding(false));
            var r = new Result(c.Id, c.Script, c.Category, s.Name, image.Width, image.Height,
                Metrics.Cer(Metrics.Strict(c.Truth), Metrics.Strict(text)),
                Metrics.Wer(Metrics.Strict(c.Truth), Metrics.Strict(text)),
                Metrics.Cer(Metrics.Lenient(c.Truth), Metrics.Lenient(text)),
                sw.Elapsed.TotalMilliseconds);
            results.Add(r);
        }
        var line = string.Join("  ", results.Where(r => r.Id == c.Id).Select(r => $"{r.Strategy}: {r.Cer:P1}"));
        Console.WriteLine($"{c.Id,-36} {line}");
    }
    OcrService.UpscaleTarget = Default;

    File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
    var markdown = Report(results, strategies.Select(s => s.Name).ToList());
    File.WriteAllText(Path.Combine(outDir, "results.md"), markdown, new UTF8Encoding(false));
    Console.WriteLine();
    Console.WriteLine(markdown);
    return 0;
}

static string Report(List<Result> results, List<string> strategies)
{
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder();
    sb.AppendLine($"Mean character error rate (strict) per group; lenient Arabic CER in brackets. {results.Select(r => r.Id).Distinct().Count()} pages.");
    sb.AppendLine();
    sb.AppendLine("| Script | Group | Pages | " + string.Join(" | ", strategies) + " |");
    sb.AppendLine("|---|---|---|" + string.Concat(strategies.Select(_ => "---|")));
    foreach (var group in results.GroupBy(r => (r.Script, r.Category)).OrderBy(g => g.Key.Script).ThenBy(g => g.Key.Category))
    {
        int pages = group.Select(r => r.Id).Distinct().Count();
        var cells = strategies.Select(s =>
        {
            var rs = group.Where(r => r.Strategy == s).ToList();
            return rs.Count == 0 ? "–" : string.Format(inv, "{0:0.0}% ({1:0.0}%)", rs.Average(r => r.Cer) * 100, rs.Average(r => r.LenientCer) * 100);
        });
        sb.AppendLine($"| {group.Key.Script} | {group.Key.Category} | {pages} | " + string.Join(" | ", cells) + " |");
    }
    sb.AppendLine();
    sb.AppendLine("| Strategy | Mean WER | Pages with CER < 2% | Mean time per page |");
    sb.AppendLine("|---|---|---|---|");
    foreach (var s in strategies)
    {
        var rs = results.Where(r => r.Strategy == s).ToList();
        sb.AppendLine(string.Format(inv, "| {0} | {1:0.0}% | {2}/{3} | {4:0} ms |", s, rs.Average(r => r.Wer) * 100, rs.Count(r => r.Cer < 0.02), rs.Count, rs.Average(r => r.Ms)));
    }
    return sb.ToString();
}

internal sealed record Result(string Id, string Script, string Category, string Strategy, int Width, int Height,
    double Cer, double Wer, double LenientCer, double Ms);
