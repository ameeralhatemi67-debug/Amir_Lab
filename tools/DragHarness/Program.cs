using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace DragHarness;

/// <summary>
/// Automated end-to-end check of the desktop interaction with real File Explorer drags.
/// It moves the real mouse and presses real keys (SendInput), so keep hands off while it runs.
///
///   DragHarness [--app path\to\FileToolbox.exe] [--only name1,name2] [--desktop] [--expect-outputs]
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        var appPath = Arg(args, "--app") ?? DefaultAppPath();
        var only = Arg(args, "--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool desktop = args.Contains("--desktop");
        bool expectOutputs = args.Contains("--expect-outputs");
        Scenarios.IncludeQa = args.Contains("--qa");

        var run = Path.Combine(Path.GetTempPath(), "ftb-harness", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var files = Path.Combine(run, "Drag Test ملفات");
        var data = Path.Combine(run, "data");
        Fixtures.Create(files);
        Directory.CreateDirectory(data);
        Snapshots.Folder = Path.Combine(run, "shots");
        Console.WriteLine($"Run folder: {run}");
        Console.WriteLine($"App: {appPath}");

        var results = new List<ScenarioResult>();
        using var app = new AppDriver(appPath, data);
        using var explorer = new ExplorerDriver(files);
        try
        {
            app.Start(TimeSpan.FromSeconds(20));
            explorer.Open(TimeSpan.FromSeconds(15));
            var harness = new Scenarios(app, explorer, files, expectOutputs);

            foreach (var (name, scenario) in harness.All(desktop))
            {
                if (only is not null && !only.Contains(name)) continue;
                Console.WriteLine($"--- {name}");
                ScenarioResult result;
                var sw = Stopwatch.StartNew();
                try
                {
                    result = scenario() with { Name = name };
                }
                catch (Exception ex)
                {
                    result = new ScenarioResult(name, false, $"{ex.GetType().Name}: {ex.Message}", null);
                }
                finally
                {
                    RealInput.ReleaseAll();
                    harness.Cleanup();
                }
                result = result with { Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1) };
                Console.WriteLine($"    {(result.Passed ? "PASS" : "FAIL")} — {result.Detail}");
                results.Add(result);
                Thread.Sleep(600);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Harness error: {ex}");
            results.Add(new ScenarioResult("setup", false, ex.Message, null));
        }
        finally
        {
            RealInput.ReleaseAll();
            Console.WriteLine("--- app log tail");
            Console.WriteLine(app.LogTail(30));
        }

        File.WriteAllText(Path.Combine(run, "results.json"), JsonSerializer.Serialize(results, Pretty));
        int passed = results.Count(r => r.Passed);
        Console.WriteLine($"=== {passed}/{results.Count} scenarios passed. Results: {Path.Combine(run, "results.json")}");
        return passed == results.Count && results.Count > 0 ? 0 : 1;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string DefaultAppPath()
    {
        var root = AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Toolbox.Windows", "bin", "Debug", "net9.0-windows10.0.19041.0", "FileToolbox.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("FileToolbox.exe not found; build src/Toolbox.Windows or pass --app.");
    }
}

internal sealed record ScenarioResult(string Name, bool Passed, string Detail, double? RevealLatencyMs, double Seconds = 0);
