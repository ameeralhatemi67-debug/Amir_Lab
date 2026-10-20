using System.Text;
using System.Text.Json;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Core.Settings;
using Toolbox.Processing;

namespace Toolbox.Cli;

/// <summary>
/// Headless access to the same local processing the desktop app uses.
///
///   ftb caps [--markdown]                      list every action, its meaning and limitations
///   ftb features                               show runtime features detected on this computer
///   ftb probe &lt;files...&gt;                       show what each file really contains
///   ftb run &lt;action&gt; [--set key=value]... [--out folder] [--json] &lt;files...&gt;
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "caps" => Caps(args.Contains("--markdown")),
                "features" => Features(),
                "probe" => Probe(args[1..]),
                "run" => Run(args[1..]),
                _ => Fail($"Unknown command '{args[0]}'. Run 'ftb help'."),
            };
        }
        catch (Exception ex) when (ex is ProcessingException or FormatException or KeyNotFoundException)
        {
            return Fail(ex.Message);
        }
    }

    private static int Caps(bool markdown)
    {
        ProcessingEngine.Initialize();
        var implemented = ProcessingEngine.CreateHandlers().Select(h => h.ActionId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!markdown)
        {
            foreach (var group in CapabilityMap.All.GroupBy(a => a.Group))
            {
                Console.WriteLine($"[{group.Key}]");
                foreach (var a in group)
                    Console.WriteLine($"  {a.Id,-22} {a.Title,-38} {Status(a, implemented)}");
            }
            return 0;
        }

        var sb = new StringBuilder();
        sb.AppendLine("# Capability map");
        sb.AppendLine();
        sb.AppendLine("Generated from `Toolbox.Core/Actions/CapabilityMap.cs` with `ftb caps --markdown`. Do not edit by hand.");
        sb.AppendLine("Each entry is a defined operation; the *Meaning* column states exactly what the result contains.");
        sb.AppendLine("HEIC input is accepted only when Windows' HEIF and HEVC codecs are installed (checked at runtime by the desktop app).");
        sb.AppendLine("*Status* is the state of the build that generated this file; see IMPLEMENTATION_STATUS.md for evidence.");
        sb.AppendLine();
        foreach (var group in CapabilityMap.All.GroupBy(a => a.Group))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            sb.AppendLine("| Action | Accepts | Produces | Meaning | Limitations | Status |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var a in group)
            {
                var inputs = string.Join(", ", a.Inputs.OrderBy(k => k).Select(k => k.Label()));
                var output = a.Output?.Label() ?? "same as input";
                var limits = a.Limitations.Count == 0 ? "—" : string.Join("<br>", a.Limitations.Select(Escape));
                sb.AppendLine($"| **{Escape(a.Title)}**<br>`{a.Id}` | {inputs} | {output} | {Escape(a.Meaning)} | {limits} | {Status(a, implemented)} |");
            }
            sb.AppendLine();
        }
        Console.Write(sb.ToString());
        return 0;
    }

    private static string Status(ActionDefinition action, HashSet<string> implemented)
    {
        if (action.RequiresFeature is { } f && !RuntimeFeatures.IsAvailable(f)) return $"Needs {f}";
        if (implemented.Contains(action.Id)) return "Working";
        return action.Mode == ActionMode.Editor ? "Editor (desktop app)" : "Not implemented";
    }

    private static int Features()
    {
        ProcessingEngine.Initialize();
        foreach (var (name, state) in RuntimeFeatures.Snapshot())
            Console.WriteLine($"{name,-14} {(state.Available ? "yes" : "no "),-4} {state.Detail}");
        return 0;
    }

    private static int Probe(string[] files)
    {
        foreach (var f in FileProbe.ProbeAll(files))
            Console.WriteLine($"{f.Kind.Label(),-8} {Bytes.Format(f.SizeBytes),10}  {f.Path}{(f.Problem is null ? "" : "  — " + f.Problem)}");
        return 0;
    }

    private static int Run(string[] args)
    {
        if (args.Length < 2) return Fail("Usage: ftb run <action> [--set key=value]... [--out folder] [--json] <files...>");
        var actionId = args[0];
        if (!CapabilityMap.TryGet(actionId, out var action)) return Fail($"Unknown action '{actionId}'. Run 'ftb caps'.");

        var sets = new List<string>();
        var files = new List<string>();
        string? outFolder = null;
        bool json = false;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--set" when i + 1 < args.Length: sets.Add(args[++i]); break;
                case "--out" when i + 1 < args.Length: outFolder = Path.GetFullPath(args[++i]); break;
                case "--json": json = true; break;
                default: files.Add(Path.GetFullPath(args[i])); break;
            }
        }
        if (files.Count == 0) return Fail("No input files given.");

        var settings = new SettingsStore().Current;
        var policy = settings.ToOutputPolicy() with
        {
            Location = outFolder is null ? settings.OutputLocation : OutputLocation.Folder,
            Folder = outFolder ?? settings.OutputFolder,
        };
        var journal = new TempJournal(AppPaths.TempJournalFile);
        journal.CleanupOrphans();
        var runner = ProcessingEngine.CreateRunner(new OutputManager(() => policy, journal));
        if (!runner.CanRun(action.Id))
            return Fail(action.Mode == ActionMode.Editor
                ? $"'{action.Title}' is an interactive editor; use the desktop app."
                : $"'{action.Title}' is not implemented yet.");

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

        var job = runner.Start(new JobRequest(action.Id, files, ActionOptions.Parse(sets)));
        using (cancel.Token.Register(job.Cancel))
            job.Completion.GetAwaiter().GetResult();

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                action = action.Id,
                summary = job.Summary.Describe(),
                items = job.Items.Select(i => new
                {
                    inputs = i.Inputs.Select(f => f.Path),
                    state = i.State.ToString(),
                    error = i.Error,
                    notes = i.Notes,
                    outputs = i.Outputs.Select(o => new { path = o.Path, bytes = o.SizeBytes, width = o.Width, height = o.Height, pages = o.Pages }),
                }),
            }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        else
        {
            Console.WriteLine($"{action.Title}: {job.Summary.Describe()}");
            foreach (var item in job.Items)
            {
                Console.WriteLine($"  [{item.State}] {item.Label}{(item.Error is null ? "" : " — " + item.Error)}");
                foreach (var o in item.Outputs)
                    Console.WriteLine($"      → {o.Path}  ({Bytes.Format(o.SizeBytes)}{(o.Width is null ? "" : $", {o.Width}×{o.Height}px")}{(o.Pages is null ? "" : $", {o.Pages} pages")})");
                foreach (var n in item.Notes) Console.WriteLine($"      note: {n}");
            }
        }
        return job.Items.All(i => i.State is JobItemState.Completed or JobItemState.Skipped) && job.Summary.Completed > 0 ? 0 : 2;
    }

    private static string Escape(string s) => s.Replace("|", "\\|");

    private static int Fail(string message)
    {
        Console.Error.WriteLine("ftb: " + message);
        return 1;
    }

    private static void PrintHelp() => Console.WriteLine("""
        File Toolbox command line (local processing only)

          ftb caps [--markdown]          List every action with its meaning, limitations and status
          ftb features                   Show codecs/engines detected on this computer
          ftb probe <files...>           Show what each file really contains
          ftb run <action> [--set key=value]... [--out folder] [--json] <files...>

        Examples
          ftb run pdf.render-jpg --set pages=1-3 --set dpi=150 report.pdf
          ftb run image.to-webp --set quality=80 photo.jpg
          ftb run image.to-pdf --set page=a4 scan1.jpg scan2.jpg
        Outputs are saved next to the source (or --out) and never overwrite existing files.
        """);
}
