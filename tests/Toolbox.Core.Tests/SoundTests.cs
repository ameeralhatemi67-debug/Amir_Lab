using System.Text.Json;
using Toolbox.Core.Pulse;
using Toolbox.Core.Settings;
using Toolbox.Core.Sound;

namespace Toolbox.Core.Tests;

/// <summary>Imp Studio's rules that don't need Windows: who a sound belongs to, grouping, steps, memory.</summary>
public class SoundTests
{
    [Theory]
    [InlineData(@"{0.0.0.00000000}.{5f3b9d1e-1111-2222-3333-444455556666}|\Device\HarddiskVolume3\Users\x\AppData\Roaming\Spotify\Spotify.exe%b{00000000-0000-0000-0000-000000000000}", "spotify.exe", false)]
    [InlineData(@"{0.0.0.00000000}.{5f3b9d1e-1111-2222-3333-444455556666}|\Device\HarddiskVolume3\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe%b{00000000-0000-0000-0000-000000000000}", "valorant-win64-shipping.exe", false)]
    [InlineData(@"{0.0.0.00000000}.{5f3b9d1e-1111-2222-3333-444455556666}|#%b{A9EF3FD9-4240-455E-A4D5-F2B3301887B2}", SoundApps.SystemKey, true)]
    [InlineData(@"\Device\HarddiskVolume3\Program Files\App\app.exe", "app.exe", false)]
    public void A_session_identifier_names_its_program(string identifier, string key, bool system)
    {
        var (k, path, isSystem) = SoundApps.FromSessionIdentifier(identifier);
        Assert.Equal(key, k);
        Assert.Equal(system, isSystem);
        if (!system) Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_identifiers_belong_to_nobody()
    {
        Assert.Equal("", SoundApps.FromSessionIdentifier(null).Key);
        Assert.Equal("", SoundApps.FromSessionIdentifier("{0.0.0.00000000}.{x}|%b{y}").Key);
    }

    [Fact]
    public void An_update_with_a_new_version_folder_keeps_the_same_key()
    {
        Assert.Equal(SoundApps.KeyOf(@"C:\Users\x\AppData\Local\Discord\app-1.0.9180\Discord.exe"), SoundApps.KeyOf(@"C:\Users\x\AppData\Local\Discord\app-1.0.9203\Discord.exe"));
    }

    [Theory]
    [InlineData("Spotify", "Spotify", @"C:\s\Spotify.exe", "Spotify")]
    [InlineData("", "Microsoft Edge", @"C:\e\msedge.exe", "Microsoft Edge")]
    [InlineData(null, null, @"C:\w\whatsapp.exe", "Whatsapp")]
    [InlineData("game.exe", null, @"C:\g\game.exe", "Game")]
    [InlineData("A description that goes on for much longer than any program's name should", null, @"C:\t\tool.exe", "Tool")]
    public void Apps_get_a_name_people_know(string? description, string? product, string path, string expected) =>
        Assert.Equal(expected, SoundApps.NameFrom(description, product, path));

    [Fact]
    public void Sessions_group_into_apps_in_a_steady_order()
    {
        SoundSession S(string id, string key, string name, bool active, float volume, bool muted = false, bool system = false) =>
            new(id, key, 1, name, null, system, active, volume, muted);
        var apps = SoundApps.Group(
        [
            S("1", "spotify.exe", "Spotify", true, 0.6f),
            S("2", SoundApps.SystemKey, "", false, 0.93f, system: true),
            S("3", "discord.exe", "Discord", true, 0.8f),
            S("4", "discord.exe", "Discord", false, 1f),
            S("5", "msedge.exe", "Microsoft Edge", true, 0.5f, muted: true),
            S("6", "msedge.exe", "Microsoft Edge", true, 0.7f, muted: false),
        ]);
        Assert.Equal(["discord.exe", "msedge.exe", "spotify.exe", SoundApps.SystemKey], apps.Select(a => a.Key));
        var discord = apps[0];
        Assert.True(discord.Playing);
        Assert.Equal(0.8f, discord.Volume); // its playing sound, not the quiet one
        Assert.Equal(2, discord.Sessions);
        Assert.False(apps[1].Muted); // muted only when every sound of it is
        Assert.Equal(0.7f, apps[1].Volume);
        Assert.True(apps[3].IsSystem);
        Assert.False(apps[3].Playing);
    }

    [Theory]
    [InlineData(0.37f, 5, true, 40)]
    [InlineData(0.40f, 5, true, 45)]
    [InlineData(0.37f, 5, false, 35)]
    [InlineData(0.40f, 5, false, 35)]
    [InlineData(0f, 5, false, 0)]
    [InlineData(1f, 5, true, 100)]
    [InlineData(0.99f, 10, true, 100)]
    [InlineData(0.03f, 2, false, 2)]
    public void A_hotkey_press_moves_to_the_next_step(float volume, int step, bool up, int expected) =>
        Assert.Equal(expected, SoundApps.Percent(SoundApps.Step(volume, step, up)));

    [Theory]
    [InlineData("Speakers (Realtek(R) Audio)", "Speakers · Realtek(R) Audio", "Speakers")]
    [InlineData("Headphones (WH-1000XM5 Stereo)", "Headphones · WH-1000XM5 Stereo", "Headphones")]
    [InlineData("FxSound Speakers (FxSound Audio Enhancer)", "FxSound Speakers · FxSound Audio Enhancer", "FxSound Speakers")]
    [InlineData("Digital Output", "Digital Output", "Digital Output")]
    public void Outputs_get_tidy_names(string name, string label, string shortName)
    {
        Assert.Equal(label, SoundNames.TidyDevice(name));
        Assert.Equal(shortName, SoundNames.ShortDevice(name));
    }

    [Theory]
    [InlineData(3, "headphones")]
    [InlineData(5, "headphones")]
    [InlineData(1, "speakers")]
    [InlineData(9, "monitor")]
    [InlineData(8, "line")]
    [InlineData(10, "other")]
    public void Form_factors_become_kinds(int formFactor, string kind) => Assert.Equal(kind, SoundNames.KindOf(formFactor));

    [Fact]
    public void Memory_keeps_a_level_per_app_and_output_and_survives_a_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), "amirlap-sound-" + Guid.NewGuid().ToString("N"), "apps.json");
        var now = DateTimeOffset.UtcNow;
        using (var memory = new SoundMemory(file, TimeSpan.FromHours(1)))
        {
            memory.Remember("spotify.exe", "Spotify", @"C:\s\Spotify.exe", "{speakers}", 0.6f, false);
            memory.Remember("spotify.exe", "Spotify", null, "{headphones}", 0.35f, true);
            memory.Heard("spotify.exe", "Spotify", null, now.AddMinutes(-5));
            memory.Heard("zoom.exe", "Zoom", null, now.AddMinutes(-1));
            memory.Heard(SoundApps.SystemKey, "", null, now);
            Assert.False(File.Exists(file)); // not yet: it waits for the changes to settle
        } // Dispose writes what is left

        using var again = new SoundMemory(file);
        Assert.Equal(0.6f, again.Recall("spotify.exe", "{speakers}")!.Volume);
        var headphones = again.Recall("SPOTIFY.EXE", "{headphones}")!;
        Assert.Equal(0.35f, headphones.Volume);
        Assert.True(headphones.Muted);
        Assert.Null(again.Recall("spotify.exe", "{monitor}"));
        Assert.Null(again.Recall("discord.exe", "{speakers}"));

        var recent = again.Recent("{speakers}", ["zoom.exe"], 5);
        Assert.Equal(["spotify.exe"], recent.Select(k => k.Key)); // Windows' sounds are never listed
        Assert.Equal(0.6f, recent[0].Level!.Volume);
        Assert.Equal(@"C:\s\Spotify.exe", recent[0].Path);
        Assert.Equal(["zoom.exe", "spotify.exe"], again.Recent(null, [], 5).Select(k => k.Key)); // most recently heard first
    }

    [Fact]
    public void A_broken_memory_file_starts_fresh()
    {
        var dir = Path.Combine(Path.GetTempPath(), "amirlap-sound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "apps.json");
        File.WriteAllText(file, "{ not json");
        using var memory = new SoundMemory(file);
        Assert.Equal(0, memory.Count);
        memory.Remember("a.exe", "A", null, "{x}", 0.5f, false);
        memory.Flush();
        Assert.Contains("a.exe", File.ReadAllText(file));
    }

    [Fact]
    public void The_sound_tile_is_added_once_after_Longshot()
    {
        var p = new PulseSettings();
        p.Sets = [new() { Id = "work", Tiles = [PulseTiles.Claude, PulseTiles.Longshot, PulseTiles.Toolbox] }, new() { Id = "study", Tiles = [PulseTiles.Timer] }];
        Assert.True(p.AddSoundTileOnce());
        Assert.Equal([PulseTiles.Claude, PulseTiles.Longshot, PulseTiles.Sound, PulseTiles.Toolbox], p.Sets[0].Tiles);
        Assert.Equal([PulseTiles.Timer], p.Sets[1].Tiles);
        p.Sets[0].Tiles.Remove(PulseTiles.Sound); // the user took it off: it stays off
        Assert.False(p.AddSoundTileOnce());
        Assert.DoesNotContain(PulseTiles.Sound, p.Sets[0].Tiles);

        var none = new PulseSettings { Sets = [new() { Id = "study", Tiles = [PulseTiles.Timer] }] };
        none.AddSoundTileOnce();
        Assert.Equal([PulseTiles.Timer, PulseTiles.Sound], none.Sets[0].Tiles);
        Assert.Contains(PulseTiles.Sound, PulseSettings.DefaultSets()[0].Tiles);
    }

    [Fact]
    public void Sound_settings_have_safe_defaults_and_round_trip()
    {
        var settings = new AppSettings();
        Assert.Equal(5, settings.Sound.Step);
        Assert.True(settings.Sound.LevelBar);
        Assert.True(settings.Sound.Remember);
        settings.Sound.Step = 10;
        settings.Sound.LevelBar = false;
        var copy = settings.Clone();
        Assert.Equal(10, copy.Sound.Step);
        Assert.False(copy.Sound.LevelBar);
        // A settings file from before Imp Studio loads with the defaults.
        var old = JsonSerializer.Deserialize<AppSettings>("""{ "language": "ar" }""", new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        Assert.Equal(5, old.Sound.Step);
    }
}
