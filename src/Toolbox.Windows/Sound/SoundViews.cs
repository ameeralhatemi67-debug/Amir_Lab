using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core.Sound;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Sound;

/// <summary>Pieces shared by the mixer window, Pulse's quick mixer and the level bar.</summary>
internal static class SoundViews
{
    private static readonly Dictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Color[] TileColours =
    [
        Color.FromRgb(0xE5, 0x48, 0x4D), Color.FromRgb(0x58, 0x65, 0xF2), Color.FromRgb(0x1D, 0xB9, 0x54), Color.FromRgb(0x0C, 0x8C, 0xE9),
        Color.FromRgb(0x25, 0xD3, 0x66), Color.FromRgb(0xF5, 0x9E, 0x0B), Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0x14, 0xB8, 0xA6),
    ];

    /// <summary>The app's own icon (from its program file), or a coloured tile with its first letter.</summary>
    public static FrameworkElement AppIcon(string key, string name, string? exePath, bool isSystem, double size)
    {
        if (isSystem)
        {
            var tile = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.3), Child = SettingsPage.Glyph("lap-sound", size * 0.6, "Brush.Text2") };
            tile.SetResourceReference(Border.BackgroundProperty, "Brush.Line2");
            return tile;
        }
        if (IconOf(exePath) is { } icon)
            return new Image { Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform, SnapsToDevicePixels = true };
        var letter = (name.Length > 0 ? name : key).Trim();
        int hash = 0;
        foreach (var ch in key) hash = hash * 31 + ch;
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.3),
            Background = new SolidColorBrush(TileColours[(hash & 0x7fffffff) % TileColours.Length]),
            Child = new TextBlock
            {
                Text = letter.Length > 0 ? char.ToUpperInvariant(letter[0]).ToString() : "?",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = size * 0.45,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static ImageSource? IconOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Icons.TryGetValue(path, out var cached)) return cached;
        ImageSource? source = null;
        try
        {
            if (File.Exists(path) && System.Drawing.Icon.ExtractIcon(path, 0, 48) is { } icon)
            {
                using (icon)
                {
                    var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bitmap.Freeze();
                    source = bitmap;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            source = null;
        }
        Icons[path] = source;
        return source;
    }

    /// <summary>The icon for a kind of output.</summary>
    public static string DeviceIcon(string? kind) => kind switch
    {
        "headphones" => "lap-headphones",
        "speakers" => "lap-speaker",
        "monitor" => "lap-monitor",
        _ => "lap-sound",
    };

    /// <summary>A small square button with an icon (mute, open).</summary>
    public static Border IconButton(string icon, string name, Action click, double size = 32, string brush = "Brush.Text2")
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.28), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = SettingsPage.Glyph(icon, size * 0.56, brush), ToolTip = name };
        AutomationProperties.SetName(b, name);
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Brush.Line2");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return b;
    }

    /// <summary>Swaps a button's icon in place (mute ↔ unmute).</summary>
    public static void SetIcon(Border button, string icon, string name, string brush = "Brush.Text2")
    {
        button.Child = SettingsPage.Glyph(icon, button.Width * 0.56, brush);
        button.ToolTip = name;
        AutomationProperties.SetName(button, name);
    }

    public static TextBlock Text(string text, double size = 13, string brush = "Brush.Text", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>The volume slider every row uses (0..100, a click jumps there, the wheel moves it by 2).</summary>
    public static Slider VolumeSlider(string name)
    {
        var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 5, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, name);
        slider.PreviewMouseWheel += (_, e) =>
        {
            slider.Value = Math.Clamp(slider.Value + Math.Sign(e.Delta) * 2, 0, 100);
            e.Handled = true;
        };
        return slider;
    }

    public static string PercentText(float volume, bool muted) => muted ? Lang.T("Muted", "صامت") : SoundApps.Percent(volume) + "%";

    /// <summary>
    /// The outputs to choose from in a narrow space: a row of buttons when their names fit
    /// (<paramref name="room"/> characters), else a drop-down list.
    /// </summary>
    public static FrameworkElement OutputPicker(SoundSnapshot snap, Action<string> pick, int room)
    {
        if (snap.Devices.Sum(d => d.Short.Length + 3) <= room)
            return SettingsPage.Choice(snap.Devices.Select(d => d.Id).ToArray(), snap.DefaultId ?? "", pick, id => snap.Devices.First(d => d.Id == id).Short);
        // Plain items with the name as their content: the app's drop-down look shows content, not DisplayMemberPath.
        var box = new ComboBox { MinWidth = 180, MaxWidth = 300 };
        foreach (var d in snap.Devices)
            box.Items.Add(new ComboBoxItem { Content = d.Label, Tag = d.Id, IsSelected = d.Id == snap.DefaultId });
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string id } && id != snap.DefaultId) pick(id); };
        return box;
    }
}

/// <summary>
/// A live level meter: green, then amber and red near the top. The level is shown on a decibel scale
/// (a whisper still moves it) and falls back smoothly.
/// </summary>
internal sealed class LevelMeter : Grid
{
    private readonly ScaleTransform _cover = new(1, 1);
    private double _shown;

    public LevelMeter(double height = 4)
    {
        Height = height;
        ClipToBounds = true; // follows the layout's direction, like the slider above it
        var fill = new Border
        {
            CornerRadius = new CornerRadius(height / 2),
            Background = new LinearGradientBrush(
                [new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0), new GradientStop(Color.FromRgb(0x22, 0xC5, 0x5E), 0.7), new GradientStop(Color.FromRgb(0xF5, 0x9E, 0x0B), 0.88), new GradientStop(Color.FromRgb(0xEF, 0x44, 0x44), 1)],
                new Point(0, 0), new Point(1, 0)),
        };
        var cover = new Border { RenderTransform = _cover, RenderTransformOrigin = new Point(1, 0.5) };
        cover.SetResourceReference(Border.BackgroundProperty, "Brush.LapTrack");
        var track = new Border { CornerRadius = new CornerRadius(height / 2) };
        track.SetResourceReference(Border.BackgroundProperty, "Brush.LapTrack");
        Children.Add(track);
        Children.Add(fill);
        Children.Add(cover);
        IsHitTestVisible = false;
    }

    /// <summary>A new peak (0..1, linear). Called about 30 times a second.</summary>
    public void Set(float peak)
    {
        double db = peak > 0.00001f ? 20 * Math.Log10(peak) : -100;
        double target = Math.Clamp((db + 54) / 54, 0, 1);
        _shown = target >= _shown ? target : Math.Max(target, _shown - 0.05);
        _cover.ScaleX = 1 - _shown;
    }

    public void Clear()
    {
        _shown = 0;
        _cover.ScaleX = 1;
    }
}

/// <summary>
/// One app in a mixer: its icon and name, the volume slider (with the live meter under it in the
/// full mixer), the percent and a mute button. Values are updated in place, never while the user
/// is dragging the slider.
/// </summary>
internal sealed class AppRow : Grid
{
    private readonly ISoundControl _control;
    private readonly Slider _slider;
    private readonly TextBlock _pct;
    private readonly TextBlock? _sub;
    private readonly Border _mute;
    private readonly LevelMeter? _meter;
    private readonly FrameworkElement _icon;
    private readonly TextBlock _name;
    private SoundApp _app;
    private bool _fromCode;
    private DateTime _userAt;

    public AppRow(SoundApp app, ISoundControl control, bool compact)
    {
        _app = app;
        _control = control;
        Key = app.Key;
        double iconSize = compact ? 26 : 32;
        _pct = SoundViews.Text("", compact ? 12 : 13, weight: FontWeights.SemiBold);
        _pct.HorizontalAlignment = HorizontalAlignment.Right;
        _pct.TextAlignment = TextAlignment.Right;
        _name = SoundViews.Text(SoundHub.NameOf(app), compact ? 12.5 : 13.5, weight: FontWeights.SemiBold);
        _icon = SoundViews.AppIcon(app.Key, SoundHub.NameOf(app), app.ExePath, app.IsSystem, iconSize);
        _icon.HorizontalAlignment = HorizontalAlignment.Left;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _mute = SoundViews.IconButton("lap-sound", "", () => _control.SetAppMute(Key, !_app.Muted), compact ? 28 : 32);
        _mute.VerticalAlignment = VerticalAlignment.Center;
        _slider = SoundViews.VolumeSlider(Lang.T($"{SoundHub.NameOf(app)} volume", $"مستوى صوت {SoundHub.NameOf(app)}"));
        _slider.ValueChanged += (_, e) =>
        {
            if (_fromCode) return;
            _userAt = DateTime.UtcNow;
            var v = (float)(e.NewValue / 100);
            _control.SetAppVolume(Key, v);
            if (_app.Muted && v > 0) _control.SetAppMute(Key, false); // turning it up unmutes, as Windows' own slider does
            _app = _app with { Volume = v, Muted = _app.Muted && v <= 0 };
            ShowValues();
        };

        if (compact)
        {
            // Pulse's card is narrow: the name and percent on one line, the slider under them.
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconSize + 10) });
            ColumnDefinitions.Add(new ColumnDefinition());
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Margin = new Thickness(0, 4, 0, 2);
            Children.Add(_icon);
            var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var top = new DockPanel { Margin = new Thickness(0, 0, 2, -2) };
            DockPanel.SetDock(_pct, Dock.Right);
            top.Children.Add(_pct);
            top.Children.Add(_name);
            middle.Children.Add(top);
            middle.Children.Add(_slider);
            Grid.SetColumn(middle, 1);
            Children.Add(middle);
            _mute.Margin = new Thickness(4, 0, 0, 0);
            Grid.SetColumn(_mute, 2);
            Children.Add(_mute);
            Apply(app, force: true);
            return;
        }

        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(iconSize + 12) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Margin = new Thickness(0, 4, 0, 4);
        MinHeight = 46;
        Children.Add(_icon);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        names.Children.Add(_name);
        _sub = SoundViews.Text("", 11.5, "Brush.Text3");
        _sub.Margin = new Thickness(0, 1, 0, 0);
        names.Children.Add(_sub);
        Grid.SetColumn(names, 1);
        Children.Add(names);

        _meter = new LevelMeter(4) { Margin = new Thickness(5, 0, 5, 0) };
        var stack = new Grid { VerticalAlignment = VerticalAlignment.Center };
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        stack.Children.Add(_slider);
        Grid.SetRow(_meter, 1);
        stack.Children.Add(_meter);
        Grid.SetColumn(stack, 2);
        Children.Add(stack);

        _pct.Margin = new Thickness(6, 0, 6, 0);
        Grid.SetColumn(_pct, 3);
        Children.Add(_pct);
        Grid.SetColumn(_mute, 4);
        Children.Add(_mute);

        Apply(app, force: true);
    }

    public string Key { get; }

    public SoundApp App => _app;

    internal Slider Slider => _slider;

    /// <summary>True while the user is dragging this row's slider (or just did).</summary>
    public bool Busy => _slider.IsMouseCaptureWithin || (DateTime.UtcNow - _userAt).TotalMilliseconds < 450;

    public void Update(SoundApp app) => Apply(app, force: false);

    public void SetLevel(float peak)
    {
        if (_meter is null) return;
        if (_app.Muted) _meter.Clear();
        else _meter.Set(peak);
    }

    private void Apply(SoundApp app, bool force)
    {
        if (!force && Busy)
        {
            _app = _app with { Muted = app.Muted, Playing = app.Playing, Sessions = app.Sessions };
        }
        else
        {
            _app = app;
            _fromCode = true;
            _slider.Value = SoundApps.Percent(app.Volume);
            _fromCode = false;
        }
        if (_sub is not null)
        {
            var file = app.IsSystem ? Lang.T("Notifications and alerts", "الإشعارات والتنبيهات") : app.Key;
            _sub.Text = app.Sessions > 1 ? Lang.T($"{file} · {app.Sessions} sounds", $"{file} · {app.Sessions} أصوات") : file;
        }
        ShowValues();
    }

    private void ShowValues()
    {
        _pct.Text = SoundViews.PercentText(_app.Volume, _app.Muted);
        _pct.SetResourceReference(TextBlock.ForegroundProperty, _app.Muted ? "Brush.Danger" : "Brush.Text");
        SoundViews.SetIcon(_mute, _app.Muted ? "lap-mute" : "lap-sound",
            _app.Muted ? Lang.T($"Unmute {SoundHub.NameOf(_app)}", $"إلغاء كتم {SoundHub.NameOf(_app)}") : Lang.T($"Mute {SoundHub.NameOf(_app)}", $"كتم {SoundHub.NameOf(_app)}"),
            _app.Muted ? "Brush.Danger" : "Brush.Text2");
        _icon.Opacity = _name.Opacity = _app.Muted ? 0.5 : 1;
        if (_app.Muted) _meter?.Clear();
    }
}
