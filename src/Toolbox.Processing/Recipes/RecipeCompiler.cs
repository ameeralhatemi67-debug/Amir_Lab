using System.Globalization;
using Toolbox.Core;
using Toolbox.Core.Inputs;
using Toolbox.Core.Presets;
using Toolbox.Core.Recipes;
using Toolbox.Processing.Studio;

namespace Toolbox.Processing.Recipes;

/// <summary>One executable stage of a compiled recipe.</summary>
public abstract record RecipeStage;

/// <summary>PDF pages → images.</summary>
public sealed record PagesStage(string Pages, double Dpi, FileKind Format, int Quality) : RecipeStage;

/// <summary>Fused image operations, rendered once from the original pixels (one decode, one encode).</summary>
public sealed record StudioStage(EditDocument Template, IReadOnlyList<string> Ratios) : RecipeStage;

/// <summary>Lossless metadata removal (only when no studio stage already strips it).</summary>
public sealed record MetadataStage(string Remove) : RecipeStage;

/// <summary>All current files into one PDF.</summary>
public sealed record CombineStage(string PageSize) : RecipeStage;

/// <summary>
/// Turns a recipe's user-facing steps into stages. Consecutive image steps (crop, resize, frame,
/// convert, compress) are fused into a single render so the picture is encoded only once.
/// </summary>
public static class RecipeCompiler
{
    public static IReadOnlyList<RecipeStage> Compile(Recipe recipe, Func<string, BrandPreset?>? brands = null)
    {
        var stages = new List<RecipeStage>();
        EditDocument? studio = null;
        IReadOnlyList<string> ratios = [];
        bool metadataPending = false;
        string metadataSpec = "all";

        void Flush()
        {
            if (studio is null) return;
            // A pure format change straight after rendering pages is folded into the render itself.
            if (stages.LastOrDefault() is PagesStage render && IsFormatOnly(studio) && ratios.Count == 0)
                stages[^1] = render with { Format = FormatOf(studio.Export.Format, render.Format), Quality = studio.Export.Quality };
            else
                stages.Add(new StudioStage(studio, ratios));
            studio = null;
            ratios = [];
        }

        EditDocument Studio() => studio ??= new EditDocument { Export = new ExportSettings { Format = "auto", Quality = 90 } };

        foreach (var step in recipe.Steps)
        {
            switch (step.Op)
            {
                case RecipeOp.RenderPdfPages:
                    Flush();
                    stages.Add(new PagesStage(step.Get("pages", "all"), step.GetInt("dpi", 150), FileKind.Png, 90));
                    break;
                case RecipeOp.CenterCrop:
                    studio = Studio() with { Crop = Studio().Crop with { Aspect = step.Get("ratio", "1:1"), Rect = null } };
                    break;
                case RecipeOp.Resize:
                    studio = Studio() with { Export = Studio().Export with { MaxEdge = step.GetInt("maxEdge", 1600) } };
                    break;
                case RecipeOp.Frame:
                {
                    var brand = step.Get("brand", "") is { Length: > 0 } id ? brands?.Invoke(id) : null;
                    var bg = brand is not null
                        ? new BackgroundSettings { Enabled = true, Fill = brand.Fill, Padding = brand.Padding, Corner = brand.Corner, Shadow = brand.Shadow }
                        : new BackgroundSettings
                        {
                            Enabled = true,
                            Fill = step.Get("fill", "preset:paper"),
                            Padding = step.GetInt("padding", 100),
                            Corner = step.GetInt("corner", 24),
                            Shadow = step.GetInt("shadow", 32),
                        };
                    bg = bg with { Ratio = step.Get("ratio", "auto") };
                    studio = Studio() with { Background = bg, Brand = brand is null ? null : BrandingSettings.From(brand) };
                    ratios = step.Get("ratios", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(r => BackgroundPresets.Ratio(r) is not null).ToList();
                    break;
                }
                case RecipeOp.Convert:
                    studio = Studio() with { Export = Studio().Export with { Format = step.Get("format", "jpg"), Quality = step.GetInt("quality", 85) } };
                    break;
                case RecipeOp.Compress:
                    studio = Studio() with
                    {
                        Export = Studio().Export with
                        {
                            TargetBytes = step.GetInt("maxBytes", 2 * 1024 * 1024),
                            Format = step.Get("format", Studio().Export.Format == "auto" ? "jpg" : Studio().Export.Format),
                            MinQuality = step.GetInt("minQuality", 40),
                            AllowDownscale = step.Get("allowDownscale", "true") == "true",
                        },
                    };
                    break;
                case RecipeOp.RemoveMetadata:
                    metadataPending = true;
                    metadataSpec = step.Get("remove", "all");
                    break;
                case RecipeOp.CombineToPdf:
                    Flush();
                    stages.Add(new CombineStage(step.Get("page", "a4")));
                    break;
            }
        }
        Flush();

        // Studio exports never carry metadata, so a separate (lossless) metadata stage is only
        // needed when nothing else re-renders the pictures.
        if (metadataPending && !stages.OfType<StudioStage>().Any() && !stages.OfType<PagesStage>().Any())
            stages.Add(new MetadataStage(metadataSpec));
        if (stages.Count == 0) throw new ProcessingException("The recipe has no steps that produce a file.");
        return stages;
    }

    /// <summary>Builds the editor document for an interactive recipe (all image steps applied, crop left to the user).</summary>
    public static EditDocument InteractiveTemplate(Recipe recipe, string sourcePath, Func<string, BrandPreset?>? brands = null)
    {
        var stages = Compile(recipe with { Steps = recipe.Steps.Where(s => s.Op is not (RecipeOp.RenderPdfPages or RecipeOp.CombineToPdf)).ToList(), Interactive = false }, brands);
        var studio = stages.OfType<StudioStage>().FirstOrDefault()?.Template ?? new EditDocument();
        return studio with { SourcePath = sourcePath };
    }

    private static bool IsFormatOnly(EditDocument d) =>
        d.Crop.IsIdentity && d.Crop.Aspect is "free" or "original" && d.Adjust.IsIdentity && !d.Background.Enabled
        && d.Export.MaxEdge is null && d.Export.TargetBytes is null && d.Brand is null;

    internal static FileKind FormatOf(string format, FileKind fallback) => format.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => FileKind.Jpeg,
        "png" => FileKind.Png,
        "webp" => FileKind.WebP,
        "avif" => FileKind.Avif,
        "tiff" => FileKind.Tiff,
        "bmp" => FileKind.Bmp,
        _ => fallback,
    };

    internal static string Invariant(double v) => v.ToString(CultureInfo.InvariantCulture);
}
