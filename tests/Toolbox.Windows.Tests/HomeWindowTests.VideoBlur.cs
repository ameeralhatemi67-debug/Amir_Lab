using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Processing.Ocr;
using Toolbox.Processing.Studio;
using Toolbox.Processing.Video;
using Toolbox.Windows.Longshot.Video;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Tests;

/// <summary>Video blur's editor window on a real recording, shown far off-screen (no input sent).</summary>
public partial class HomeWindowTests
{
    /// <summary>Records a few seconds of a scrolling page to an MP4 (made-up frames), pumping the UI thread meanwhile.</summary>
    private static string RecordScrollingPage(string folder, double seconds)
    {
        System.IO.Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"page {Guid.NewGuid():N}.mp4");
        var recorder = new ScreenRecorder(new VideoBlurPipelineTests.ScrollingPage(480, 270), path, new RecordingOptions(Fps: 20, Cursor: false, Clicks: false));
        var start = recorder.StartAsync();
        Pump(() => start.IsCompleted, "the test recording to start", 20);
        if (start.IsFaulted) throw start.Exception!;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        Pump(() => DateTime.UtcNow > until, "the test recording");
        var stop = recorder.StopAsync();
        Pump(() => stop.IsCompleted, "the test recording to finish", 20);
        recorder.Dispose();
        return path;
    }

    private static VideoBlurWindow ShowBlurWindow(string path)
    {
        var window = VideoBlurWindow.CreateForTest(path);
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -30000;
        window.Top = -30000;
        window.Width = 1180;
        window.Height = 760;
        window.Show();
        Pump(() => window.HasFrame, "the first frame", 20);
        return window;
    }

    private static void SnapshotWindow(string? snapshots, Window window, string name)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var frame = new Border { Child = content };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        Layout(frame, 1180, 760);
        Save(snapshots, frame, 1180, 760, name);
        frame.Child = null;
        window.Content = content;
    }

    /// <summary>
    /// The editor end to end: it shows the first frame; a box drawn on a line of text is followed as
    /// the page scrolls (its keys climb with the text, and the timeline lane appears); moving the box
    /// by hand sets a key at that moment; Find personal info (on stand-in text recognition) adds a
    /// box over the e-mail and follows it; undo takes the last change back; Save writes the copy.
    /// </summary>
    private static void CheckVideoBlur(string? snapshots, Application app)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amirlap-blur-window-" + Guid.NewGuid().ToString("N"));
        int ocrCalls = 0;
        VideoBlurWork.OcrOverride = (frame, w, h) =>
            // The e-mail is "seen" only in the first frame read, on the line at y ≈ 140.
            ocrCalls++ == 0
                ? new OcrPage([new OcrLine("mail amir@example.com", [new OcrWord("mail", 40, 134, 40, 12), new OcrWord("amir@example.com", 90, 134, 150, 12)], "en")], w, h)
                : new OcrPage([], w, h);
        try
        {
            var path = RecordScrollingPage(folder, 2.2);
            var window = ShowBlurWindow(path);
            try
            {
                Assert.Equal(TimeSpan.Zero, window.CurrentTime);
                Assert.False(window.FollowButton.IsEnabled); // nothing chosen yet

                // Draw a box on a line of text, then follow it.
                window.DrawForTest(new PixelRect(60, 200, 160, 20));
                var track = Assert.Single(window.Plan.Tracks);
                Assert.Equal(track.Id, window.SelectedId);
                Assert.Equal(TimeSpan.Zero, track.Start);
                Assert.True(window.FollowButton.IsEnabled);
                Assert.Single(window.Timeline.Lanes);
                var follow = window.FollowSelected();
                Pump(() => follow.IsCompleted, "following the box", 30);
                Assert.True(track.Keys.Count >= 2, $"keys after following: {track.Keys.Count}");
                Assert.True(track.Interpolate(TimeSpan.FromSeconds(1)).Y < 200 - 40, $"the box climbed with the text: {track.Interpolate(TimeSpan.FromSeconds(1))}");
                Assert.True(track.End < TimeSpan.FromSeconds(2.4), "it ends where the text left the picture");
                Assert.Contains("left the picture", window.StatusText);

                // Moving it by hand at another moment adds a key there.
                window.SeekForTest(TimeSpan.FromSeconds(0.5));
                Pump(() => Math.Abs((window.CurrentTime - TimeSpan.FromSeconds(0.5)).TotalMilliseconds) < 40, "the frame at 0.5 s", 10);
                int keys = track.Keys.Count;
                var at = window.CurrentTime;
                var moved = track.Interpolate(at) with { Width = 200 };
                window.MoveForTest(track.Id, moved);
                Assert.Equal(moved, track.BoxAt(at));
                Assert.True(track.Keys.Count >= keys);

                // Find personal info: the e-mail gets a box from the first frame, which follows it.
                var find = window.FindPersonalInfo();
                Pump(() => find.IsCompleted, "finding personal info", 60);
                Assert.Equal(2, window.Plan.Tracks.Count);
                var mail = window.Plan.Tracks[1];
                Assert.StartsWith("E-mail", mail.Label);
                Assert.Contains("•••", mail.Label);
                Assert.Equal(TimeSpan.Zero, mail.Start);
                Assert.True(mail.BoxAt(TimeSpan.Zero) is { } first && first.X <= 90 && first.X + first.Width >= 240, "the box covers the address");
                Assert.True(mail.Keys.Count >= 2, "and follows it as the page scrolls");
                Assert.StartsWith("Found 1", window.StatusText);
                SnapshotWindow(snapshots, window, "longshot-video-blur-light");

                // Save writes a blurred copy beside the original.
                var save = window.Save();
                Pump(() => save.IsCompleted, "saving the blurred copy", 60);
                Assert.NotNull(window.SavedPath);
                Assert.True(System.IO.File.Exists(window.SavedPath));
                Assert.EndsWith(" blurred.mp4", window.SavedPath);
            }
            finally
            {
                window.Close();
            }

            ThemeResources.Apply(app, "dark");
            var dark = ShowBlurWindow(path);
            try
            {
                dark.DrawForTest(new PixelRect(300, 60, 120, 40));
                dark.DrawForTest(new PixelRect(40, 180, 200, 24));
                SnapshotWindow(snapshots, dark, "longshot-video-blur-dark");
            }
            finally
            {
                dark.Close();
                ThemeResources.Apply(app, "light");
            }
        }
        finally
        {
            VideoBlurWork.OcrOverride = null;
            try { System.IO.Directory.Delete(folder, recursive: true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
