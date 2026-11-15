using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Core.Sound;
using Toolbox.Windows.Pulse;
using Toolbox.Windows.Sound;

namespace Toolbox.Windows.Tests;

/// <summary>A stand-in for Imp Studio's engine: a made-up PC, and a note of everything asked of it.</summary>
internal sealed class FakeSound : ISoundControl
{
    public FakeSound(SoundSnapshot snap) => Current = snap;

    public SoundSnapshot Current { get; set; }

    public List<string> Calls { get; } = [];

    public int Watchers { get; private set; }

    public event Action<SoundSnapshot>? Changed;

    public event Action<IReadOnlyDictionary<string, float>>? Levels;

    public IDisposable WatchLevels()
    {
        Watchers++;
        return new Undo(() => Watchers--);
    }

    public void SetAppVolume(string appKey, float volume)
    {
        Calls.Add($"volume {appKey} {SoundApps.Percent(volume)}");
        Current = Current with { Sessions = Current.Sessions.Select(s => s.AppKey == appKey ? s with { Volume = volume } : s).ToList() };
        Changed?.Invoke(Current);
    }

    public void SetAppMute(string appKey, bool muted)
    {
        Calls.Add($"mute {appKey} {muted}");
        Current = Current with { Sessions = Current.Sessions.Select(s => s.AppKey == appKey ? s with { Muted = muted } : s).ToList() };
        Changed?.Invoke(Current);
    }

    public void SetMaster(float volume)
    {
        Calls.Add($"master {SoundApps.Percent(volume)}");
        Current = Current with { Master = volume };
        Changed?.Invoke(Current);
    }

    public void SetMasterMute(bool muted)
    {
        Calls.Add($"master mute {muted}");
        Current = Current with { MasterMuted = muted };
        Changed?.Invoke(Current);
    }

    public void SetOutput(string deviceId) => Calls.Add("output " + deviceId);

    public void Raise() => Changed?.Invoke(Current);

    public void Level(Dictionary<string, float> levels) => Levels?.Invoke(levels);

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>The owner's example from the design: a game, voice chat, music, a browser, Windows' sounds and a quiet chat app.</summary>
    public static SoundSnapshot Example() => new(
        [
            new SoundDevice("{headphones}", "Headphones (WH-1000XM5)", "headphones"),
            new SoundDevice("{speakers}", "Speakers (Realtek(R) Audio)", "speakers"),
            new SoundDevice("{monitor}", "LG ULTRAGEAR (NVIDIA High Definition Audio)", "monitor"),
        ],
        "{headphones}", 0.72f, false,
        [
            new SoundSession("a", "valorant-win64-shipping.exe", 101, "VALORANT", @"C:\Riot Games\VALORANT\VALORANT-Win64-Shipping.exe", false, true, 0.35f, false),
            new SoundSession("b", "discord.exe", 102, "Discord", @"C:\Users\x\AppData\Local\Discord\app-1.0.9180\Discord.exe", false, true, 1f, false),
            new SoundSession("c", "discord.exe", 103, "Discord", @"C:\Users\x\AppData\Local\Discord\app-1.0.9180\Discord.exe", false, true, 0.8f, false),
            new SoundSession("d", "spotify.exe", 104, "Spotify", @"C:\Users\x\AppData\Roaming\Spotify\Spotify.exe", false, true, 0.6f, false),
            new SoundSession("e", "msedge.exe", 105, "Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", false, true, 0.8f, true),
            new SoundSession("f", SoundApps.SystemKey, 0, "", null, true, false, 0.93f, false),
            new SoundSession("g", "whatsapp.exe", 106, "WhatsApp", null, false, false, 0.7f, false),
        ],
        CanSwitchOutput: true);
}

public partial class HomeWindowTests
{
    /// <summary>
    /// Imp Studio's mixer, Pulse's quick mixer card and the level bar on the design's example, built
    /// off-screen with a stand-in engine (nothing touches the PC's sound). Checks the rows land in
    /// the right groups, that a slider and the mute button ask the engine, and that the meters run
    /// only while the window is shown.
    /// </summary>
    private static void RenderSound(string? snapshots, string theme, string lang)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amirlap-sound-" + Guid.NewGuid().ToString("N"));
        using var memory = new SoundMemory(System.IO.Path.Combine(dir, "apps.json"));
        memory.Remember("zoom.exe", "Zoom", null, "{headphones}", 0.9f, false);
        memory.Heard("zoom.exe", "Zoom", null, DateTimeOffset.UtcNow.AddHours(-2));
        memory.Remember("obs64.exe", "OBS Studio", null, "{headphones}", 1f, false);
        memory.Heard("obs64.exe", "OBS Studio", null, DateTimeOffset.UtcNow.AddDays(-1));
        memory.Heard("spotify.exe", "Spotify", null, DateTimeOffset.UtcNow); // playing now: not among the quiet ones

        var sound = new FakeSound(FakeSound.Example());
        var window = new MixerWindow(sound, memory, id => id switch { "louder" => "Alt+Shift+Up", "quieter" => "Alt+Shift+Down", "mute" => "Alt+Shift+M", _ => null });
        var rows = window.Rows.ToList();
        Assert.Equal(6, rows.Count); // Discord's two sounds are one app
        var playing = rows.Where(r => r.App.Playing).Select(r => r.Key).ToList();
        Assert.Equal(["discord.exe", "msedge.exe", "spotify.exe", "valorant-win64-shipping.exe"], playing);
        Assert.Equal(1f, rows.Single(r => r.Key == "discord.exe").App.Volume); // the louder of its two sounds
        Assert.True(rows.Single(r => r.Key == "msedge.exe").App.Muted);

        // A slider and the mute button ask the engine; the window follows what it reports.
        var spotify = rows.Single(r => r.Key == "spotify.exe");
        spotify.Slider.Value = 45;
        Assert.Contains("volume spotify.exe 45", sound.Calls);
        Assert.Equal(0, sound.Watchers); // not shown: the meters don't run
        sound.Level(new() { ["discord.exe"] = 0.8f, ["valorant-win64-shipping.exe"] = 0.3f, ["spotify.exe"] = 0.5f });
        DoEvents();

        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var frame = new Border { Child = content, Background = theme == "dark"
            ? new LinearGradientBrush(Color.FromRgb(0x10, 0x16, 0x2C), Color.FromRgb(0x24, 0x10, 0x22), 30)
            : new LinearGradientBrush(Color.FromRgb(0xD6, 0xE0, 0xF6), Color.FromRgb(0xF4, 0xE4, 0xEC), 30) };
        Layout(frame, 980, 720);
        Assert.True(spotify.ActualWidth > 300, "the rows should lay out");
        Save(snapshots, frame, 980, 720, $"sound-mixer-{theme}-{lang}");
        window.Close();

        // Pulse's card: the quick mixer, then the tile's own look with a muted output.
        var card = new SoundCard(sound, () => { });
        Assert.True(card.Element.IsHitTestVisible, "the quick mixer takes the pointer");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16), VerticalAlignment = VerticalAlignment.Top };
        bar.Children.Add(card.Element);
        var tiles = new StackPanel { Margin = new Thickness(24, 0, 0, 0) };
        var tile = new PulseTileView(Core.Pulse.PulseTiles.Sound, "lap-sound");
        tile.SetUsage(72, Theme.ThemeResources.Lap.Accent);
        tile.SetLabel("72%");
        tiles.Children.Add(tile);
        var muted = new PulseTileView(Core.Pulse.PulseTiles.Sound, "lap-sound") { Margin = new Thickness(0, 12, 0, 0) };
        muted.SetUsage(0);
        muted.SetLabel(SoundViews.PercentText(0.72f, true), "Brush.Danger");
        tiles.Children.Add(muted);
        bar.Children.Add(tiles);
        var stage = new Border { Child = bar };
        stage.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        stage.FlowDirection = Theme.Lang.Flow;
        Layout(stage, 520, 440);
        Save(snapshots, stage, 520, 440, $"sound-card-{theme}-{lang}");
        var cardSlider = FindAll<Slider>(card.Element).First();
        cardSlider.Value = 20;
        Assert.Contains(sound.Calls, c => c.StartsWith("volume ", StringComparison.Ordinal) && c.EndsWith(" 20", StringComparison.Ordinal));
        Assert.Contains(FindAll<TextBlock>(card.Element), t => t.Text == "72%"); // the output's volume in the head
        Detach(card.Element);

        // The level bar after Alt+Shift+Up in a game, and after a press in an app that has no sound yet.
        var bars = new StackPanel { Margin = new Thickness(12) };
        foreach (var level in new[]
        {
            LevelBar.Preview(new SoundApp("valorant-win64-shipping.exe", "VALORANT", null, false, true, 0.45f, false, 1, 101)),
            LevelBar.Preview(new SoundApp("discord.exe", "Discord", null, false, true, 0.8f, true, 1, 102)),
            LevelBar.Preview(new SoundApp("", "Notepad", null, false, false, 0, false, 0, 0), "Notepad hasn't made a sound yet"),
        })
        {
            var inner = (FrameworkElement)level.Content;
            level.Content = null;
            level.Close();
            bars.Children.Add(inner);
        }
        var barStage = new Border { Child = bars, Background = theme == "dark"
            ? new LinearGradientBrush(Color.FromRgb(0x14, 0x1B, 0x3C), Color.FromRgb(0x3C, 0x15, 0x32), 30)
            : new LinearGradientBrush(Color.FromRgb(0xC3, 0xD2, 0xF6), Color.FromRgb(0xF2, 0xD3, 0xE2), 30) };
        barStage.FlowDirection = Theme.Lang.Flow;
        Layout(barStage, 360, 330);
        Save(snapshots, barStage, 360, 330, $"sound-levelbar-{theme}-{lang}");
    }

    private static void Detach(FrameworkElement element)
    {
        if (element.Parent is Panel p) p.Children.Remove(element);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var inner in FindAll<T>(child)) yield return inner;
        }
    }
}
