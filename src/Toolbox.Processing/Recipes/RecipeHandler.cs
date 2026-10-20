using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Core.Presets;
using Toolbox.Core.Recipes;
using Toolbox.Processing.Imaging;
using Toolbox.Processing.Metadata;
using Toolbox.Processing.Pdf;
using Toolbox.Processing.Studio;

namespace Toolbox.Processing.Recipes;

/// <summary>
/// Runs a recipe for one job item. Intermediate files live in a private staging folder that is
/// always deleted; only the final stage writes real outputs (verified and named like any other
/// result). Results are then checked against the recipe's requirements and reported as met or not.
/// </summary>
internal sealed class RecipeHandler(string actionId, Func<string, BrandPreset?> brands) : IActionHandler
{
    private sealed record Artifact(string Path, FileKind Kind, int? Page, int? PageCount, string OriginName, string Variant, int? Width = null, int? Height = null);

    public string ActionId => actionId;

    public Task RunAsync(ItemContext context)
    {
        var recipe = RecipeStore.FromJson(context.Options.GetString("recipe", ""));
        var stages = RecipeCompiler.Compile(recipe, brands);
        var staging = Path.Combine(AppPaths.DataDir, "staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var current = context.Inputs.Select(f => new Artifact(f.Path, f.Kind, null, null, f.BaseName, "")).ToList();
            var finals = new List<(OutputSlot Slot, Artifact Artifact)>();
            int counter = 0;

            for (int i = 0; i < stages.Count; i++)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                bool last = i == stages.Count - 1;

                string Sink(Artifact like, FileKind kind, out OutputSlot? slot)
                {
                    if (!last)
                    {
                        slot = null;
                        return Path.Combine(staging, $"{counter++:D4}{kind.Extension()}");
                    }
                    slot = context.CreateOutput(new OutputSpec(like.OriginName, recipe.Suffix + like.Variant, kind, like.Page, like.PageCount));
                    return slot.TempPath;
                }

                var next = new List<Artifact>();
                switch (stages[i])
                {
                    case PagesStage render:
                        foreach (var a in current)
                        {
                            if (a.Kind != FileKind.Pdf) throw new ProcessingException($"\"{recipe.Name}\" starts with PDF pages, but {a.OriginName} is not a PDF.");
                            using var pdf = PdfDocument.Open(a.Path);
                            var pages = PageSet.Parse(render.Pages, pdf.PageCount);
                            foreach (var page in pages)
                            {
                                context.Cancellation.ThrowIfCancellationRequested();
                                using var image = pdf.RenderPage(page, render.Dpi, out _);
                                var art = a with { Page = pdf.PageCount > 1 ? page : null, PageCount = pdf.PageCount, Kind = render.Format, Width = image.Width, Height = image.Height };
                                var path = Sink(art, render.Format, out var slot);
                                ImageEncoder.Save(image, render.Format, path, new EncodeSettings { Quality = render.Quality, KeepMetadata = false });
                                next.Add(art with { Path = path });
                                if (slot is not null) { slot.Expected = new ExpectedOutput(render.Format, image.Width, image.Height); finals.Add((slot, art with { Path = path })); }
                            }
                        }
                        break;

                    case StudioStage studio:
                        foreach (var a in current)
                        {
                            using var source = StudioSource.Open(a.Path, 1, 300);
                            foreach (var n in source.Notes.Where(n => !n.StartsWith("Page ", StringComparison.Ordinal))) context.Note(n);
                            var variants = studio.Ratios.Count == 0
                                ? [(Doc: studio.Template, Variant: a.Variant)]
                                : studio.Ratios.Select(r => (Doc: studio.Template with { Background = studio.Template.Background with { Enabled = true, Ratio = r } }, Variant: $"{a.Variant} {r.Replace(':', 'x')}")).ToList();
                            foreach (var (doc, variant) in variants)
                            {
                                context.Cancellation.ThrowIfCancellationRequested();
                                var withSource = doc with { SourcePath = a.Path };
                                var format = StudioExporter.ResolveFormat(withSource, source);
                                var art = a with { Kind = format, Variant = variant };
                                var path = Sink(art, format, out var slot);
                                var result = StudioExporter.Export(withSource, source, format, path, context.Cancellation);
                                foreach (var note in result.Notes) context.Note(note);
                                var produced = art with { Path = path, Width = result.Width, Height = result.Height };
                                next.Add(produced);
                                if (slot is not null) { slot.Expected = new ExpectedOutput(format, result.Width, result.Height); finals.Add((slot, produced)); }
                            }
                        }
                        break;

                    case MetadataStage meta:
                        foreach (var a in current)
                        {
                            var path = Sink(a, a.Kind, out var slot);
                            foreach (var note in MetadataCleaner.Clean(FileProbe.Probe(a.Path), MetadataCategoryNames.Parse(meta.Remove), path)) context.Note(note);
                            next.Add(a with { Path = path });
                            if (slot is not null) finals.Add((slot, a with { Path = path }));
                        }
                        break;

                    case CombineStage combine:
                    {
                        var first = current[0];
                        var art = new Artifact("", FileKind.Pdf, null, null,
                            current.Select(c => c.OriginName).Distinct().Count() == 1 ? first.OriginName : $"{first.OriginName} + {current.Count - 1} more", "");
                        var path = Sink(art, FileKind.Pdf, out var slot);
                        var probed = current.Select(c => FileProbe.Probe(c.Path)).ToList();
                        int pages = PdfComposer.ImagesToPdf(probed, new PdfPageOptions(combine.PageSize), path, null, context.Cancellation);
                        next.Add(art with { Path = path });
                        if (slot is not null) { slot.Expected = new ExpectedOutput(FileKind.Pdf, Pages: pages); finals.Add((slot, art with { Path = path })); }
                        break;
                    }
                }
                current = next;
                context.Report((i + 1.0) / stages.Count);
            }

            ReportRequirements(context, recipe, finals);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return Task.CompletedTask;
    }

    private static void ReportRequirements(ItemContext context, Recipe recipe, List<(OutputSlot Slot, Artifact Artifact)> finals)
    {
        var req = recipe.Requirements;
        if (req.IsEmpty) return;
        foreach (var (slot, art) in finals)
        {
            var failures = new List<string>();
            long size = new FileInfo(slot.TempPath).Length;
            if (req.MaxBytes is { } max && size > max) failures.Add($"{Bytes.Format(size)} is over the {Bytes.Format(max)} limit");
            if (req.Formats is { Count: > 0 } formats && !formats.Any(f => RecipeCompiler.FormatOf(f, FileKind.Unknown) == art.Kind))
                failures.Add($"{art.Kind.Label()} is not one of {string.Join("/", formats.Select(f => f.ToUpperInvariant()))}");
            if (req.MaxEdge is { } edge && art.Width is { } w && art.Height is { } h && Math.Max(w, h) > edge)
                failures.Add($"{Math.Max(w, h)} px is larger than {edge} px");
            context.Note(failures.Count == 0
                ? $"✓ Meets the recipe's requirements ({string.Join(", ", recipe.RequirementsText())}): {Bytes.Format(size)}."
                : "✗ Requirement not met: " + string.Join("; ", failures) + ".");
        }
    }
}
