using NetVips;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Jobs;
using Toolbox.Core.Presets;
using Toolbox.Core.Recipes;
using Toolbox.Processing.Recipes;
using Toolbox.Processing.Studio;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

namespace Toolbox.Processing.Tests;

public sealed class RecipeTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private Task<Job> RunRecipe(Recipe recipe, params string[] files) =>
        _ws.Run(recipe.Steps.Any(s => s.Op == RecipeOp.CombineToPdf) ? Ids.RunRecipeCombined : Ids.RunRecipe, files, ("recipe", RecipeStore.ToJson(recipe)));

    private static Recipe BuiltIn(string id) => BuiltInRecipes.All.Single(r => r.Id == id);

    [Fact]
    public void Consecutive_image_steps_fuse_into_one_render()
    {
        var stages = RecipeCompiler.Compile(BuiltIn(BuiltInRecipes.WebsiteImages));
        var studio = Assert.IsType<StudioStage>(Assert.Single(stages)); // resize + webp + metadata = one encode
        Assert.Equal(1600, studio.Template.Export.MaxEdge);
        Assert.Equal("webp", studio.Template.Export.Format);
    }

    [Fact]
    public void Page_render_followed_by_format_is_a_single_render()
    {
        var render = Assert.IsType<PagesStage>(Assert.Single(RecipeCompiler.Compile(BuiltIn(BuiltInRecipes.SharePages))));
        Assert.Equal(Toolbox.Core.Inputs.FileKind.Jpeg, render.Format);
        Assert.Equal(150, render.Dpi);
    }

    [Fact]
    public void Metadata_only_recipe_stays_lossless()
    {
        Assert.IsType<MetadataStage>(Assert.Single(RecipeCompiler.Compile(BuiltIn(BuiltInRecipes.PrivacyClean))));
    }

    [Fact]
    public async Task Website_images_recipe_resizes_converts_and_reports_requirements()
    {
        var src = _ws.Png("hero banner.png", 3200, 2000);
        var job = await RunRecipe(BuiltIn(BuiltInRecipes.WebsiteImages), src);
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error);
        var output = Assert.Single(item.Outputs);
        Assert.Equal(_ws.PathOf("hero banner Web.webp"), output.Path);
        Assert.Equal((1600, 1000), (output.Width!.Value, output.Height!.Value));
        Assert.Contains(item.Notes, n => n.StartsWith("✓ Meets"));
        Assert.Empty(StagingLeftovers());
    }

    [Fact]
    public async Task Email_recipe_reports_when_the_size_cannot_be_met()
    {
        using var noise = Image.Gaussnoise(1200, 900, mean: 128, sigma: 60).Cast(Enums.BandFormat.Uchar);
        using var rgb = noise.Bandjoin(noise, noise).Copy(interpretation: Enums.Interpretation.Srgb);
        var src = _ws.PathOf("noisy.png");
        rgb.Pngsave(src);
        var strict = BuiltIn(BuiltInRecipes.EmailAttachment) with
        {
            Steps = [new(RecipeOp.Compress, new() { ["maxBytes"] = "4096", ["format"] = "jpg", ["allowDownscale"] = "false" })],
            Requirements = new RecipeRequirements { MaxBytes = 4096, Formats = ["jpg"] },
        };
        var job = await RunRecipe(strict, src);
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error); // the best effort is still saved…
        Assert.Contains(item.Notes, n => n.StartsWith("Size target NOT met"));
        Assert.Contains(item.Notes, n => n.StartsWith("✗ Requirement not met") && n.Contains("over the 4 KB limit")); // …and clearly flagged
        Assert.Equal("4 KB", Bytes.Format(4096));
    }

    [Fact]
    public async Task Share_pages_recipe_exports_every_page_in_order()
    {
        var pdf = _ws.Pdf("Minutes.pdf", 3);
        var job = await RunRecipe(BuiltIn(BuiltInRecipes.SharePages), pdf);
        var names = job.Items[0].Outputs.Select(o => Path.GetFileName(o.Path)).ToList();
        Assert.Equal(["Minutes - Page 01.jpg", "Minutes - Page 02.jpg", "Minutes - Page 03.jpg"], names);
        Assert.Equal((1275, 1650), (job.Items[0].Outputs[0].Width!.Value, job.Items[0].Outputs[0].Height!.Value)); // 150 DPI letter
    }

    [Fact]
    public async Task Chain_with_combine_makes_one_pdf_and_no_intermediate_files()
    {
        var a = _ws.Png("Scan 1.png", 2400, 3200);
        var b = _ws.Png("Scan 2.png", 2400, 3200);
        var recipe = new Recipe
        {
            Name = "Scans to PDF",
            Steps = [new(RecipeOp.Resize, new() { ["maxEdge"] = "1200" }), new(RecipeOp.Convert, new() { ["format"] = "jpg", ["quality"] = "80" }), new(RecipeOp.CombineToPdf)],
            Suffix = " Combined",
        };
        var job = await RunRecipe(recipe, a, b);
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error);
        var output = Assert.Single(item.Outputs);
        Assert.Equal(2, output.Pages);
        Assert.EndsWith(" Combined.pdf", output.Path);
        // The two sources + the PDF; nothing in between (the workspace's own "_" bookkeeping files aside).
        Assert.Equal(3, Directory.GetFiles(_ws.Root).Count(f => !Path.GetFileName(f).StartsWith('_')));
        Assert.Empty(StagingLeftovers());
    }

    [Fact]
    public async Task Export_pack_frames_every_ratio_without_cutting()
    {
        var src = _ws.Png("post.png", 1000, 600);
        var recipe = new Recipe
        {
            Name = "Social pack",
            Steps = [new(RecipeOp.Frame, new() { ["fill"] = "preset:dawn", ["padding"] = "50", ["ratios"] = "1:1,9:16,16:9" }), new(RecipeOp.Convert, new() { ["format"] = "png" })],
            Suffix = "",
        };
        var job = await RunRecipe(recipe, src);
        var outputs = job.Items[0].Outputs.OrderBy(o => o.Path).ToList();
        Assert.Equal(3, outputs.Count);
        var square = outputs.Single(o => o.Path.EndsWith(" 1x1.png"));
        Assert.Equal(square.Width, square.Height);
        Assert.True(square.Width >= 1100); // content (1000) + padding never cut
        var story = outputs.Single(o => o.Path.EndsWith(" 9x16.png"));
        Assert.InRange((double)story.Width!.Value / story.Height!.Value, 0.55, 0.575);
    }

    [Fact]
    public void Branding_draws_logo_and_caption()
    {
        var src = _ws.Png("chart.png", 800, 500);
        var logo = _ws.PathOf("logo.png");
        using (var red = Image.Black(200, 80, bands: 3).Linear([1, 1, 1], [230, 20, 20]).Cast(Enums.BandFormat.Uchar).Copy(interpretation: Enums.Interpretation.Srgb))
            red.Pngsave(logo);
        using var source = StudioSource.Open(src);
        var doc = new EditDocument
        {
            SourcePath = src,
            Background = new BackgroundSettings { Enabled = true, Fill = "#FFFFFF", Padding = 120, Shadow = 0, Corner = 0 },
            Brand = new BrandingSettings { LogoPath = logo, LogoPosition = "bottom-right", LogoScale = 0.15, Caption = "تقرير الربع الأول — Q1 report", CaptionColor = "#000000" },
        };
        using var result = StudioRenderer.Render(doc, source, Toolbox.Processing.Studio.RenderStage.Final, null, out _);
        // Bottom-right corner area: the red logo.
        var p = result.Getpoint(result.Width - 40, result.Height - 30);
        Assert.True(p[0] > 200 && p[1] < 60, $"logo pixel {string.Join(",", p)}");
        // The caption darkens part of the bottom padding band.
        using var band = result.ExtractArea(0, 500 + 120 + 10, result.Width, 100);
        Assert.True(band.Min() < 100, "caption text should be drawn in the bottom padding");
    }

    [Fact]
    public void Recipe_store_saves_duplicates_and_imports()
    {
        var folder = Path.Combine(_ws.Root, "recipes");
        var store = new RecipeStore(folder);
        Assert.Contains(store.All(), r => r.Id == BuiltInRecipes.WebsiteImages);

        var mine = new Recipe { Name = "Thumbnails", Steps = [new(RecipeOp.Resize, new() { ["maxEdge"] = "320" })], Suffix = " Thumb" };
        store.Save(mine);
        Assert.Equal("Thumbnails", store.Find(mine.Id)!.Name);

        var copy = store.Duplicate(store.Find(BuiltInRecipes.EmailAttachment)!);
        Assert.False(copy.BuiltIn);
        Assert.Equal("Email attachment (under 2 MB) (copy)", store.Find(copy.Id)!.Name);

        var exported = Path.Combine(_ws.Root, "share.ftbrecipe.json");
        RecipeStore.Export(mine, exported);
        var imported = store.Import(exported);
        Assert.NotEqual(mine.Id, imported.Id);
        Assert.Equal(mine.Steps.Count, imported.Steps.Count);

        store.Save(store.Find(BuiltInRecipes.SharePages)! with { Pinned = false });
        Assert.False(store.Find(BuiltInRecipes.SharePages)!.Pinned);
    }

    [Fact]
    public void Will_produce_explains_the_plan_before_running()
    {
        var text = BuiltIn(BuiltInRecipes.WebsiteImages).WillProduce(3);
        Assert.Contains("each of 3 files", text);
        Assert.Contains("1600 px", text);
        Assert.Contains("WEBP", text);
        Assert.Contains("originals are not changed", text);
        Assert.Contains("checked", text);
    }

    private static string[] StagingLeftovers()
    {
        var staging = Path.Combine(AppPaths.DataDir, "staging");
        return Directory.Exists(staging) ? Directory.GetDirectories(staging) : [];
    }
}
