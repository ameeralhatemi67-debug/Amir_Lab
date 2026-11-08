using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Toolbox.Windows.Theme;

/// <summary>
/// Colours of the Amir Lap window that code-drawn visuals need directly (ambient light, glare,
/// edge light, shadows). Brushes for styles are published as resources instead.
/// </summary>
internal sealed record LapTokens(
    bool IsDark,
    bool GlassOff,
    Color Accent,
    Color AccentLight,
    Color AccentDeep,
    Color EdgeLight,
    Color Glare,
    Color GlareSoft,
    Color Orb1,
    Color Orb2,
    Color Shadow,
    double ShadowOpacity);

/// <summary>
/// Publishes the current palette as application-wide brush resources (used through DynamicResource
/// by the control styles), so light/dark, accent and glass changes apply to every open window.
/// Light is white with light glass, dark is black with dark glass (see the design system board).
/// </summary>
internal static class ThemeResources
{
    public static Palette Current { get; private set; } = Palette.Current();

    public static LapTokens Lap { get; private set; } = Tokens(false, false, Palette.BrandBlue);

    /// <summary>Raised after the resources changed (windows refresh their frame and code-drawn parts).</summary>
    public static event Action? Changed;

    public static void Apply(Application app, string themeSetting, string accentSetting = "blue", string transparencySetting = "system")
    {
        var p = Palette.Current(themeSetting, accentSetting);
        Current = p;
        bool dark = p.IsDark;
        bool glassOff = IsGlassOff(transparencySetting);
        var a = p.Accent;
        var r = app.Resources;

        // Shared by every window.
        Set(r, "Brush.Accent", a);
        Set(r, "Brush.AccentHover", Mix(a, dark ? Colors.White : Colors.Black, 0.12));
        Set(r, "Brush.AccentPressed", Mix(a, dark ? Colors.White : Colors.Black, 0.22));
        Set(r, "Brush.AccentSoft", Color.FromArgb(0x33, a.R, a.G, a.B));
        Set(r, "Brush.AccentText", p.AccentText);
        Set(r, "Brush.Text", p.Text);
        Set(r, "Brush.TextMuted", dark ? Rgb(0xB6, 0xBA, 0xC4) : Rgb(0x47, 0x4C, 0x58));
        Set(r, "Brush.TextDisabled", Color.FromArgb(0x60, p.Text.R, p.Text.G, p.Text.B));
        Set(r, "Brush.Surface", dark ? Color.FromArgb(0xC0, 0x0E, 0x0E, 0x11) : Color.FromArgb(0xC0, 0xFA, 0xFA, 0xFC));
        Set(r, "Brush.SurfaceSolid", dark ? Rgb(0x14, 0x14, 0x18) : Rgb(0xF5, 0xF6, 0xF8));
        Set(r, "Brush.Card", dark ? Color.FromArgb(0x80, 0x26, 0x28, 0x30) : Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF));
        Set(r, "Brush.Control", dark ? Color.FromArgb(0x60, 0x5A, 0x5C, 0x66) : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
        Set(r, "Brush.ControlHover", dark ? Color.FromArgb(0x80, 0x6A, 0x6C, 0x78) : Color.FromArgb(0xFF, 0xF2, 0xF3, 0xF6));
        Set(r, "Brush.ControlPressed", dark ? Color.FromArgb(0x50, 0x4A, 0x4C, 0x55) : Rgb(0xE6, 0xE7, 0xEB));
        Set(r, "Brush.Border", dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        Set(r, "Brush.BorderStrong", dark ? Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x38, 0x00, 0x00, 0x00));
        Set(r, "Brush.Track", dark ? Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2A, 0x00, 0x00, 0x00));
        Set(r, "Brush.Danger", dark ? Rgb(0xFF, 0x8A, 0x80) : Rgb(0xB4, 0x23, 0x18));
        Set(r, "Brush.Success", dark ? Rgb(0x5E, 0xE0, 0x8F) : Rgb(0x15, 0x80, 0x3D));
        Set(r, "Brush.Warning", dark ? Rgb(0xFC, 0xE1, 0x00) : Rgb(0x9D, 0x5D, 0x00));
        Set(r, "Brush.Shade", Color.FromArgb(0x99, 0x00, 0x00, 0x00));
        Set(r, "Brush.Checker", dark ? Rgb(0x30, 0x31, 0x36) : Rgb(0xE4, 0xE5, 0xE9));

        // The Amir Lap window: white or black, glass on the navigation layer, solid content.
        var white = Colors.White;
        var ink = Rgb(0x0C, 0x0E, 0x14);
        Set(r, "Brush.Window", glassOff ? (dark ? Rgb(0x07, 0x07, 0x09) : Rgb(0xF1, 0xF2, 0xF6))
                                        : (dark ? Color.FromArgb(0xBD, 0x08, 0x08, 0x0A) : Color.FromArgb(0xB8, 0xFA, 0xFA, 0xFC)));
        Set(r, "Brush.Content", dark ? Rgb(0x0A, 0x0A, 0x0C) : white);
        Set(r, "Brush.LapCard", dark ? Rgb(0x13, 0x13, 0x17) : white);
        Set(r, "Brush.Card2", dark ? Rgb(0x1A, 0x1B, 0x20) : Rgb(0xF5, 0xF6, 0xF8));
        Set(r, "Brush.Line", dark ? Alpha(0x14, white) : Alpha(0x14, ink));
        Set(r, "Brush.Line2", dark ? Alpha(0x29, white) : Alpha(0x26, ink));
        Set(r, "Brush.Text2", dark ? Rgb(0xB6, 0xBA, 0xC4) : Rgb(0x47, 0x4C, 0x58));
        Set(r, "Brush.Text3", dark ? Rgb(0x9A, 0x9F, 0xAA) : Rgb(0x64, 0x69, 0x76));
        var rimA = dark ? Alpha(0x42, white) : Alpha(0xFA, white);
        var rimB = dark ? Alpha(0x08, white) : Alpha(0x33, white);
        r["Brush.Glass"] = glassOff
            ? Frozen(new SolidColorBrush(dark ? Rgb(0x18, 0x19, 0x1E) : Rgb(0xE9, 0xEB, 0xF0)))
            : Frozen(new LinearGradientBrush(dark ? Color.FromArgb(0x94, 0x2C, 0x2D, 0x34) : Alpha(0xBD, white),
                                             dark ? Color.FromArgb(0x61, 0x16, 0x17, 0x1C) : Alpha(0x70, white), 90));
        r["Brush.Rim"] = glassOff ? Frozen(new SolidColorBrush(dark ? Alpha(0x29, white) : Alpha(0x26, ink))) : Rim(rimA, rimB);
        Set(r, "Brush.RimTop", glassOff ? Colors.Transparent : rimA);
        Set(r, "Brush.Pill", glassOff ? (dark ? Rgb(0x26, 0x27, 0x2E) : white) : (dark ? Alpha(0x17, white) : Alpha(0xEB, white)));
        Set(r, "Brush.Key", dark ? Rgb(0x1D, 0x1E, 0x23) : white);
        Set(r, "Brush.KeyEdge", dark ? Alpha(0x2E, white) : Alpha(0x2E, ink));
        Set(r, "Brush.LapTrack", dark ? Rgb(0x2B, 0x2C, 0x33) : Rgb(0xDA, 0xDD, 0xE3));
        Set(r, "Brush.NavHover", dark ? Alpha(0x0E, white) : Alpha(0x0B, ink));
        Set(r, "Brush.NavHoverTop", dark ? Alpha(0x14, white) : Alpha(0xE6, white));
        Set(r, "Brush.SegmentSelected", dark ? Rgb(0x2A, 0x2B, 0x32) : white);
        Set(r, "Brush.CloseHover", Rgb(0xC4, 0x2B, 0x1C));
        Set(r, "Brush.AccentWash", Color.FromArgb(0x1F, a.R, a.G, a.B));
        r["Brush.AccentButton"] = Frozen(new LinearGradientBrush(Mix(a, white, 0.14), a, 90));
        r["Brush.AccentTile"] = Frozen(new LinearGradientBrush(Mix(a, white, 0.30), Mix(a, Colors.Black, 0.28), 55));
        r["Bool.IsDark"] = dark;

        Lap = Tokens(dark, glassOff, a);
        Changed?.Invoke();
    }

    private static LapTokens Tokens(bool dark, bool glassOff, Color a) => new(
        dark,
        glassOff,
        a,
        Mix(a, Colors.White, 0.28),
        Mix(a, Colors.Black, 0.28),
        EdgeLight: dark ? Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF) : Mix(a, Colors.White, 0.35),
        Glare: dark ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF),
        GlareSoft: dark ? Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
        Orb1: glassOff ? Colors.Transparent : dark ? Color.FromArgb(0x38, 0x50, 0x6E, 0xFF) : Color.FromArgb(0x4D, 0x63, 0x8C, 0xFF),
        Orb2: glassOff ? Colors.Transparent : dark ? Color.FromArgb(0x29, 0xBE, 0x50, 0x8C) : Color.FromArgb(0x47, 0xFF, 0xAA, 0x8C),
        Shadow: dark ? Colors.Black : Rgb(0x10, 0x14, 0x1E),
        ShadowOpacity: dark ? 0.45 : 0.08);

    /// <summary>True when panels should be solid: "on", or "system" while Windows' transparency effects are off.</summary>
    public static bool IsGlassOff(string transparencySetting) => transparencySetting switch
    {
        "on" => true,
        "off" => false,
        _ => !SystemTransparency(),
    };

    /// <summary>Windows' "Transparency effects" switch (Personalisation › Colours).</summary>
    private static bool SystemTransparency()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int on || on != 0;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>A 1 px glass edge: bright at two corners, fading in between.</summary>
    private static Brush Rim(Color a, Color b)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(a, 0));
        brush.GradientStops.Add(new GradientStop(b, 0.36));
        brush.GradientStops.Add(new GradientStop(b, 0.64));
        brush.GradientStops.Add(new GradientStop(a, 1));
        return Frozen(brush);
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static void Set(ResourceDictionary r, string key, Color color) => r[key] = Frozen(new SolidColorBrush(color));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Alpha(byte a, Color c) => Color.FromArgb(a, c.R, c.G, c.B);

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        a.A,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
