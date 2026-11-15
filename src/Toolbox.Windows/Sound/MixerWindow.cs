using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Toolbox.Core.Sound;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Sound;

/// <summary>
/// Imp Studio's mixer: the main output with one volume for everything, then every app playing now
/// (its slider, live level and mute), the apps that are open but quiet, and the apps it remembers.
/// On the side: the hotkeys for the app in front and what comes next.
/// </summary>
internal sealed class MixerWindow : GlassWindow
{
    private const double TitleHeight = 44;

    private readonly ISoundControl _control;
    private readonly SoundMemory? _memory;
    private readonly Func<string, string?> _shortcut;
    private readonly Grid _root = new();
    private readonly Dictionary<string, AppRow> _rows = new(StringComparer.Ordinal);
    private readonly StackPanel _playing = new();
    private readonly StackPanel _quiet = new();
    private readonly WrapPanel _known = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly FrameworkElement _quietSection;
    private readonly TextBlock _nothing;
    private readonly ContentControl _output = new();
    private readonly DispatcherTimer _later = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Slider? _master;
    private TextBlock? _masterPct;
    private Border? _masterMute;
    private TextBlock? _outputSub;
    private string _outputKey = "";
    private IDisposable? _watching;
    private IReadOnlyDictionary<string, float>? _levels;
    private int _snapQueued, _levelsQueued;
    private bool _masterFromCode;
    private DateTime _masterAt;

    /// <param name="shortcut">The active shortcut text for one of Imp Studio's commands, or null.</param>
    public MixerWindow(ISoundControl control, SoundMemory? memory, Func<string, string?> shortcut)
    {
        _control = control;
        _memory = memory;
        _shortcut = shortcut;
        UseNativeFrame(TitleHeight);
        FollowsGlassSetting = true;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(980, work.Width * 0.9);
        Height = Math.Min(680, work.Height * 0.9);
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Imp Studio";
        Content = _root;
        _nothing = SoundViews.Text(Lang.T("Nothing is playing right now. Apps show here as soon as they make a sound.", "لا شيء يعمل الآن. تظهر التطبيقات هنا فور أن تُصدر صوتًا."), 13, "Brush.Text3");
        _nothing.TextWrapping = TextWrapping.Wrap;
        _nothing.Margin = new Thickness(2, 10, 0, 6);
        _quietSection = QuietSection();
        _later.Tick += (_, _) => { _later.Stop(); Render(); };
        control.Changed += OnChanged;
        control.Levels += OnLevels;
        ThemeResources.Changed += Rebuild;
        IsVisibleChanged += (_, _) => Watch();
        StateChanged += (_, _) => Watch();
        Closed += (_, _) =>
        {
            control.Changed -= OnChanged;
            control.Levels -= OnLevels;
            ThemeResources.Changed -= Rebuild;
            _later.Stop();
            _watching?.Dispose();
            _watching = null;
        };
        Rebuild();
    }

    internal IReadOnlyCollection<AppRow> Rows => _rows.Values;

    // ---- updates from the engine ------------------------------------------------------------------

    private void OnChanged(SoundSnapshot _)
    {
        if (Interlocked.Exchange(ref _snapQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _snapQueued, 0);
            Render();
        });
    }

    private void OnLevels(IReadOnlyDictionary<string, float> levels)
    {
        _levels = levels;
        if (Interlocked.Exchange(ref _levelsQueued, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            Interlocked.Exchange(ref _levelsQueued, 0);
            if (_levels is not { } l) return;
            foreach (var row in _rows.Values) row.SetLevel(l.TryGetValue(row.Key, out var p) ? p : 0);
        });
    }

    /// <summary>The meters run only while the window can be seen.</summary>
    private void Watch()
    {
        bool shown = IsVisible && WindowState != WindowState.Minimized;
        if (shown && _watching is null) _watching = _control.WatchLevels();
        else if (!shown && _watching is not null)
        {
            _watching.Dispose();
            _watching = null;
        }
    }

    // ---- layout ---------------------------------------------------------------------------------

    private void Rebuild()
    {
        _root.Children.Clear();
        _root.FlowDirection = Lang.Flow;
        var tint = new Border();
        tint.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
        _root.Children.Add(tint);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(TitleBar());

        var body = new Grid { Margin = new Thickness(20, 6, 20, 20) };
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var main = new StackPanel();
        Detach(_output);
        main.Children.Add(_output);
        main.Children.Add(Heading(Lang.T("Playing now", "يعمل الآن"), Lang.T("Drag to set each app's volume. Imp Studio remembers it for this output.", "اسحب لضبط صوت كل تطبيق. يتذكّره استوديو إمب لهذا المخرج.")));
        Detach(_playing);
        Detach(_nothing);
        main.Children.Add(Panel(_playing, _nothing));
        Detach(_quietSection);
        main.Children.Add(_quietSection);
        var scroll = new ScrollViewer { Content = main, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = SettingsPage.Res("Lap.Scroll"), Padding = new Thickness(0, 0, 6, 0) };
        body.Children.Add(scroll);

        var side = Side();
        Grid.SetColumn(side, 2);
        body.Children.Add(side);
        _root.Children.Add(layout);
        _outputKey = "";
        foreach (var row in _rows.Values) Detach(row);
        _rows.Clear();
        Render();
    }

    private static void Detach(FrameworkElement element)
    {
        switch (element.Parent)
        {
            case Panel p: p.Children.Remove(element); break;
            case ContentControl c: c.Content = null; break;
            case Decorator d: d.Child = null; break;
        }
    }

    private static FrameworkElement Heading(string title, string help)
    {
        var stack = new StackPanel { Margin = new Thickness(2, 22, 0, 8) };
        stack.Children.Add(SoundViews.Text(title, 15, weight: FontWeights.SemiBold));
        var h = SoundViews.Text(help, 12, "Brush.Text3");
        h.TextWrapping = TextWrapping.Wrap;
        h.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(h);
        return stack;
    }

    private static Border Panel(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var c in children) stack.Children.Add(c);
        var card = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(16, 8, 10, 8), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return card;
    }

    private FrameworkElement QuietSection()
    {
        var stack = new StackPanel();
        stack.Children.Add(Heading(Lang.T("Quiet right now", "هادئ الآن"), Lang.T("Open but silent, or used before. Each one comes back at the level you gave it.", "مفتوح لكنه صامت، أو استُخدم من قبل. يعود كلٌّ منها بالمستوى الذي ضبطته له.")));
        stack.Children.Add(Panel(_quiet, _known));
        return stack;
    }

    private FrameworkElement Side()
    {
        var stack = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        var keys = new StackPanel();
        keys.Children.Add(SoundViews.Text(Lang.T("Hotkeys for the app in front", "اختصارات للتطبيق الأمامي"), 13.5, weight: FontWeights.SemiBold));
        var help = SoundViews.Text(Lang.T("Works in games too: a small bar shows the new level without leaving the game.", "تعمل في الألعاب أيضًا: يظهر شريط صغير بالمستوى الجديد دون مغادرة اللعبة."), 12, "Brush.Text3");
        help.TextWrapping = TextWrapping.Wrap;
        help.Margin = new Thickness(0, 4, 0, 8);
        keys.Children.Add(help);
        foreach (var (id, label) in new[] { ("louder", Lang.T("Louder", "أعلى")), ("quieter", Lang.T("Quieter", "أخفض")), ("mute", Lang.T("Mute or unmute", "كتم أو إلغاء الكتم")) })
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            FrameworkElement value = _shortcut(id) is { } text ? SettingsPage.Keys(text.Split('+'), 11) : SoundViews.Text(Lang.T("Off", "متوقف"), 12, "Brush.Text3");
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(SoundViews.Text(label, 12.5, "Brush.Text2"));
            keys.Children.Add(row);
        }
        stack.Children.Add(SideCard(keys));

        var next = new StackPanel();
        next.Children.Add(SoundViews.Text(Lang.T("Coming next", "القادم"), 13.5, weight: FontWeights.SemiBold));
        foreach (var line in new[]
        {
            Lang.T("Rules when an app starts, and profiles", "قواعد عند بدء تطبيق، وملفات تعريف"),
            Lang.T("Auto-duck: music and games get quieter while a voice app talks", "خفض تلقائي: تنخفض الموسيقى والألعاب أثناء حديث تطبيق صوتي"),
            Lang.T("An output per app: music on the speakers, the game on the headphones", "مخرج لكل تطبيق: الموسيقى على السماعات، واللعبة على سماعات الرأس"),
            Lang.T("Effects (equalizer, night mode), later", "المؤثرات (المعادل، الوضع الليلي)، لاحقًا"),
        })
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 6, 9, 0), VerticalAlignment = VerticalAlignment.Top };
            dot.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            var t = SoundViews.Text(line, 12.5, "Brush.Text2");
            t.TextWrapping = TextWrapping.Wrap;
            row.Children.Add(t);
            next.Children.Add(row);
        }
        stack.Children.Add(SideCard(next));
        return stack;
    }

    private static Border SideCard(UIElement child)
    {
        var card = new Border { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 0, 14), Child = child };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return card;
    }

    // ---- what it shows ----------------------------------------------------------------------------

    private void Render()
    {
        var snap = _control.Current;
        RenderOutput(snap);
        var apps = SoundApps.Group(snap.Sessions);
        if (_rows.Values.Any(r => r.Busy) && !SameLayout(apps))
        {
            // An app started or stopped while a slider is held: move rows once it is let go.
            foreach (var app in apps)
                if (_rows.TryGetValue(app.Key, out var r)) r.Update(app);
            _later.Stop();
            _later.Start();
            return;
        }
        var playing = apps.Where(a => a.Playing).ToList();
        var quiet = apps.Where(a => !a.Playing).ToList();
        foreach (var gone in _rows.Keys.Except(apps.Select(a => a.Key)).ToList())
        {
            Detach(_rows[gone]);
            _rows.Remove(gone);
        }
        Place(_playing, playing);
        Place(_quiet, quiet);
        _nothing.Visibility = playing.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        _known.Children.Clear();
        var known = _memory?.Recent(snap.DefaultId, apps.Select(a => a.Key), 8) ?? [];
        foreach (var k in known) _known.Children.Add(KnownChip(k));
        _known.Visibility = known.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _quietSection.Visibility = quiet.Count > 0 || known.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool SameLayout(IReadOnlyList<SoundApp> apps) =>
        apps.Where(a => a.Playing).Select(a => a.Key).SequenceEqual(_playing.Children.OfType<AppRow>().Select(r => r.Key))
        && apps.Where(a => !a.Playing).Select(a => a.Key).SequenceEqual(_quiet.Children.OfType<AppRow>().Select(r => r.Key));

    private void Place(StackPanel panel, IReadOnlyList<SoundApp> apps)
    {
        var wanted = apps.Select(a => a.Key).ToList();
        bool same = wanted.SequenceEqual(panel.Children.OfType<AppRow>().Select(r => r.Key));
        if (!same)
        {
            foreach (var row in panel.Children.OfType<AppRow>().ToList()) panel.Children.Remove(row);
        }
        for (int i = 0; i < apps.Count; i++)
        {
            var app = apps[i];
            if (!_rows.TryGetValue(app.Key, out var row))
            {
                row = new AppRow(app, _control, compact: false);
                _rows[app.Key] = row;
            }
            else row.Update(app);
            if (!same)
            {
                Detach(row);
                panel.Children.Add(row);
            }
        }
        // Lines between rows.
        for (int i = 0; i < panel.Children.Count; i++)
            if (panel.Children[i] is AppRow r) r.Margin = new Thickness(0, i == 0 ? 4 : 6, 0, 4);
    }

    private static FrameworkElement KnownChip(SoundMemory.Known k)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = SoundViews.AppIcon(k.Key, k.Name, k.Path, false, 20);
        icon.Margin = new Thickness(0, 0, 7, 0);
        stack.Children.Add(icon);
        var level = k.Level is { } l ? " · " + SoundViews.PercentText(l.Volume, l.Muted) : "";
        stack.Children.Add(SoundViews.Text(k.Name + level, 12, "Brush.Text2", FontWeights.SemiBold));
        var chip = new Border { CornerRadius = new CornerRadius(15), BorderThickness = new Thickness(1), Padding = new Thickness(4, 4, 11, 4), Margin = new Thickness(0, 0, 8, 8), Child = stack,
            ToolTip = Lang.T($"Not playing now. Imp Studio sets it back to this level when it plays again (last heard {Lang.Ago(k.LastHeard, DateTimeOffset.UtcNow)}).", $"لا يعمل الآن. يعيده استوديو إمب إلى هذا المستوى عندما يعمل مجددًا (آخر صوت {Lang.Ago(k.LastHeard, DateTimeOffset.UtcNow)}).") };
        chip.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
        chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return chip;
    }

    private void RenderOutput(SoundSnapshot snap)
    {
        var key = string.Join("|", snap.Devices.Select(d => d.Id + d.Name)) + "#" + snap.DefaultId + snap.CanSwitchOutput + snap.Ready;
        if (key != _outputKey)
        {
            _outputKey = key;
            _output.Content = OutputCard(snap);
        }
        int playing = SoundApps.Group(snap.Sessions).Count(a => a.Playing);
        if (_outputSub is not null)
            _outputSub.Text = !snap.Ready ? Lang.T("Looking for your outputs…", "جارٍ البحث عن المخارج…")
                : snap.Default is null ? Lang.T("No output is plugged in or switched on.", "لا يوجد مخرج موصول أو مُشغَّل.")
                : playing switch
                {
                    0 => Lang.T("Main output · nothing playing", "المخرج الرئيسي · لا شيء يعمل"),
                    1 => Lang.T("Main output · 1 app playing", "المخرج الرئيسي · تطبيق واحد يعمل"),
                    _ => Lang.T($"Main output · {playing} apps playing", $"المخرج الرئيسي · {playing} تطبيقات تعمل"),
                };
        if (_master is not null && !(_master.IsMouseCaptureWithin || (DateTime.UtcNow - _masterAt).TotalMilliseconds < 450))
        {
            _masterFromCode = true;
            _master.Value = SoundApps.Percent(snap.Master);
            _masterFromCode = false;
        }
        ShowMaster(snap.Master, snap.MasterMuted);
    }

    private FrameworkElement OutputCard(SoundSnapshot snap)
    {
        var device = snap.Default;
        var stack = new StackPanel();
        var head = new DockPanel { LastChildFill = true };
        var tile = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(13), Margin = new Thickness(0, 0, 12, 0), Child = SettingsPage.Glyph(SoundViews.DeviceIcon(device?.Kind), 22, "Brush.Accent") };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        if (snap.CanSwitchOutput && snap.Devices.Count > 1)
        {
            var choice = SoundViews.OutputPicker(snap, id => _control.SetOutput(id), room: 46);
            choice.VerticalAlignment = VerticalAlignment.Center;
            choice.Margin = new Thickness(12, 0, 0, 0);
            AutomationProperties.SetName(choice, Lang.T("Main output", "المخرج الرئيسي"));
            DockPanel.SetDock(choice, Dock.Right);
            head.Children.Add(choice);
        }
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(SoundViews.Text(device?.Label ?? Lang.T("No output", "لا يوجد مخرج"), 15.5, weight: FontWeights.SemiBold));
        _outputSub = SoundViews.Text("", 12, "Brush.Text3");
        _outputSub.Margin = new Thickness(0, 2, 0, 0);
        names.Children.Add(_outputSub);
        head.Children.Add(names);
        stack.Children.Add(head);

        var master = new Grid { Margin = new Thickness(0, 14, 0, 0), IsEnabled = device is not null };
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        master.ColumnDefinitions.Add(new ColumnDefinition());
        master.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        _masterMute = SoundViews.IconButton("lap-sound", "", () => _control.SetMasterMute(!_control.Current.MasterMuted), 34);
        _masterMute.Margin = new Thickness(0, 0, 10, 0);
        master.Children.Add(_masterMute);
        _master = SoundViews.VolumeSlider(Lang.T("Volume of everything", "مستوى صوت كل شيء"));
        _master.ValueChanged += (_, e) =>
        {
            if (_masterFromCode) return;
            _masterAt = DateTime.UtcNow;
            _control.SetMaster((float)(e.NewValue / 100));
            if (_control.Current.MasterMuted && e.NewValue > 0) _control.SetMasterMute(false);
            ShowMaster((float)(e.NewValue / 100), false);
        };
        Grid.SetColumn(_master, 1);
        master.Children.Add(_master);
        _masterPct = SoundViews.Text("", 15, weight: FontWeights.SemiBold);
        _masterPct.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(_masterPct, 2);
        master.Children.Add(_masterPct);
        stack.Children.Add(master);
        var card = new Border { CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1), Padding = new Thickness(18, 16, 18, 14), Child = stack };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return card;
    }

    private void ShowMaster(float volume, bool muted)
    {
        if (_masterPct is not null)
        {
            _masterPct.Text = SoundViews.PercentText(volume, muted);
            _masterPct.SetResourceReference(TextBlock.ForegroundProperty, muted ? "Brush.Danger" : "Brush.Text");
        }
        if (_masterMute is not null)
            SoundViews.SetIcon(_masterMute, muted ? "lap-mute" : "lap-sound", muted ? Lang.T("Unmute everything", "إلغاء كتم كل شيء") : Lang.T("Mute everything", "كتم كل شيء"), muted ? "Brush.Danger" : "Brush.Text2");
    }

    // ---- title bar ------------------------------------------------------------------------------

    private UIElement TitleBar()
    {
        var bar = new DockPanel { Margin = new Thickness(16, 0, 8, 0), LastChildFill = false };
        var captions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var min = Caption("Lap.Caption", "M3,8 H13", Lang.T("Minimize", "تصغير"));
        min.Click += (_, _) => WindowState = WindowState.Minimized;
        var max = Caption("Lap.Caption", "M3.5,3.5 H12.5 V12.5 H3.5 Z", Lang.T("Maximize", "تكبير"));
        max.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        var close = Caption("Lap.Caption.Close", "M4,4 L12,12 M12,4 L4,12", Lang.T("Close", "إغلاق"));
        close.Click += (_, _) => Close();
        captions.Children.Add(min);
        captions.Children.Add(max);
        captions.Children.Add(close);
        DockPanel.SetDock(captions, Dock.Right);
        bar.Children.Add(captions);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(SettingsPage.AccentTile("lap-mixer", 24, 14).With(t => { t.Margin = new Thickness(0, 0, 10, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        brand.Children.Add(new TextBlock { Text = Lang.T("Imp Studio", "استوديو إمب"), Style = SettingsPage.Res("Lap.Text"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(new TextBlock { Text = Lang.T(" · Mixer", " · المازج"), Style = SettingsPage.Res("Lap.Help"), FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(brand, Dock.Left);
        bar.Children.Add(brand);
        return bar;
    }

    private static Button Caption(string style, string data, string name)
    {
        var p = new Path { Data = Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = 1.25, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, SnapsToDevicePixels = true };
        p.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding { Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty), RelativeSource = System.Windows.Data.RelativeSource.Self });
        var b = new Button { Style = SettingsPage.Res(style), Content = p, ToolTip = name };
        AutomationProperties.SetName(b, name);
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        return b;
    }
}
