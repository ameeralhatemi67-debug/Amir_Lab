using System.IO;
using System.Media;
using Toolbox.Core.Sound;
using Toolbox.Windows.Audio;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Imp Studio's engine against this PC's real sound system. The first test only reads. The live
/// one plays a quiet tone from this test for a few seconds and changes only this test's own volume
/// (then puts it back); it runs with AMIRLAP_LIVE_SOUND=1.
/// </summary>
public class SoundEngineTests
{
    [Fact]
    public void The_engine_reads_this_PC_and_changes_nothing()
    {
        using var engine = new SoundEngine();
        Assert.True(engine.WaitReady(TimeSpan.FromSeconds(10)), "Windows' sound system should answer");
        var snap = engine.Current;
        Assert.True(snap.Ready);
        if (snap.Devices.Count > 0)
        {
            Assert.NotNull(snap.Default);
            Assert.InRange(snap.Master, 0f, 1f);
        }
        Assert.All(snap.Sessions, s => Assert.InRange(s.Volume, 0f, 1f));
        var report = SoundCheck.Format(snap, engine.SampleLevels(TimeSpan.FromSeconds(3)), Environment.OSVersion.Version);
        Assert.Contains("Imp Studio sound check", report);
        using (engine.WatchLevels()) Thread.Sleep(150); // the meters start and stop without trouble
    }

    [Fact]
    public void Live_the_engine_follows_and_sets_this_test_s_own_sound()
    {
        if (Environment.GetEnvironmentVariable("AMIRLAP_LIVE_SOUND") != "1") return;
        var key = SoundApps.KeyOf(Environment.ProcessPath!);
        var file = Path.Combine(Path.GetTempPath(), "amirlap-sound-live-" + Guid.NewGuid().ToString("N"), "apps.json");
        using var memory = new SoundMemory(file);
        using var engine = new SoundEngine(memory);
        Assert.True(engine.WaitReady(TimeSpan.FromSeconds(10)));
        if (engine.Current.Default is null) return; // no output on this PC

        using var tone = new MemoryStream(Tone(seconds: 1, amplitude: 0.06));
        using var player = new SoundPlayer(tone);
        player.PlayLooping();
        try
        {
            SoundSession? Mine() => engine.Current.Sessions.FirstOrDefault(s => s.ProcessId == Environment.ProcessId && s.Active);
            Wait(() => Mine() is not null, "this test's sound to show up");
            var mine = Mine()!;
            Assert.Equal(key, mine.AppKey);
            Assert.False(string.IsNullOrEmpty(mine.Name));
            float before = mine.Volume;

            // Its level moves while it plays.
            float peak = 0;
            for (int i = 0; i < 20 && peak <= 0; i++)
            {
                peak = engine.SampleLevels(TimeSpan.FromSeconds(2)).GetValueOrDefault(key);
                Thread.Sleep(50);
            }
            Assert.True(peak > 0, "the level meter should see the tone");

            try
            {
                engine.SetAppVolume(key, 0.5f);
                Wait(() => Math.Abs(Mine()!.Volume - 0.5f) < 0.01f, "the volume to change");
                // A second, fresh look at Windows agrees: the change is Windows', not only the engine's.
                using (var other = new SoundEngine())
                {
                    Assert.True(other.WaitReady(TimeSpan.FromSeconds(10)));
                    var seen = other.Current.Sessions.First(s => s.ProcessId == Environment.ProcessId);
                    Assert.Equal(0.5f, seen.Volume, 2);
                }
                Assert.Equal(0.5f, memory.Recall(key, engine.Current.DefaultId!)!.Volume, 2);

                engine.SetAppMute(key, true);
                Wait(() => Mine()!.Muted, "the mute");
                engine.SetAppMute(key, false);
                Wait(() => !Mine()!.Muted, "the unmute");
            }
            finally
            {
                engine.SetAppVolume(key, before);
                Wait(() => Math.Abs(Mine()!.Volume - before) < 0.01f, "the volume to go back");
            }
        }
        finally
        {
            player.Stop();
        }
    }

    private void Wait(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Waited too long for " + what);
            Thread.Sleep(30);
        }
    }

    /// <summary>A 440 Hz tone as a WAV file (16-bit mono).</summary>
    private static byte[] Tone(double seconds, double amplitude)
    {
        const int rate = 44100;
        int samples = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + samples * 2);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(samples * 2);
        for (int i = 0; i < samples; i++) w.Write((short)(Math.Sin(2 * Math.PI * 440 * i / rate) * amplitude * short.MaxValue));
        w.Flush();
        return ms.ToArray();
    }
}
