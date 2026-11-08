using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot;

internal enum FrameMode { Idle, Auto, Follow }

/// <summary>
/// The coloured frame around the capture area: drawn just outside it, click-through, never
/// activated and left out of screenshots, so it never ends up in the capture.
/// </summary>
internal sealed class CaptureFrame : Window
{
    private const double Thickness = 3;
    private readonly PixelRect _area;
    private readonly Border _border = new() { BorderThickness = new Thickness(Thickness), CornerRadius = new CornerRadius(2) };

    public CaptureFrame(PixelRect area)
    {
        _area = area;
        Title = "Longshot frame";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        Content = _border;
        SetMode(FrameMode.Idle);
    }

    public void SetMode(FrameMode mode)
    {
        switch (mode)
        {
            case FrameMode.Auto: _border.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xB2, 0x24)); break;
            case FrameMode.Follow: _border.BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0xC2, 0x7A)); break;
            default: _border.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent"); break;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeClickThrough(hwnd);
        CaptureWindows.ExcludeFromCapture(hwnd);
        double scale = ScreenGeometry.MonitorAt(new Native.POINT { X = _area.CenterX, Y = _area.CenterY }).Scale;
        CaptureWindows.Place(hwnd, _area.Inflate((int)Math.Ceiling(Thickness * scale)));
    }
}

/// <summary>
/// The floating Longshot panel beside the capture area: a preview of the long image, Extend /
/// Auto / Follow / Undo / Done. It never takes focus from the app being captured (so keyboard
/// scrolling in Follow still goes there) and never appears in screenshots.
/// </summary>
internal sealed class CapturePanel : GlassWindow
{
    public const double PreviewWidth = 228;
    public const double PreviewHeight = 170;

    private readonly Image _preview = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _info = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 34, Margin = new Thickness(0, 2, 0, 0) };
    private PixelRect? _beside;

    public CapturePanel()
    {
        Title = "Longshot";
        BackdropKind = Backdrop.Solid;
        NoActivate = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CloseOnEscape = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Left = -32000;
        Top = -32000;

        Extend = Make("↓   Extend", "Scroll down once and add the new part", accent: true);
        Auto = Make("▶   Auto", "Keep extending until the bottom (Esc stops)");
        Follow = Make("◎   Follow", "You scroll; Longshot adds as you go");
        Undo = Make("↶   Undo", "Remove the last part that was added");
        Done = Make("✓   Done", "Save, copy to the clipboard and show the result");
        Done.FontWeight = FontWeights.SemiBold;
        CloseButton = new Button { Style = SettingsPage.Res("Toolbox.Button.Icon"), Content = SettingsPage.Icon("close", 11), ToolTip = "Cancel without saving", Width = 26, Height = 26 };
        Content = Build();
    }

    public Button Extend { get; }
    public Button Auto { get; }
    public Button Follow { get; }
    public Button Undo { get; }
    public Button Done { get; }
    public Button CloseButton { get; }

    private static Button Make(string text, string tip, bool accent = false)
    {
        var b = new Button { Content = text, ToolTip = tip, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(10, 6, 10, 6) };
        if (accent) b.Style = SettingsPage.Res("Toolbox.Button.Accent");
        System.Windows.Automation.AutomationProperties.SetName(b, text.Trim(' ', '↓', '▶', '◎', '↶', '✓', '■'));
        return b;
    }

    private UIElement Build()
    {
        var stack = new StackPanel { Width = PreviewWidth };

        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(CloseButton, Dock.Right);
        head.Children.Add(CloseButton);
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var icon = SettingsPage.Icon("longshot", 15, accent: true);
        icon.Margin = new Thickness(0, 0, 8, 0);
        title.Children.Add(icon);
        title.Children.Add(new TextBlock { Text = "Longshot", Style = SettingsPage.Res("Text.Title"), FontSize = 14 });
        head.Children.Add(title);
        head.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { } };
        head.Cursor = Cursors.SizeAll;
        stack.Children.Add(head);

        var previewBox = new Border { Height = PreviewHeight, CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = _preview, BorderThickness = new Thickness(1) };
        previewBox.SetResourceReference(Border.BackgroundProperty, "Brush.Checker");
        previewBox.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        stack.Children.Add(previewBox);
        stack.Children.Add(_info);
        _status.Style = SettingsPage.Res("Text.Caption");
        _status.Text = "Getting ready…";
        stack.Children.Add(_status);

        var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(Button b, int row, int col, int span = 1)
        {
            b.Margin = new Thickness(col == 1 ? 3 : 0, row == 0 ? 0 : 6, col == 0 && span == 1 ? 3 : 0, 0);
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Grid.SetColumnSpan(b, span);
            grid.Children.Add(b);
        }
        Put(Extend, 0, 0, 2);
        Put(Auto, 1, 0);
        Put(Follow, 1, 1);
        Put(Undo, 2, 0);
        Put(Done, 2, 1);
        stack.Children.Add(grid);
        stack.Children.Add(new TextBlock { Text = "Esc stops Auto and Follow. Drag the title to move this panel.", Style = SettingsPage.Res("Text.Caption"), Margin = new Thickness(0, 10, 0, 0) });

        var border = new Border { Child = stack, Padding = new Thickness(14, 10, 14, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        return border;
    }

    public void ShowUpdate(BitmapSource preview, int width, int height, int parts, string? message)
    {
        _preview.Source = preview;
        _info.Text = $"{width:N0} × {height:N0} px  ·  {parts} part{(parts == 1 ? "" : "s")}";
        if (message is not null) _status.Text = message;
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>Puts the panel beside the area: right, left, below, above; inside its corner as a last resort.</summary>
    public void PlaceBeside(PixelRect area)
    {
        _beside = area;
        if (IsLoaded) PlaceAtSpot();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureWindows.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PlaceAtSpot();
    }

    private void PlaceAtSpot()
    {
        if (_beside is not { } area) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
        var spot = Spot(area, r.Width, r.Height);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, spot.X, spot.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    internal static (int X, int Y) Spot(PixelRect area, int width, int height)
    {
        var monitor = ScreenGeometry.MonitorAt(new Native.POINT { X = area.CenterX, Y = area.CenterY });
        var work = monitor.WorkArea;
        int gap = (int)(14 * monitor.Scale), edge = (int)(4 * monitor.Scale);
        var keepOut = area.Inflate(gap / 2);
        (int X, int Y)[] tries =
        [
            (area.Right + gap, area.Y), (area.X - gap - width, area.Y),
            (area.Right - width, area.Bottom + gap), (area.Right - width, area.Y - gap - height),
        ];
        int ClampX(int x) => Math.Clamp(x, work.Left + edge, Math.Max(work.Left + edge, work.Right - width - edge));
        int ClampY(int y) => Math.Clamp(y, work.Top + edge, Math.Max(work.Top + edge, work.Bottom - height - edge));
        foreach (var (x, y) in tries)
        {
            var candidate = new PixelRect(ClampX(x), ClampY(y), width, height);
            if (!candidate.Overlaps(keepOut)) return (candidate.X, candidate.Y);
        }
        // No room outside (the whole screen was chosen): inside the bottom-right corner. The panel is
        // left out of screenshots, so it never shows in the capture.
        return (ClampX(area.Right - width - gap), ClampY(area.Bottom - height - gap));
    }
}

/// <summary>"Longshot in 3…" before a delayed capture. Never activated, never captured.</summary>
internal sealed class CountdownToast : GlassWindow
{
    private readonly TextBlock _text = new() { FontSize = 15, FontWeight = FontWeights.SemiBold };

    public CountdownToast()
    {
        BackdropKind = Backdrop.Solid;
        NoActivate = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CloseOnEscape = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        var border = new Border { Child = _text, Padding = new Thickness(18, 10, 18, 11), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        Content = border;
        PlaceNear(ScreenGeometry.CursorPosition(), 28);
    }

    public void SetSeconds(int secondsLeft) => _text.Text = $"Longshot in {secondsLeft}…  open what you need now";

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureWindows.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }
}
