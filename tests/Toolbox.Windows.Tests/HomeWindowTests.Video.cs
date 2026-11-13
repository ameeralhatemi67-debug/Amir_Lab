using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core.Settings;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Longshot.Video;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Tests;

/// <summary>Longshot's Video mode: its panel, pill and result window, and a whole recording through the real controller.</summary>
public partial class HomeWindowTests
{
    private static void RenderVideoWindows(string? snapshots, string theme, string lang)
    {
        Brush Desktop() => theme == "dark"
            ? new LinearGradientBrush(Color.FromRgb(0x10, 0x16, 0x2C), Color.FromRgb(0x24, 0x10, 0x22), 30)
            : new LinearGradientBrush(Color.FromRgb(0xD6, 0xE0, 0xF6), Color.FromRgb(0xF4, 0xE4, 0xEC), 30);

        var settings = new LongshotSettings();
        var panel = new VideoPanel(() => settings, edit => edit(settings), "Alt+Shift+V");
        panel.SetTarget(VideoTarget.Window);
        panel.SetArea(new PixelRect(0, 0, 1281, 721));
        panel.SetStatus(lang == "ar" ? "حرّك الإطار أو غيّر حجمه إن لزم، ثم اضغط «تسجيل»." : "Move or resize the frame if needed, then press Record.");
        Assert.Equal("1,280 × 720 px · 30 fps", panel.InfoText); // even sizes, as recorded
        Assert.Equal(VideoTarget.Window, panel.ShownTarget);
        var panelContent = (FrameworkElement)panel.Content;
        panel.Content = null;
        var panelFrame = new Border { Child = panelContent, Background = Desktop() };
        Layout(panelFrame, 344, 600);
        Assert.True(panelContent.ActualHeight > 400, "the video panel lays out its options and Record button");
        Save(snapshots, panelFrame, 344, 600, $"longshot-video-panel-{theme}-{lang}");
        panel.Close();

        if (lang != "en") return;

        var pill = new RecordingPill();
        pill.SetTime(TimeSpan.FromSeconds(72), paused: false);
        Assert.Equal("1:12", pill.TimeText);
        var pillContent = (FrameworkElement)pill.Content;
        pill.Content = null;
        var pillStage = new Border { Child = pillContent, Background = Desktop() };
        Layout(pillStage, 340, 80);
        Save(snapshots, pillStage, 340, 80, $"longshot-video-pill-{theme}");
        pill.Close();

        var result = new VideoResultWindow(new RecordingResult(@"C:\Users\You\Videos\Screen Recordings\Longshot video 2026-11-13 214512.mp4", 1280, 720, TimeSpan.FromSeconds(12.4), 372, 0), () => { }, fileToolboxOn: true);
        Assert.Contains("1,280 × 720 px", result.SubText);
        Assert.Contains("0:12", result.SubText);
        // The frame strip, as it would be read from the video: a few made-up frames.
        var strip = new BitmapSource[8];
        for (int i = 0; i < strip.Length; i++)
        {
            int w = 80, h = 45;
            var px = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int k = (y * w + x) * 4;
                    bool bar = y < 8;
                    px[k] = (byte)(bar ? 60 : 230 - i * 12); px[k + 1] = (byte)(bar ? 60 : 210 - x); px[k + 2] = (byte)(bar ? 70 : 120 + i * 15); px[k + 3] = 255;
                }
            strip[i] = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        }
        result.Trim.StripCount = strip.Length;
        result.Trim.Strip = strip;
        result.Trim.Start = TimeSpan.FromSeconds(2);
        result.Trim.End = TimeSpan.FromSeconds(9.5);
        result.Trim.Position = TimeSpan.FromSeconds(4);
        var resultContent = (FrameworkElement)result.Content;
        result.Content = null;
        var resultFrame = new Border { Child = resultContent };
        resultFrame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        Layout(resultFrame, 820, 700);
        Assert.True(result.Trim.ActualWidth > 600, "the trim bar runs the width of the window");
        // Dragging the start handle to the middle of the bar keeps the second half.
        result.Trim.DragHandle(start: true, result.Trim.ActualWidth / 2);
        Assert.InRange(result.Trim.Start.TotalSeconds, 5.5, 6.9);
        result.Trim.DragHandle(start: true, result.Trim.ActualWidth * 0.17);
        Layout(resultFrame, 820, 700);
        Save(snapshots, resultFrame, 820, 700, $"longshot-video-result-{theme}");
        result.Close();
    }

    /// <summary>Made-up frames for a recording (nothing is read from the screen).</summary>
    private sealed class FakeFrames(int width, int height) : IFrameGrabber
    {
        private int _n;
        public int Width => width;
        public int Height => height;

        public void Grab(byte[] into)
        {
            byte shade = (byte)(40 + _n++ % 180);
            for (int i = 0; i < width * height * 4; i += 4) { into[i] = shade; into[i + 1] = 90; into[i + 2] = (byte)(255 - shade); into[i + 3] = 255; }
        }

        public void Dispose() { }
    }

    private static RadioButton? FindRadio(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton r && System.Windows.Automation.AutomationProperties.GetName(r) == name) return r;
            if (FindRadio(child, name) is { } found) return found;
        }
        return null;
    }

    private static void ClickRadio(RadioButton r)
    {
        r.IsChecked = true;
        r.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, r));
    }

    /// <summary>
    /// A whole Longshot video through the real controller, panel, frame, pill and recorder, on
    /// made-up frames (every window stays off the screen; nothing is read from it): it opens on the
    /// window in front; resizing the frame makes it a region (rounded to even sizes); Window goes
    /// back; Record counts down, and Discard or the shortcut during the countdown go back to the
    /// panel; recording runs, pauses, resumes and Stop writes the MP4; the shortcut alone frames,
    /// records and stops; Discard deletes the file; and the switch at the top of both panels moves
    /// between Long screenshot and Video on the same area.
    /// </summary>
    private static void CheckVideoFlow(ToolboxHost host, LongshotTool longshot)
    {
        var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "amirlap-video-flow-" + Guid.NewGuid().ToString("N"));
        var window = new PixelRect(-30000, -30000, 521, 381);
        var pointer = new PixelPoint(-29800, -29800);
        RecordingResult? finished = null;
        VideoCapture.StartOverride = (window, pointer);
        VideoCapture.GrabberOverride = area => new FakeFrames(area.Width, area.Height);
        VideoCapture.FolderOverride = folder;
        VideoCapture.FinishedOverride = r => finished = r;
        VideoPanel.KeepOffScreen = true;
        RecordingPill.KeepOffScreen = true;
        CapturePanel.KeepOffScreen = true;
        LongshotCapture.StartOverride = (window, pointer);
        LongshotCapture.ScreenOverride = area => new PageScreen(() =>
        {
            var px = new byte[area.Width * area.Height * 4];
            Array.Fill(px, (byte)250);
            return new Processing.Capture.Frame(area.Width, area.Height, px);
        });
        LongshotCapture.ScrollerOverride = _ => new PageScroller(_ => { });
        LongshotCapture.TimingOverride = new SessionTiming(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(3));
        void Settings(Action<LongshotSettings> edit)
        {
            var next = host.Settings.Current.Clone();
            edit(next.Longshot);
            host.Settings.Save(next);
        }
        try
        {
            Settings(s => s.VideoCountdown = true);
            VideoCapture.Start(host, longshot);
            var video = VideoCapture.Current!;
            var panel = video.Panel!;
            Assert.Equal(VideoTarget.Window, video.Kind);
            Assert.Equal(window, video.Area);
            Assert.Equal(VideoTarget.Window, panel.ShownTarget);
            Assert.Equal("520 × 380 px · 30 fps", panel.InfoText);

            // Resizing the frame: a region now.
            video.ChangeArea(new PixelRect(-30000, -30000, 301, 201));
            Assert.Equal(VideoTarget.Region, video.Kind);
            Assert.Equal(VideoTarget.Region, panel.ShownTarget);
            Assert.Equal("300 × 200 px · 30 fps", panel.InfoText);
            ClickRadio(panel.TargetButton(VideoTarget.Window));
            Assert.Equal(window, video.Area);

            // Record counts down; Discard goes back to the panel, and so does the shortcut.
            Click(panel.RecordButton);
            Assert.Equal(VideoCapture.Stage.Countdown, video.State);
            Assert.False(panel.IsVisible);
            Assert.Equal("Starting in 3", video.Pill!.TimeText);
            Click(video.Pill.DiscardButton);
            Assert.Equal(VideoCapture.Stage.Setup, video.State);
            Assert.True(panel.IsVisible);
            Assert.Null(video.Pill);
            Click(panel.RecordButton);
            longshot.Capture(); // the screenshot shortcut works the video while one is open
            Assert.Equal(VideoCapture.Stage.Setup, video.State);

            // Without the countdown: recording, pause, resume, Stop.
            Settings(s => s.VideoCountdown = false);
            Click(panel.RecordButton);
            Pump(() => video.State == VideoCapture.Stage.Recording, "the recording to start", 20);
            Assert.Equal(new PixelRect(-30000, -30000, 520, 380), video.Area); // even, for the encoder
            var until = DateTime.UtcNow.AddMilliseconds(700);
            Pump(() => DateTime.UtcNow > until, "a moment of recording");
            Click(video.Pill!.PauseButton);
            Assert.True(video.Recorder!.Paused);
            var pausedAt = video.Recorder.Elapsed;
            until = DateTime.UtcNow.AddMilliseconds(400);
            Pump(() => DateTime.UtcNow > until, "a moment paused");
            Assert.Equal(pausedAt, video.Recorder.Elapsed);
            Click(video.Pill.PauseButton);
            Assert.False(video.Recorder.Paused);
            until = DateTime.UtcNow.AddMilliseconds(400);
            Pump(() => DateTime.UtcNow > until, "a moment more");
            Click(video.Pill.StopButton);
            Pump(() => finished is not null && VideoCapture.Current is null, "the recording to be saved", 30);
            Assert.True(System.IO.File.Exists(finished!.Path));
            Assert.StartsWith(folder, finished.Path);
            Assert.Equal(520, finished.Width);
            Assert.Equal(380, finished.Height);
            Assert.InRange(finished.Duration.TotalSeconds, 0.8, 2.5); // the pause is not in it
            Assert.True(finished.Frames > 20, $"frames: {finished.Frames}");

            // The shortcut alone: frame, record, stop.
            finished = null;
            longshot.RecordVideo();
            Assert.Equal(VideoCapture.Stage.Setup, VideoCapture.Current!.State);
            longshot.RecordVideo();
            Pump(() => VideoCapture.Current?.State == VideoCapture.Stage.Recording, "recording from the shortcut", 20);
            until = DateTime.UtcNow.AddMilliseconds(400);
            Pump(() => DateTime.UtcNow > until, "a moment of recording");
            longshot.RecordVideo();
            Pump(() => finished is not null && VideoCapture.Current is null, "the shortcut to stop and save", 30);
            Assert.True(System.IO.File.Exists(finished!.Path));

            // Discard while recording deletes the file.
            int before = System.IO.Directory.GetFiles(folder).Length;
            longshot.RecordVideo();
            longshot.RecordVideo();
            Pump(() => VideoCapture.Current?.State == VideoCapture.Stage.Recording, "recording to discard", 20);
            var discarded = VideoCapture.Current!.Recorder!.Path;
            Click(VideoCapture.Current.Pill!.DiscardButton);
            Pump(() => VideoCapture.Current is null, "the discard", 20);
            Assert.False(System.IO.File.Exists(discarded));
            Assert.Equal(before, System.IO.Directory.GetFiles(folder).Length);

            // The switch at the top: Long screenshot → Video → Long screenshot, on the same area.
            var region = new PixelRect(-30000, -30000, 400, 300);
            LongshotCapture.Start(host, longshot, region);
            var shotPanel = LongshotCapture.Current!.Panel!;
            ClickRadio(FindRadio(shotPanel, "Video")!);
            Assert.Null(LongshotCapture.Current);
            Assert.Equal(region, VideoCapture.Current!.Area);
            Assert.Equal(VideoTarget.Region, VideoCapture.Current.Kind);
            ClickRadio(FindRadio(VideoCapture.Current.Panel!, "Screenshot")!);
            Assert.Null(VideoCapture.Current);
            Assert.Equal(region, LongshotCapture.Current!.Target.Area);
            longshot.RecordVideo(); // the video shortcut during a long screenshot switches it to Video
            Assert.Null(LongshotCapture.Current);
            Assert.Equal(region, VideoCapture.Current!.Area);
            Click(VideoCapture.Current.Panel!.CloseButton);
            Assert.Null(VideoCapture.Current);
        }
        finally
        {
            VideoCapture.CancelActive();
            LongshotCapture.CancelActive();
            Pump(() => VideoCapture.Current is null && LongshotCapture.Current is null, "everything to close", 10);
            VideoCapture.StartOverride = null;
            VideoCapture.GrabberOverride = null;
            VideoCapture.FolderOverride = null;
            VideoCapture.FinishedOverride = null;
            VideoPanel.KeepOffScreen = false;
            RecordingPill.KeepOffScreen = false;
            CapturePanel.KeepOffScreen = false;
            LongshotCapture.StartOverride = null;
            LongshotCapture.ScreenOverride = null;
            LongshotCapture.ScrollerOverride = null;
            LongshotCapture.TimingOverride = null;
            try { System.IO.Directory.Delete(folder, recursive: true); } catch (System.IO.IOException) { }
        }
    }
}
