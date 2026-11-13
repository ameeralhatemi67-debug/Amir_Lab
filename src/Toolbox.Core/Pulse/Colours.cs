using System.Globalization;

namespace Toolbox.Core.Pulse;

/// <summary>An sRGB colour as picked from the screen (8 bits per channel, no alpha).</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Reads "#RRGGBB" or "RRGGBB" (any case); anything else is refused.</summary>
    public static bool TryParse(string? text, out Rgb rgb)
    {
        rgb = default;
        var t = text?.Trim().TrimStart('#');
        if (t is not { Length: 6 } || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        rgb = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public override string ToString() => Hex;
}

/// <summary>
/// The colour picker's maths: the codes it shows (HEX, RGB, HSL, HSV, OKLCH, CMYK, WPF, Android),
/// WCAG contrast, lighter and darker steps in OKLCH, the nearest CSS colour name, and the history
/// of picks. Every number is written with the invariant culture, so codes paste the same on an
/// Arabic system.
/// </summary>
public static class Colours
{
    public const string Hex = "hex";
    public const string RgbCode = "rgb";
    public const string Hsl = "hsl";
    public const string Hsv = "hsv";
    public const string Oklch = "oklch";
    public const string Cmyk = "cmyk";
    public const string Wpf = "wpf";
    public const string Android = "android";

    /// <summary>Every code, in the order the Pro window lists them.</summary>
    public static readonly IReadOnlyList<string> Formats = [Hex, RgbCode, Hsl, Hsv, Oklch, Cmyk, Wpf, Android];

    public const int HistoryMin = 10;
    public const int HistoryMax = 30;

    public static string Label(string format) => format switch
    {
        Hex => "HEX",
        RgbCode => "RGB",
        Hsl => "HSL",
        Hsv => "HSV",
        Oklch => "OKLCH",
        Cmyk => "CMYK",
        Wpf => "WPF / C#",
        Android => "Android",
        _ => format.ToUpperInvariant(),
    };

    /// <summary>The colour written as one code (an unknown format falls back to HEX).</summary>
    public static string Format(Rgb c, string format)
    {
        switch (format)
        {
            case RgbCode:
                return I($"rgb({c.R}, {c.G}, {c.B})");
            case Hsl:
            {
                var (h, s, l) = ToHsl(c);
                return I($"hsl({h:0}, {s * 100:0}%, {l * 100:0}%)");
            }
            case Hsv:
            {
                var (h, s, v) = ToHsv(c);
                return I($"hsv({h:0}, {s * 100:0}%, {v * 100:0}%)");
            }
            case Oklch:
            {
                var (l, ch, h) = ToOklch(c);
                return ch < 0.0005 ? I($"oklch({l * 100:0.#}% 0 0)") : I($"oklch({l * 100:0.#}% {ch:0.###} {h:0.#})");
            }
            case Cmyk:
            {
                var (cy, m, y, k) = ToCmyk(c);
                return I($"cmyk({cy * 100:0}%, {m * 100:0}%, {y * 100:0}%, {k * 100:0}%)");
            }
            case Wpf:
                return I($"Color.FromRgb({c.R}, {c.G}, {c.B})");
            case Android:
                return $"0xFF{c.R:X2}{c.G:X2}{c.B:X2}";
            default:
                return c.Hex;
        }
    }

    // ---- conversions -------------------------------------------------------------------------------

    /// <summary>Hue in degrees (0–360), saturation and lightness 0–1.</summary>
    public static (double H, double S, double L) ToHsl(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double l = (max + min) / 2;
        double s = d == 0 ? 0 : d / (1 - Math.Abs(2 * l - 1));
        return (Hue(r, g, b, max, d), s, l);
    }

    /// <summary>Hue in degrees (0–360), saturation and value 0–1.</summary>
    public static (double H, double S, double V) ToHsv(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        return (Hue(r, g, b, max, d), max == 0 ? 0 : d / max, max);
    }

    private static double Hue(double r, double g, double b, double max, double d)
    {
        if (d == 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    /// <summary>Plain (device) CMYK, 0–1. Print shops convert with an ICC profile instead; this is the usual approximation.</summary>
    public static (double C, double M, double Y, double K) ToCmyk(Rgb c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double k = 1 - Math.Max(r, Math.Max(g, b));
        if (k >= 1) return (0, 0, 0, 1);
        return ((1 - r - k) / (1 - k), (1 - g - k) / (1 - k), (1 - b - k) / (1 - k), k);
    }

    /// <summary>OKLab (Björn Ottosson's): lightness 0–1 and the two opponent axes.</summary>
    public static (double L, double A, double B) ToOklab(Rgb c)
    {
        double r = Linear(c.R), g = Linear(c.G), b = Linear(c.B);
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    /// <summary>OKLCH: lightness 0–1, chroma (0 to about 0.37), hue in degrees.</summary>
    public static (double L, double C, double H) ToOklch(Rgb c)
    {
        var (l, a, b) = ToOklab(c);
        double h = Math.Atan2(b, a) * 180 / Math.PI;
        return (l, Math.Sqrt(a * a + b * b), h < 0 ? h + 360 : h);
    }

    /// <summary>Back from OKLCH to sRGB; colours outside sRGB are brought in by lowering the chroma.</summary>
    public static Rgb FromOklch(double l, double c, double h)
    {
        l = Math.Clamp(l, 0, 1);
        for (int i = 0; i < 24; i++)
        {
            if (TryFromOklab(l, c * Math.Cos(h * Math.PI / 180), c * Math.Sin(h * Math.PI / 180), out var rgb)) return rgb;
            c *= 0.85;
        }
        TryFromOklab(l, 0, 0, out var grey);
        return grey;
    }

    private static bool TryFromOklab(double L, double a, double b, out Rgb rgb)
    {
        double l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        double m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        double s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        double r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        const double slack = 0.002;
        bool inside = r is >= -slack and <= 1 + slack && g is >= -slack and <= 1 + slack && bl is >= -slack and <= 1 + slack;
        rgb = new Rgb(Encode(r), Encode(g), Encode(bl));
        return inside;
    }

    private static double Linear(byte channel)
    {
        double v = channel / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    private static byte Encode(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        double v = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }

    // ---- contrast, steps, names ----------------------------------------------------------------------

    /// <summary>WCAG relative luminance, 0 (black) to 1 (white).</summary>
    public static double Luminance(Rgb c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>"AAA", "AA", "AA large" (large or bold text only) or "Fail", for normal-size text.</summary>
    public static string Grade(double ratio) => ratio >= 7 ? "AAA" : ratio >= 4.5 ? "AA" : ratio >= 3 ? "AA large" : "Fail";

    /// <summary>White or black, whichever reads better on the colour.</summary>
    public static Rgb TextOn(Rgb c) => Contrast(c, new Rgb(255, 255, 255)) >= Contrast(c, new Rgb(0, 0, 0)) ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);

    /// <summary>
    /// Nine steps from light to dark with the colour in the middle (index 4): four lighter, four
    /// darker, keeping its hue. Light steps lose some chroma, as tints do.
    /// </summary>
    public static IReadOnlyList<Rgb> Steps(Rgb c)
    {
        var (l, ch, h) = ToOklch(c);
        var steps = new List<Rgb>(9);
        foreach (double k in new[] { 0.85, 0.65, 0.45, 0.25 })
            steps.Add(FromOklch(l + (0.98 - l) * k, ch * (1 - k * 0.7), h));
        steps.Add(c);
        foreach (double k in new[] { 0.25, 0.45, 0.65, 0.85 })
            steps.Add(FromOklch(l - (l - 0.16) * k, ch * (1 - k * 0.3), h));
        return steps;
    }

    /// <summary>
    /// The nearest CSS colour name (by distance in OKLab), and whether it is an exact match. A grey
    /// is only matched to grey names: the CSS list has few dark greys, so plain distance called
    /// #191919 "midnightblue".
    /// </summary>
    public static (string Name, bool Exact) NearestName(Rgb c)
    {
        const double greyChroma = 0.02;
        var (l, a, b) = ToOklab(c);
        bool grey = Math.Sqrt(a * a + b * b) < greyChroma;
        string best = "black";
        double bestD = double.MaxValue;
        foreach (var (name, hex) in Names)
        {
            var n = new Rgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
            if (n == c) return (name, true);
            var (nl, na, nb) = ToOklab(n);
            if (grey && Math.Sqrt(na * na + nb * nb) >= greyChroma) continue;
            double d = (l - nl) * (l - nl) + (a - na) * (a - na) + (b - nb) * (b - nb);
            if (d < bestD) { bestD = d; best = name; }
        }
        return (best, false);
    }

    // ---- history -----------------------------------------------------------------------------------

    /// <summary>The newest pick first, each colour once, at most <paramref name="size"/> (10–30).</summary>
    public static List<string> Remember(IEnumerable<string>? history, Rgb picked, int size)
    {
        var list = new List<string> { picked.Hex };
        foreach (var h in history ?? [])
            if (Rgb.TryParse(h, out var c) && c != picked && !list.Contains(c.Hex)) list.Add(c.Hex);
        int keep = Math.Clamp(size, HistoryMin, HistoryMax);
        return list.Count > keep ? list.GetRange(0, keep) : list;
    }

    /// <summary>A size the settings accept (10–30, default 10).</summary>
    public static int HistorySize(int size) => size <= 0 ? HistoryMin : Math.Clamp(size, HistoryMin, HistoryMax);

    private static string I(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    /// <summary>CSS named colours (one spelling each: "gray", "cyan", "magenta").</summary>
    private static readonly (string Name, int Hex)[] Names =
    [
        ("aliceblue", 0xF0F8FF), ("antiquewhite", 0xFAEBD7), ("aquamarine", 0x7FFFD4), ("azure", 0xF0FFFF),
        ("beige", 0xF5F5DC), ("bisque", 0xFFE4C4), ("black", 0x000000), ("blanchedalmond", 0xFFEBCD),
        ("blue", 0x0000FF), ("blueviolet", 0x8A2BE2), ("brown", 0xA52A2A), ("burlywood", 0xDEB887),
        ("cadetblue", 0x5F9EA0), ("chartreuse", 0x7FFF00), ("chocolate", 0xD2691E), ("coral", 0xFF7F50),
        ("cornflowerblue", 0x6495ED), ("cornsilk", 0xFFF8DC), ("crimson", 0xDC143C), ("cyan", 0x00FFFF),
        ("darkblue", 0x00008B), ("darkcyan", 0x008B8B), ("darkgoldenrod", 0xB8860B), ("darkgray", 0xA9A9A9),
        ("darkgreen", 0x006400), ("darkkhaki", 0xBDB76B), ("darkmagenta", 0x8B008B), ("darkolivegreen", 0x556B2F),
        ("darkorange", 0xFF8C00), ("darkorchid", 0x9932CC), ("darkred", 0x8B0000), ("darksalmon", 0xE9967A),
        ("darkseagreen", 0x8FBC8F), ("darkslateblue", 0x483D8B), ("darkslategray", 0x2F4F4F), ("darkturquoise", 0x00CED1),
        ("darkviolet", 0x9400D3), ("deeppink", 0xFF1493), ("deepskyblue", 0x00BFFF), ("dimgray", 0x696969),
        ("dodgerblue", 0x1E90FF), ("firebrick", 0xB22222), ("floralwhite", 0xFFFAF0), ("forestgreen", 0x228B22),
        ("gainsboro", 0xDCDCDC), ("ghostwhite", 0xF8F8FF), ("gold", 0xFFD700), ("goldenrod", 0xDAA520),
        ("gray", 0x808080), ("green", 0x008000), ("greenyellow", 0xADFF2F), ("honeydew", 0xF0FFF0),
        ("hotpink", 0xFF69B4), ("indianred", 0xCD5C5C), ("indigo", 0x4B0082), ("ivory", 0xFFFFF0),
        ("khaki", 0xF0E68C), ("lavender", 0xE6E6FA), ("lavenderblush", 0xFFF0F5), ("lawngreen", 0x7CFC00),
        ("lemonchiffon", 0xFFFACD), ("lightblue", 0xADD8E6), ("lightcoral", 0xF08080), ("lightcyan", 0xE0FFFF),
        ("lightgoldenrodyellow", 0xFAFAD2), ("lightgray", 0xD3D3D3), ("lightgreen", 0x90EE90), ("lightpink", 0xFFB6C1),
        ("lightsalmon", 0xFFA07A), ("lightseagreen", 0x20B2AA), ("lightskyblue", 0x87CEFA), ("lightslategray", 0x778899),
        ("lightsteelblue", 0xB0C4DE), ("lightyellow", 0xFFFFE0), ("lime", 0x00FF00), ("limegreen", 0x32CD32),
        ("linen", 0xFAF0E6), ("magenta", 0xFF00FF), ("maroon", 0x800000), ("mediumaquamarine", 0x66CDAA),
        ("mediumblue", 0x0000CD), ("mediumorchid", 0xBA55D3), ("mediumpurple", 0x9370DB), ("mediumseagreen", 0x3CB371),
        ("mediumslateblue", 0x7B68EE), ("mediumspringgreen", 0x00FA9A), ("mediumturquoise", 0x48D1CC), ("mediumvioletred", 0xC71585),
        ("midnightblue", 0x191970), ("mintcream", 0xF5FFFA), ("mistyrose", 0xFFE4E1), ("moccasin", 0xFFE4B5),
        ("navajowhite", 0xFFDEAD), ("navy", 0x000080), ("oldlace", 0xFDF5E6), ("olive", 0x808000),
        ("olivedrab", 0x6B8E23), ("orange", 0xFFA500), ("orangered", 0xFF4500), ("orchid", 0xDA70D6),
        ("palegoldenrod", 0xEEE8AA), ("palegreen", 0x98FB98), ("paleturquoise", 0xAFEEEE), ("palevioletred", 0xDB7093),
        ("papayawhip", 0xFFEFD5), ("peachpuff", 0xFFDAB9), ("peru", 0xCD853F), ("pink", 0xFFC0CB),
        ("plum", 0xDDA0DD), ("powderblue", 0xB0E0E6), ("purple", 0x800080), ("rebeccapurple", 0x663399),
        ("red", 0xFF0000), ("rosybrown", 0xBC8F8F), ("royalblue", 0x4169E1), ("saddlebrown", 0x8B4513),
        ("salmon", 0xFA8072), ("sandybrown", 0xF4A460), ("seagreen", 0x2E8B57), ("seashell", 0xFFF5EE),
        ("sienna", 0xA0522D), ("silver", 0xC0C0C0), ("skyblue", 0x87CEEB), ("slateblue", 0x6A5ACD),
        ("slategray", 0x708090), ("snow", 0xFFFAFA), ("springgreen", 0x00FF7F), ("steelblue", 0x4682B4),
        ("tan", 0xD2B48C), ("teal", 0x008080), ("thistle", 0xD8BFD8), ("tomato", 0xFF6347),
        ("turquoise", 0x40E0D0), ("violet", 0xEE82EE), ("wheat", 0xF5DEB3), ("white", 0xFFFFFF),
        ("whitesmoke", 0xF5F5F5), ("yellow", 0xFFFF00), ("yellowgreen", 0x9ACD32),
    ];
}
