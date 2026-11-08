using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot;

/// <summary>The finished long screenshot: preview, where it was saved, and what to do with it next.</summary>
internal sealed class LongshotResultWindow : GlassWindow
{
    private readonly BitmapSource _image;
    private readonly string _path;
    private readonly TextBlock _sub = new() { TextWrapping = TextWrapping.Wrap };

    public LongshotResultWindow(BitmapSource image, BitmapSource preview, string path, bool copied, Action newCapture, bool fileToolboxOn)
    {
        _image = image;
        _path = path;
        Title = $"Longshot · {image.PixelWidth} × {image.PixelHeight}";
        Resizable = true;
        Width = 760;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(20, 14, 12, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 8, 10) };
        var close = new Button { Style = SettingsPage.Res("Toolbox.Button.Icon"), Content = SettingsPage.Icon("close", 12), ToolTip = "Close (Esc)" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Long screenshot ready", Style = SettingsPage.Res("Text.Title"), FontSize = 17 });
        _sub.Style = SettingsPage.Res("Text.Caption");
        _sub.FontSize = 12;
        _sub.Text = $"{image.PixelWidth:N0} × {image.PixelHeight:N0} px   ·   saved as {IOPath.GetFileName(path)}" + (copied ? "   ·   copied, paste it anywhere with Ctrl+V" : "");
        titles.Children.Add(_sub);
        head.Children.Add(titles);
        MakeDragHandle(head);
        root.Children.Add(head);

        var picture = new Image { Source = preview, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.HighQuality);
        var frame = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Child = picture, Margin = new Thickness(0, 0, 8, 0) };
        frame.SetResourceReference(Border.BackgroundProperty, "Brush.Checker");
        var scroll = new ScrollViewer { Content = frame, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        // Shown at its real width when it fits, never wider than the window.
        double scale = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition()).Scale;
        picture.MaxWidth = image.PixelWidth / scale;

        var bar = new WrapPanel { Margin = new Thickness(0, 14, 8, 0) };
        void Add(Button b) { b.Margin = new Thickness(0, 0, 6, 6); bar.Children.Add(b); }
        Add(SettingsPage.Button("New capture", () => { Close(); newCapture(); }, accent: true));
        Add(SettingsPage.Button("Copy", Copy));
        if (fileToolboxOn) Add(SettingsPage.Button("Open in File Toolbox", () => { Close(); App.ShowPalette([path]); }));
        Add(SettingsPage.Button("Save as…", SaveAs));
        Add(SettingsPage.Button("Open", () => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })));
        Add(SettingsPage.Button("Show in folder", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })));
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        Content = border;

        var work = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition()).WorkArea;
        Height = Math.Min(work.Height / scale * 0.85, preview.PixelHeight / scale + 230);
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { Copy(); e.Handled = true; }
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { SaveAs(); e.Handled = true; }
        };
    }

    private void Copy() => Say(LongshotOutput.CopyToClipboard(_image) ? "Copied. Paste it anywhere with Ctrl+V." : "The clipboard is busy. Try again in a moment.");

    private void SaveAs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save long screenshot",
            InitialDirectory = IOPath.GetDirectoryName(_path),
            FileName = IOPath.GetFileName(_path),
            Filter = "PNG image|*.png|JPEG image|*.jpg",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (File.Exists(dialog.FileName)) File.Delete(dialog.FileName); // the dialog already asked to replace it
            LongshotOutput.Save(_image, dialog.FileName);
            Say($"Saved to {dialog.FileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"Couldn't save there: {ex.Message}");
        }
    }

    private void Say(string text) => _sub.Text = text;
}
