using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Pulse;
using Toolbox.Core.Settings;
using Toolbox.Windows.Home;
using Toolbox.Windows.Pulse;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Tool #3: Pulse, a dock on the edge of the screen. It shows how much of Claude's and Codex's
/// limits is used (from their own files on this PC) and what Claude Code is doing, and holds
/// shortcuts the user picks (ChatGPT, Longshot, File Toolbox, a timer, a note, ...). While on, it
/// keeps the dock and two file watchers; off, nothing runs.
/// </summary>
internal sealed partial class PulseTool : ToolModule
{
    public const string ToolId = "pulse";

    private readonly ToolboxHost _host;
    private readonly PulseAlerts _alerts = new();
    private DockWindow? _dock;
    private PulseMonitor? _monitor;
    private NoteWindow? _note;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset? _timerEnds;
    private ColourWindow? _colours;
    private string _pulseKey = "";
    private double _cpu;

    public PulseTool(ToolboxHost host)
    {
        _host = host;
        Commands =
        [
            new ToolCommand("toggleDock", "Show or hide the dock", null, ToggleDock) { TitleAr = "إظهار الشريط أو إخفاؤه" },
        ];
        _clock.Tick += (_, _) => Tick();
    }

    public override string Id => ToolId;
    public override string Name => Lang.T("Pulse", "نبض");
    public override string Summary => Lang.T("A dock on the edge of your screen: Claude and Codex limits at a glance, plus your shortcuts.", "شريط على حافة الشاشة: حدود Claude وCodex في لمحة، مع اختصاراتك.");
    public override string Icon => "lap-pulse";
    public override IReadOnlyList<ToolCommand> Commands { get; }

    private PulseSettings S => _host.Settings.Current.Pulse;

    public static string NotePath => Path.Combine(ClaudeCodeConnection.DefaultDataDir, "note.txt");

    internal DockWindow? DockView => _dock;

    internal PulseMonitor? Monitor => _monitor;

    /// <summary>The connection to Claude Code on this PC (its settings file, this copy of Amir Lap).</summary>
    public static ClaudeCodeConnection Connection() =>
        new(ClaudeCodeConnection.DefaultSettingsPath, Environment.ProcessPath ?? "AmirLap.exe", ClaudeCodeConnection.DefaultDataDir);

    // ---- start / stop -----------------------------------------------------------------------------

    protected override void OnStart()
    {
        _pulseKey = Key(S);
        _monitor = new PulseMonitor(ClaudeStateStore.DefaultPath, CodexUsageReader.DefaultHome, () => _host.Settings.Current.Pulse.ReadCodex);
        _monitor.Changed += Update;
        _dock = new DockWindow(() => _host.Settings.Current.Pulse, Save)
        {
            CardFor = CardFor,
            MenuFor = Menu,
        };
        _dock.TileClicked += OnTile;
        _dock.FilesDropped += (_, files) => App.ShowPalette(files);
        _dock.Show();
        _host.Settings.Changed += OnSettingsChanged;
        _monitor.Start();
        _clock.Start();

        // Amir Lap was moved or reinstalled elsewhere: point Claude Code at this copy.
        try
        {
            var connection = Connection();
            if (connection.NeedsRefresh) connection.Link();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn($"Pulse could not refresh its Claude Code connection: {ex.Message}");
        }
    }

    protected override void OnStop()
    {
        _host.Settings.Changed -= OnSettingsChanged;
        _clock.Stop();
        _monitor?.Dispose();
        _monitor = null;
        _dock?.Close();
        _dock = null;
        _note?.Close();
        _colours?.Close();
    }

    /// <summary>The Dashboard's "Show dock": brings the dock back if it was hidden and slides it out.</summary>
    public override void Open()
    {
        if (_dock is null) return;
        _dock.SetUserHidden(false);
        _dock.Reveal();
    }

    private void ToggleDock()
    {
        _dock?.SetUserHidden(!_dock.UserHidden);
    }

    private void Save(Action<PulseSettings> edit)
    {
        var next = _host.Settings.Current.Clone();
        edit(next.Pulse);
        _host.Settings.Save(next);
    }

    /// <summary>Everything about the dock's look and tiles, to rebuild it only when one of them changed.</summary>
    private static string Key(PulseSettings p) => JsonSerializer.Serialize(new { p.Edge, p.Position, p.Size, p.Show, p.Screen, p.MonitorPoint, p.CurrentSet, Tiles = p.ActiveTiles, p.ShowClaudeStatus });

    private void OnSettingsChanged(AppSettings settings) => ToolboxHost.OnUi(() =>
    {
        var key = Key(settings.Pulse);
        if (key != _pulseKey)
        {
            _pulseKey = key;
            _dock?.Rebuild();
        }
        _monitor?.Refresh();
        // The history size or saved colours may have changed on the settings page (later, so a
        // click in the colour window that saved them has finished first).
        if (_colours is { } w) w.Dispatcher.BeginInvoke(w.RenderLists);
    });

    // ---- what the tiles show ---------------------------------------------------------------------

    private void Update(PulseSnapshot snap)
    {
        if (_dock is null) return;
        var now = snap.Now;
        var linked = IsLinkedCached();
        if (_dock.Tile(PulseTiles.Claude) is { } claude)
        {
            var five = snap.Claude.FiveHour;
            claude.SetUsage(five?.UsedAt(now));
            claude.SetLabel(five is null ? "Claude" : $"{five.UsedAt(now):0}%");
            claude.SetStatus(S.ShowClaudeStatus && linked ? snap.Status.State : ClaudeActivity.Idle, snap.Status.Waiting);
        }
        if (_dock.Tile(PulseTiles.Codex) is { } codex)
        {
            var w = snap.Codex?.Session;
            codex.SetUsage(w?.UsedAt(now));
            codex.SetLabel(w is null ? "Codex" : $"{w.UsedAt(now):0}%");
        }
        foreach (var (id, tile) in _dock.Tiles)
            if (id is not (PulseTiles.Claude or PulseTiles.Codex or PulseTiles.System or PulseTiles.Timer)) tile.SetLabel(ShortName(id));
        Tick();
        _dock.RefreshCard();

        foreach (var alert in _alerts.Check(linked ? snap.Claude : null, snap.Codex, S.WarnAt, S.NotifyFinished, now))
            ShowAlert(alert, now);
    }

    private bool? _linked;
    private DateTime _linkedAt;

    /// <summary>Whether Claude Code is connected (read from its settings at most every 10 seconds).</summary>
    private bool IsLinkedCached()
    {
        if (_linked is { } known && (DateTime.UtcNow - _linkedAt).TotalSeconds < 10) return known;
        try { _linked = Connection().IsLinked; }
        catch (InvalidOperationException) { _linked = false; }
        _linkedAt = DateTime.UtcNow;
        return _linked.Value;
    }

    /// <summary>Every second: the timer's countdown and the PC status tile.</summary>
    private void Tick()
    {
        if (_dock is null) return;
        if (_dock.Tile(PulseTiles.Timer) is { } timer)
        {
            if (_timerEnds is { } ends)
            {
                var left = ends - DateTimeOffset.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    _timerEnds = null;
                    PulseToast.Show(PulseTileView.Green, "lap-timer", Lang.T("Focus time is over", "انتهى وقت التركيز"), Lang.T("Take a short break.", "خذ استراحة قصيرة."));
                    timer.SetUsage(0);
                    timer.SetLabel(ShortName(PulseTiles.Timer));
                }
                else
                {
                    double total = Math.Max(1, S.TimerMinutes) * 60;
                    timer.SetUsage(100 * left.TotalSeconds / total, ThemeResources.Lap.Accent);
                    timer.SetLabel(left.ToString(left.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture), "Brush.Accent");
                }
            }
            else
            {
                timer.SetUsage(0);
                timer.SetLabel($"{S.TimerMinutes}:00");
            }
        }
        if (_dock.Tile(PulseTiles.System) is { } system)
        {
            _cpu = SystemStatus.Cpu();
            system.SetUsage(_cpu);
            system.SetLabel(Lang.T($"CPU {_cpu:0}%", $"المعالج {_cpu:0}%"));
        }
    }

    public static string ShortName(string id) => id switch
    {
        PulseTiles.Claude => "Claude",
        PulseTiles.Codex => "Codex",
        PulseTiles.ChatGpt => "ChatGPT",
        PulseTiles.Longshot => Lang.T("Longshot", "لقطة"),
        PulseTiles.Toolbox => Lang.T("Toolbox", "الملفات"),
        PulseTiles.Timer => Lang.T("Timer", "مؤقت"),
        PulseTiles.Note => Lang.T("Note", "ملاحظة"),
        PulseTiles.Colour => Lang.T("Colour", "لون"),
        PulseTiles.Folder => Lang.T("Folder", "مجلد"),
        PulseTiles.Link => Lang.T("Open", "فتح"),
        PulseTiles.System => Lang.T("PC", "الحاسوب"),
        _ => "",
    };

    public static string TileName(string id) => id switch
    {
        PulseTiles.Claude => "Claude",
        PulseTiles.Codex => "Codex",
        PulseTiles.ChatGpt => "ChatGPT",
        PulseTiles.Longshot => Lang.T("Longshot", "اللقطة الطويلة"),
        PulseTiles.Toolbox => Lang.T("File Toolbox", "أدوات الملفات"),
        PulseTiles.Timer => Lang.T("Focus timer", "مؤقت التركيز"),
        PulseTiles.Note => Lang.T("Quick note", "ملاحظة سريعة"),
        PulseTiles.Colour => Lang.T("Colour picker", "منتقي الألوان"),
        PulseTiles.Folder => Lang.T("Folder", "مجلد"),
        PulseTiles.Link => Lang.T("App or website", "تطبيق أو موقع"),
        PulseTiles.System => Lang.T("PC status", "حالة الحاسوب"),
        _ => id,
    };

    public static string TileHelp(string id) => id switch
    {
        PulseTiles.Claude => Lang.T("Session and weekly limits · opens Claude", "حدود الجلسة والأسبوع · يفتح Claude"),
        PulseTiles.Codex => Lang.T("5-hour and weekly limits · opens Codex", "حدود الساعات الخمس والأسبوع · يفتح Codex"),
        PulseTiles.ChatGpt => Lang.T("Opens ChatGPT", "يفتح ChatGPT"),
        PulseTiles.Longshot => Lang.T("Starts a capture · shows the last one", "يبدأ لقطة · ويعرض آخرها"),
        PulseTiles.Toolbox => Lang.T("Drop a file on it, or click for the File Toolbox window", "أسقِط ملفًا عليه، أو انقر لنافذة أدوات الملفات"),
        PulseTiles.Timer => Lang.T("A focus countdown, shown as a ring", "عدّ تنازلي للتركيز يظهر كحلقة"),
        PulseTiles.Note => Lang.T("Jot something down", "دوّن شيئًا بسرعة"),
        PulseTiles.Colour => Lang.T("Pick a colour from the screen", "التقط لونًا من الشاشة"),
        PulseTiles.Folder => Lang.T("Opens a folder you choose", "يفتح مجلدًا تختاره"),
        PulseTiles.Link => Lang.T("Any app, file or web address", "أي تطبيق أو ملف أو عنوان ويب"),
        PulseTiles.System => Lang.T("Processor, memory and battery", "المعالج والذاكرة والبطارية"),
        _ => "",
    };

    // ---- clicks --------------------------------------------------------------------------------

    private void OnTile(string id)
    {
        switch (id)
        {
            case PulseTiles.Claude:
                var status = _monitor?.Current.Status;
                if (_monitor is not null) _monitor.SeenAt = DateTimeOffset.UtcNow;
                if (S.JumpToTerminal && status?.Focus is { Window: not 0 } focus && status.State is ClaudeActivity.Waiting or ClaudeActivity.Done or ClaudeActivity.Working
                    && PulseLaunch.Focus(focus.Window))
                {
                    _monitor?.Refresh();
                    return;
                }
                PulseLaunch.Claude();
                _monitor?.Refresh();
                break;
            case PulseTiles.Codex: PulseLaunch.Codex(); break;
            case PulseTiles.ChatGpt: PulseLaunch.ChatGpt(); break;
            case PulseTiles.Longshot:
                if (App.FindTool(LongshotTool.ToolId) is LongshotTool longshot && App.IsToolEnabled(LongshotTool.ToolId)) longshot.Capture();
                else App.ShowHome(LongshotTool.ToolId);
                break;
            case PulseTiles.Toolbox: App.ShowPalette(); break;
            case PulseTiles.Timer:
                _timerEnds = _timerEnds is null ? DateTimeOffset.UtcNow.AddMinutes(Math.Clamp(S.TimerMinutes, 1, 180)) : null;
                Tick();
                break;
            case PulseTiles.Note:
                if (_note is null)
                {
                    _note = new NoteWindow(NotePath);
                    _note.Closed += (_, _) => _note = null;
                    _note.Show();
                }
                Foreground.Activate(_note);
                break;
            case PulseTiles.Colour:
                PickColour();
                break;
            case PulseTiles.Folder:
                PulseLaunch.Open(FolderPath);
                break;
            case PulseTiles.Link:
                if (S.LinkTarget is { Length: > 0 } target) PulseLaunch.Open(target);
                else App.ShowHome(ToolId);
                break;
            case PulseTiles.System:
                PulseLaunch.Open("taskmgr.exe");
                break;
            default:
                App.ShowHome(ToolId);
                break;
        }
    }

    /// <summary>
    /// The Colour tile: pick from the screen. Basic copies the HEX code and says so; Pro (or a
    /// Shift+click, or while the colour window is open) copies the chosen code and shows the colour
    /// window beside the pick. Every pick goes into the history.
    /// </summary>
    private void PickColour()
    {
        bool pro = S.ColourMode == "pro";
        bool windowOpen = _colours is { IsVisible: true };
        _colours?.Hide(); // so the window is never in the way of what is picked
        var picker = new ColourPicker(pro);
        picker.Picked += (colour, shift) => OnColourPicked(colour, pro || shift || windowOpen, picker.PickedAt);
        picker.Cancelled += () => { if (windowOpen) _colours?.Show(); };
        picker.Show();
    }

    private void OnColourPicked(Rgb colour, bool pro, Longshot.PixelPoint at)
    {
        Save(p => p.ColourHistory = Colours.Remember(p.ColourHistory, colour, Colours.HistorySize(p.ColourHistorySize)));
        if (!pro)
        {
            try { Clipboard.SetText(colour.Hex); } catch (System.Runtime.InteropServices.COMException) { }
            PulseToast.Show(Color.FromRgb(colour.R, colour.G, colour.B), "lap-colour", Lang.T($"Copied {colour.Hex}", $"نُسخ {colour.Hex}"), Lang.T("Paste it anywhere with Ctrl+V.", "الصقه في أي مكان باستخدام Ctrl+V."));
            return;
        }
        string? copied = Colours.Formats.Contains(S.ColourCopy) ? S.ColourCopy : Colours.Hex;
        try { Clipboard.SetText(Colours.Format(colour, copied)); }
        catch (System.Runtime.InteropServices.COMException) { copied = null; }
        if (_colours is null)
        {
            _colours = new ColourWindow(() => _host.Settings.Current.Pulse, Save, PickColour);
            _colours.Closed += (_, _) => _colours = null;
        }
        _colours.ShowPicked(colour, at, copied);
    }

    private string FolderPath => S.FolderPath is { Length: > 0 } f && Directory.Exists(f)
        ? f
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    // ---- cards ---------------------------------------------------------------------------------

    private FrameworkElement? CardFor(string id)
    {
        var snap = _monitor?.Current;
        var now = DateTimeOffset.UtcNow;
        switch (id)
        {
            case PulseTiles.Claude:
            {
                bool linked = IsLinkedCached();
                var parts = new List<UIElement> { PulseCards.Head("lap-claude", "Claude") };
                var c = snap?.Claude;
                if (linked && c?.Limits is not null)
                {
                    parts.Add(PulseCards.Meter(Lang.T("Current session", "الجلسة الحالية"), c.FiveHour, now));
                    parts.Add(PulseCards.Meter(Lang.T("This week", "هذا الأسبوع"), c.SevenDay, now));
                }
                if (linked && S.ShowClaudeStatus && snap is not null) parts.Add(StatusLine(snap.Status, now));
                parts.Add(PulseCards.Note(!linked
                    ? Lang.T("Connect Claude Code in Amir Lap › Pulse to see its limits and what it is doing here. Click to open Claude.", "اربط Claude Code من أمير لاب › نبض لترى حدوده وما يفعله هنا. انقر لفتح Claude.")
                    : c?.LimitsUpdated is { } at
                        ? Lang.T($"From Claude Code on this PC, {PulseCards.Age(at, now)}. Limits update while it runs. Click to open Claude.", $"من Claude Code على هذا الحاسوب، {PulseCards.Age(at, now)}. تتحدّث الحدود أثناء عمله. انقر لفتح Claude.")
                        : Lang.T("Limits show after Claude Code's next answer (claude.ai Pro and Max plans). Click to open Claude.", "تظهر الحدود بعد الإجابة التالية من Claude Code (خطط Pro وMax). انقر لفتح Claude.")));
                return PulseCards.Card([.. parts]);
            }
            case PulseTiles.Codex:
            {
                var u = snap?.Codex;
                var parts = new List<UIElement> { PulseCards.Head("lap-codex", "Codex", u?.Plan is { Length: > 0 } plan ? Lang.T($"{char.ToUpperInvariant(plan[0])}{plan[1..]} plan", $"خطة {plan}") : null) };
                if (u is not null)
                {
                    parts.Add(PulseCards.Meter(Lang.T("5-hour limit", "حد الساعات الخمس"), u.Session, now));
                    parts.Add(PulseCards.Meter(Lang.T("This week", "هذا الأسبوع"), u.Week, now));
                    parts.Add(PulseCards.Note(Lang.T($"Read from Codex's own logs on this PC, {PulseCards.Age(u.ReadAt, now)}. Click to open Codex.", $"مقروءة من سجلات Codex نفسه على هذا الحاسوب، {PulseCards.Age(u.ReadAt, now)}. انقر لفتح Codex.")));
                }
                else
                {
                    parts.Add(PulseCards.Body(S.ReadCodex
                        ? Lang.T("No Codex sessions on this PC yet. Its limits show here after you use it.", "لا توجد جلسات Codex على هذا الحاسوب بعد. تظهر حدوده هنا بعد استخدامه.")
                        : Lang.T("Reading Codex's limits is off in Amir Lap › Pulse.", "قراءة حدود Codex متوقفة في أمير لاب › نبض.")));
                    parts.Add(PulseCards.Note(Lang.T("Click to open Codex.", "انقر لفتح Codex.")));
                }
                return PulseCards.Card([.. parts]);
            }
            case PulseTiles.ChatGpt:
                return PulseCards.Card(PulseCards.Head("lap-chat", "ChatGPT"),
                    PulseCards.Body(Lang.T("Opens the ChatGPT app (or chatgpt.com). ChatGPT keeps no usage numbers on this PC, so this is a shortcut only.", "يفتح تطبيق ChatGPT (أو chatgpt.com). لا يحتفظ ChatGPT بأرقام استخدام على هذا الحاسوب، لذا هذا اختصار فقط.")));
            case PulseTiles.Longshot:
            {
                var keys = App.ShortcutText(LongshotTool.ToolId, "capture") is { } text ? SettingsPage.Keys(text.Split('+'), 11) : null;
                var parts = new List<UIElement> { PulseCards.Head("lap-longshot", TileName(id), trailing: keys as FrameworkElement), PulseCards.Body(Lang.T("Click to start a scrolling capture of the window in front.", "انقر لبدء لقطة طويلة للنافذة الأمامية.")) };
                if (LastCapture() is { } last)
                    parts.Add(PulseCards.Note(Lang.T($"Last: {Path.GetFileName(last.FullName)} · {PulseCards.Age(last.LastWriteTimeUtc, now)}", $"الأخيرة: {Path.GetFileName(last.FullName)} · {PulseCards.Age(last.LastWriteTimeUtc, now)}")));
                return PulseCards.Card([.. parts]);
            }
            case PulseTiles.Toolbox:
                return PulseCards.Card(PulseCards.Head("lap-toolbox", TileName(id)),
                    PulseCards.Body(Lang.T("Drop a file on this tile for File Toolbox's actions: convert, compress, read the text, share.", "أسقِط ملفًا على هذا المربع لإجراءات أدوات الملفات: التحويل والضغط وقراءة النص والمشاركة.")),
                    PulseCards.DropBox(Lang.T("Drop a file here", "أسقِط ملفًا هنا")));
            case PulseTiles.Timer:
                return PulseCards.Card(PulseCards.Head("lap-timer", TileName(id)),
                    PulseCards.Body(_timerEnds is { } ends
                        ? Lang.T($"{Math.Max(0, (int)(ends - now).TotalMinutes)} min left. Click to stop.", $"متبقٍ {Math.Max(0, (int)(ends - now).TotalMinutes)} د. انقر للإيقاف.")
                        : Lang.T($"Click to start {S.TimerMinutes} minutes of focus. A note tells you when it's over.", $"انقر لبدء {S.TimerMinutes} دقيقة من التركيز. يخبرك إشعار عند انتهائها.")));
            case PulseTiles.Note:
            {
                var text = NoteWindow.Read(NotePath).Trim();
                return PulseCards.Card(PulseCards.Head("lap-note", TileName(id)),
                    PulseCards.Body(text.Length == 0 ? Lang.T("Click to write a note. It is saved on this PC as you type.", "انقر لكتابة ملاحظة. تُحفظ على هذا الحاسوب أثناء الكتابة.") : text.Length > 180 ? text[..180] + "…" : text));
            }
            case PulseTiles.Colour:
            {
                bool pro = S.ColourMode == "pro";
                var parts = new List<UIElement>
                {
                    PulseCards.Head("lap-colour", TileName(id)),
                    PulseCards.Body(pro
                        ? Lang.T("Click, then click anywhere on the screen: its code is copied and the colour window opens beside it. Esc cancels.", "انقر، ثم انقر في أي مكان على الشاشة: يُنسخ رمزه وتفتح نافذة اللون بجانبه. Esc للإلغاء.")
                        : Lang.T("Click, then click anywhere on the screen to copy that colour's code (Shift+click for more). Esc cancels.", "انقر، ثم انقر في أي مكان على الشاشة لنسخ رمز ذلك اللون (Shift+نقر للمزيد). Esc للإلغاء.")),
                };
                var recent = S.ColourHistory.Select(h => Rgb.TryParse(h, out var c) ? c : (Rgb?)null).OfType<Rgb>().Take(8).ToList();
                if (recent.Count > 0)
                {
                    var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
                    foreach (var c in recent)
                        row.Children.Add(new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 5, 0), Background = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), ToolTip = c.Hex });
                    row.Children.Add(new TextBlock { Text = recent[0].Hex, Margin = new Thickness(3, 0, 0, 0), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight });
                    parts.Add(row);
                }
                return PulseCards.Card([.. parts]);
            }
            case PulseTiles.Folder:
                return PulseCards.Card(PulseCards.Head("lap-folder", TileName(id)), PulseCards.Body(FolderPath).With(t => t.FlowDirection = FlowDirection.LeftToRight), PulseCards.Note(Lang.T("Choose the folder in Amir Lap › Pulse.", "اختر المجلد من أمير لاب › نبض.")));
            case PulseTiles.Link:
                return PulseCards.Card(PulseCards.Head("lap-link", TileName(id)), PulseCards.Body(S.LinkTarget is { Length: > 0 } t2 ? t2 : Lang.T("Not set yet. Click to choose what it opens.", "لم يُحدَّد بعد. انقر لاختيار ما يفتحه.")).With(t => t.FlowDirection = FlowDirection.LeftToRight));
            case PulseTiles.System:
            {
                var parts = new List<UIElement> { PulseCards.Head("lap-system", TileName(id)) };
                parts.Add(PulseCards.Meter(Lang.T("Processor", "المعالج"), new UsageWindow(_cpu, null), now));
                parts.Add(PulseCards.Meter(Lang.T("Memory", "الذاكرة"), new UsageWindow(SystemStatus.Memory(), null), now));
                if (SystemStatus.Battery() is { } battery)
                    parts.Add(PulseCards.Body(Lang.T($"Battery {battery.Percent}%{(battery.Charging ? " · charging" : "")}", $"البطارية {battery.Percent}%{(battery.Charging ? " · قيد الشحن" : "")}")));
                parts.Add(PulseCards.Note(Lang.T("Click to open Task Manager.", "انقر لفتح إدارة المهام.")));
                return PulseCards.Card([.. parts]);
            }
            default:
                return PulseCards.Card(PulseCards.Head("lap-plus", Lang.T("Your tiles", "مربعاتك")),
                    PulseCards.Body(Lang.T("Click to add, remove and reorder tiles, or switch sets, in Amir Lap › Pulse. Right-click the dock for more.", "انقر لإضافة المربعات وإزالتها وترتيبها، أو لتبديل المجموعات، في أمير لاب › نبض. انقر بزر الماوس الأيمن على الشريط للمزيد.")));
        }
    }

    private static FrameworkElement StatusLine(ClaudeStatus status, DateTimeOffset now)
    {
        var project = status.Focus?.Project is { Length: > 0 } p ? " · " + p : "";
        return status.State switch
        {
            ClaudeActivity.Waiting => PulseCards.Status(PulseTileView.Amber, status.Waiting > 1
                ? Lang.T($"{status.Waiting} sessions need you", $"{status.Waiting} جلسات تحتاجك")
                : Lang.T("Needs you" + project, "يحتاجك" + project)),
            ClaudeActivity.Working => PulseCards.Status(ThemeResources.Lap.Accent, Lang.T("Working" + project, "يعمل" + project)),
            ClaudeActivity.Done => PulseCards.Status(PulseTileView.Green, Lang.T("Done" + project + (status.Focus?.Finished is { } f ? " · " + PulseCards.Age(f, now) : ""), "انتهى" + project)),
            _ => PulseCards.Status(Color.FromRgb(0x9A, 0x9F, 0xAA), Lang.T("Idle", "خامل")),
        };
    }

    private FileInfo? LastCapture()
    {
        try
        {
            if (App.FindTool(LongshotTool.ToolId) is not LongshotTool longshot) return null;
            var dir = new DirectoryInfo(longshot.Folder);
            return dir.Exists ? dir.EnumerateFiles("Longshot *.*").Where(f => f.Extension is ".png" or ".jpg").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // ---- notes ---------------------------------------------------------------------------------

    private void ShowAlert(PulseAlert alert, DateTimeOffset now)
    {
        string service = alert.Service == "claude" ? "Claude" : "Codex";
        string icon = alert.Service == "claude" ? "lap-claude" : "lap-codex";
        Action open = alert.Service == "claude" ? PulseLaunch.Claude : PulseLaunch.Codex;
        switch (alert.Kind)
        {
            case PulseAlert.Warn:
            {
                string window = alert.Window == "week" ? Lang.T("this week's limit", "حد هذا الأسبوع") : alert.Service == "claude" ? Lang.T("this session", "هذه الجلسة") : Lang.T("the 5-hour limit", "حد الساعات الخمس");
                PulseToast.Show(PulseTileView.Amber, icon, Lang.T($"{service}: {alert.Used:0}% of {window} used", $"{service}: استُخدم {alert.Used:0}% من {window}"), Capital(PulseCards.ResetText(alert.ResetsAt, now)) + ".", Lang.T("Open", "فتح"), open, TopGap());
                break;
            }
            case PulseAlert.Limit:
                PulseToast.Show(PulseTileView.Red, icon, Lang.T($"{service} limit reached", $"بلغ {service} الحد"), Capital(PulseCards.ResetText(alert.ResetsAt, now)) + ".", null, null, TopGap());
                break;
            case PulseAlert.Finished:
                var session = alert.Session;
                PulseToast.Show(PulseTileView.Green, "lap-check", Lang.T("Claude finished", "انتهى Claude"),
                    (session?.Project is { Length: > 0 } p ? p + " · " : "") + (alert.Took is { } took ? PulseCards.Duration(took) : ""),
                    session is { Window: not 0 } ? Lang.T("Open", "فتح") : null,
                    session is { Window: not 0 } s ? () => PulseLaunch.Focus(s.Window) : null, TopGap());
                break;
        }
    }

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Notes appear below the dock when it sits on the top edge.</summary>
    private int TopGap() => _dock is { Edge: "top" } d ? d.Placed.Height - (int)(300 * ScreenScale()) : 0;

    private static double ScreenScale() => Interop.ScreenGeometry.MonitorAt(new Interop.Native.POINT()).Scale;

    // ---- right-click menu ----------------------------------------------------------------------

    private IReadOnlyList<DockMenuItem> Menu()
    {
        var s = S;
        var items = new List<DockMenuItem>();
        foreach (var set in s.Sets)
        {
            var id = set.Id;
            items.Add(new DockMenuItem(SetName(id), () => Save(p => p.CurrentSet = id), Checked: string.Equals(id, s.Active.Id, StringComparison.OrdinalIgnoreCase)));
        }
        // On means slide away (Tap counts as on); off means always shown.
        items.Add(new DockMenuItem(Lang.T("Slide away when not used", "الانزلاق جانبًا عند عدم الاستخدام"), () => Save(p => p.Show = p.Show is "slide" or "tap" ? "always" : "slide"), Checked: s.Show is "slide" or "tap", Separator: true));
        items.Add(new DockMenuItem(Lang.T("Pulse settings…", "إعدادات نبض…"), () => App.ShowHome(ToolId)));
        items.Add(new DockMenuItem(Lang.T("Turn off Pulse", "إيقاف نبض"), () => App.SetToolEnabled(ToolId, false)));
        return items;
    }

    public static string SetName(string id) => id switch
    {
        "work" => Lang.T("Work", "العمل"),
        "study" => Lang.T("Study", "الدراسة"),
        _ when id.StartsWith("set", StringComparison.Ordinal) && int.TryParse(id[3..], out var n) => Lang.T($"Set {n}", $"مجموعة {n}"),
        _ => id,
    };

    public override IEnumerable<TrayItem> TrayItems(Func<ToolCommand, string?> shortcutText)
    {
        yield return new TrayItem(Lang.T("Show or hide the dock", "إظهار الشريط أو إخفاؤه"), ToggleDock, Shortcut: shortcutText(Commands[0]));
        foreach (var set in S.Sets)
        {
            var id = set.Id;
            yield return new TrayItem(Lang.T("Use set: ", "استخدام المجموعة: ") + SetName(id), () => Save(p => p.CurrentSet = id));
        }
    }
}
