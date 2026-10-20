using System.Windows.Media;
using Microsoft.Win32;

namespace Toolbox.Windows.Theme;

/// <summary>
/// Colours for the wheel and compact windows. Original neutral-glass look; the highlight uses
/// the user's Windows accent colour so the app feels native rather than borrowed.
/// </summary>
internal sealed record Palette(
    bool IsDark,
    Color Accent,
    Color AccentText,
    Color Disc,
    Color DiscRim,
    Color Tile,
    Color TileDisabled,
    Color Text,
    Color TextMuted,
    Color Hub,
    Color Pill,
    Color PillText,
    Color Surface,
    Color SurfaceRaised,
    Color Border)
{
    public static Palette Current(string themeSetting = "system")
    {
        bool dark = themeSetting switch
        {
            "dark" => true,
            "light" => false,
            _ => SystemPrefersDark(),
        };
        var accent = SystemAccent() ?? Color.FromRgb(0x2F, 0x6F, 0xED);
        var accentText = Luminance(accent) > 0.6 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White;

        return dark
            ? new Palette(true, accent, accentText,
                Disc: Color.FromArgb(0xF4, 0x17, 0x18, 0x1B),
                DiscRim: Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
                Tile: Color.FromRgb(0x3A, 0x3C, 0x43),
                TileDisabled: Color.FromRgb(0x26, 0x27, 0x2B),
                Text: Color.FromRgb(0xF4, 0xF4, 0xF7),
                TextMuted: Color.FromArgb(0x70, 0xF4, 0xF4, 0xF7),
                Hub: Color.FromRgb(0x1F, 0x20, 0x24),
                Pill: Color.FromRgb(0x4A, 0x4C, 0x54),
                PillText: Color.FromRgb(0xF2, 0xF2, 0xF5),
                Surface: Color.FromArgb(0xF0, 0x20, 0x21, 0x25),
                SurfaceRaised: Color.FromRgb(0x2C, 0x2E, 0x33),
                Border: Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
            : new Palette(false, accent, accentText,
                Disc: Color.FromArgb(0xF0, 0xDE, 0xE0, 0xE5),
                DiscRim: Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF),
                Tile: Colors.White,
                TileDisabled: Color.FromRgb(0xEC, 0xED, 0xF0),
                Text: Color.FromRgb(0x1C, 0x1C, 0x1F),
                TextMuted: Color.FromArgb(0x66, 0x1C, 0x1C, 0x1F),
                Hub: Color.FromRgb(0xCF, 0xD2, 0xD9),
                Pill: Colors.White,
                PillText: Color.FromRgb(0x1C, 0x1C, 0x1F),
                Surface: Color.FromArgb(0xF4, 0xF7, 0xF7, 0xF9),
                SurfaceRaised: Colors.White,
                Border: Color.FromArgb(0x24, 0x00, 0x00, 0x00));
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Color? SystemAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
            {
                var u = unchecked((uint)abgr);
                return Color.FromRgb((byte)(u & 0xFF), (byte)((u >> 8) & 0xFF), (byte)((u >> 16) & 0xFF));
            }
        }
        catch
        {
            // fall through to the default accent
        }
        return null;
    }

    private static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
}

internal static class Motion
{
    /// <summary>True when the user (or Windows) asked for reduced animation.</summary>
    public static bool Reduced(string setting) => setting switch
    {
        "on" => true,
        "off" => false,
        _ => !System.Windows.SystemParameters.ClientAreaAnimation,
    };
}
