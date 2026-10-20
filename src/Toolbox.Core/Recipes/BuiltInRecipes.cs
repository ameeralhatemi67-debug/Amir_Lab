namespace Toolbox.Core.Recipes;

/// <summary>Result-oriented recipes from the product brief. They can be duplicated and edited.</summary>
public static class BuiltInRecipes
{
    public const string WebsiteImages = "builtin-web";
    public const string EmailAttachment = "builtin-email";
    public const string ReportGraphic = "builtin-report";
    public const string SharePages = "builtin-pages";
    public const string PresentationScreenshot = "builtin-slide";
    public const string PrivacyClean = "builtin-private";

    public static IReadOnlyList<Recipe> All { get; } =
    [
        new Recipe
        {
            Id = WebsiteImages,
            Label = "Web",
            Name = "Website images",
            Description = "Smaller pictures for web pages: at most 1600 px, WebP, no metadata.",
            Steps =
            [
                new(RecipeOp.Resize, new() { ["maxEdge"] = "1600" }),
                new(RecipeOp.Convert, new() { ["format"] = "webp", ["quality"] = "80" }),
                new(RecipeOp.RemoveMetadata, new() { ["remove"] = "all" }),
            ],
            Suffix = " Web",
            Requirements = new RecipeRequirements { MaxEdge = 1600, Formats = ["webp"] },
            Pinned = true,
            BuiltIn = true,
        },
        new Recipe
        {
            Id = EmailAttachment,
            Label = "Email",
            Name = "Email attachment (under 2 MB)",
            Description = "A JPG that fits under 2 MB, lowering quality first and size only if needed.",
            Steps = [new(RecipeOp.Compress, new() { ["maxBytes"] = (2 * 1024 * 1024).ToString(), ["format"] = "jpg", ["allowDownscale"] = "true" })],
            Suffix = " Email",
            Requirements = new RecipeRequirements { MaxBytes = 2 * 1024 * 1024, Formats = ["jpg"] },
            Pinned = true,
            BuiltIn = true,
        },
        new Recipe
        {
            Id = ReportGraphic,
            Label = "Report",
            Name = "Report graphic",
            Description = "Pick a page or picture, crop it, then frame it on a soft paper background as a PNG.",
            Interactive = true,
            Steps =
            [
                new(RecipeOp.Frame, new() { ["fill"] = "preset:paper", ["padding"] = "80", ["corner"] = "18", ["shadow"] = "28", ["ratio"] = "auto" }),
                new(RecipeOp.Convert, new() { ["format"] = "png" }),
            ],
            Suffix = " Report",
            Pinned = true,
            BuiltIn = true,
        },
        new Recipe
        {
            Id = SharePages,
            Label = "Pages",
            Name = "Share document pages",
            Description = "Every page of a PDF as a JPG at 150 DPI, numbered in order.",
            Steps =
            [
                new(RecipeOp.RenderPdfPages, new() { ["pages"] = "all", ["dpi"] = "150" }),
                new(RecipeOp.Convert, new() { ["format"] = "jpg", ["quality"] = "85" }),
            ],
            Suffix = "",
            Pinned = true,
            BuiltIn = true,
        },
        new Recipe
        {
            Id = PresentationScreenshot,
            Label = "Slide",
            Name = "Presentation screenshot",
            Description = "Crop and mark up a screenshot, then frame it for a 16:9 slide.",
            Interactive = true,
            Steps =
            [
                new(RecipeOp.Frame, new() { ["fill"] = "preset:slate", ["padding"] = "90", ["corner"] = "22", ["shadow"] = "40", ["ratio"] = "16:9" }),
                new(RecipeOp.Convert, new() { ["format"] = "png" }),
            ],
            Suffix = " Slide",
            Pinned = true,
            BuiltIn = true,
        },
        new Recipe
        {
            Id = PrivacyClean,
            Label = "Private",
            Name = "Remove location and camera details",
            Description = "Copies the photo without GPS, camera serials and the hidden thumbnail. JPEG pixels are not recompressed.",
            Steps = [new(RecipeOp.RemoveMetadata, new() { ["remove"] = "location,camera,thumbnail" })],
            Suffix = " Private",
            BuiltIn = true,
        },
    ];
}
