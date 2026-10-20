using System.Windows.Media;

namespace Toolbox.Windows.Theme;

/// <summary>Original line icons on a 24×24 grid, drawn with a round-capped stroke.</summary>
internal static class Icons
{
    private static readonly Dictionary<string, Geometry> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Data = new(StringComparer.OrdinalIgnoreCase)
    {
        ["crop"] = "M6,2 L6,18 L22,18 M2,6 L18,6 L18,22",
        ["compress"] = "M4,14 L10,14 L10,20 M20,10 L14,10 L14,4 M10,14 L3,21 M14,10 L21,3",
        ["tag"] = "M3,3 L12,3 L21,12 L12,21 L3,12 Z M8.5,7.5 A1,1 0 1 0 6.5,7.5 A1,1 0 1 0 8.5,7.5",
        ["sliders"] = "M4,6 L20,6 M4,12 L20,12 M4,18 L20,18 M9,3 L9,9 M15,9 L15,15 M7,15 L7,21",
        ["pen"] = "M3,21 L3.5,16.5 L15,5 L19,9 L7.5,20.5 Z M13,7 L17,11",
        ["frame"] = "M3,5 L21,5 L21,19 L3,19 Z M3,16 L9,10 L14,15 L16,13 L21,18 M16.5,9.5 A1,1 0 1 0 14.5,9.5 A1,1 0 1 0 16.5,9.5",
        ["image"] = "M3,5 L21,5 L21,19 L3,19 Z M3,16 L9,10 L14,15 L16,13 L21,18",
        ["eye-off"] = "M2,12 C5,6 19,6 22,12 C19,18 5,18 2,12 Z M14.5,12 A2.5,2.5 0 1 0 9.5,12 A2.5,2.5 0 1 0 14.5,12 M4,4 L20,20",
        ["text"] = "M5,5 L19,5 M12,5 L12,19 M9,19 L15,19",
        ["doc"] = "M6,2 L14,2 L19,7 L19,22 L6,22 Z M14,2 L14,7 L19,7 M9,12 L16,12 M9,16 L16,16",
        ["pdf"] = "M6,2 L14,2 L19,7 L19,22 L6,22 Z M14,2 L14,7 L19,7 M9,13 L16,13 M9,17 L13,17",
        ["scan"] = "M3,8 L3,3 L8,3 M16,3 L21,3 L21,8 M21,16 L21,21 L16,21 M8,21 L3,21 L3,16 M7,12 L17,12",
        ["export"] = "M12,3 L12,15 M7,8 L12,3 L17,8 M5,14 L5,21 L19,21 L19,14",
        ["recipe"] = "M13,2 L4,14 L12,14 L11,22 L20,10 L12,10 Z",
        ["rotate"] = "M20,11 A8,8 0 1 0 17.6,17.6 M20,4 L20,11 L13,11",
        ["close"] = "M6,6 L18,18 M18,6 L6,18",
        ["folder"] = "M3,6 L9,6 L11,8 L21,8 L21,19 L3,19 Z",
        ["open"] = "M14,3 L21,3 L21,10 M21,3 L11,13 M19,14 L19,21 L3,21 L3,5 L10,5",
        ["undo"] = "M9,14 L4,9 L9,4 M4,9 L15,9 A5,5 0 0 1 15,19 L11,19",
        ["repeat"] = "M17,2 L21,6 L17,10 M3,11 L3,10 A4,4 0 0 1 7,6 L21,6 M7,22 L3,18 L7,14 M21,13 L21,14 A4,4 0 0 1 17,18 L3,18",
        ["check"] = "M5,12 L10,17 L19,7",
        ["warning"] = "M12,3 L22,20 L2,20 Z M12,10 L12,14 M12,17 L12,17.5",
    };

    public static Geometry Get(string key)
    {
        if (Cache.TryGetValue(key, out var geometry)) return geometry;
        var data = Data.TryGetValue(key, out var d) ? d : Data["image"];
        geometry = Geometry.Parse(data);
        geometry.Freeze();
        Cache[key] = geometry;
        return geometry;
    }
}
