using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Home;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows.Tests;

/// <summary>
/// Builds every page of the Amir Lap window off-screen (nothing is shown and no tool is started),
/// in light and dark. Set AMIRLAP_SNAPSHOT_DIR to also save each page as a PNG for visual review.
/// </summary>
public class HomeWindowTests
{
    [Fact]
    public void Every_page_builds_in_light_and_dark()
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
            var tool = new FileToolboxTool(host, dispatcher, new DiagnosticsChannel(false));
            using var registry = new ToolRegistry(host.Settings, [tool]); // never applied: nothing starts

            foreach (var theme in new[] { "light", "dark" })
            {
                ThemeResources.Apply(app, theme);
                var window = new AmirLapWindow(host, registry);
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                var frame = new Border { Child = content };
                frame.SetResourceReference(Border.BackgroundProperty, "Brush.SurfaceSolid");

                foreach (var page in new[] { AmirLapWindow.DashboardPage, FileToolboxTool.ToolId, AmirLapWindow.GeneralPage })
                {
                    window.ShowPage(page);
                    Assert.Equal(page, window.CurrentPage);
                    double height = page == FileToolboxTool.ToolId ? 1700 : 820;
                    frame.Measure(new Size(960, height));
                    frame.Arrange(new Rect(0, 0, 960, height));
                    frame.UpdateLayout();
                    var scroll = FindScroll(content);
                    Assert.NotNull(scroll?.Content);

                    if (!string.IsNullOrEmpty(snapshots))
                    {
                        System.IO.Directory.CreateDirectory(snapshots);
                        var bitmap = new RenderTargetBitmap(960, (int)height, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(frame);
                        var png = new PngBitmapEncoder();
                        png.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file = System.IO.File.Create(System.IO.Path.Combine(snapshots, $"{page}-{theme}.png"));
                        png.Save(file);
                    }
                }
                window.Close();
            }
            app.Shutdown();
        }
        finally
        {
            Environment.SetEnvironmentVariable(Core.AppPaths.DataEnvironmentVariable, previous);
            try { System.IO.Directory.Delete(data, recursive: true); } catch (System.IO.IOException) { }
        }
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
