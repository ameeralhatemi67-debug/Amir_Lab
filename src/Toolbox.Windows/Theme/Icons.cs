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
        ["eye"] = "M2,12 C5,6 19,6 22,12 C19,18 5,18 2,12 Z M14.5,12 A2.5,2.5 0 1 0 9.5,12 A2.5,2.5 0 1 0 14.5,12",
        ["sign"] = "M3,17 C5,11 7,9 8,11 C9,13 6,18 9,17 C11,16.5 12,12 13.5,13 C15,14 14,17 16,16 L18,15 M3,21 L21,21",
        ["pages"] = "M8,3 L17,3 L20,6 L20,17 L8,17 Z M4,7 L4,21 L16,21",
        ["moon"] = "M20,14.5 A8,8 0 0 1 9.5,4 A8,8 0 1 0 20,14.5 Z",
        ["search"] = "M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 Z M15.5,15.5 L20.5,20.5",
        ["zoom-in"] = "M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 Z M15.5,15.5 L20.5,20.5 M7.5,10.5 H13.5 M10.5,7.5 V13.5",
        ["zoom-out"] = "M10.5,4 A6.5,6.5 0 1 0 10.5,17 A6.5,6.5 0 1 0 10.5,4 Z M15.5,15.5 L20.5,20.5 M7.5,10.5 H13.5",
        ["fit-page"] = "M6,3 L18,3 L18,21 L6,21 Z M9,7 L15,7 M9,11 L15,11 M9,15 L13,15",
        ["fit-width"] = "M3,6 L21,6 L21,18 L3,18 Z M6,12 L18,12 M6,12 L8.5,9.5 M6,12 L8.5,14.5 M18,12 L15.5,9.5 M18,12 L15.5,14.5",
        ["outline"] = "M4,6 L5,6 M8,6 L20,6 M4,12 L5,12 M8,12 L20,12 M4,18 L5,18 M8,18 L20,18",
        ["split"] = "M3,4 L21,4 L21,20 L3,20 Z M12,4 L12,20",
        ["fullscreen"] = "M4,9 L4,4 L9,4 M15,4 L20,4 L20,9 M20,15 L20,20 L15,20 M9,20 L4,20 L4,15",
        ["present"] = "M3,4 L21,4 L21,16 L3,16 Z M12,16 L12,20 M8,20 L16,20 M10,8 L15,10 L10,12 Z",
        ["back"] = "M15,5 L8,12 L15,19",
        ["forward"] = "M9,5 L16,12 L9,19",
        ["first"] = "M17,5 L10,12 L17,19 M7,5 L7,19",
        ["last"] = "M7,5 L14,12 L7,19 M17,5 L17,19",
        ["rotate-left"] = "M4,11 A8,8 0 1 1 6.4,17.6 M4,4 L4,11 L11,11",
        ["theme"] = "M12,3 A9,9 0 1 0 12,21 Z M12,3 A9,9 0 0 1 12,21",
        ["cover"] = "M4,5 L20,5 L20,19 L4,19 Z M4,12 L20,12",
        ["highlighter"] = "M14,4 L20,10 L11,19 L5,19 L5,13 Z M3,21 L21,21",
        ["pointer"] = "M6,3 L6,19 L10,15 L13,21 L15.5,20 L12.5,14 L18,14 Z",
        ["overview"] = "M4,4 L10,4 L10,10 L4,10 Z M14,4 L20,4 L20,10 L14,10 Z M4,14 L10,14 L10,20 L4,20 Z M14,14 L20,14 L20,20 L14,20 Z",
        ["save"] = "M5,4 L16,4 L20,8 L20,20 L5,20 Z M8,4 L8,9 L15,9 L15,4 M8,20 L8,14 L17,14 L17,20",
        ["trash"] = "M5,7 L19,7 M10,7 L10,4 L14,4 L14,7 M7,7 L8,20 L16,20 L17,7 M10.5,11 L10.5,16.5 M13.5,11 L13.5,16.5",
        ["up"] = "M6,15 L12,9 L18,15",
        ["down"] = "M6,9 L12,15 L18,9",
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
        ["dashboard"] = "M3,3 L10,3 L10,10 L3,10 Z M14,3 L21,3 L21,10 L14,10 Z M3,14 L10,14 L10,21 L3,21 Z M14,14 L21,14 L21,21 L14,21 Z",
        ["toolbox"] = "M3,8 L21,8 L21,20 L3,20 Z M9,8 L9,5 L15,5 L15,8 M3,13 L21,13 M10,12 L14,12 L14,15 L10,15 Z",
        ["keyboard"] = "M2,6 L22,6 L22,18 L2,18 Z M6,10 L6,10.5 M10,10 L10,10.5 M14,10 L14,10.5 M18,10 L18,10.5 M7,14 L17,14",
        ["info"] = "M12,3 A9,9 0 1 0 12,21 A9,9 0 1 0 12,3 Z M12,11 L12,16 M12,7.5 L12,8",
        ["longshot"] = "M5,2 L19,2 L19,13 L5,13 Z M8,6 L16,6 M8,9 L13,9 M12,15.5 L12,22 M8.5,18.5 L12,22 L15.5,18.5",

        // Amir Lap window (glass design): drawn on the full 24-unit grid, shown with SettingsPage.Glyph.
        ["lap-dashboard"] = RoundRect(3.5, 3.5, 7, 7, 2) + RoundRect(13.5, 3.5, 7, 7, 2) + RoundRect(3.5, 13.5, 7, 7, 2) + RoundRect(13.5, 13.5, 7, 7, 2),
        ["lap-toolbox"] = RoundRect(3, 8, 18, 12, 3) + " M9,8 V6 A2,2 0 0 1 11,4 H13 A2,2 0 0 1 15,6 V8 M3,13 H21",
        ["lap-longshot"] = RoundRect(5, 3, 14, 11, 2.5) + " M12,17 V21.5 M9,18.8 L12,21.5 L15,18.8",
        ["lap-general"] = "M4,7 H13.8 M18.2,7 H20 M4,17 H7.8 M12.2,17 H20" + Circle(16, 7, 2.2) + Circle(10, 17, 2.2),
        ["lap-about"] = Circle(12, 12, 8.5) + " M12,11 V16.5 M12,7.6 V8",
        ["lap-sun"] = Circle(12, 12, 4.2) + " M12,2.5 V4.5 M12,19.5 V21.5 M2.5,12 H4.5 M19.5,12 H21.5 M5.3,5.3 L6.7,6.7 M17.3,17.3 L18.7,18.7 M5.3,18.7 L6.7,17.3 M17.3,6.7 L18.7,5.3",
        ["lap-moon"] = "M20,14.5 A8,8 0 0 1 9.5,4 A8,8 0 1 0 20,14.5 Z",
        ["lap-image"] = RoundRect(4, 4, 16, 16, 3) + " M4,16 L9,11 L13,15 L15,13 L20,18",
        ["lap-doc"] = "M7,3 H14 L18,7 V21 H7 Z M14,3 V7 H18 M10,12 H15 M10,16 H15",
        ["lap-check"] = "M5,12.5 L9.5,17 L19,7.5",
        ["lap-folder"] = "M3.5,7 A2,2 0 0 1 5.5,5 H9.5 L11.5,7 H18.5 A2,2 0 0 1 20.5,9 V17 A2,2 0 0 1 18.5,19 H5.5 A2,2 0 0 1 3.5,17 Z",
        ["lap-open"] = "M14,4 H20 V10 M20,4 L11,13 M18,14 V18 A2,2 0 0 1 16,20 H6 A2,2 0 0 1 4,18 V8 A2,2 0 0 1 6,6 H10",
        ["lap-warning"] = "M12,4 L21,19.5 H3 Z M12,10 V14 M12,16.8 V17.2",
        ["lap-refresh"] = "M19.5,12 A7.5,7.5 0 1 1 16.8,6.2 M17.5,2.8 L17,6.6 L20.8,7",
        ["lap-move"] = "M12,3 V21 M3,12 H21 M9,6 L12,3 L15,6 M9,18 L12,21 L15,18 M6,9 L3,12 L6,15 M18,9 L21,12 L18,15",
        ["lap-down"] = "M12,4 V19 M6,13 L12,19 L18,13",
        ["lap-play"] = "M7.5,5 L18.5,12 L7.5,19 Z",
        ["lap-stop"] = RoundRect(6.5, 6.5, 11, 11, 2.5),
        ["lap-follow"] = Circle(12, 12, 8.5) + Circle(12, 12, 3),
        // Longshot's targets: a window, a scroll area inside it (with its scrollbar), a region drawn by hand.
        ["lap-window"] = RoundRect(3.5, 4.5, 17, 15, 2.5) + " M3.5,8.5 H20.5 M6.5,6.5 V6.6 M9,6.5 V6.6",
        ["lap-scrollarea"] = RoundRect(3.5, 3.5, 17, 17, 2.5) + " M16.5,7 V17 M14.8,8.7 L16.5,7 L18.2,8.7 M14.8,15.3 L16.5,17 L18.2,15.3 M7,8 H12 M7,12 H12 M7,16 H11",
        ["lap-region"] = "M4,8 V5.5 A1.5,1.5 0 0 1 5.5,4 H8 M16,4 H18.5 A1.5,1.5 0 0 1 20,5.5 V8 M20,16 V18.5 A1.5,1.5 0 0 1 18.5,20 H16 M8,20 H5.5 A1.5,1.5 0 0 1 4,18.5 V16 M11,4 H13 M11,20 H13 M4,11 V13 M20,11 V13",
        ["lap-undo"] = "M9,14 L4,9 L9,4 M4,9 H14 A6,6 0 0 1 14,21 H11",
        ["lap-share"] = Circle(17.5, 5.5, 2.6) + Circle(6.5, 12, 2.6) + Circle(17.5, 18.5, 2.6) + " M8.8,10.7 L15.2,6.8 M8.8,13.3 L15.2,17.2",
        // Pulse and its tiles (generic marks, not the services' logos).
        ["lap-pulse"] = "M3,12 H7 L9.5,6 L13.5,18 L16,12 H21",
        ["lap-claude"] = "M12,3.5 V20.5 M3.5,12 H20.5 M6,6 L18,18 M18,6 L6,18",
        ["lap-codex"] = "M12,3 L20,7.5 V16.5 L12,21 L4,16.5 V7.5 Z M12,12 L20,7.5 M12,12 V21 M12,12 L4,7.5",
        ["lap-chat"] = "M5,5 H19 A2,2 0 0 1 21,7 V15 A2,2 0 0 1 19,17 H12 L8,20.5 V17 H5 A2,2 0 0 1 3,15 V7 A2,2 0 0 1 5,5 Z",
        ["lap-timer"] = Circle(12, 13.5, 7.5) + " M12,9.5 V13.5 L14.5,15.5 M10,2.5 H14",
        ["lap-note"] = "M6,3.5 H15 L19,7.5 V20.5 H6 Z M15,3.5 V7.5 H19 M9,12 H15 M9,16 H13",
        ["lap-colour"] = "M12,3.5 C15.5,7.7 18,11 18,14 A6,6 0 0 1 6,14 C6,11 8.5,7.7 12,3.5 Z",
        ["lap-link"] = "M10,14 A4,4 0 0 0 15.7,14 L18.7,11 A4,4 0 0 0 13,5.3 L12,6.3 M14,10 A4,4 0 0 0 8.3,10 L5.3,13 A4,4 0 0 0 11,18.7 L12,17.7",
        ["lap-system"] = RoundRect(6, 6, 12, 12, 2) + " M9,3 V6 M15,3 V6 M9,18 V21 M15,18 V21 M3,9 H6 M3,15 H6 M18,9 H21 M18,15 H21",
        ["lap-plus"] = "M12,6 V18 M6,12 H18",
        ["lap-copy"] = RoundRect(9, 9, 11, 11, 2) + " M15,9 V6 A2,2 0 0 0 13,4 H6 A2,2 0 0 0 4,6 V13 A2,2 0 0 0 6,15 H9",
        ["lap-star"] = "M12,4 L14.3,8.9 L19.6,9.5 L15.7,13.1 L16.7,18.4 L12,15.8 L7.3,18.4 L8.3,13.1 L4.4,9.5 L9.7,8.9 Z",
        ["lap-picker"] = "M4.5,19.5 L6.5,17.5 L14,10 M12.5,7.5 L16.5,11.5 M14.5,9.5 L18.6,5.4 A1.9,1.9 0 0 0 15.9,2.7 L11.8,6.8",
        ["lap-chev-up"] = "M6,15 L12,9 L18,15",
        ["lap-chev-down"] = "M6,9 L12,15 L18,9",
        ["lap-x"] = "M7,7 L17,17 M17,7 L7,17",
    };

    private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string RoundRect(double x, double y, double w, double h, double r) =>
        $" M{F(x + r)},{F(y)} H{F(x + w - r)} A{F(r)},{F(r)} 0 0 1 {F(x + w)},{F(y + r)} V{F(y + h - r)} A{F(r)},{F(r)} 0 0 1 {F(x + w - r)},{F(y + h)}" +
        $" H{F(x + r)} A{F(r)},{F(r)} 0 0 1 {F(x)},{F(y + h - r)} V{F(y + r)} A{F(r)},{F(r)} 0 0 1 {F(x + r)},{F(y)} Z";

    private static string Circle(double cx, double cy, double r) =>
        $" M{F(cx - r)},{F(cy)} A{F(r)},{F(r)} 0 1 0 {F(cx + r)},{F(cy)} A{F(r)},{F(r)} 0 1 0 {F(cx - r)},{F(cy)} Z";

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
