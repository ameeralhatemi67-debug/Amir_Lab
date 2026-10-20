using System.Windows;
using System.Windows.Media;

namespace Toolbox.Windows.Theme;

/// <summary>
/// Publishes the current palette as application-wide brush resources (used through DynamicResource
/// by the control styles), so light/dark and accent changes apply to every open window.
/// </summary>
internal static class ThemeResources
{
    public static Palette Current { get; private set; } = Palette.Current();

    public static void Apply(Application app, string themeSetting)
    {
        var p = Palette.Current(themeSetting);
        Current = p;
        var r = app.Resources;
        Set(r, "Brush.Accent", p.Accent);
        Set(r, "Brush.AccentHover", Mix(p.Accent, p.IsDark ? Colors.White : Colors.Black, 0.12));
        Set(r, "Brush.AccentPressed", Mix(p.Accent, p.IsDark ? Colors.White : Colors.Black, 0.22));
        Set(r, "Brush.AccentSoft", Color.FromArgb(0x33, p.Accent.R, p.Accent.G, p.Accent.B));
        Set(r, "Brush.AccentText", p.AccentText);
        Set(r, "Brush.Text", p.Text);
        Set(r, "Brush.TextMuted", Color.FromArgb(0xA8, p.Text.R, p.Text.G, p.Text.B));
        Set(r, "Brush.TextDisabled", Color.FromArgb(0x60, p.Text.R, p.Text.G, p.Text.B));
        Set(r, "Brush.Surface", p.IsDark ? Color.FromArgb(0xB0, 0x20, 0x21, 0x25) : Color.FromArgb(0xB8, 0xF6, 0xF6, 0xF8));
        Set(r, "Brush.SurfaceSolid", p.IsDark ? Color.FromRgb(0x24, 0x25, 0x29) : Color.FromRgb(0xF4, 0xF4, 0xF6));
        Set(r, "Brush.Card", p.IsDark ? Color.FromArgb(0x70, 0x3A, 0x3C, 0x43) : Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
        Set(r, "Brush.Control", p.IsDark ? Color.FromArgb(0x60, 0x5A, 0x5C, 0x66) : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
        Set(r, "Brush.ControlHover", p.IsDark ? Color.FromArgb(0x80, 0x6A, 0x6C, 0x78) : Color.FromArgb(0xFF, 0xF2, 0xF3, 0xF6));
        Set(r, "Brush.ControlPressed", p.IsDark ? Color.FromArgb(0x50, 0x4A, 0x4C, 0x55) : Color.FromRgb(0xE6, 0xE7, 0xEB));
        Set(r, "Brush.Border", p.IsDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        Set(r, "Brush.BorderStrong", p.IsDark ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x38, 0x00, 0x00, 0x00));
        Set(r, "Brush.Track", p.IsDark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2A, 0x00, 0x00, 0x00));
        Set(r, "Brush.Danger", p.IsDark ? Color.FromRgb(0xFF, 0x7B, 0x72) : Color.FromRgb(0xC4, 0x2B, 0x1C));
        Set(r, "Brush.Success", p.IsDark ? Color.FromRgb(0x6C, 0xCB, 0x5F) : Color.FromRgb(0x0F, 0x7B, 0x0F));
        Set(r, "Brush.Warning", p.IsDark ? Color.FromRgb(0xFC, 0xE1, 0x00) : Color.FromRgb(0x9D, 0x5D, 0x00));
        Set(r, "Brush.Shade", Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        Set(r, "Brush.Checker", p.IsDark ? Color.FromRgb(0x30, 0x31, 0x36) : Color.FromRgb(0xE4, 0xE5, 0xE9));
        r["Bool.IsDark"] = p.IsDark;
    }

    private static void Set(ResourceDictionary r, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        r[key] = brush;
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        a.A,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
