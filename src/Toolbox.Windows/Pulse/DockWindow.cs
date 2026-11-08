using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Toolbox.Core.Pulse;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse;

/// <summary>
/// The dock: a strip of frosted glass flush with one edge of the screen, rounded only on its inner
/// side, with corner pieces that melt it into the edge. It stays above other windows, never takes
/// focus, and keeps out of screenshots. Drag its dots to slide it along the edge or onto another
/// edge; drag its inner edge to make it bigger or smaller (it snaps to Small, Medium or Large).
/// Hovering a tile opens its card beside it. It can slide away to a thin line, fade, and hide while
/// a full-screen app is in front.
/// </summary>
internal sealed partial class DockWindow : Window
{
    private const double Pad = 28;         // room along the edge for the corner pieces
    private const double CardRoom = 300;   // room beside the dock, inside the window, for the cards
    private const double Sliver = 6;       // what stays on screen when it slides away
    private const double Radius = 24;
    private const double Fillet = 22;
    private const string PlusTile = "+";

    private readonly Func<PulseSettings> _settings;
    private readonly Action<Action<PulseSettings>> _save;
    private readonly Grid _root = new() { Background = Brushes.Transparent };
    private readonly Canvas _layer = new() { IsHitTestVisible = false };
    private readonly Canvas _menuLayer = new();
    private readonly DispatcherTimer _menuTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private Border? _menu;
    private readonly Grid _dock = new();
    private readonly DockBackdrop _backdrop = new();
    private readonly Border _tint = new();
    private readonly Border _grain = new() { IsHitTestVisible = false, Opacity = 0.5 };
    private readonly Border _rim = new() { BorderThickness = new Thickness(1), IsHitTestVisible = false };
    private readonly StackPanel _content = new();
    private readonly Border _grip = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeAll, CornerRadius = new CornerRadius(9) };
    private readonly Border _sizer = new() { Background = Brushes.Transparent, Opacity = 0 };
    private readonly Border _sizerBar = new() { CornerRadius = new CornerRadius(2.5) };
    private readonly Border _sliver = new() { CornerRadius = new CornerRadius(1.5), Opacity = 0, IsHitTestVisible = false };
    private readonly Path[] _fillets = [new(), new()];
    private readonly TranslateTransform _slide = new();
    private readonly Dictionary<string, PulseTileView> _tiles = [];
    private readonly DispatcherTimer _keepUp = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly DispatcherTimer _fadeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly DispatcherTimer _cardTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private Border? _card;
    private string? _cardFor;
    private PulseTileView? _hovered;
    private PulseTileView? _pressed;
    private IntPtr _hwnd;
    private string _edge = "right";
    private bool _hidden;
    private bool _fullScreenHidden;
    private MonitorArea _monitor;
    private PixelRect _placed;
    private double _tileScale = 1;
    private Drag? _drag;
    private string? _liveEdge;
    private double? _livePos;
    private Native.POINT? _liveMonitorPoint;
    private DateTime _lastBackdrop;

    private sealed record Drag(bool Move, Native.POINT Start, double StartScale);

    public DockWindow(Func<PulseSettings> settings, Action<Action<PulseSettings>> save)
    {
        _settings = settings;
        _save = save;
        Title = "Pulse";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowDrop = true;
        Left = -32000;
        Top = -32000;
        Width = 400;
        Height = 600;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        if (Application.Current?.Resources["Font.UI"] is FontFamily font) FontFamily = font;
        SetResourceReference(ForegroundProperty, "Brush.Text");
        FlowDirection = FlowDirection.LeftToRight; // the dock follows the screen, not the language

        BuildDock();
        _root.Children.Add(_layer);
        _root.Children.Add(_dock);
        _root.Children.Add(_menuLayer);
        _menuTimer.Tick += (_, _) =>
        {
            // Closes once the pointer has left both the menu and the dock for a moment.
            if (_menu is not null && !_menu.IsMouseOver && !_dock.IsMouseOver) CloseMenu();
        };
        Content = _root;

        _keepUp.Tick += (_, _) => KeepUp();
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); _hideTimer.Interval = TimeSpan.FromMilliseconds(900); if (_drag is null && _menu is null && SlideMode && !_dock.IsMouseOver) SlideAway(true); };
        _fadeTimer.Tick += (_, _) => { _fadeTimer.Stop(); if (FadeMode && !_dock.IsMouseOver && _drag is null) Motion.To(_dock, OpacityProperty, 0.4, 600); };
        _cardTimer.Tick += (_, _) => { _cardTimer.Stop(); if (_hovered is not null && _drag is null) ShowCard(_hovered); };
        ThemeResources.Changed += OnThemeChanged;
        Closed += (_, _) =>
        {
            ThemeResources.Changed -= OnThemeChanged;
            _keepUp.Stop();
            _menuTimer.Stop();
            _hideTimer.Stop();
            _fadeTimer.Stop();
            _cardTimer.Stop();
        };
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Layout, DispatcherPriority.Background);
    }

    /// <summary>A tile was clicked (its id, or "+" for the add tile).</summary>
    public event Action<string>? TileClicked;

    /// <summary>Files were dropped on a tile.</summary>
    public event Action<string, string[]>? FilesDropped;

    /// <summary>The card for a tile (null: no card).</summary>
    public Func<string, FrameworkElement?>? CardFor { get; set; }

    /// <summary>The right-click menu's items.</summary>
    public Func<IReadOnlyList<DockMenuItem>>? MenuFor { get; set; }

    /// <summary>For tests: the menu card while it is open.</summary>
    internal Border? OpenMenu => _menu;

    public PulseTileView? Tile(string id) => _tiles.GetValueOrDefault(id);

    public IReadOnlyDictionary<string, PulseTileView> Tiles => _tiles;

    /// <summary>For tests: where the window was placed last (physical pixels).</summary>
    internal PixelRect Placed => _placed;

    internal string Edge => _edge;

    internal bool IsSlidAway => _hidden;

    /// <summary>Slides the dock out (if it slid away) for a few seconds, so the user sees where it is.</summary>
    public void Reveal()
    {
        if (_hidden) SlideAway(false);
        if (FadeMode) { Motion.To(_dock, OpacityProperty, 1, 200); _fadeTimer.Start(); }
        if (SlideMode) { _hideTimer.Stop(); _hideTimer.Interval = TimeSpan.FromSeconds(3); _hideTimer.Start(); }
    }

    /// <summary>Hidden from the tray or its shortcut until shown again.</summary>
    public bool UserHidden { get; private set; }

    public void SetUserHidden(bool hidden)
    {
        UserHidden = hidden;
        if (_hwnd == IntPtr.Zero) return;
        if (hidden) Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | SWP_HIDEWINDOW);
        else Layout();
    }

    private bool SlideMode => _settings().Show == "slide";
    private bool FadeMode => _settings().Show == "fade";

    // ---- building ------------------------------------------------------------------------------

    private void BuildDock()
    {
        _dock.RenderTransform = _slide;
        _dock.Children.Add(_backdrop);
        _dock.Children.Add(_tint);
        _dock.Children.Add(_grain);
        _dock.Children.Add(_rim);
        _content.Margin = new Thickness(7, 10, 7, 6);
        _dock.Children.Add(_content);

        var dots = new Canvas { Width = 18, Height = 7, IsHitTestVisible = false };
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 3; col++)
            {
                var dot = new Ellipse { Width = 2.8, Height = 2.8 };
                dot.SetResourceReference(Shape.FillProperty, "Brush.Text3");
                Canvas.SetLeft(dot, 1.6 + col * 6);
                Canvas.SetTop(dot, 0.1 + row * 4);
                dots.Children.Add(dot);
            }
        _grip.Child = new Viewbox { Width = 18, Height = 7, Child = dots, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _grip.ToolTip = Lang.T("Drag to move the dock, along the edge or to another edge", "Ø§Ø³Ø­Ø¨ Ù„ØªØ­Ø±ÙŠÙƒ Ø§Ù„Ø´Ø±ÙŠØ· Ø¹Ù„Ù‰ Ø·ÙˆÙ„ Ø§Ù„Ø­Ø§ÙØ© Ø£Ùˆ Ø¥Ù„Ù‰ Ø­Ø§ÙØ© Ø£Ø®Ø±Ù‰");
        _grip.MouseEnter += (_, _) => _grip.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
        _grip.MouseLeave += (_, _) => { if (_drag is null) _grip.Background = Brushes.Transparent; };
        _grip.MouseLeftButtonDown += (_, e) => BeginDrag(_grip, move: true, e);
        _grip.MouseMove += (_, _) => DragMove2();
        _grip.MouseLeftButtonUp += (_, _) => EndDrag(_grip);
        _grip.LostMouseCapture += (_, _) => EndDrag(_grip);

        _sizerBar.SetResourceReference(Border.BackgroundProperty, "Brush.Text3");
        _sizer.Child = _sizerBar;
        _sizer.ToolTip = Lang.T("Drag to make the dock bigger or smaller", "Ø§Ø³Ø­Ø¨ Ù„ØªÙƒØ¨ÙŠØ± Ø§Ù„Ø´Ø±ÙŠØ· Ø£Ùˆ ØªØµØºÙŠØ±Ù‡");
        _sizer.MouseEnter += (_, _) => GrowSizer(true);
        _sizer.MouseLeave += (_, _) => { if (_drag is null) GrowSizer(false); };
        _sizer.MouseLeftButtonDown += (_, e) => BeginDrag(_sizer, move: false, e);
        _sizer.MouseMove += (_, _) => DragMove2();
        _sizer.MouseLeftButtonUp += (_, _) => EndDrag(_sizer);
        _sizer.LostMouseCapture += (_, _) => EndDrag(_sizer);
        _dock.Children.Add(_sizer);

        _sliver.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        _sliver.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 0, Color = ThemeResources.Lap.Accent, Opacity = 0.9 };
        _dock.Children.Add(_sliver);

        foreach (var f in _fillets) _layer.Children.Add(f);

        _dock.MouseEnter += (_, _) =>
        {
            _hideTimer.Stop();
            _fadeTimer.Stop();
            if (_hidden) SlideAway(false);
            Motion.To(_dock, OpacityProperty, 1, 200);
            Motion.To(_sizer, OpacityProperty, 1, 200);
        };
        _dock.MouseLeave += (_, _) =>
        {
            if (_drag is not null) return;
            Motion.To(_sizer, OpacityProperty, 0, 300);
            if (SlideMode) _hideTimer.Start();
            if (FadeMode) _fadeTimer.Start();
        };
        _dock.MouseRightButtonUp += (_, e) =>
        {
            ShowMenu(e.GetPosition(_root));
            e.Handled = true;
        };
        _dock.DragEnter += (_, _) => { if (_hidden) SlideAway(false); };
        ApplyLook();
    }

    /// <summary>Glass colours for the current theme (and solid when transparency is reduced).</summary>
    private void ApplyLook()
    {
        var t = ThemeResources.Lap;
        Brush tint;
        if (t.GlassOff)
        {
            tint = (Brush)FindResource("Brush.SurfaceSolid");
            _backdrop.Visibility = Visibility.Collapsed;
            _grain.Background = null;
        }
        else
        {
            tint = t.IsDark
                ? new LinearGradientBrush(Color.FromArgb(0xB3, 0x2A, 0x2B, 0x32), Color.FromArgb(0x9E, 0x0E, 0x0F, 0x13), 90)
                : new LinearGradientBrush(Color.FromArgb(0xC2, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x85, 0xFF, 0xFF, 0xFF), 90);
            _backdrop.Visibility = Visibility.Visible;
            _grain.Background = GlassTexture.Grain(t.IsDark);
        }
        _tint.Background = tint;
        _rim.SetResourceReference(Border.BorderBrushProperty, "Brush.Rim");
        var filletFill = t.GlassOff ? tint : new SolidColorBrush(t.IsDark ? Color.FromArgb(0xE6, 0x24, 0x25, 0x2B) : Color.FromArgb(0xD9, 0xF7, 0xF8, 0xFB));
        foreach (var f in _fillets) f.Fill = filletFill;
        _dock.Effect = new DropShadowEffect { BlurRadius = 26, ShadowDepth = 0, Opacity = t.IsDark ? 0.55 : 0.18, Color = Colors.Black };
        if (_sliver.Effect is DropShadowEffect glow) glow.Color = t.Accent;
    }

    private void OnThemeChanged() => Dispatcher.BeginInvoke(() =>
    {
        ApplyLook();
        Rebuild();
    });

    /// <summary>Rebuilds the tiles from the settings and places the dock.</summary>
    public void Rebuild()
    {
        var s = _settings();
        _tileScale = PulseSettings.ScaleOf(s.Size);
        HideCard(immediate: true);
        _content.Children.Clear();
        _tiles.Clear();
        _content.Children.Add(_grip);
        var ids = s.ActiveTiles;
        for (int i = 0; i < ids.Count; i++)
        {
            // A thin line between the usage tiles and the shortcuts.
            if (i > 0 && PulseTiles.HasRing(ids[i - 1]) && !PulseTiles.HasRing(ids[i]) && ids[i - 1] != PulseTiles.System) _content.Children.Add(Separator());
            _content.Children.Add(MakeTile(ids[i]));
        }
        _content.Children.Add(Separator());
        _content.Children.Add(MakeTile(PlusTile));
        Layout();
        if (SlideMode && !_dock.IsMouseOver) _hideTimer.Start();
        else if (_hidden) SlideAway(false);
        if (!FadeMode) _dock.Opacity = 1;
        else _fadeTimer.Start();
    }

    private Border Separator()
    {
        var b = new Border { IsHitTestVisible = false, Tag = "sep" };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Line2");
        return b;
    }

    private PulseTileView MakeTile(string id)
    {
        var tile = new PulseTileView(id, IconOf(id)) { TileScale = _tileScale, ShowLabel = _tileScale >= 0.9 };
        tile.SetLabel(id == PlusTile ? "" : Tools.PulseTool.ShortName(id));
        tile.MouseEnter += (_, _) =>
        {
            _hovered = tile;
            if (_card is not null) ShowCard(tile);
            else { _cardTimer.Stop(); _cardTimer.Start(); }
        };
        tile.MouseLeave += (_, _) =>
        {
            if (_hovered == tile) _hovered = null;
            Dispatcher.BeginInvoke(() => { if (_hovered is null) HideCard(); }, DispatcherPriority.Input);
        };
        tile.MouseLeftButtonDown += (_, e) => { _pressed = tile; e.Handled = true; };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_pressed == tile && _drag is null)
            {
                HideCard(immediate: true);
                TileClicked?.Invoke(id);
            }
            _pressed = null;
            e.Handled = true;
        };
        if (id == PulseTiles.Toolbox)
        {
            tile.AllowDrop = true;
            tile.DragEnter += (_, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) { tile.SetDropTarget(true); e.Effects = DragDropEffects.Copy; } else e.Effects = DragDropEffects.None; e.Handled = true; };
            tile.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            tile.DragLeave += (_, _) => tile.SetDropTarget(false);
            tile.Drop += (_, e) =>
            {
                tile.SetDropTarget(false);
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) FilesDropped?.Invoke(id, files);
                e.Handled = true;
            };
        }
        _tiles[id] = tile;
        return tile;
    }

    public static string IconOf(string id) => id switch
    {
        PulseTiles.Claude => "lap-claude",
        PulseTiles.Codex => "lap-codex",
        PulseTiles.ChatGpt => "lap-chat",
        PulseTiles.Longshot => "lap-longshot",
        PulseTiles.Toolbox => "lap-toolbox",
        PulseTiles.Timer => "lap-timer",
        PulseTiles.Note => "lap-note",
        PulseTiles.Colour => "lap-colour",
        PulseTiles.Folder => "lap-folder",
        PulseTiles.Link => "lap-link",
        PulseTiles.System => "lap-system",
        _ => "lap-plus",
    };

    // ---- placing -------------------------------------------------------------------------------

    /// <summary>The screen the dock belongs on: the one with the pointer, or the one it was put on (the main screen at first).</summary>
    /// <summary>For tests: the screen to use instead of a real one (an area far off-screen).</summary>
    internal Func<MonitorArea>? ScreenOverride { get; set; }

    private MonitorArea MonitorFor(PulseSettings s)
    {
        if (ScreenOverride is not null) return ScreenOverride();
        if (_liveMonitorPoint is { } live) return ScreenGeometry.MonitorAt(live);
        if (s.Screen == "pointer") return ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition());
        return ScreenGeometry.MonitorAt(MainPoint(s));
    }

    /// <summary>A point on the screen the user put the dock on (stored as "x,y"), else the main screen.</summary>
    private static Native.POINT MainPoint(PulseSettings s)
    {
        if (s.MonitorPoint is { } text && text.Split(',') is [var x, var y]
            && int.TryParse(x, System.Globalization.CultureInfo.InvariantCulture, out var px) && int.TryParse(y, System.Globalization.CultureInfo.InvariantCulture, out var py))
            return new Native.POINT { X = px, Y = py };
        return new Native.POINT { X = 0, Y = 0 };
    }

    private void Orient(string edge)
    {
        _edge = edge;
        bool vertical = edge is "left" or "right";
        _content.Orientation = vertical ? Orientation.Vertical : Orientation.Horizontal;
        _content.Margin = vertical ? new Thickness(7, 10, 7, 6) : new Thickness(10, 7, 6, 7);
        _grip.Width = vertical ? 36 * _tileScale : 18;
        _grip.Height = vertical ? 18 : 40 * _tileScale;
        _grip.Margin = vertical ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 2, 0);
        if (_grip.Child is Viewbox vb) vb.LayoutTransform = vertical ? Transform.Identity : new RotateTransform(90);
        foreach (var child in _content.Children.OfType<Border>().Where(b => (string?)b.Tag == "sep"))
        {
            child.Width = vertical ? 32 * _tileScale : 1;
            child.Height = vertical ? 1 : 40 * _tileScale;
            child.Margin = vertical ? new Thickness(0, 4, 0, 4) : new Thickness(4, 0, 4, 0);
        }
        foreach (var t in _tiles.Values)
        {
            t.TileScale = _tileScale;
            t.ShowLabel = _tileScale >= 0.9;
        }

        var radius = edge switch
        {
            "left" => new CornerRadius(0, Radius, Radius, 0),
            "right" => new CornerRadius(Radius, 0, 0, Radius),
            "top" => new CornerRadius(0, 0, Radius, Radius),
            _ => new CornerRadius(Radius, Radius, 0, 0),
        };
        _tint.CornerRadius = radius;
        _grain.CornerRadius = radius;
        _rim.CornerRadius = radius;
        _rim.BorderThickness = edge switch
        {
            "left" => new Thickness(0, 1, 1, 1),
            "right" => new Thickness(1, 1, 0, 1),
            "top" => new Thickness(1, 0, 1, 1),
            _ => new Thickness(1, 1, 1, 0),
        };
        _dock.HorizontalAlignment = edge switch { "left" => HorizontalAlignment.Left, "right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        _dock.VerticalAlignment = edge switch { "top" => VerticalAlignment.Top, "bottom" => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };

        // The resize handle and the thin "slid away" line sit on the dock's inner side.
        _sizer.Width = vertical ? 16 : 72;
        _sizer.Height = vertical ? 72 : 16;
        _sizer.HorizontalAlignment = edge switch { "right" => HorizontalAlignment.Left, "left" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        _sizer.VerticalAlignment = edge switch { "bottom" => VerticalAlignment.Top, "top" => VerticalAlignment.Bottom, _ => VerticalAlignment.Center };
        _sizer.Margin = edge switch { "right" => new Thickness(-8, 0, 0, 0), "left" => new Thickness(0, 0, -8, 0), "bottom" => new Thickness(0, -8, 0, 0), _ => new Thickness(0, 0, 0, -8) };
        _sizer.Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS;
        GrowSizer(false);

        _sliver.Width = vertical ? 3 : double.NaN;
        _sliver.Height = vertical ? double.NaN : 3;
        _sliver.HorizontalAlignment = edge switch { "right" => HorizontalAlignment.Left, "left" => HorizontalAlignment.Right, _ => HorizontalAlignment.Stretch };
        _sliver.VerticalAlignment = edge switch { "bottom" => VerticalAlignment.Top, "top" => VerticalAlignment.Bottom, _ => VerticalAlignment.Stretch };
        _sliver.Margin = edge switch
        {
            "right" => new Thickness(1.5, 0, 0, 0),
            "left" => new Thickness(0, 0, 1.5, 0),
            "bottom" => new Thickness(0, 1.5, 0, 0),
            _ => new Thickness(0, 0, 0, 1.5),
        };
    }

    private void GrowSizer(bool on)
    {
        bool vertical = _edge is "left" or "right";
        double length = on || _drag is { Move: false } ? 52 : 28, thick = on ? 5 : 4;
        var along = vertical ? HeightProperty : WidthProperty;
        var across = vertical ? WidthProperty : HeightProperty;
        _sizerBar.BeginAnimation(across, null);
        _sizerBar.SetValue(across, thick);
        if (double.IsNaN((double)_sizerBar.GetValue(along))) _sizerBar.SetValue(along, 28.0); // animations need a number to start from
        Motion.To(_sizerBar, along, length, 300, Motion.Spring);
        if (on) _sizerBar.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        else _sizerBar.SetResourceReference(Border.BackgroundProperty, "Brush.Text3");
    }

    /// <summary>Sizes the window around the dock and puts it on its edge (physical pixels, without activating it).</summary>
    public void Layout()
    {
        var s = _settings();
        string edge = _liveEdge ?? Normal(s.Edge);
        double pos = Math.Clamp(_livePos ?? s.Position, 0, 1);
        Orient(edge);
        if (_hwnd == IntPtr.Zero) return; // built but not shown yet (and in tests)
        var monitor = MonitorFor(s);
        bool vertical = edge is "left" or "right";

        _content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = _content.DesiredSize;
        var m = _content.Margin;
        // The dock is exactly its tiles plus padding (nothing else may size it), and never thicker than a large tile.
        double dockLen = vertical ? d.Height + m.Top + m.Bottom : d.Width + m.Left + m.Right;
        double dockThick = Math.Min(vertical ? d.Width + m.Left + m.Right : d.Height + m.Top + m.Bottom, 100);
        _dock.Width = vertical ? dockThick : dockLen;
        _dock.Height = vertical ? dockLen : dockThick;
        double along = Math.Max(dockLen + 2 * Pad, 320), cross = dockThick + CardRoom;
        var work = PixelRect.From(monitor.WorkArea);
        double k = monitor.Scale;
        int start = vertical ? work.Y : work.X, end = vertical ? work.Bottom : work.Right;
        int alongPx = Math.Min((int)Math.Ceiling(along * k), end - start);
        int crossPx = (int)Math.Ceiling(cross * k);
        int center = (int)Math.Round(start + pos * (end - start));
        int a0 = Math.Clamp(center - alongPx / 2, start, end - alongPx);
        var r = edge switch
        {
            "left" => new PixelRect(work.X, a0, crossPx, alongPx),
            "right" => new PixelRect(work.Right - crossPx, a0, crossPx, alongPx),
            "top" => new PixelRect(a0, work.Y, alongPx, crossPx),
            _ => new PixelRect(a0, work.Bottom - crossPx, alongPx, crossPx),
        };
        _monitor = monitor;
        _placed = r;
        if (!_fullScreenHidden && !UserHidden) CaptureWindows.Place(_hwnd, r);
        _slide.X = _slide.Y = 0;
        if (_hidden) SlideAway(true, animate: false);
        Dispatcher.BeginInvoke(() =>
        {
            PlaceFillets();
            RefreshBackdrop();
        }, DispatcherPriority.Loaded);
    }

    private static string Normal(string? edge) => edge is "left" or "top" or "bottom" ? edge : "right";

    /// <summary>The corner pieces that join the dock to the screen edge, just outside its two ends.</summary>
    private void PlaceFillets()
    {
        if (!_dock.IsArrangeValid || _dock.ActualWidth <= 0) return;
        var tl = _dock.TranslatePoint(new Point(0, 0), _root);
        double l = tl.X, t = tl.Y, r = l + _dock.ActualWidth, b = t + _dock.ActualHeight, f = Fillet;
        const string A = "M22,0 L22,22 L0,22 A22,22 0 0 0 22,0 Z"; // filled corner: bottom right
        const string B = "M0,0 L22,0 L22,22 A22,22 0 0 0 0,0 Z";   // top right
        const string C = "M0,0 L0,22 L22,22 A22,22 0 0 1 0,0 Z";   // bottom left
        const string D = "M0,22 L0,0 L22,0 A22,22 0 0 0 0,22 Z";   // top left
        (string, double, double)[] spots = _edge switch
        {
            "right" => [(A, r - f, t - f), (B, r - f, b)],
            "left" => [(C, l, t - f), (D, l, b)],
            "top" => [(B, l - f, t), (D, r, t)],
            _ => [(A, l - f, b - f), (C, r, b - f)],
        };
        for (int i = 0; i < 2; i++)
        {
            _fillets[i].Data = Geometry.Parse(spots[i].Item1);
            Canvas.SetLeft(_fillets[i], spots[i].Item2 + _slide.X);
            Canvas.SetTop(_fillets[i], spots[i].Item3 + _slide.Y);
            _fillets[i].Opacity = _hidden ? 0 : 1;
        }
    }

    // ---- moving and resizing -------------------------------------------------------------------

    private void BeginDrag(FrameworkElement handle, bool move, MouseButtonEventArgs e)
    {
        HideCard(immediate: true);
        _drag = new Drag(move, ScreenGeometry.CursorPosition(), _tileScale);
        handle.CaptureMouse();
        if (!move) GrowSizer(true);
        e.Handled = true;
    }

    private void DragMove2()
    {
        if (_drag is null) return;
        var p = ScreenGeometry.CursorPosition();
        if (_drag.Move)
        {
            var monitor = ScreenGeometry.MonitorAt(p);
            var w = PixelRect.From(monitor.WorkArea);
            double k = monitor.Scale;
            string edge = _liveEdge ?? _edge;
            var near = new[] { ("left", p.X - w.X), ("right", w.Right - p.X), ("top", p.Y - w.Y), ("bottom", w.Bottom - p.Y) }.MinBy(x => x.Item2);
            if (near.Item2 < 160 * k) edge = near.Item1;
            bool vertical = edge is "left" or "right";
            // Keep the dots under the pointer: the dock's middle is half its length past them.
            double half = (vertical ? _dock.ActualHeight : _dock.ActualWidth) / 2 - 18;
            double along = vertical ? p.Y - w.Y + half * k : p.X - w.X + half * k;
            _liveEdge = edge;
            _livePos = Math.Clamp(along / (vertical ? w.Height : w.Width), 0, 1);
            _liveMonitorPoint = new Native.POINT { X = w.CenterX, Y = w.Y + w.Height / 2 };
            Layout();
        }
        else
        {
            double k = _monitor.Scale <= 0 ? 1 : _monitor.Scale;
            double dx = (p.X - _drag.Start.X) / k, dy = (p.Y - _drag.Start.Y) / k;
            double outward = _edge switch { "right" => -dx, "left" => dx, "top" => dy, _ => -dy };
            _tileScale = Math.Clamp(_drag.StartScale + outward / 160, 0.7, 1.35);
            Layout();
        }
    }

    private void EndDrag(FrameworkElement handle)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        if (handle.IsMouseCaptured) handle.ReleaseMouseCapture();
        if (drag.Move)
        {
            var edge = _liveEdge;
            var pos = _livePos;
            var point = _liveMonitorPoint;
            _liveEdge = null;
            _livePos = null;
            _liveMonitorPoint = null;
            _grip.Background = Brushes.Transparent;
            if (edge is not null && pos is not null)
                _save(s =>
                {
                    s.Edge = edge;
                    s.Position = Math.Round(pos.Value, 4);
                    if (point is { } mp) s.MonitorPoint = FormattableString.Invariant($"{mp.X},{mp.Y}");
                });
        }
        else
        {
            var size = PulseSettings.SizeNearest(_tileScale);
            _tileScale = PulseSettings.ScaleOf(size);
            GrowSizer(_sizer.IsMouseOver);
            _save(s => s.Size = size);
        }
        Layout();
        if (!_dock.IsMouseOver)
        {
            Motion.To(_sizer, OpacityProperty, 0, 300);
            if (SlideMode) _hideTimer.Start();
        }
    }

    // ---- slide away, fade, full screen ---------------------------------------------------------

    private void SlideAway(bool hide, bool animate = true)
    {
        _hidden = hide;
        bool vertical = _edge is "left" or "right";
        double thick = vertical ? _dock.ActualWidth : _dock.ActualHeight;
        double off = hide ? Math.Max(0, thick - Sliver) : 0;
        double x = _edge == "right" ? off : _edge == "left" ? -off : 0;
        double y = _edge == "bottom" ? off : _edge == "top" ? -off : 0;
        int ms = animate ? (hide ? 420 : 380) : 0;
        Motion.To(_slide, TranslateTransform.XProperty, x, ms, hide ? Motion.InOut : Motion.Settle);
        Motion.To(_slide, TranslateTransform.YProperty, y, ms, hide ? Motion.InOut : Motion.Settle);
        Motion.To(_sliver, OpacityProperty, hide ? 1 : 0, 250);
        foreach (var f in _fillets) Motion.To(f, OpacityProperty, hide ? 0 : 1, 200);
        if (hide && !Motion.IsReduced)
            _sliver.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(1.5)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(300) });
        if (!hide)
        {
            var refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms + 40) };
            refresh.Tick += (_, _) => { refresh.Stop(); RefreshBackdrop(); };
            refresh.Start();
        }
    }

    private void KeepUp()
    {
        if (_hwnd == IntPtr.Zero || UserHidden) return;
        var s = _settings();
        bool full = s.HideInFullScreen && FullScreenAppIn(_monitor);
        if (full != _fullScreenHidden)
        {
            _fullScreenHidden = full;
            if (full) Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | SWP_HIDEWINDOW);
            else Layout();
            return;
        }
        if (full) return;
        // The pointer moved to another screen, or the taskbar / resolution changed: place it again.
        var now = MonitorFor(s);
        if (now.WorkArea.Left != _monitor.WorkArea.Left || now.WorkArea.Top != _monitor.WorkArea.Top || now.WorkArea.Right != _monitor.WorkArea.Right || now.WorkArea.Bottom != _monitor.WorkArea.Bottom || Math.Abs(now.Scale - _monitor.Scale) > 0.01)
        {
            Layout();
            return;
        }
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        RefreshBackdrop();
    }

    private const uint SWP_HIDEWINDOW = 0x0080;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(IntPtr hwnd, [Out] char[] name, int max);

    /// <summary>True when the window in front covers the whole of that screen (a game, a video, a presentation).</summary>
    private static bool FullScreenAppIn(MonitorArea monitor)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;
        var name = new char[64];
        int length = GetClassName(hwnd, name, name.Length);
        if (length > 0 && ShellClasses.Contains(new string(name, 0, length))) return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var m = monitor.Bounds;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>For tests: how many times the glass read the screen.</summary>
    internal int BackdropRefreshes { get; private set; }

    /// <summary>For tests: one round of the every-few-seconds upkeep.</summary>
    internal void KeepUpNow()
    {
        _lastBackdrop = DateTime.MinValue;
        KeepUp();
    }

    private void RefreshBackdrop()
    {
        if (_hwnd == IntPtr.Zero || _hidden || _fullScreenHidden || !_dock.IsArrangeValid || _dock.ActualWidth <= 0 || ThemeResources.Lap.GlassOff) return;
        if ((DateTime.UtcNow - _lastBackdrop).TotalMilliseconds < 120) return;
        _lastBackdrop = DateTime.UtcNow;
        try
        {
            var a = _dock.PointToScreen(new Point(0, 0));
            var b = _dock.PointToScreen(new Point(_dock.ActualWidth, _dock.ActualHeight));
            var rect = new PixelRect((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y), (int)Math.Abs(b.X - a.X), (int)Math.Abs(b.Y - a.Y));
            _backdrop.Refresh(rect, _tint.CornerRadius, new Size(_dock.ActualWidth, _dock.ActualHeight));
            BackdropRefreshes++;
        }
        catch (InvalidOperationException)
        {
            // Not on screen yet.
        }
    }

    // ---- right-click menu ----------------------------------------------------------------------

    /// <summary>Opens the menu beside the dock at the pointer, as a glass card in this window.</summary>
    internal void ShowMenu(Point at)
    {
        CloseMenu();
        HideCard(immediate: true);
        if (MenuFor?.Invoke() is not { Count: > 0 } items) return;
        var stack = new StackPanel { Margin = new Thickness(6) };
        foreach (var item in items)
        {
            if (item.Separator)
            {
                var line = new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5) };
                line.SetResourceReference(Border.BackgroundProperty, "Brush.Line2");
                stack.Children.Add(line);
            }
            var row = new DockPanel();
            var check = SettingsPage.Glyph("lap-check", 14, "Brush.Accent", stroke: 2.4);
            check.Margin = new Thickness(0, 0, 10, 0);
            check.Opacity = item.Checked ? 1 : 0;
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(check);
            row.Children.Add(new TextBlock { Text = item.Label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Style = SettingsPage.Res("Lap.Button.Ghost"),
                Content = row,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderThickness = new Thickness(0),
                Height = 34,
                Padding = new Thickness(10, 0, 14, 0),
                FontWeight = FontWeights.Normal,
                Focusable = false,
            };
            var run = item.Run;
            button.Click += (_, _) =>
            {
                CloseMenu();
                run();
            };
            stack.Children.Add(button);
        }
        var menu = new Border { Child = stack, MinWidth = 220, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), FlowDirection = Lang.Flow };
        menu.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        menu.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        menu.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text");
        menu.Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = ThemeResources.Lap.IsDark ? 0.55 : 0.2, Color = Colors.Black };
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = menu.DesiredSize;
        var dl = _dock.TranslatePoint(new Point(0, 0), _root);
        double x, y;
        switch (_edge)
        {
            case "right": x = dl.X - 12 - size.Width; y = at.Y - 20; break;
            case "left": x = dl.X + _dock.ActualWidth + 12; y = at.Y - 20; break;
            case "top": x = at.X - 20; y = dl.Y + _dock.ActualHeight + 12; break;
            default: x = at.X - 20; y = dl.Y - 12 - size.Height; break;
        }
        Canvas.SetLeft(menu, Math.Clamp(x, 6, Math.Max(6, _root.ActualWidth - size.Width - 6)));
        Canvas.SetTop(menu, Math.Clamp(y, 6, Math.Max(6, _root.ActualHeight - size.Height - 6)));
        _menuLayer.Children.Add(menu);
        _menu = menu;
        Motion.FromTo(menu, OpacityProperty, 0, 1, 160);
        _menuTimer.Start();
    }

    internal void CloseMenu()
    {
        _menuTimer.Stop();
        if (_menu is null) return;
        _menuLayer.Children.Remove(_menu);
        _menu = null;
        if (!_dock.IsMouseOver && SlideMode) _hideTimer.Start();
    }

    // ---- cards ---------------------------------------------------------------------------------

    private void ShowCard(PulseTileView tile, bool animate = true)
    {
        var content = CardFor?.Invoke(tile.Id);
        if (content is null)
        {
            HideCard();
            return;
        }
        if (_card is not null) _layer.Children.Remove(_card);
        _card = content as Border ?? PulseCards.Card(content);
        _cardFor = tile.Id;
        _card.FlowDirection = Lang.Flow;
        _layer.Children.Add(_card);
        _card.Measure(new Size(PulseCards.Width, double.PositiveInfinity));
        var size = _card.DesiredSize;
        var tl = tile.TranslatePoint(new Point(0, 0), _root);
        var dl = _dock.TranslatePoint(new Point(0, 0), _root);
        double cx = tl.X + tile.ActualWidth / 2, cy = tl.Y + tile.ActualHeight / 2, gap = 16;
        double x, y;
        switch (_edge)
        {
            case "right": x = dl.X - gap - size.Width; y = cy - size.Height / 2; break;
            case "left": x = dl.X + _dock.ActualWidth + gap; y = cy - size.Height / 2; break;
            case "top": x = cx - size.Width / 2; y = dl.Y + _dock.ActualHeight + gap; break;
            default: x = cx - size.Width / 2; y = dl.Y - gap - size.Height; break;
        }
        x = Math.Clamp(x, 6, Math.Max(6, _root.ActualWidth - size.Width - 6));
        y = Math.Clamp(y, 6, Math.Max(6, _root.ActualHeight - size.Height - 6));
        Canvas.SetLeft(_card, x);
        Canvas.SetTop(_card, y);
        var move = new TranslateTransform(_edge == "right" ? 10 : _edge == "left" ? -10 : 0, _edge == "bottom" ? 10 : _edge == "top" ? -10 : 0);
        _card.RenderTransform = move;
        if (!animate)
        {
            move.X = move.Y = 0;
            return;
        }
        Motion.FromTo(_card, OpacityProperty, 0, 1, 180);
        Motion.To(move, TranslateTransform.XProperty, 0, 320, Motion.Settle);
        Motion.To(move, TranslateTransform.YProperty, 0, 320, Motion.Settle);
    }

    /// <summary>Rebuilds the open card (its numbers changed).</summary>
    public void RefreshCard()
    {
        if (_cardFor is not null && _tiles.TryGetValue(_cardFor, out var tile) && _card is not null)
            ShowCard(tile, animate: false);
    }

    private void HideCard(bool immediate = false)
    {
        _cardTimer.Stop();
        if (_card is not { } card) return;
        _card = null;
        _cardFor = null;
        if (immediate || Motion.IsReduced)
        {
            _layer.Children.Remove(card);
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) => _layer.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);
    }

    // ---- window --------------------------------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        CaptureWindows.MakeNoActivate(_hwnd);
        CaptureWindows.ExcludeFromCapture(_hwnd); // stays out of screenshots, and lets the glass read what is behind it
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Rebuild();
        _keepUp.Start();
    }
}

/// <summary>The screen behind the dock, blurred, clipped to the dock's shape (frosted glass).</summary>
internal sealed class DockBackdrop : Border
{
    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
        Effect = new BlurEffect { Radius = 28, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance },
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new ScaleTransform(1.1, 1.1),
    };

    public DockBackdrop()
    {
        IsHitTestVisible = false;
        Child = _image;
    }

    /// <summary>
    /// Takes no room of its own: the picture fills whatever the dock is. (Its picture is the size of
    /// the dock in pixels; counting it made the dock bigger on every refresh.)
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        _image.Measure(new Size(0, 0));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        _image.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }

    public void Refresh(PixelRect rect, CornerRadius radius, Size size)
    {
        if (Environment.GetEnvironmentVariable("AMIRLAP_SHOW_IN_CAPTURES") == "1" || rect.Width <= 0 || rect.Height <= 0) return;
        try
        {
            using var source = new GdiScreenSource(rect);
            var frame = source.Grab();
            _image.Source = LongshotOutput.ToBitmap(frame.Width, frame.Height, frame.Pixels);
            Clip = Rounded(new Rect(size), radius);
        }
        catch (InvalidOperationException)
        {
            // The screen could not be read (locked, secure desktop): the tint alone still works.
        }
    }

    /// <summary>A rectangle with its own radius at each corner.</summary>
    public static Geometry Rounded(Rect r, CornerRadius c)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(r.Left + c.TopLeft, r.Top), true, true);
            ctx.LineTo(new Point(r.Right - c.TopRight, r.Top), true, false);
            if (c.TopRight > 0) ctx.ArcTo(new Point(r.Right, r.Top + c.TopRight), new Size(c.TopRight, c.TopRight), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - c.BottomRight), true, false);
            if (c.BottomRight > 0) ctx.ArcTo(new Point(r.Right - c.BottomRight, r.Bottom), new Size(c.BottomRight, c.BottomRight), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Left + c.BottomLeft, r.Bottom), true, false);
            if (c.BottomLeft > 0) ctx.ArcTo(new Point(r.Left, r.Bottom - c.BottomLeft), new Size(c.BottomLeft, c.BottomLeft), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(r.Left, r.Top + c.TopLeft), true, false);
            if (c.TopLeft > 0) ctx.ArcTo(new Point(r.Left + c.TopLeft, r.Top), new Size(c.TopLeft, c.TopLeft), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>One line of the dock's right-click menu.</summary>
internal sealed record DockMenuItem(string Label, Action Run, bool Checked = false, bool Separator = false);
