using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toolbox.Core.Presets;

/// <summary>
/// A reusable visual style for framed exports: background, spacing, corner and shadow treatment,
/// an optional logo/watermark and caption. Stored locally; no account or team service needed.
/// </summary>
public sealed record BrandPreset
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; init; } = "My brand";

    /// <summary>"preset:&lt;id&gt;", "#RRGGBB" or "image:&lt;path&gt;".</summary>
    public string Fill { get; init; } = "preset:paper";
    public int Padding { get; init; } = 100;
    public int Corner { get; init; } = 24;
    public int Shadow { get; init; } = 32;

    /// <summary>PNG or SVG logo; null for none.</summary>
    public string? LogoPath { get; init; }

    /// <summary>top-left, top-right, bottom-left, bottom-right, bottom-center.</summary>
    public string LogoPosition { get; init; } = "bottom-right";

    /// <summary>Logo width as a fraction of the canvas width.</summary>
    public double LogoScale { get; init; } = 0.14;
    public double LogoOpacity { get; init; } = 0.9;

    public string? Caption { get; init; }

    /// <summary>top or bottom (inside the padding).</summary>
    public string CaptionPosition { get; init; } = "bottom";
    public string CaptionColor { get; init; } = "#1F2937";

    /// <summary>Caption height as a fraction of the canvas height.</summary>
    public double CaptionScale { get; init; } = 0.035;
    public string FontFamily { get; init; } = "Segoe UI";

    /// <summary>Optional naming suffix used by exports with this brand.</summary>
    public string? Suffix { get; init; }
}

public sealed class BrandStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _folder;

    public BrandStore(string? folder = null)
    {
        _folder = folder ?? AppPaths.PresetsDir;
        Directory.CreateDirectory(_folder);
    }

    public event Action? Changed;

    public IReadOnlyList<BrandPreset> All()
    {
        var list = new List<BrandPreset>();
        foreach (var file in Directory.EnumerateFiles(_folder, "brand-*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<BrandPreset>(File.ReadAllText(file), Json) is { } b) list.Add(b);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Diagnostics.Log.Warn($"Skipped an unreadable brand preset: {Path.GetFileName(file)}");
            }
        }
        return list.OrderBy(b => b.Name, NaturalComparer.Instance).ToList();
    }

    public BrandPreset? Find(string idOrName) =>
        All().FirstOrDefault(b => b.Id == idOrName || b.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public void Save(BrandPreset preset)
    {
        File.WriteAllText(Path.Combine(_folder, $"brand-{Safe(preset.Id)}.json"), JsonSerializer.Serialize(preset, Json));
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        var path = Path.Combine(_folder, $"brand-{Safe(id)}.json");
        if (File.Exists(path)) File.Delete(path);
        Changed?.Invoke();
    }

    private static string Safe(string id) => string.Concat(id.Where(char.IsLetterOrDigit));
}
