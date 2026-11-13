using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot;

/// <summary>
/// Mouse and keyboard hooks used only while an area is being picked. They keep clicks (and Esc)
/// from reaching the app under the pointer, except over Amir Lap's own windows (the Longshot
/// panel), and report moves. Nothing is slow in the callbacks: they only post to the UI thread.
/// </summary>
internal sealed class PickHooks : IDisposable
{
    private const int WH_MOUSE_LL = 14, WH_KEYBOARD_LL = 13;
    private const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205;
    private const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_KEYUP = 0x101;
    private const int VK_ESCAPE = 0x1B, VK_RETURN = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public Native.POINT pt;
        public uint mouseData, flags, time;
        public IntPtr extra;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    private readonly HookProc _mouseProc, _keyProc;
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private IntPtr _mouse, _keys;
    private bool _swallowingLeft, _swallowingRight;

    public PickHooks()
    {
        _mouseProc = OnMouse;
        _keyProc = OnKey;
    }

    public event Action<PixelPoint>? Moved;
    public event Action<PixelPoint>? LeftDown;
    public event Action<PixelPoint>? LeftUp;
    public event Action? Cancel;
    public event Action? Accept;

    /// <summary>Clicks at points where this is true go through (the panel stays usable while picking).</summary>
    public Func<PixelPoint, bool> PassThrough { get; init; } = WheelScroller.IsOurs;

    public void Install()
    {
        var module = Native.GetModuleHandle(null);
        _mouse = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keys = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, module, 0);
    }

    public void Dispose()
    {
        if (_mouse != IntPtr.Zero) UnhookWindowsHookEx(_mouse);
        if (_keys != IntPtr.Zero) UnhookWindowsHookEx(_keys);
        _mouse = _keys = IntPtr.Zero;
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var p = new PixelPoint(info.pt.X, info.pt.Y);
            switch ((int)wParam)
            {
                case WM_MOUSEMOVE:
                    _ui.BeginInvoke(() => Moved?.Invoke(p));
                    break;
                case WM_LBUTTONDOWN:
                    if (PassThrough(p)) break;
                    _swallowingLeft = true;
                    _ui.BeginInvoke(() => LeftDown?.Invoke(p));
                    return 1;
                case WM_LBUTTONUP when _swallowingLeft:
                    _swallowingLeft = false;
                    _ui.BeginInvoke(() => LeftUp?.Invoke(p));
                    return 1;
                case WM_RBUTTONDOWN:
                    if (PassThrough(p)) break;
                    _swallowingRight = true;
                    return 1;
                case WM_RBUTTONUP when _swallowingRight:
                    _swallowingRight = false;
                    _ui.BeginInvoke(() => Cancel?.Invoke());
                    return 1;
            }
        }
        return CallNextHookEx(_mouse, code, wParam, lParam);
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);
            int msg = (int)wParam;
            if (vk is VK_ESCAPE or VK_RETURN)
            {
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN) _ui.BeginInvoke(() => { if (vk == VK_ESCAPE) Cancel?.Invoke(); else Accept?.Invoke(); });
                if (msg is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP) return 1;
            }
        }
        return CallNextHookEx(_keys, code, wParam, lParam);
    }
}

/// <summary>
/// The outline round what would be picked, with a label ("Scroll area · 820 × 640"). Click-through,
/// never focused, left out of screenshots.
/// </summary>
internal sealed class PickHighlight : Window
{
    private readonly Border _line = new() { BorderThickness = new Thickness(2.5), CornerRadius = new CornerRadius(7) };
    private readonly DropShadowEffect _glow = new() { BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9 };
    private readonly TextBlock _kind = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _size = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl _icon = new() { Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
    private const double Pad = 12;
    private IntPtr _hwnd;
    private PixelRect _area;
    private Border? _pill;

    public PickHighlight()
    {
        Title = "Longshot pick";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false; // see SeeThrough: a ring-shaped window, not a layered one the size of the area
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        IsHitTestVisible = false;
        Left = -32000;
        Top = -32000;
        Width = Height = 10;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        var color = ThemeResources.Lap.Accent;
        _line.BorderBrush = new SolidColorBrush(color);
        _glow.Color = color;
        _line.Effect = _glow;
        _line.Margin = new Thickness(Pad - 1.5);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(_kind);
        row.Children.Add(_size);
        var pill = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 0, 12, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(Pad + 10, Pad + 10, 0, 0),
            Background = new SolidColorBrush(color),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 },
            Child = row,
            FlowDirection = Lang.Flow,
        };
        _pill = pill;
        pill.SizeChanged += (_, _) => UpdateShape();
        Content = new Grid { Children = { _line, pill } };
    }

    /// <summary>The ring and the label are the window; the inside is a hole (clicks go to the app).</summary>
    private void UpdateShape()
    {
        if (_hwnd == IntPtr.Zero) return;
        double scale = ScreenGeometry.MonitorAt(_area.Center.ToPoint()).Scale, dip = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pad = (int)Math.Ceiling(Pad * scale);
        double inset = 3 * scale;
        using var shape = new WindowShape()
            .Add(new Rect(0, 0, _area.Width + 2 * pad, _area.Height + 2 * pad))
            .Cut(new Rect(pad + inset, pad + inset, Math.Max(0, _area.Width - 2 * inset), Math.Max(0, _area.Height - 2 * inset)));
        if (_pill is { IsVisible: true, ActualWidth: > 0 } p && Content is Visual root)
        {
            var b = p.TransformToAncestor(root).TransformBounds(new Rect(0, 0, p.ActualWidth, p.ActualHeight));
            b.Inflate(4, 4);
            shape.Add(new Rect(b.X * dip, b.Y * dip, b.Width * dip, b.Height * dip), 16 * dip);
        }
        shape.ApplyTo(_hwnd);
    }

    public static string KindText(TargetKind kind) => kind switch
    {
        TargetKind.ScrollArea => Lang.T("Scroll area", "منطقة تمرير"),
        TargetKind.Region => Lang.T("Region", "تحديد"),
        _ => Lang.T("Window", "نافذة"),
    };

    public static string KindIcon(TargetKind kind) => kind switch
    {
        TargetKind.ScrollArea => "lap-scrollarea",
        TargetKind.Region => "lap-region",
        _ => "lap-window",
    };

    /// <summary>What the label says (also used by tests, which never show the window).</summary>
    internal void SetLabel(PixelRect area, TargetKind kind, string? label = null)
    {
        _area = area;
        _kind.Text = label ?? KindText(kind);
        _size.Text = $"{area.Width:N0} × {area.Height:N0}";
        _icon.Content = SettingsPage.Glyph(KindIcon(kind), 14, brush: Brushes.White, stroke: 2);
    }

    public void ShowAt(PixelRect area, TargetKind kind, string? label = null)
    {
        SetLabel(area, kind, label);
        if (!IsVisible) Show();
        Place();
        Opacity = 1;
    }

    public void HideAway()
    {
        if (_hwnd != IntPtr.Zero) Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        double scale = ScreenGeometry.MonitorAt(_area.Center.ToPoint()).Scale;
        CaptureWindows.Place(_hwnd, _area.Inflate((int)Math.Ceiling(Pad * scale)));
        UpdateShape();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeNoActivate(_hwnd);
        CaptureWindows.ExcludeFromCapture(_hwnd);
        SeeThrough.Enable(this, _hwnd);
        Place();
    }
}

/// <summary>The hint at the top of the screen while picking. Never focused, left out of screenshots.</summary>
internal sealed class PickHint : GlassWindow
{
    private readonly TextBlock _text = new() { FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _keys = new() { FontSize = 12, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };

    public PickHint()
    {
        BackdropKind = Backdrop.Solid;
        NoActivate = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        CloseOnEscape = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        _keys.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = Lang.Flow };
        row.Children.Add(SettingsPage.Glyph("lap-longshot", 18, "Brush.Accent").With(g => g.Margin = new Thickness(0, 0, 10, 0)));
        row.Children.Add(_text);
        row.Children.Add(_keys);
        var border = new Border { Child = row, Padding = new Thickness(18, 11, 20, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        Content = border;
    }

    public void Set(string text, string keys)
    {
        _text.Text = text;
        _keys.Text = keys;
    }

    /// <summary>Near the top middle of the screen the pointer is on.</summary>
    public void PlaceOn(PixelPoint pointer)
    {
        var monitor = ScreenGeometry.MonitorAt(pointer.ToPoint());
        var work = monitor.WorkArea;
        PlaceNear(new Native.POINT { X = work.Left + work.Width / 2, Y = work.Top }, 18);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureWindows.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }
}

/// <summary>
/// Picking what to capture: the screen dims, the scroll area (or window) under the pointer lights
/// up as it moves, a click takes it, and dragging draws a region instead. Esc or a right-click goes
/// back. The Longshot panel stays usable throughout.
/// </summary>
internal sealed class AreaPicker : IDisposable
{
    private readonly TargetKind _mode;
    private readonly Action<CaptureTarget> _picked;
    private readonly Action _cancelled;
    private readonly CaptureScrim _scrim;
    private readonly PickHighlight _highlight = new();
    private readonly PickHint _hint = new();
    private readonly PickHooks _hooks = new();
    private readonly DispatcherTimer _hover = new() { Interval = TimeSpan.FromMilliseconds(70) };
    private PixelPoint _pointer;
    private PixelPoint? _down;
    private bool _dragging;
    private CaptureTarget? _hovered;
    private bool _lookingUp;
    private PixelPoint _lookedUpAt = new(int.MinValue, int.MinValue);
    private bool _done;

    /// <param name="mode">ScrollArea: hovering lights up scroll areas (and windows); Region: only drawing.</param>
    public AreaPicker(TargetKind mode, CaptureScrim scrim, Action<CaptureTarget> picked, Action cancelled)
    {
        _mode = mode;
        _scrim = scrim;
        _picked = picked;
        _cancelled = cancelled;
        _hooks.Moved += OnMoved;
        _hooks.LeftDown += p => { _down = p; _dragging = false; };
        _hooks.LeftUp += OnUp;
        _hooks.Cancel += () => Finish(null);
        _hooks.Accept += () => { if (_hovered is { } t) Finish(t); };
        _hover.Tick += (_, _) => LookUp();
    }

    /// <summary>For tests: the hooks aren't installed (nothing real is clicked).</summary>
    internal static bool NoHooks { get; set; }

    public void Start()
    {
        _pointer = PixelPoint.From(ScreenGeometry.CursorPosition());
        _hint.Set(_mode == TargetKind.Region
                ? Lang.T("Drag to draw the area to capture", "اسحب لرسم المنطقة المراد التقاطها")
                : Lang.T("Click the part that scrolls, or drag to draw an area", "انقر الجزء الذي يتمرّر، أو اسحب لرسم منطقة"),
            Lang.T("Esc to go back", "Esc للرجوع"));
        _hint.PlaceOn(_pointer);
        _hint.Show();
        _scrim.Strength = 120;
        _scrim.Show(null);
        if (!NoHooks) _hooks.Install();
        if (_mode == TargetKind.ScrollArea) _hover.Start();
        LookUp();
    }

    private void OnMoved(PixelPoint p)
    {
        if (_done) return;
        _pointer = p;
        if (_down is { } from)
        {
            int dx = p.X - from.X, dy = p.Y - from.Y;
            if (!_dragging && dx * dx + dy * dy > 36) _dragging = true;
            if (_dragging)
            {
                var area = Rect(from, p);
                _hovered = null;
                if (area.Width >= 2 && area.Height >= 2)
                {
                    _highlight.ShowAt(area, TargetKind.Region);
                    _scrim.SetHole(area);
                }
            }
        }
    }

    private static PixelRect Rect(PixelPoint a, PixelPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void OnUp(PixelPoint p)
    {
        if (_done) return;
        var from = _down;
        _down = null;
        if (_dragging && from is { } start)
        {
            _dragging = false;
            var area = Rect(start, p);
            if (area.Width >= 60 && area.Height >= 60) Finish(new CaptureTarget(TargetKind.Region, area, area.Center));
            else { _highlight.HideAway(); _scrim.SetHole(null); }
            return;
        }
        if (_hovered is { } t) Finish(t with { Anchor = t.Area.Contains(p) ? p : t.Area.Center });
    }

    /// <summary>What is under the pointer (asked again only when it has moved, one question at a time).</summary>
    private async void LookUp()
    {
        if (_done || _dragging || _lookingUp || _mode != TargetKind.ScrollArea) return;
        var at = _pointer;
        if (Math.Abs(at.X - _lookedUpAt.X) < 3 && Math.Abs(at.Y - _lookedUpAt.Y) < 3) return;
        _lookingUp = true;
        _lookedUpAt = at;
        try
        {
            if (WheelScroller.IsOurs(at)) return; // over the panel
            var scroll = await TargetFinder.ScrollAreaAtAsync(at, TimeSpan.FromMilliseconds(900));
            if (_done || _dragging) return;
            CaptureTarget? target = scroll is { } area
                ? new CaptureTarget(TargetKind.ScrollArea, area, at)
                : TargetFinder.WindowAt(at) is { } window ? new CaptureTarget(TargetKind.Window, window.Rect, at) : null;
            _hovered = target;
            if (target is { } t)
            {
                _highlight.ShowAt(t.Area, t.Kind);
                _scrim.SetHole(t.Area);
            }
            else
            {
                _highlight.HideAway();
                _scrim.SetHole(null);
            }
        }
        finally
        {
            _lookingUp = false;
        }
    }

    private void Finish(CaptureTarget? target)
    {
        if (_done) return;
        _done = true;
        Dispose();
        if (target is { } t) _picked(t);
        else _cancelled();
    }

    /// <summary>For tests: as if the user clicked (or finished drawing) this target.</summary>
    internal void Pick(CaptureTarget target) => Finish(target);

    public void Dispose()
    {
        _done = true;
        _hover.Stop();
        _hooks.Dispose();
        _highlight.Close();
        _hint.Close();
    }
}
