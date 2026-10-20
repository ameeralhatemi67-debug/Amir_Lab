using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toolbox.Core.Inputs;

namespace Toolbox.Core.Recipes;

/// <summary>The operations a recipe can chain. Each has a defined meaning and settings.</summary>
public enum RecipeOp
{
    /// <summary>PDF → one image per selected page. Settings: pages, dpi.</summary>
    RenderPdfPages,

    /// <summary>Keep the centred area with an aspect ratio. Settings: ratio (e.g. 1:1).</summary>
    CenterCrop,

    /// <summary>Scale down so the longest side is at most N pixels. Settings: maxEdge.</summary>
    Resize,

    /// <summary>Place on a styled canvas. Settings: brand (preset id) or fill, padding, corner, shadow, ratio.</summary>
    Frame,

    /// <summary>Encode as a format. Settings: format (jpg/png/webp/avif/tiff), quality.</summary>
    Convert,

    /// <summary>Fit under a size. Settings: maxBytes, minQuality, allowDownscale.</summary>
    Compress,

    /// <summary>Remove metadata. Settings: remove (all, location, …).</summary>
    RemoveMetadata,

    /// <summary>All current files → one PDF. Settings: page (a4/letter/fit).</summary>
    CombineToPdf,
}

public sealed record RecipeStep(RecipeOp Op, Dictionary<string, string>? Settings = null)
{
    public string Get(string key, string fallback) =>
        Settings is not null && Settings.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    public int GetInt(string key, int fallback) =>
        int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Plain-language description used in the "will produce" summary and the editor.</summary>
    public string Describe() => Op switch
    {
        RecipeOp.RenderPdfPages => $"render {(Get("pages", "all") == "all" ? "every page" : "pages " + Get("pages", "all"))} at {Get("dpi", "150")} DPI",
        RecipeOp.CenterCrop => $"crop to {Get("ratio", "1:1")} (centred)",
        RecipeOp.Resize => $"resize so the longest side is at most {GetInt("maxEdge", 1600)} px",
        RecipeOp.Frame => Get("brand", "") is { Length: > 0 } brand ? $"frame with the \"{brand}\" brand style" : $"place on a {Get("fill", "preset:paper").Replace("preset:", "")} background{(Get("ratio", "auto") == "auto" ? "" : $" at {Get("ratio", "auto")}")}",
        RecipeOp.Convert => Get("format", "jpg").ToLowerInvariant() is "png" or "tiff" ? $"save as {Get("format", "jpg").ToUpperInvariant()}" : $"save as {Get("format", "jpg").ToUpperInvariant()} (quality {GetInt("quality", 85)})",
        RecipeOp.Compress => $"fit under {Bytes.Format(GetInt("maxBytes", 2 * 1024 * 1024))}{(Get("allowDownscale", "true") == "true" ? " (may reduce size)" : "")}",
        RecipeOp.RemoveMetadata => Get("remove", "all") == "all" ? "remove all metadata" : $"remove {Get("remove", "all").Replace(",", ", ")} metadata",
        RecipeOp.CombineToPdf => "combine everything into one PDF",
        _ => Op.ToString(),
    };
}

/// <summary>Conditions every result must meet; checked after running and reported as met or not met.</summary>
public sealed record RecipeRequirements
{
    public long? MaxBytes { get; init; }
    public List<string>? Formats { get; init; }
    public int? MaxEdge { get; init; }

    [JsonIgnore]
    public bool IsEmpty => MaxBytes is null && (Formats is null || Formats.Count == 0) && MaxEdge is null;
}

/// <summary>
/// A saved, named sequence of operations. Execution is deterministic and local; nothing is guessed.
/// Interactive recipes open the editor with the recipe's settings applied so the user can crop or annotate first.
/// </summary>
public sealed record Recipe
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; init; } = "New recipe";

    /// <summary>Short wheel label (about 8 characters); derived from the name when empty.</summary>
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";

    [JsonIgnore]
    public string WheelLabel => Label.Length > 0 ? Label : Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Recipe";
    public List<RecipeStep> Steps { get; init; } = [];

    /// <summary>Appended to the original name of each result, e.g. " Web".</summary>
    public string Suffix { get; init; } = "";

    /// <summary>Folder for results; null saves next to each original.</summary>
    public string? Destination { get; init; }

    /// <summary>Opens the editor (crop step first) with this recipe's framing and export applied.</summary>
    public bool Interactive { get; init; }

    public bool Pinned { get; init; }
    public bool BuiltIn { get; init; }
    public RecipeRequirements Requirements { get; init; } = new();

    /// <summary>What kinds of input the first step accepts.</summary>
    [JsonIgnore]
    public IReadOnlySet<FileKind> Accepts =>
        Steps.FirstOrDefault()?.Op == RecipeOp.RenderPdfPages
            ? new HashSet<FileKind> { FileKind.Pdf }
            : Interactive
                ? new HashSet<FileKind>(Actions.CapabilityMap.ImageInputs.Append(FileKind.Pdf))
                : Actions.CapabilityMap.ImageInputs;

    /// <summary>Human-readable "what will happen" text, shown before running.</summary>
    public string WillProduce(int fileCount = 1)
    {
        var parts = Steps.Select(s => s.Describe()).ToList();
        if (Interactive) parts.Insert(0, "open the editor so you can crop and mark up");
        var what = parts.Count == 0 ? "copy the file" : string.Join(" → ", parts);
        var where = Destination is null ? "next to each original" : $"in {Destination}";
        var combined = Steps.Any(s => s.Op == RecipeOp.CombineToPdf);
        var count = combined ? "one PDF" : fileCount == 1 ? "one result" : $"{fileCount} results";
        var checks = Requirements.IsEmpty ? "" : " Each result is checked: " + string.Join(", ", RequirementsText()) + ".";
        return $"For {(fileCount == 1 ? "the file" : $"each of {fileCount} files")}: {what}. Saves {count} {where}; originals are not changed.{checks}";
    }

    public IEnumerable<string> RequirementsText()
    {
        if (Requirements.MaxBytes is { } max) yield return $"under {Bytes.Format(max)}";
        if (Requirements.Formats is { Count: > 0 } formats) yield return string.Join("/", formats.Select(f => f.ToUpperInvariant()));
        if (Requirements.MaxEdge is { } edge) yield return $"at most {edge} px";
    }
}

/// <summary>Stores user recipes as JSON files; built-in recipes are always available and cannot be overwritten.</summary>
public sealed class RecipeStore
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;

    public RecipeStore(string? folder = null)
    {
        _folder = folder ?? AppPaths.RecipesDir;
        Directory.CreateDirectory(_folder);
    }

    public event Action? Changed;

    public IReadOnlyList<Recipe> All()
    {
        var user = new List<Recipe>();
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<Recipe>(File.ReadAllText(file), Json) is { } r) user.Add(r with { BuiltIn = false });
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Diagnostics.Log.Warn($"Skipped an unreadable recipe file: {Path.GetFileName(file)} ({ex.Message})");
            }
        }
        var pins = ReadPins();
        var builtIns = BuiltInRecipes.All.Select(b => pins.TryGetValue(b.Id, out var pinned) ? b with { Pinned = pinned } : b);
        return builtIns.Concat(user.OrderBy(r => r.Name, NaturalComparer.Instance)).ToList();
    }

    public Recipe? Find(string id) => All().FirstOrDefault(r => r.Id == id);

    public void Save(Recipe recipe)
    {
        if (recipe.BuiltIn)
        {
            // Built-ins only remember whether they are pinned.
            var pins = ReadPins();
            pins[recipe.Id] = recipe.Pinned;
            File.WriteAllText(PinsFile, JsonSerializer.Serialize(pins, Json));
        }
        else
        {
            File.WriteAllText(PathFor(recipe.Id), JsonSerializer.Serialize(recipe with { BuiltIn = false }, Json));
        }
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
        Changed?.Invoke();
    }

    public Recipe Duplicate(Recipe recipe)
    {
        var copy = recipe with { Id = Guid.NewGuid().ToString("N")[..10], Name = recipe.Name + " (copy)", BuiltIn = false, Pinned = false };
        Save(copy);
        return copy;
    }

    /// <summary>Exports a recipe for sharing (local file; no synchronisation service).</summary>
    public static void Export(Recipe recipe, string path) => File.WriteAllText(path, JsonSerializer.Serialize(recipe with { BuiltIn = false, Pinned = false }, Json));

    public Recipe Import(string path)
    {
        var recipe = JsonSerializer.Deserialize<Recipe>(File.ReadAllText(path), Json) ?? throw new ProcessingException("The file is not a recipe.");
        if (recipe.Steps.Count == 0 && !recipe.Interactive) throw new ProcessingException("The recipe has no steps.");
        var imported = recipe with { Id = Guid.NewGuid().ToString("N")[..10], BuiltIn = false };
        Save(imported);
        return imported;
    }

    public static string ToJson(Recipe recipe) => JsonSerializer.Serialize(recipe, Json);

    public static Recipe FromJson(string json) => JsonSerializer.Deserialize<Recipe>(json, Json) ?? throw new FormatException("Invalid recipe.");

    private string PinsFile => Path.Combine(_folder, "_builtin-pins.state");

    private Dictionary<string, bool> ReadPins()
    {
        try
        {
            return File.Exists(PinsFile)
                ? JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(PinsFile), Json) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private string PathFor(string id) => Path.Combine(_folder, OutputNamerSafe(id) + ".json");

    private static string OutputNamerSafe(string id) => string.Concat(id.Where(char.IsLetterOrDigit));
}
