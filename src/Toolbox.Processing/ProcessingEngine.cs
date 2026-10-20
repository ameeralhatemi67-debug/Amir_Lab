using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Processing.Handlers;
using Toolbox.Processing.Imaging;
using Toolbox.Processing.Verify;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

namespace Toolbox.Processing;

/// <summary>Entry point that wires the local engines into the platform-neutral job model.</summary>
public static class ProcessingEngine
{
    public static void Initialize() => VipsRuntime.EnsureInitialized();

    /// <summary>Resolves brand presets for recipes (set by the host; defaults to the local preset store).</summary>
    public static Func<string, Toolbox.Core.Presets.BrandPreset?> Brands { get; set; } = id => new Toolbox.Core.Presets.BrandStore().Find(id);

    public static IReadOnlyList<IActionHandler> CreateHandlers() =>
    [
        new Recipes.RecipeHandler(Ids.RunRecipe, id => Brands(id)),
        new Recipes.RecipeHandler(Ids.RunRecipeCombined, id => Brands(id)),
        new ImageConvertHandler(Ids.ImageToJpg, FileKind.Jpeg, 90),
        new ImageConvertHandler(Ids.ImageToPng, FileKind.Png, 90),
        new ImageConvertHandler(Ids.ImageToWebp, FileKind.WebP, 85),
        new ImageConvertHandler(Ids.ImageToAvif, FileKind.Avif, 60),
        new ImageConvertHandler(Ids.ImageToTiff, FileKind.Tiff, 90),
        new ImageConvertHandler(Ids.ImageToBmp, FileKind.Bmp, 90),
        new ImagesToPdfHandler(),
        new ImagesToDocxHandler(),
        new PdfRenderHandler(Ids.PdfToJpg, FileKind.Jpeg),
        new PdfRenderHandler(Ids.PdfToPng, FileKind.Png),
        new PdfTextHandler(Ids.PdfToTxt, FileKind.Text),
        new PdfTextHandler(Ids.PdfToDocxText, FileKind.Docx),
        new Studio.StudioExportHandler(),
        new Metadata.MetadataCleanHandler(),
    ];

    public static IOutputVerifier CreateVerifier() => new OutputVerifier();

    public static JobRunner CreateRunner(OutputManager outputs, int? maxConcurrency = null, IEnumerable<IActionHandler>? extraHandlers = null)
    {
        Initialize();
        var handlers = CreateHandlers().Concat(extraHandlers ?? []).ToList();
        return new JobRunner(handlers, outputs, CreateVerifier(), maxConcurrency);
    }
}
