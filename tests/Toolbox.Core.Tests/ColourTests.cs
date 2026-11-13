using System.Globalization;
using System.Text.Json;
using Toolbox.Core.Pulse;
using Toolbox.Core.Settings;

namespace Toolbox.Core.Tests;

/// <summary>The colour picker's codes, contrast, steps, names and history (Pulse's Colour tile).</summary>
public sealed class ColourTests
{
    private static readonly Rgb Red = new(255, 0, 0);
    private static readonly Rgb Blue600 = new(0x25, 0x63, 0xEB);

    [Theory]
    [InlineData(Colours.Hex, "#FF0000")]
    [InlineData(Colours.RgbCode, "rgb(255, 0, 0)")]
    [InlineData(Colours.Hsl, "hsl(0, 100%, 50%)")]
    [InlineData(Colours.Hsv, "hsv(0, 100%, 100%)")]
    [InlineData(Colours.Oklch, "oklch(62.8% 0.258 29.2)")]
    [InlineData(Colours.Cmyk, "cmyk(0%, 100%, 100%, 0%)")]
    [InlineData(Colours.Wpf, "Color.FromRgb(255, 0, 0)")]
    [InlineData(Colours.Android, "0xFFFF0000")]
    [InlineData("unknown", "#FF0000")]
    public void Red_in_every_code(string format, string expected) => Assert.Equal(expected, Colours.Format(Red, format));

    [Fact]
    public void Codes_of_a_real_blue_and_of_grey_and_white()
    {
        Assert.Equal("hsl(221, 83%, 53%)", Colours.Format(Blue600, Colours.Hsl));
        Assert.Equal("oklch(100% 0 0)", Colours.Format(new Rgb(255, 255, 255), Colours.Oklch));
        Assert.Equal("hsl(0, 0%, 50%)", Colours.Format(new Rgb(128, 128, 128), Colours.Hsl));
        Assert.Equal("cmyk(0%, 0%, 0%, 100%)", Colours.Format(new Rgb(0, 0, 0), Colours.Cmyk));
    }

    [Fact]
    public void Codes_paste_the_same_on_an_Arabic_system()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("oklch(62.8% 0.258 29.2)", Colours.Format(Red, Colours.Oklch));
            Assert.Equal("hsl(221, 83%, 53%)", Colours.Format(Blue600, Colours.Hsl));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("#abcdef", true)]
    [InlineData("ABCDEF", true)]
    [InlineData(" #2563eb ", true)]
    [InlineData("#abc", false)]
    [InlineData("zzzzzz", false)]
    [InlineData(null, false)]
    public void Reads_hex_codes(string? text, bool ok)
    {
        Assert.Equal(ok, Rgb.TryParse(text, out var c));
        if (ok) Assert.Equal(text!.Trim().TrimStart('#').ToUpperInvariant(), c.Hex[1..]);
    }

    [Fact]
    public void Contrast_follows_WCAG()
    {
        var white = new Rgb(255, 255, 255);
        Assert.Equal(21, Colours.Contrast(white, new Rgb(0, 0, 0)), 3);
        Assert.Equal(1, Colours.Contrast(Red, Red), 6);
        double grey = Colours.Contrast(new Rgb(0x76, 0x76, 0x76), white); // the classic AA grey
        Assert.InRange(grey, 4.5, 4.6);
        Assert.Equal("AA", Colours.Grade(grey));
        Assert.Equal("AAA", Colours.Grade(7.1));
        Assert.Equal("AA large", Colours.Grade(3.2));
        Assert.Equal("Fail", Colours.Grade(2.9));
        Assert.Equal(white, Colours.TextOn(Blue600));
        Assert.Equal(new Rgb(0, 0, 0), Colours.TextOn(new Rgb(0xFF, 0xE0, 0x66)));
    }

    [Fact]
    public void OKLCH_goes_there_and_back()
    {
        foreach (var c in new[] { Red, Blue600, new Rgb(0x12, 0x34, 0x56), new Rgb(250, 250, 250), new Rgb(0, 0, 0) })
        {
            var (l, ch, h) = Colours.ToOklch(c);
            var back = Colours.FromOklch(l, ch, h);
            Assert.InRange(Math.Abs(back.R - c.R), 0, 1);
            Assert.InRange(Math.Abs(back.G - c.G), 0, 1);
            Assert.InRange(Math.Abs(back.B - c.B), 0, 1);
        }
    }

    [Fact]
    public void Steps_run_from_light_to_dark_around_the_colour()
    {
        foreach (var c in new[] { Blue600, Red, new Rgb(128, 128, 128), new Rgb(0xF5, 0xF5, 0xF5) })
        {
            var steps = Colours.Steps(c);
            Assert.Equal(9, steps.Count);
            Assert.Equal(c, steps[4]);
            for (int i = 1; i < steps.Count; i++)
                Assert.True(Colours.Luminance(steps[i - 1]) >= Colours.Luminance(steps[i]) - 0.002, $"{c}: step {i - 1} {steps[i - 1]} should not be darker than step {i} {steps[i]}");
        }
    }

    [Fact]
    public void Nearest_CSS_name()
    {
        Assert.Equal(("royalblue", true), Colours.NearestName(new Rgb(0x41, 0x69, 0xE1)));
        Assert.Equal(("royalblue", false), Colours.NearestName(new Rgb(0x42, 0x69, 0xE1)));
        Assert.Equal(("red", true), Colours.NearestName(Red));
        Assert.Equal("white", Colours.NearestName(new Rgb(0xFE, 0xFE, 0xFE)).Name);
    }

    [Fact]
    public void History_keeps_the_newest_first_each_colour_once()
    {
        var history = Colours.Remember(["#00FF00", "#0000FF", "junk"], Red, 10);
        Assert.Equal(["#FF0000", "#00FF00", "#0000FF"], history);
        history = Colours.Remember(history, new Rgb(0, 0, 255), 10);
        Assert.Equal(["#0000FF", "#FF0000", "#00FF00"], history);

        var many = new List<string>();
        for (int i = 0; i < 40; i++) many = Colours.Remember(many, new Rgb((byte)i, 0, 0), 30);
        Assert.Equal(30, many.Count);
        Assert.Equal("#270000", many[0]);
        Assert.Equal(10, Colours.Remember(many, Red, 10).Count);
        Assert.Equal(10, Colours.Remember(many, Red, 3).Count); // never fewer than 10
        Assert.Equal(10, Colours.HistorySize(0));
        Assert.Equal(30, Colours.HistorySize(99));
    }

    [Fact]
    public void Pulse_settings_keep_the_picker_choices()
    {
        var s = new AppSettings();
        Assert.Equal("basic", s.Pulse.ColourMode);
        Assert.Equal(Colours.Hex, s.Pulse.ColourCopy);
        Assert.Equal(10, s.Pulse.ColourHistorySize);
        s.Pulse.ColourMode = "pro";
        s.Pulse.ColourHistory = ["#FF0000"];
        s.Pulse.ColourSaved = ["#2563EB"];
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
        Assert.Equal("pro", back.Pulse.ColourMode);
        Assert.Equal(["#FF0000"], back.Pulse.ColourHistory);
        Assert.Equal(["#2563EB"], back.Pulse.ColourSaved);
        Assert.Equal("tap", JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings { Pulse = { Show = "tap" } }, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!.Pulse.Show);
    }
}
