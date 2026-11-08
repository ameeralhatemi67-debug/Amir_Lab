using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Home;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Builds every page of the Amir Lap window, and Longshot's panel, result window and area chooser,
/// off-screen (nothing is shown, no tool is started, no input is sent), in light and dark. Set
/// AMIRLAP_SNAPSHOT_DIR to also save each one as a PNG for visual review.
/// </summary>
public class HomeWindowTests
{
    [Fact]
    public void Every_page_and_window_builds_in_light_and_dark()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { BuildAndRender(); }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void BuildAndRender()
    {
        var data = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"amirlap-home-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable(Core.AppPaths.DataEnvironmentVariable);
        Environment.SetEnvironmentVariable(Core.AppPaths.DataEnvironmentVariable, data);
        var snapshots = Environment.GetEnvironmentVariable("AMIRLAP_SNAPSHOT_DIR");
        try
        {
            var app = new App();
            app.InitializeComponent(); // loads the shared styles (Theme/Controls.xaml)
            var host = new ToolboxHost(new DiagnosticsChannel(false));
            var dispatcher = new ActionDispatcher(host, new ToastManager(host, _ => { }));
            var fileToolbox = new FileToolboxTool(host, dispatcher, new DiagnosticsChannel(false));
            var longshot = new LongshotTool(host);
            using var registry = new ToolRegistry(host.Settings, [fileToolbox, longshot]); // never applied: nothing starts

            foreach (var theme in new[] { "light", "dark" })
            {
                ThemeResources.Apply(app, theme);
                var window = new AmirLapWindow(host, registry);
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                var frame = new Border { Child = content };
                frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");

                foreach (var page in new[] { AmirLapWindow.DashboardPage, FileToolboxTool.ToolId, LongshotTool.ToolId, AmirLapWindow.GeneralPage })
                {
                    window.ShowPage(page);
                    Assert.Equal(page, window.CurrentPage);
                    double height = page switch { FileToolboxTool.ToolId => 1700, LongshotTool.ToolId => 1250, _ => 900 };
                    Layout(frame, 960, height);
                    var scroll = FindScroll(content);
                    Assert.NotNull(scroll?.Content);
                    Save(snapshots, frame, 960, height, $"{page}-{theme}");
                }
                window.Close();

                RenderLongshotWindows(snapshots, theme);
            }
            app.Shutdown();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Core.AppPaths.DataEnvironmentVariable, previous);
            try { System.IO.Directory.Delete(data, recursive: true); } catch (System.IO.IOException) { }
        }
    }

    /// <summary>The Longshot panel (with a fake preview), result window and area chooser, built off-screen.</summary>
    private static void RenderLongshotWindows(string? snapshots, string theme)
    {
        // A fake "page": rows of text-like blocks.
        int w = 900, h = 1600;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 250; px[i + 3] = 255; }
        for (int y = 20; y < h - 20; y += 26)
            for (int x = 40; x < w - 80; x += 70)
                for (int yy = y; yy < y + 10; yy++)
                    for (int xx = x; xx < x + 50; xx++) { int i = (yy * w + xx) * 4; px[i] = 60; px[i + 1] = 60; px[i + 2] = 70; }
        int tailRows = (int)(170.0 * w / 228); // what the session sends: the bottom of the image, sized to the box
        var preview = CaptureSession.Downscale(px[^(tailRows * w * 4)..], w, tailRows, 228);

        var panel = new CapturePanel();
        panel.ShowUpdate(preview, w, h, 4, "Added 466 px.");
        var panelContent = (FrameworkElement)panel.Content;
        panel.Content = null;
        var panelFrame = new Border { Child = panelContent };
        panelFrame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        Layout(panelFrame, 300, 520);
        Assert.True(panelContent.ActualHeight > 300, "the panel should lay out its preview and buttons");
        Save(snapshots, panelFrame, 300, 520, $"longshot-panel-{theme}");
        panel.Close();

        var image = LongshotOutput.ToBitmap(w, h, px);
        var result = new LongshotResultWindow(image, CaptureSession.Downscale(px, w, h, 600),
            @"C:\Users\You\Pictures\Screenshots\Longshot 2026-11-08 143012.png", copied: true, newCapture: () => { }, fileToolboxOn: true);
        var resultContent = (FrameworkElement)result.Content;
        result.Content = null;
        var resultFrame = new Border { Child = resultContent };
        resultFrame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");
        Layout(resultFrame, 760, 700);
        Save(snapshots, resultFrame, 760, 700, $"longshot-result-{theme}");
        result.Close();

        if (theme == "dark")
        {
            var overlay = new SelectionOverlay(new Processing.Capture.Frame(w, h, px), new PixelRect(0, 0, w, h));
            var overlayContent = (FrameworkElement)overlay.Content;
            overlay.Content = null;
            var overlayFrame = new Border { Child = overlayContent };
            Layout(overlayFrame, w, 500);
            Save(snapshots, overlayFrame, w, 500, "longshot-choose-area");
            overlay.Close();
        }
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static void Save(string? folder, FrameworkElement element, double width, double height, string name)
    {
        if (string.IsNullOrEmpty(folder)) return;
        System.IO.Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(System.IO.Path.Combine(folder, name + ".png"));
        png.Save(file);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroll(child) is { } found) return found;
        }
        return null;
    }
}
