using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot;

internal enum FrameMode { Idle, Auto, Follow }

/// <summary>
/// The glowing frame around the capture area. Hover an edge and a handle appears under the pointer
/// (wiggle and it grows, to make it easy to grab); drag it, or a corner knob, to resize; drag the
/// Move grip (inside the top edge) to move. Everywhere else clicks and the wheel go straight through
/// to the app below. It never takes focus and is left out of screenshots, so it never ends up in
/// the capture. Resizing stays inside the screen's work area, so the taskbar is never taken in.
/// </summary>
internal sealed class CaptureFrame : Window
{
    [Flags]
    private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

    private const double Reach = 22;     // room around the area (DIPs) for the handles
    private const double Line = 3;
    private const double Band = 24;       // how far from the line an edge can be grabbed (both sides together)
    private const int MinSize = 120;      // smallest area, in physical pixels

    private readonly Border _line = new() { BorderThickness = new Thickness(Line), CornerRadius = new CornerRadius(6), IsHitTestVisible = false };
    private readonly DropShadowEffect _glow = new() { BlurRadius = 16, ShadowDepth = 0, Opacity = 0.85 };
    private readonly Grid _handles = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Border _grab = new() { CornerRadius = new CornerRadius(5), Background = Brushes.White, BorderThickness = new Thickness(2.5), Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly ScaleTransform _grabScale = new(1, 1);
    private readonly TextBlock _sizeText = new() { FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
    private readonly Border _sizePill = new() { Height = 24, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly DispatcherTimer _calm = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private PixelRect _area;
    private IntPtr _hwnd;
    private double _scale = 1;
    private Edge _drag;
    private Edge _hover;
    private PixelRect _dragFrom;
    private Native.POINT _cursorFrom;
    private Point? _lastPointer;
    private double _wiggle; // how much the pointer moved near an edge lately (makes the handle grow)
    private Rect _limits;    // the work area (physical pixels) a resize stays inside

    public CaptureFrame(PixelRect area)
    {
        _area = area;
        Title = "Longshot frame";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent; // fully transparent: clicks pass through to the app below
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        Left = -32000;
        Top = -32000;
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;

        _line.Margin = new Thickness(Reach - Line);
        _line.Effect = _glow;
        _sizePill.Child = _sizeText;
        _sizePill.Background = new SolidColorBrush(Color.FromArgb(0xD9, 0x10, 0x12, 0x18));
        _grab.RenderTransform = _grabScale;
        _grab.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.4 };
        _grab.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        _overlay.Children.Add(_grab);
        var root = new Grid();
        root.Children.Add(_line);
        BuildHandles();
        root.Children.Add(_handles);
        root.Children.Add(_overlay);
        Content = root;
        SetMode(FrameMode.Idle);
        UpdateSizeText();
        _calm.Tick += (_, _) => Calm();
    }

    /// <summary>Raised when a move or resize ends, with the new area (physical pixels).</summary>
    public event Action<PixelRect>? AreaChanged;

    public PixelRect Area => _area;

    public void SetMode(FrameMode mode)
    {
        var color = mode switch
        {
            FrameMode.Auto => Color.FromRgb(0xFF, 0xB2, 0x24),
            FrameMode.Follow => Color.FromRgb(0x30, 0xC2, 0x7A),
            _ => ThemeResources.Lap.Accent,
        };
        _line.BorderBrush = new SolidColorBrush(color);
        _glow.Color = color;
        // While Longshot scrolls or follows, the area stays put.
        bool idle = mode == FrameMode.Idle;
        _handles.Visibility = idle ? Visibility.Visible : Visibility.Collapsed;
        if (!idle) HideGrab();
    }

    private void BuildHandles()
    {
        double edge = Reach - Line / 2;      // where the line runs, from the window's edge
        var catchAll = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)); // invisible but grabbable
        catchAll.Freeze();

        void Add(FrameworkElement e, Edge what, Cursor cursor)
        {
            e.Cursor = cursor;
            e.MouseLeftButtonDown += (_, args) => Begin(e, what, args);
            e.MouseMove += (_, args) => { Hover(what, args.GetPosition(_overlay)); Drag(); };
            e.MouseEnter += (_, args) => Hover(what, args.GetPosition(_overlay));
            e.MouseLeave += (_, _) => { if (_drag == Edge.None) Leave(what); };
            e.MouseLeftButtonUp += (_, _) => End(e);
            e.LostMouseCapture += (_, _) => End(e);
            _handles.Children.Add(e);
        }

        // Edges: invisible bands over the line (mostly outside the area), where a handle appears.
        double inset = edge + 14;
        Add(new Border { Background = catchAll, Height = Band, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(inset, edge - Band * 0.6, inset, 0) }, Edge.Top, Cursors.SizeNS);
        Add(new Border { Background = catchAll, Height = Band, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(inset, 0, inset, edge - Band * 0.6) }, Edge.Bottom, Cursors.SizeNS);
        Add(new Border { Background = catchAll, Width = Band, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(edge - Band * 0.6, inset, 0, inset) }, Edge.Left, Cursors.SizeWE);
        Add(new Border { Background = catchAll, Width = Band, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, inset, edge - Band * 0.6, inset) }, Edge.Right, Cursors.SizeWE);

        // Corners: glass knobs.
        foreach (var (h, v, what, cursor) in new[]
        {
            (HorizontalAlignment.Left, VerticalAlignment.Top, Edge.Left | Edge.Top, Cursors.SizeNWSE),
            (HorizontalAlignment.Right, VerticalAlignment.Bottom, Edge.Right | Edge.Bottom, Cursors.SizeNWSE),
            (HorizontalAlignment.Right, VerticalAlignment.Top, Edge.Right | Edge.Top, Cursors.SizeNESW),
            (HorizontalAlignment.Left, VerticalAlignment.Bottom, Edge.Left | Edge.Bottom, Cursors.SizeNESW),
        })
        {
            var knob = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = Brushes.White,
                BorderThickness = new Thickness(2.5),
                HorizontalAlignment = h,
                VerticalAlignment = v,
                Margin = new Thickness(edge - 9),
                Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35 },
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            knob.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
            knob.MouseEnter += (_, _) => Pop(knob, 1.35);
            knob.MouseLeave += (_, _) => { if (_drag == Edge.None) Pop(knob, 1); };
            Add(knob, what, cursor);
        }

        // The move grip, just inside the top edge, so it can always be reached.
        var grip = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        grip.Children.Add(SettingsPage.Glyph("lap-move", 14, brush: Brushes.White, stroke: 2));
        grip.Children.Add(new TextBlock { Text = Lang.T("Move", "تحريك"), Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 1), VerticalAlignment = VerticalAlignment.Center });
        var pill = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(12, 0, 14, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, edge + 10, 0, 0),
            Child = grip,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Opacity = 0.35 },
            ToolTip = Lang.T("Drag to move the area", "اسحب لتحريك المنطقة"),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1),
        };
        pill.SetResourceReference(Border.BackgroundProperty, "Brush.AccentButton");
        pill.MouseEnter += (_, _) => Pop(pill, 1.08);
        pill.MouseLeave += (_, _) => { if (_drag == Edge.None) Pop(pill, 1); };
        Add(pill, Edge.Move, Cursors.SizeAll);

        _sizePill.Margin = new Thickness(0, 0, 0, edge + 10);
        _handles.Children.Add(_sizePill);
    }

    private static void Pop(FrameworkElement e, double to)
    {
        if (e.RenderTransform is not ScaleTransform s) return;
        Motion.To(s, ScaleTransform.ScaleXProperty, to, 320, Motion.Spring);
        Motion.To(s, ScaleTransform.ScaleYProperty, to, 320, Motion.Spring);
    }

    /// <summary>The pointer is near an edge: a handle sits under it, following along the edge and growing as the pointer wiggles.</summary>
    private void Hover(Edge what, Point at)
    {
        if (what is not (Edge.Left or Edge.Right or Edge.Top or Edge.Bottom))
        {
            if (_drag == Edge.None) HideGrab();
            return;
        }
        if (_lastPointer is { } last && _hover == what) _wiggle = Math.Min(160, _wiggle + (at - last).Length);
        _lastPointer = at;
        if (_hover != what)
        {
            _hover = what;
            _grab.BeginAnimation(OpacityProperty, null);
            Motion.FromTo(_grab, OpacityProperty, 0, 1, 160);
            Motion.FromTo(_grabScale, ScaleTransform.ScaleXProperty, 0.4, 1, 320, Motion.Spring);
            Motion.FromTo(_grabScale, ScaleTransform.ScaleYProperty, 0.4, 1, 320, Motion.Spring);
        }
        bool vertical = what is Edge.Left or Edge.Right;
        double grow = 1 + Math.Min(0.9, _wiggle / 110); // up to almost twice as big
        double length = 46 * grow, thickness = 10 + 3 * (grow - 1);
        double edge = Reach - Line / 2;
        double w = ActualWidth, h = ActualHeight;
        _grab.Width = vertical ? thickness : length;
        _grab.Height = vertical ? length : thickness;
        double x = what switch
        {
            Edge.Left => edge - thickness / 2,
            Edge.Right => w - edge - thickness / 2,
            _ => Math.Clamp(at.X, edge + length / 2, Math.Max(edge + length / 2, w - edge - length / 2)) - length / 2,
        };
        double y = what switch
        {
            Edge.Top => edge - thickness / 2,
            Edge.Bottom => h - edge - thickness / 2,
            _ => Math.Clamp(at.Y, edge + length / 2, Math.Max(edge + length / 2, h - edge - length / 2)) - length / 2,
        };
        Canvas.SetLeft(_grab, x);
        Canvas.SetTop(_grab, y);
        if (!_calm.IsEnabled) _calm.Start();
    }

    private void Leave(Edge what)
    {
        if (_hover == what) HideGrab();
    }

    private void HideGrab()
    {
        _hover = Edge.None;
        _lastPointer = null;
        _wiggle = 0;
        _calm.Stop();
        Motion.To(_grab, OpacityProperty, 0, 200);
    }

    /// <summary>Once the pointer rests, the handle eases back to its normal size.</summary>
    private void Calm()
    {
        if (_drag != Edge.None) return;
        _wiggle *= 0.9;
        if (_wiggle < 1) _calm.Stop();
    }

    private void Begin(UIElement handle, Edge what, MouseButtonEventArgs e)
    {
        _drag = what;
        _dragFrom = _area;
        _cursorFrom = ScreenGeometry.CursorPosition();
        var work = ScreenGeometry.MonitorAt(new Native.POINT { X = _area.CenterX, Y = _area.CenterY }).WorkArea;
        _limits = new Rect(work.Left, work.Top, work.Width, work.Height);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void Drag()
    {
        if (_drag == Edge.None) return;
        var now = ScreenGeometry.CursorPosition();
        int dx = now.X - _cursorFrom.X, dy = now.Y - _cursorFrom.Y;
        int l = _dragFrom.X, t = _dragFrom.Y, r = _dragFrom.Right, b = _dragFrom.Bottom;
        int left = (int)_limits.Left, top = (int)_limits.Top, right = (int)_limits.Right, bottom = (int)_limits.Bottom;
        if (_drag == Edge.Move)
        {
            int w = r - l, h = b - t;
            l = Math.Clamp(l + dx, left, Math.Max(left, right - w));
            t = Math.Clamp(t + dy, top, Math.Max(top, bottom - h));
            r = l + w;
            b = t + h;
        }
        else
        {
            if (_drag.HasFlag(Edge.Left)) l = Math.Clamp(l + dx, left, r - MinSize);
            if (_drag.HasFlag(Edge.Right)) r = Math.Clamp(r + dx, l + MinSize, right);
            if (_drag.HasFlag(Edge.Top)) t = Math.Clamp(t + dy, top, b - MinSize);
            if (_drag.HasFlag(Edge.Bottom)) b = Math.Clamp(b + dy, t + MinSize, bottom);
        }
        _area = new PixelRect(l, t, r - l, b - t);
        Place();
        UpdateSizeText();
    }

    private void End(UIElement handle)
    {
        if (_drag == Edge.None) return;
        _drag = Edge.None;
        if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
        if (!handle.IsMouseOver)
        {
            HideGrab();
            if (handle is FrameworkElement fe) Pop(fe, 1);
        }
        if (_area != _dragFrom) AreaChanged?.Invoke(_area);
    }

    private void UpdateSizeText() => _sizeText.Text = $"{_area.Width:N0} × {_area.Height:N0}";

    private void Place()
    {
        if (_hwnd == IntPtr.Zero) return;
        CaptureWindows.Place(_hwnd, _area.Inflate((int)Math.Ceiling(Reach * _scale)));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeNoActivate(_hwnd);
        CaptureWindows.ExcludeFromCapture(_hwnd);
        _scale = ScreenGeometry.MonitorAt(new Native.POINT { X = _area.CenterX, Y = _area.CenterY }).Scale;
        Place();
    }
}

/// <summary>
/// The Longshot panel, in Amir Lap's glass: the blurred screen behind it under a frosted tint,
/// a live preview of the whole long image that grows (and scrolls to each new part) as the capture
/// goes on, and Extend / Auto / Follow / Undo / Done. It stays on top of everything until it is
/// closed, never takes focus from the app being captured (so keyboard scrolling in Follow still
/// goes there), can be moved by its title and resized from its corner, and never appears in
/// screenshots.
/// </summary>
internal sealed class CapturePanel : Window
{
    /// <summary>Width (DIPs) the session renders the whole-image preview at.</summary>
    public const double PreviewWidth = 300;
    private const double Shadow = 14;  // room around the glass for its shadow
    private const double Radius = 22;

    private readonly ScreenBackdrop _backdrop = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    private readonly Image _preview = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _seam = new() { VerticalAlignment = VerticalAlignment.Top, Opacity = 0, IsHitTestVisible = false, BorderThickness = new Thickness(0, 2, 0, 0) };
    private readonly Grid _stage = new();
    private readonly Border _live = new() { Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Ellipse _liveDot = new() { Width = 7, Height = 7, Fill = Brushes.White, Margin = new Thickness(0, 0, 6, 0) };
    private readonly TextBlock _liveText = new() { Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _info = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 12, 0, 0) };
    private readonly TextBlock _status = new() { MinHeight = 34, Margin = new Thickness(2, 2, 0, 0) };
    private readonly DispatcherTimer _keepUp = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private PixelRect? _beside;
    private IntPtr _hwnd;
    private DateTime _lastBackdrop;

    public CapturePanel()
    {
        Title = "Longshot";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = 300 + 2 * Shadow;
        Height = 620;
        MinWidth = 264 + 2 * Shadow;
        MinHeight = 500;
        MaxWidth = 560;
        Left = -32000;
        Top = -32000;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        SetResourceReference(ForegroundProperty, "Brush.Text");

        Extend = Make("lap-down", Lang.T("Extend", "مدّ"), Lang.T("Scroll down once and add the new part", "مرّر مرة واحدة وأضف الجزء الجديد"), "Lap.Button.Accent");
        Auto = Make("lap-play", Lang.T("Auto", "تلقائي"), Lang.T("Keep extending until the bottom (Esc stops)", "استمر حتى النهاية (Esc للإيقاف)"));
        Follow = Make("lap-follow", Lang.T("Follow", "تتبّع"), Lang.T("You scroll; Longshot adds as you go", "أنت تمرّر واللقطة تُضاف"));
        Undo = Make("lap-undo", Lang.T("Undo", "تراجع"), Lang.T("Remove the last part that was added", "إزالة آخر جزء أُضيف"));
        Done = Make("lap-check", Lang.T("Done", "تم"), Lang.T("Save, copy to the clipboard and show the result", "حفظ ونسخ إلى الحافظة وإظهار النتيجة"));
        CloseButton = new Button { Style = SettingsPage.Res("Lap.Caption.Close"), Width = 34, Height = 30, Content = SettingsPage.Glyph("close", 12, stroke: 2.2), ToolTip = Lang.T("Cancel without saving", "إلغاء دون حفظ") };
        RefreshButton = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 34, Height = 30, Content = SettingsPage.Glyph("lap-refresh", 15, stroke: 2), ToolTip = Lang.T("Start again: take the first part afresh", "البدء من جديد: التقاط الجزء الأول مرة أخرى"), Margin = new Thickness(0, 0, 2, 0) };
        System.Windows.Automation.AutomationProperties.SetName(RefreshButton, Lang.T("Start again", "البدء من جديد"));
        System.Windows.Automation.AutomationProperties.SetName(CloseButton, Lang.T("Cancel", "إلغاء"));
        Content = Build();

        _keepUp.Tick += (_, _) => KeepUp();
        LocationChanged += (_, _) =>
        {
            // While the panel is dragged, refresh the glass behind it at most ~30 times a second.
            if ((DateTime.UtcNow - _lastBackdrop).TotalMilliseconds > 33) RefreshBackdrop();
        };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(RefreshBackdrop, DispatcherPriority.Render);
        Closed += (_, _) => _keepUp.Stop();
    }

    public Button Extend { get; }
    public Button Auto { get; }
    public Button Follow { get; }
    public Button Undo { get; }
    public Button Done { get; }
    public Button CloseButton { get; }
    public Button RefreshButton { get; }

    private static Button Make(string icon, string text, string tip, string style = "Lap.Button")
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(SettingsPage.Glyph(icon, 15, stroke: 2).With(g => g.Margin = new Thickness(0, 0, 8, 0)));
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Style = SettingsPage.Res(style), Content = content, ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 0, 10, 0) };
        System.Windows.Automation.AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>Shows a button as Stop (while Auto or Follow runs) or as itself: swaps its icon's shape and its text.</summary>
    public static void SetButton(Button button, string icon, string text)
    {
        if (button.Content is not StackPanel { Children.Count: 2 } content) return;
        if (content.Children[0] is Viewbox { Child: Canvas { Children.Count: > 0 } canvas } && canvas.Children[0] is System.Windows.Shapes.Path path)
            path.Data = Icons.Get(icon);
        if (content.Children[1] is TextBlock label) label.Text = text;
        System.Windows.Automation.AutomationProperties.SetName(button, text);
    }

    /// <summary>For tests: the size line under the preview ("900 × 1,600 px · 4 parts").</summary>
    internal string InfoText => _info.Text;

    /// <summary>For tests: the status line ("Added 466 px.").</summary>
    internal string StatusText => _status.Text;

    /// <summary>For tests: keeps the panel off the screen (and does not read the screen behind it).</summary>
    internal static bool KeepOffScreen { get; set; }

    /// <summary>Empties the preview (the capture starts again).</summary>
    public void ClearPreview()
    {
        _preview.Source = null;
        _info.Text = "";
        _seam.BeginAnimation(OpacityProperty, null);
        _seam.Opacity = 0;
    }

    private UIElement Build()
    {
        var dark = ThemeResources.Lap.IsDark;
        var root = new Grid();

        var shadow = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = dark ? 0.6 : 0.25 } };
        shadow.SetResourceReference(Border.BackgroundProperty, "Brush.Content");
        root.Children.Add(shadow);
        root.Children.Add(_backdrop);
        var tint = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius) };
        tint.SetResourceReference(Border.BackgroundProperty, "Brush.Glass");
        root.Children.Add(tint);
        root.Children.Add(new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), Background = GlassTexture.Grain(dark), Opacity = dark ? 0.45 : 0.6, IsHitTestVisible = false });
        var rim = new Border { Margin = new Thickness(Shadow), CornerRadius = new CornerRadius(Radius), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        rim.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        root.Children.Add(rim);
        var top = new Border { Margin = new Thickness(Shadow + 1), CornerRadius = new CornerRadius(Radius - 1), BorderThickness = new Thickness(0, 1, 0, 0), IsHitTestVisible = false };
        top.SetResourceReference(Border.BorderBrushProperty, "Brush.RimTop");
        root.Children.Add(top);

        var dock = new DockPanel { Margin = new Thickness(Shadow + 16, Shadow + 12, Shadow + 16, Shadow + 14), FlowDirection = Lang.Flow };

        // Title (drag to move).
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12), Cursor = Cursors.SizeAll };
        DockPanel.SetDock(CloseButton, Dock.Right);
        head.Children.Add(CloseButton);
        DockPanel.SetDock(RefreshButton, Dock.Right);
        head.Children.Add(RefreshButton);
        var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 10, 0), Child = SettingsPage.Glyph("lap-longshot", 17, "Brush.Accent") };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        head.Children.Add(new TextBlock { Text = Lang.T("Longshot", "اللقطة الطويلة"), FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        head.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is DependencyObject d && (IsInside(d, CloseButton) || IsInside(d, RefreshButton))) return;
            try { DragMove(); } catch (InvalidOperationException) { }
            RefreshBackdrop();
        };
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        // Footer hint, buttons, then the size and status line above them.
        var hint = new TextBlock { Text = Lang.T("Esc stops Auto and Follow. Drag the frame's corners to resize the area, or its Move grip to move it.", "Esc يوقف التلقائي والتتبّع. اسحب زوايا الإطار لتغيير حجم المنطقة، أو مقبض التحريك لنقلها."), Style = SettingsPage.Res("Lap.Help"), Margin = new Thickness(2, 12, 18, 0) };
        DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.Add(hint);

        var buttons = new Grid();
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 3; i++) buttons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Put(Button b, int row, int col, int span = 1)
        {
            b.Margin = new Thickness(0, row == 0 ? 0 : 8, 0, 0);
            Grid.SetRow(b, row);
            Grid.SetColumn(b, col);
            Grid.SetColumnSpan(b, span);
            buttons.Children.Add(b);
        }
        Extend.Height = 40;
        Put(Extend, 0, 0, 3);
        Put(Auto, 1, 0);
        Put(Follow, 1, 2);
        Put(Undo, 2, 0);
        Put(Done, 2, 2);
        DockPanel.SetDock(buttons, Dock.Bottom);
        dock.Children.Add(buttons);

        var stats = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        stats.Children.Add(SettingsPage.Ltr(_info));
        _status.Style = SettingsPage.Res("Lap.Help");
        _status.Text = Lang.T("Getting ready…", "جارٍ التجهيز…");
        stats.Children.Add(_status);
        DockPanel.SetDock(stats, Dock.Bottom);
        dock.Children.Add(stats);

        // The live preview: the whole long image, newest part at the bottom.
        _seam.SetResourceReference(Border.BorderBrushProperty, "Brush.Accent");
        _seam.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        _stage.Children.Add(_preview);
        _stage.Children.Add(_seam);
        _scroll.Content = _stage;
        _scroll.Style = SettingsPage.Res("Lap.Scroll");
        _scroll.SizeChanged += (_, _) => _stage.Width = Math.Max(1, _scroll.ActualWidth);

        var liveRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        liveRow.Children.Add(_liveDot);
        liveRow.Children.Add(_liveText);
        _live.Child = liveRow;
        _live.Height = 22;
        _live.Padding = new Thickness(8, 0, 10, 0);
        _live.CornerRadius = new CornerRadius(11);
        _live.Margin = new Thickness(8);
        _live.HorizontalAlignment = HorizontalAlignment.Left;
        _live.VerticalAlignment = VerticalAlignment.Top;
        _live.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x12, 0x18));

        var frame = new Grid();
        var card = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Child = _scroll, Padding = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        card.SizeChanged += (_, e) => _scroll.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(0, e.NewSize.Width - 2), Math.Max(0, e.NewSize.Height - 2)), 13, 13);
        frame.Children.Add(card);
        frame.Children.Add(_live);
        dock.Children.Add(frame);
        root.Children.Add(dock);

        // Resize from the corner.
        var grip = new Thumb { Width = 22, Height = 22, Cursor = Cursors.SizeNWSE, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, Shadow + 4, Shadow + 4), ToolTip = Lang.T("Drag to resize", "اسحب لتغيير الحجم") };
        grip.Template = GripTemplate();
        grip.DragDelta += (_, e) =>
        {
            Width = Math.Clamp(Width + e.HorizontalChange, MinWidth, MaxWidth);
            Height = Math.Clamp(Height + e.VerticalChange, MinHeight, SystemParameters.WorkArea.Height);
        };
        grip.DragCompleted += (_, _) => RefreshBackdrop();
        root.Children.Add(grip);
        return root;
    }

    private static ControlTemplate GripTemplate()
    {
        var dots = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        dots.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M14,4 L4,14 M14,9 L9,14"));
        dots.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.6);
        dots.SetValue(System.Windows.Shapes.Shape.StrokeStartLineCapProperty, PenLineCap.Round);
        dots.SetValue(System.Windows.Shapes.Shape.StrokeEndLineCapProperty, PenLineCap.Round);
        dots.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Brush.Text3");
        dots.SetValue(FrameworkElement.MarginProperty, new Thickness(2));
        var host = new FrameworkElementFactory(typeof(Border));
        host.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        host.AppendChild(dots);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = host };
    }

    private static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x == ancestor) return true;
        return false;
    }

    /// <summary>
    /// The capture so far: the whole image (shrunk), scrolled down to the newest part, which
    /// glows for a moment so you can see what was just added.
    /// </summary>
    public void ShowUpdate(BitmapSource preview, int width, int height, int parts, string? message, int addedRows = 0)
    {
        _preview.Source = preview;
        _info.Text = $"{width:N0} × {height:N0} px  ·  " + Lang.T(parts == 1 ? "1 part" : $"{parts} parts", parts == 1 ? "جزء واحد" : $"{parts} أجزاء");
        if (message is not null) _status.Text = message;

        _scroll.UpdateLayout();
        double shown = _preview.ActualHeight;
        if (addedRows > 0 && height > 0 && shown > 0)
        {
            double top = (height - addedRows) * shown / height;
            _seam.Margin = new Thickness(0, top, 0, 0);
            _seam.Height = Math.Max(2, shown - top);
            _seam.BeginAnimation(OpacityProperty, Motion.IsReduced ? null : new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(1400)) { EasingFunction = Motion.InOut });
        }
        ScrollAnimator.To(_scroll, _scroll.ScrollableHeight);
    }

    public void SetStatus(string text) => _status.Text = text;

    /// <summary>The "Auto" or "Follow" badge on the preview while Longshot is scrolling or following.</summary>
    public void SetLive(string? text)
    {
        _live.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        _liveText.Text = text ?? "";
        if (text is null || Motion.IsReduced) _liveDot.BeginAnimation(OpacityProperty, null);
        else _liveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(650)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
    }

    /// <summary>Puts the panel beside the area: right, left, below, above; inside its corner as a last resort.</summary>
    public void PlaceBeside(PixelRect area)
    {
        _beside = area;
        if (IsLoaded) PlaceAtSpot();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeNoActivate(_hwnd);
        CaptureWindows.ExcludeFromCapture(_hwnd);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        PlaceAtSpot();
        RefreshBackdrop();
        _keepUp.Start();
    }

    /// <summary>Stays above every other window until closed, and keeps the glass in step with what is behind it.</summary>
    private void KeepUp()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        RefreshBackdrop();
    }

    private void RefreshBackdrop()
    {
        _lastBackdrop = DateTime.UtcNow;
        if (_hwnd != IntPtr.Zero && !KeepOffScreen) _backdrop.Refresh(_hwnd, Shadow, Radius);
    }

    private void PlaceAtSpot()
    {
        if (KeepOffScreen && _hwnd != IntPtr.Zero)
        {
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            return;
        }
        if (_beside is not { } area || _hwnd == IntPtr.Zero || !Native.GetWindowRect(_hwnd, out var r)) return;
        var spot = Spot(area, r.Width, r.Height);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, spot.X, spot.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
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
        // No room outside (the whole screen was chosen): inside the area's right edge. The panel is
        // left out of screenshots, so it never shows in the capture.
        return (ClampX(area.Right - width - gap), ClampY(area.Y + gap));
    }
}

/// <summary>
/// What is on the screen behind a window, blurred, so a see-through panel reads as frosted glass.
/// The window is left out of screen captures, so reading the screen shows what is under it.
/// In memory only.
/// </summary>
internal sealed class ScreenBackdrop : Border
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
        Effect = new BlurEffect { Radius = 30, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(1.08, 1.08), // past the edges, so the blur never fades out there
    };

    public ScreenBackdrop()
    {
        IsHitTestVisible = false;
        Child = _image;
    }

    public void Refresh(IntPtr hwnd, double inset, double radius)
    {
        // With the debugging switch that shows Longshot's windows in captures, it would see itself.
        if (Environment.GetEnvironmentVariable("AMIRLAP_SHOW_IN_CAPTURES") == "1") return;
        if (!Native.GetWindowRect(hwnd, out var r) || r.Width <= 0 || r.Height <= 0) return;
        try
        {
            using var source = new GdiScreenSource(PixelRect.From(r));
            var frame = source.Grab();
            _image.Source = LongshotOutput.ToBitmap(frame.Width, frame.Height, frame.Pixels);
            Clip = new RectangleGeometry(new Rect(inset, inset, Math.Max(0, ActualWidth - 2 * inset), Math.Max(0, ActualHeight - 2 * inset)), radius, radius);
        }
        catch (InvalidOperationException)
        {
            // The screen could not be read (locked, secure desktop): the tint alone still works.
        }
    }
}

/// <summary>Smooth scrolling for the preview (a ScrollViewer's offset cannot be animated directly).</summary>
internal static class ScrollAnimator
{
    private static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(ScrollAnimator), new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    public static void To(ScrollViewer scroll, double offset)
    {
        if (Motion.IsReduced)
        {
            scroll.BeginAnimation(OffsetProperty, null);
            scroll.ScrollToVerticalOffset(offset);
            return;
        }
        scroll.BeginAnimation(OffsetProperty, new DoubleAnimation(scroll.VerticalOffset, offset, TimeSpan.FromMilliseconds(520)) { EasingFunction = Motion.Out });
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
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(SettingsPage.Glyph("lap-longshot", 18, "Brush.Accent").With(g => g.Margin = new Thickness(0, 0, 10, 0)));
        row.Children.Add(_text);
        var border = new Border { Child = row, Padding = new Thickness(18, 11, 20, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        Content = border;
        PlaceNear(ScreenGeometry.CursorPosition(), 28);
    }

    public void SetSeconds(int secondsLeft) => _text.Text = Lang.T($"Longshot in {secondsLeft}…  open what you need now", $"اللقطة الطويلة بعد {secondsLeft}…  افتح ما تحتاجه الآن");

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CaptureWindows.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
    }
}
