using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse;

/// <summary>A short note from Pulse at the top of the screen (a limit getting close, a task finished). Never takes focus.</summary>
internal sealed class PulseToast : GlassWindow
{
    private static PulseToast? _current;
    private readonly DispatcherTimer _close = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly int _topGap;

    private PulseToast(Color dot, string icon, string title, string text, string? action, Action? onAction, int topGap)
    {
        BackdropKind = Backdrop.Solid;
        NoActivate = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CloseOnEscape = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FlowDirection = Lang.Flow;
        _topGap = topGap;

        var row = new DockPanel { MaxWidth = 420 };
        var mark = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(dot), Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top, Child = SettingsPage.Glyph(icon, 16, stroke: 2.2, brush: Brushes.White) };
        DockPanel.SetDock(mark, Dock.Left);
        row.Children.Add(mark);
        if (action is not null && onAction is not null)
        {
            var open = SettingsPage.Button(action, () => { onAction(); Close(); }, ghost: true);
            open.Margin = new Thickness(14, 0, 0, 0);
            open.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(open, Dock.Right);
            row.Children.Add(open);
        }
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        var sub = new TextBlock { Text = text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        words.Children.Add(sub);
        row.Children.Add(words);
        var border = new Border { Child = row, Padding = new Thickness(14, 12, 16, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        border.MouseLeftButtonUp += (_, _) => Close();
        Content = border;
        Left = -32000;
        Top = -32000;
        _close.Tick += (_, _) => Close();
        Closed += (_, _) => { _close.Stop(); if (_current == this) _current = null; };
    }

    /// <summary>Shows a note (replacing the one on screen) at the top middle of the main screen.</summary>
    public static void Show(Color dot, string icon, string title, string text, string? action = null, Action? onAction = null, int topGap = 0)
    {
        _current?.Close();
        _current = new PulseToast(dot, icon, title, text, action, onAction, topGap);
        _current.Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureWindows.MakeNoActivate(new WindowInteropHelper(this).Handle);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Native.GetWindowRect(hwnd, out var r)) return;
        var monitor = ScreenGeometry.MonitorAt(new Native.POINT { X = 0, Y = 0 });
        var work = PixelRect.From(monitor.WorkArea);
        int x = work.X + (work.Width - r.Width) / 2, y = work.Y + (int)(16 * monitor.Scale) + _topGap;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        Motion.FromTo(this, OpacityProperty, 0, 1, 220);
        _close.Start();
    }
}

/// <summary>The Quick note tile's note: a small glass window that saves as you type.</summary>
internal sealed class NoteWindow : GlassWindow
{
    private readonly TextBox _box;
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly string _path;

    public NoteWindow(string path)
    {
        _path = path;
        Title = Lang.T("Quick note", "ملاحظة سريعة");
        Width = 380;
        Height = 320;
        Topmost = true;
        ShowInTaskbar = false;
        FlowDirection = Lang.Flow;
        var root = new DockPanel { Margin = new Thickness(16, 12, 16, 16) };
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Style = SettingsPage.Res("Lap.Caption.Close"), Width = 32, Height = 28, Content = SettingsPage.Glyph("close", 11, stroke: 2.2), ToolTip = Lang.T("Close", "إغلاق") };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(SettingsPage.Glyph("lap-note", 17, "Brush.Accent").With(g => { g.Margin = new Thickness(0, 0, 8, 0); DockPanel.SetDock(g, Dock.Left); }));
        head.Children.Add(new TextBlock { Text = Title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        MakeDragHandle(head);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        var hint = new TextBlock { Text = Lang.T("Saved as you type, on this PC only.", "تُحفظ أثناء الكتابة، على هذا الحاسوب فقط."), FontSize = 11.5, Margin = new Thickness(2, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(hint);
        _box = SettingsPage.TextBox(Read(path));
        _box.AcceptsReturn = true;
        _box.TextWrapping = TextWrapping.Wrap;
        _box.VerticalContentAlignment = VerticalAlignment.Top;
        _box.Height = double.NaN;
        _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _box.Padding = new Thickness(10, 8, 10, 8);
        _box.TextChanged += (_, _) => { _save.Stop(); _save.Start(); };
        root.Children.Add(_box);
        var surface = new Border { Child = root, CornerRadius = new CornerRadius(14) };
        Content = surface;
        _save.Tick += (_, _) => { _save.Stop(); Save(); };
        Closed += (_, _) => Save();
        PlaceNear(ScreenGeometry.CursorPosition(), -160);
        Loaded += (_, _) => { _box.Focus(); _box.CaretIndex = _box.Text.Length; };
    }

    public static string Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, _box.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ToolTip = ex.Message;
        }
    }
}
