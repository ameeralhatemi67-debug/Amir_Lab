namespace Toolbox.Processing.Studio;

/// <summary>A gradient stop: colour (#RRGGBB) at an offset in [0,1].</summary>
public readonly record struct GradientStop(string Color, double Offset);

/// <summary>
/// Original background presets (shared by the editor swatches and the renderer, so what the user
/// picks is exactly what is drawn). Angle is in degrees, 0 = left→right, 90 = top→bottom.
/// </summary>
public sealed record GradientPreset(string Id, string Name, double Angle, IReadOnlyList<GradientStop> Stops, string? GlowColor = null);

public static class BackgroundPresets
{
    public static IReadOnlyList<GradientPreset> Gradients { get; } =
    [
        new("dawn", "Dawn", 135, [new("#FF8A5B", 0), new("#FF5E8E", 0.55), new("#8E6CFF", 1)], "#FFD36E"),
        new("lagoon", "Lagoon", 135, [new("#00C6A7", 0), new("#1E6FFF", 1)], "#7CF4D8"),
        new("lilac", "Lilac", 160, [new("#E9D7FF", 0), new("#B49CFF", 0.6), new("#7C83FD", 1)]),
        new("mint", "Mint", 120, [new("#D4FCE4", 0), new("#7BE0B5", 1)]),
        new("citrus", "Citrus", 125, [new("#FFF27A", 0), new("#FFB547", 0.6), new("#FF7A45", 1)]),
        new("aurora", "Aurora", 145, [new("#0F2A47", 0), new("#1B6B8C", 0.45), new("#46C2A3", 1)], "#9B7BFF"),
        new("rose", "Rose", 140, [new("#FFE1E8", 0), new("#FF9AB2", 0.6), new("#E8577A", 1)]),
        new("slate", "Slate", 150, [new("#3A4256", 0), new("#1A1E29", 1)]),
        new("paper", "Paper", 90, [new("#FBFAF7", 0), new("#ECE8E1", 1)]),
        new("ember", "Ember", 135, [new("#3B0A1E", 0), new("#B3263E", 0.6), new("#FF8A3D", 1)], "#FFC56B"),
    ];

    public static IReadOnlyList<string> Solids { get; } =
    [
        "#FFFFFF", "#F2F2F4", "#FFD166", "#8BD17C", "#2BB673", "#26C6DA", "#3478F6", "#1F2937", "#7B61FF", "#111111",
    ];

    public static GradientPreset? Find(string id) => Gradients.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Width/height for a ratio name, or null for auto.</summary>
    public static double? Ratio(string ratio) => ratio.ToLowerInvariant() switch
    {
        "1:1" => 1.0,
        "16:9" => 16.0 / 9,
        "9:16" => 9.0 / 16,
        "4:5" => 4.0 / 5,
        "4:3" => 4.0 / 3,
        "3:4" => 3.0 / 4,
        "3:2" => 3.0 / 2,
        _ => null,
    };
}
