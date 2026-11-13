using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Toolbox.Core.Actions;
using Toolbox.Processing.Ocr;
using Toolbox.Processing.Studio;
using Toolbox.Processing.Video;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot.Video;

using Log = Toolbox.Core.Diagnostics.Log;
using PixelRect = Toolbox.Processing.Studio.PixelRect;

/// <summary>
/// Blur for video. Pause where something private shows, drag a box over it, and press Follow: the
/// box stays on it as it scrolls or slides, until it leaves the picture. Moving a box by hand at any
/// moment adds a key there (keys are the data; following just writes keys). Find personal info reads
/// the whole video's text and hides e-mails, numbers and keys from the frame they first appear. What
/// is shown is exactly what will be saved; Save writes a new MP4 beside the original.
/// </summary>
internal sealed class VideoBlurWindow : GlassWindow
{
    private readonly string _path;
    private readonly FramePump _pump;
    private readonly VideoBlurPlan _plan = new();
    private readonly Stack<VideoBlurPlan> _undo = new(), _redo = new();
    private readonly WriteableBitmap _bitmap;
    private readonly byte[] _raw, _shown;
    private readonly object _pendingGate = new();
    private byte[]? _pending;
    private TimeSpan _pendingAt;
    private bool _pendingQueued;
    private TimeSpan _at;
    private bool _hasFrame;
    private int? _selected;
    private RedactionMode _mode = RedactionMode.Blur;
    private CancellationTokenSource? _work;      // following, finding or saving
    private string? _workName;
    private bool _dirty, _closeAsked;
    private string? _saved;
    private bool _findOnOpen;

    private readonly Image _picture = new() { Stretch = Stretch.Uniform };
    private readonly BlurOverlay _overlay = new();
    private readonly BlurTimeline _timeline = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _sub = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12.5, MinWidth = 120 };
    private readonly Button _play;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), MinHeight = 18 };
    private readonly Dictionary<RedactionMode, RadioButton> _modes = [];
    private readonly StackPanel _boxCard = new() { Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _boxName = new() { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _boxInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) };
    private readonly Button _follow, _startHere, _endHere, _delete, _find, _save, _openSaved, _showSaved;
    private readonly StackPanel _list = new();
    private readonly TextBlock _listTitle = new() { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
    private readonly TextBlock _saveStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly StackPanel _savedRow = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

    private VideoBlurWindow(string path, FramePump pump)
    {
        _path = path;
        _pump = pump;
        Title = $"Blur video · {IOPath.GetFileName(path)}";
        Resizable = true;
        CloseOnEscape = false;
        MinWidth = 900;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _raw = new byte[pump.Width * pump.Height * 4];
        _shown = new byte[_raw.Length];
        _bitmap = new WriteableBitmap(pump.Width, pump.Height, 96, 96, PixelFormats.Bgr32, null);
        _picture.Source = _bitmap;
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        _overlay.VideoSize = new Size(pump.Width, pump.Height);
        _overlay.Boxes = ShownBoxes;
        _timeline.Duration = pump.Duration;

        _play = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 38, Height = 34, ToolTip = Lang.T("Play or pause (Space)", "تشغيل أو إيقاف مؤقت (مسافة)") };
        _follow = SettingsPage.Button(Lang.T("Follow it", "تتبّعه"), () => _ = FollowSelected(), accent: true);
        _follow.ToolTip = Lang.T("Keeps the box on what is under it from here on, as it scrolls or slides (F)", "يُبقي الصندوق على ما تحته من هنا فصاعدًا وهو يتمرّر أو ينزلق (F)");
        _startHere = SettingsPage.Button(Lang.T("Starts here", "يبدأ هنا"), () => SetSpan(start: true));
        _startHere.ToolTip = Lang.T("The box is on from this moment", "الصندوق ظاهر من هذه اللحظة");
        _endHere = SettingsPage.Button(Lang.T("Ends here", "ينتهي هنا"), () => SetSpan(start: false));
        _endHere.ToolTip = Lang.T("The box is off after this moment", "الصندوق مخفي بعد هذه اللحظة");
        _delete = SettingsPage.Button(Lang.T("Delete", "حذف"), DeleteSelected, ghost: true);
        _delete.ToolTip = Lang.T("Remove this box (Delete)", "إزالة هذا الصندوق (Delete)");
        _find = SettingsPage.Button(Lang.T("Find personal info", "البحث عن معلومات شخصية"), () => _ = FindPersonalInfo());
        _find.ToolTip = Lang.T("Reads the text in the whole video and hides e-mails, phone and card numbers, IBANs, IP addresses and keys from the frame they appear", "يقرأ النص في الفيديو كله ويخفي البريد وأرقام الهواتف والبطاقات وIBAN وعناوين IP والمفاتيح من أول ظهورها");
        _save = SettingsPage.Button(Lang.T("Save blurred copy", "حفظ نسخة مموّهة"), () => _ = Save(), accent: true);
        _save.Height = 40;
        _save.HorizontalAlignment = HorizontalAlignment.Stretch;
        _openSaved = SettingsPage.Button(Lang.T("Open", "فتح"), () => { if (_saved is not null) Process.Start(new ProcessStartInfo(_saved) { UseShellExecute = true }); }, ghost: true);
        _showSaved = SettingsPage.Button(Lang.T("Show in folder", "إظهار في المجلد"), () => { if (_saved is not null) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_saved}\"") { UseShellExecute = true }); }, ghost: true);

        Content = Build();
        SetPlaying(false);
        SizeToScreen();

        _overlay.EditStarted += () => { if (_pump.Playing) Pause(); };
        _overlay.Drawn += AddBox;
        _overlay.Chosen += id => Select(id, seek: false);
        _overlay.Changed += MoveBox;
        _timeline.Seeked += at => { _pump.SeekTo(at); };
        _timeline.Chosen += id => Select(id, seek: false);
        _timeline.SpanChanged += ChangeSpan;
        _pump.Frame += OnFrame;
        _pump.Ended += () => Dispatcher.BeginInvoke(() => SetPlaying(false));
        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            _pump.SeekTo(TimeSpan.Zero);
            _ = LoadStrip();
            if (_findOnOpen) Dispatcher.BeginInvoke(() => _ = FindPersonalInfo(), DispatcherPriority.Background);
        };
        Closed += (_, _) =>
        {
            _work?.Cancel();
            _pump.Dispose();
        };
        Refresh();
        Say(Lang.T("Pause where something private shows and drag a box over it. Follow keeps the box on it as it moves.", "أوقف مؤقتًا حيث يظهر شيء خاص واسحب صندوقًا فوقه. «تتبّعه» يُبقي الصندوق عليه وهو يتحرك."));
    }

    /// <summary>
    /// Opens the editor on a video (and, with <paramref name="findPersonalInfo"/>, starts finding
    /// personal info at once); says why in a notice if it can't be read.
    /// </summary>
    public static VideoBlurWindow? Open(string path, bool findPersonalInfo = false)
    {
        try
        {
            var window = new VideoBlurWindow(path, new FramePump(path)) { _findOnOpen = findPersonalInfo };
            window.Show();
            Shell.Foreground.Activate(window);
            return window;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Video blur could not open the video", ex);
            var toast = new NoticeToast(Lang.T("This video can't be opened for blurring", "لا يمكن فتح هذا الفيديو للتمويه"), ex.Message, warning: true);
            toast.PlaceNear(ScreenGeometry.CursorPosition(), 24);
            toast.Show();
            return null;
        }
    }

    /// <summary>For tests: the editor without showing it.</summary>
    internal static VideoBlurWindow CreateForTest(string path) => new(path, new FramePump(path));

    internal VideoBlurPlan Plan => _plan;
    internal TimeSpan CurrentTime => _at;
    internal bool HasFrame => _hasFrame;
    internal int? SelectedId => _selected;
    internal string StatusText => _status.Text;
    internal string? SavedPath => _saved;
    internal BlurTimeline Timeline => _timeline;
    internal Button FollowButton => _follow;
    internal Button FindButton => _find;
    internal Button SaveButton => _save;
    internal bool Working => _work is not null;
    internal void SeekForTest(TimeSpan at) => _pump.SeekTo(at);
    internal void DrawForTest(PixelRect box) => AddBox(box);
    internal void MoveForTest(int id, PixelRect box) => MoveBox(id, box);

    // ---- layout ------------------------------------------------------------------------------

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(18, 12, 12, 14), FlowDirection = Lang.Flow };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        // Title.
        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 6, 10) };
        var close = new Button { Style = SettingsPage.Res("Toolbox.Button.Icon"), Content = SettingsPage.Icon("close", 12), ToolTip = Lang.T("Close", "إغلاق") };
        close.Click += (_, _) => TryClose();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var undo = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 34, Height = 30, Content = SettingsPage.Glyph("lap-undo", 14, stroke: 2), ToolTip = Lang.T("Undo (Ctrl+Z)", "تراجع (Ctrl+Z)"), Margin = new Thickness(0, 0, 4, 0) };
        undo.Click += (_, _) => Undo();
        DockPanel.SetDock(undo, Dock.Right);
        head.Children.Add(undo);
        var tile = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 10, 0), Child = SettingsPage.Glyph("video", 17, "Brush.Accent").With(g => g.FlowDirection = FlowDirection.LeftToRight) };
        tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(tile, Dock.Left);
        head.Children.Add(tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = Lang.T("Blur video", "تمويه الفيديو"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        _sub.Style = SettingsPage.Res("Lap.Help");
        _sub.Text = $"{IOPath.GetFileName(_path)}   ·   {_pump.Width:N0} × {_pump.Height:N0} px   ·   {VideoResultWindow.Format(_pump.Duration)}";
        titles.Children.Add(SettingsPage.Ltr(_sub));
        head.Children.Add(titles);
        MakeDragHandle(head);
        Grid.SetColumnSpan(head, 2);
        root.Children.Add(head);

        // The picture, the controls under it, and the timeline.
        var left = new Grid { Margin = new Thickness(0, 0, 14, 0) };
        left.RowDefinitions.Add(new RowDefinition());
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var stageGrid = new Grid { FlowDirection = FlowDirection.LeftToRight };
        stageGrid.Children.Add(_picture);
        stageGrid.Children.Add(_overlay);
        var stage = new Border { CornerRadius = new CornerRadius(10), Background = Brushes.Black, ClipToBounds = true, Child = stageGrid };
        left.Children.Add(stage);

        var controls = new DockPanel { Margin = new Thickness(0, 10, 0, 0), FlowDirection = FlowDirection.LeftToRight };
        var back = Small("lap-undo", Lang.T("One frame back (←)", "إطار للخلف (←)"), () => StepFrame(-1));
        back.Content = SettingsPage.Glyph("lap-play", 13, stroke: 2).With(g => g.RenderTransform = new ScaleTransform(-1, 1, 6.5, 6.5));
        var next = Small("lap-play", Lang.T("One frame on (→)", "إطار للأمام (→)"), () => StepFrame(1));
        _play.Click += (_, _) => TogglePlay();
        foreach (var b in new[] { back, _play, next }) { DockPanel.SetDock(b, Dock.Left); controls.Children.Add(b); }
        _play.Margin = new Thickness(4, 0, 4, 0);
        DockPanel.SetDock(_time, Dock.Left);
        controls.Children.Add(_time);
        var keys = new TextBlock { Text = Lang.T("Space play · ← → frame · Delete · Ctrl+Z", "مسافة تشغيل · ← → إطار · Delete · Ctrl+Z"), Style = SettingsPage.Res("Lap.Help"), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        controls.Children.Add(keys);
        Grid.SetRow(controls, 1);
        left.Children.Add(controls);
        _timeline.FlowDirection = FlowDirection.LeftToRight;
        Grid.SetRow(_timeline, 2);
        left.Children.Add(_timeline);
        Grid.SetRow(left, 1);
        root.Children.Add(left);

        // The panel.
        var panel = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        panel.Children.Add(Heading(Lang.T("How to hide", "طريقة الإخفاء")));
        var bar = new Border { Style = SettingsPage.Res("Lap.SegmentBar"), Margin = new Thickness(0, 0, 0, 4) };
        var segments = new UniformGrid { Rows = 1 };
        string group = "video-blur-mode-" + Guid.NewGuid().ToString("N");
        foreach (var (mode, text) in new[] { (RedactionMode.Blur, Lang.T("Blur", "تمويه")), (RedactionMode.Pixelate, Lang.T("Pixelate", "بكسلة")), (RedactionMode.Solid, Lang.T("Solid", "معتم")) })
        {
            var rb = new RadioButton { Style = SettingsPage.Res("Lap.Segment"), GroupName = group, Content = text, IsChecked = mode == _mode, HorizontalContentAlignment = HorizontalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(rb, text);
            rb.Click += (_, _) => SetMode(mode);
            _modes[mode] = rb;
            segments.Children.Add(rb);
        }
        bar.Child = segments;
        panel.Children.Add(bar);
        panel.Children.Add(Help(Lang.T("Solid is the safe choice for passwords and card numbers.", "«معتم» هو الخيار الآمن لكلمات المرور وأرقام البطاقات.")));

        // The chosen box.
        _boxCard.Children.Add(_boxName);
        _boxInfo.Style = SettingsPage.Res("Lap.Help");
        _boxCard.Children.Add(_boxInfo);
        _follow.HorizontalAlignment = HorizontalAlignment.Stretch;
        _boxCard.Children.Add(_follow);
        var span = new UniformGrid { Rows = 1, Margin = new Thickness(0, 6, 0, 0) };
        _startHere.Margin = new Thickness(0, 0, 3, 0);
        _endHere.Margin = new Thickness(3, 0, 0, 0);
        span.Children.Add(_startHere);
        span.Children.Add(_endHere);
        _boxCard.Children.Add(span);
        _delete.Margin = new Thickness(0, 6, 0, 0);
        _delete.HorizontalAlignment = HorizontalAlignment.Left;
        _boxCard.Children.Add(_delete);
        var card = new Border { Padding = new Thickness(12, 10, 12, 10), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 14, 0, 0), BorderThickness = new Thickness(1), Child = _boxCard };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        panel.Children.Add(card);

        _find.Margin = new Thickness(0, 14, 0, 0);
        _find.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(_find);
        _status.Style = SettingsPage.Res("Lap.Help");
        panel.Children.Add(_status);
        panel.Children.Add(_listTitle);
        panel.Children.Add(_list);

        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var side = new DockPanel();
        var saveArea = new StackPanel { Margin = new Thickness(0, 12, 6, 0) };
        saveArea.Children.Add(_save);
        _saveStatus.Style = SettingsPage.Res("Lap.Help");
        saveArea.Children.Add(_saveStatus);
        _openSaved.Margin = new Thickness(0, 0, 6, 0);
        _savedRow.Children.Add(_openSaved);
        _savedRow.Children.Add(_showSaved);
        saveArea.Children.Add(_savedRow);
        DockPanel.SetDock(saveArea, Dock.Bottom);
        side.Children.Add(saveArea);
        side.Children.Add(scroll);
        Grid.SetRow(side, 1);
        Grid.SetColumn(side, 1);
        root.Children.Add(side);

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };

    private static TextBlock Help(string text) => new() { Text = text, Style = SettingsPage.Res("Lap.Help"), TextWrapping = TextWrapping.Wrap };

    private static Button Small(string icon, string tip, Action click)
    {
        var b = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 34, Height = 34, Content = SettingsPage.Glyph(icon, 13, stroke: 2), ToolTip = tip };
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    private void SizeToScreen()
    {
        var monitor = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition());
        double scale = monitor.Scale;
        double maxW = monitor.WorkArea.Width / scale * 0.92, maxH = monitor.WorkArea.Height / scale * 0.92;
        // The picture as big as fits beside the panel, with room for the controls and timeline.
        double pictureW = Math.Min(maxW - 360, 1100);
        double pictureH = pictureW * _pump.Height / Math.Max(1, _pump.Width);
        Width = Math.Clamp(pictureW + 360, MinWidth, Math.Max(MinWidth, maxW));
        Height = Math.Clamp(pictureH + 250, MinHeight, Math.Max(MinHeight, maxH));
    }

    private async Task LoadStrip()
    {
        try
        {
            double scale = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition()).Scale;
            _timeline.Strip = await VideoBlurWork.StripAsync(_path, 16, (int)(BlurTimeline.StripHeight * scale), CancellationToken.None);
            _timeline.InvalidateVisual();
        }
        catch (Exception ex)
        {
            Log.Warn($"Video blur: the frame strip could not be read: {ex.Message}");
        }
    }

    // ---- frames ------------------------------------------------------------------------------

    /// <summary>A frame from the player (its thread): kept as the latest, shown at the next chance.</summary>
    private void OnFrame(byte[] frame, TimeSpan at) => Offer(frame, at);

    private void Offer(byte[] frame, TimeSpan at)
    {
        lock (_pendingGate)
        {
            _pending ??= new byte[_raw.Length];
            Buffer.BlockCopy(frame, 0, _pending, 0, Math.Min(frame.Length, _pending.Length));
            _pendingAt = at;
            if (_pendingQueued) return;
            _pendingQueued = true;
        }
        Dispatcher.BeginInvoke(ShowPending, DispatcherPriority.Render);
    }

    private void ShowPending()
    {
        lock (_pendingGate)
        {
            _pendingQueued = false;
            if (_pending is null) return;
            Buffer.BlockCopy(_pending, 0, _raw, 0, _raw.Length);
            _at = _pendingAt;
        }
        _hasFrame = true;
        Render();
    }

    /// <summary>The frame as it will be saved: the boxes on now, hidden.</summary>
    private void Render()
    {
        Buffer.BlockCopy(_raw, 0, _shown, 0, _raw.Length);
        foreach (var (box, mode) in _plan.BoxesAt(_at)) FrameBlur.Apply(_shown, _pump.Width, _pump.Height, box, mode);
        _bitmap.WritePixels(new Int32Rect(0, 0, _pump.Width, _pump.Height), _shown, _pump.Width * 4, 0);
        _overlay.InvalidateVisual();
        _timeline.Position = _at;
        _timeline.InvalidateVisual();
        _time.Text = $"{VideoResultWindow.Format(_at, tenths: true)} / {VideoResultWindow.Format(_pump.Duration)}";
        RefreshCard();
    }

    private IReadOnlyList<ShownBox> ShownBoxes() =>
        _plan.Tracks.Where(t => t.IsOn(_at)).Select(t => new ShownBox(t.Id, t.BoxAt(_at)!.Value, t.Mode, BoxName(t))).ToList();

    private static string BoxName(BlurTrack t) => t.Label ?? Lang.T($"Box {t.Id}", $"صندوق {t.Id}");

    // ---- playback ----------------------------------------------------------------------------

    private void TogglePlay()
    {
        if (_work is not null) return;
        if (_pump.Playing) { Pause(); return; }
        if (_at >= _pump.Duration - _pump.FrameTime * 2) _pump.SeekTo(TimeSpan.Zero);
        _pump.Play();
        SetPlaying(true);
    }

    private void Pause()
    {
        _pump.Pause();
        SetPlaying(false);
    }

    private void SetPlaying(bool playing) => _play.Content = SettingsPage.Glyph(playing ? "pause" : "lap-play", 15, stroke: 2);

    private void StepFrame(int direction)
    {
        if (_work is not null) return;
        Pause();
        if (direction > 0) _pump.Step();
        else _pump.SeekTo(_at - _pump.FrameTime);
    }

    // ---- editing -----------------------------------------------------------------------------

    private void Remember()
    {
        _undo.Push(_plan.Clone());
        if (_undo.Count > 60) { var keep = _undo.Take(60).Reverse().ToList(); _undo.Clear(); foreach (var p in keep) _undo.Push(p); }
        _redo.Clear();
        _dirty = true;
        _closeAsked = false;
    }

    private void Restore(VideoBlurPlan from)
    {
        _plan.Tracks.Clear();
        _plan.Tracks.AddRange(from.Tracks.Select(t => t.Clone()));
        if (_selected is { } id && _plan.Tracks.All(t => t.Id != id)) _selected = null;
        Changed();
    }

    private void Undo()
    {
        if (_work is not null || _undo.Count == 0) return;
        _redo.Push(_plan.Clone());
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        if (_work is not null || _redo.Count == 0) return;
        _undo.Push(_plan.Clone());
        Restore(_redo.Pop());
    }

    private TimeSpan Near => TimeSpan.FromTicks(_pump.FrameTime.Ticks / 2);

    private void AddBox(PixelRect box)
    {
        if (_work is not null) return;
        Remember();
        var track = new BlurTrack { Id = _plan.NextId(), Mode = _mode, Start = _at, End = _pump.Duration };
        track.SetKey(_at, box, Near);
        _plan.Tracks.Add(track);
        _selected = track.Id;
        Changed();
        Say(Lang.T("Box added from this moment to the end. Press Follow it to keep it on what's under it as it moves.", "أُضيف صندوق من هذه اللحظة حتى النهاية. اضغط «تتبّعه» ليبقى على ما تحته وهو يتحرك."));
    }

    private void MoveBox(int id, PixelRect box)
    {
        if (_work is not null || _plan.Tracks.FirstOrDefault(t => t.Id == id) is not { } track) return;
        Remember();
        track.SetKey(_at, box, Near);
        Changed();
        Say(Lang.T($"Key set at {VideoResultWindow.Format(_at, true)}: the box moves smoothly between its keys.", $"ضُبط مفتاح عند {VideoResultWindow.Format(_at, true)}: يتحرك الصندوق بسلاسة بين مفاتيحه."));
    }

    private void Select(int? id, bool seek)
    {
        _selected = id;
        if (seek && id is { } chosen && _plan.Tracks.FirstOrDefault(t => t.Id == chosen) is { } track && !track.IsOn(_at))
            _pump.SeekTo(track.Start);
        Changed(dirty: false);
    }

    private void SetMode(RedactionMode mode)
    {
        _mode = mode;
        if (_selected is { } id && _plan.Tracks.FirstOrDefault(t => t.Id == id) is { } track && track.Mode != mode)
        {
            Remember();
            track.Mode = mode;
            Changed();
        }
    }

    private void SetSpan(bool start)
    {
        if (_work is not null || Selected() is not { } track) return;
        Remember();
        if (start)
        {
            // Earlier than its first key, the box holds the first key's place: move it there if it was elsewhere then.
            track.Start = _at < track.End ? _at : track.End - _pump.FrameTime;
        }
        else track.End = _at > track.Start ? _at : track.Start + _pump.FrameTime;
        Changed();
    }

    private void ChangeSpan(int id, TimeSpan start, TimeSpan end)
    {
        if (_work is not null || _plan.Tracks.FirstOrDefault(t => t.Id == id) is not { } track) return;
        Remember();
        track.Start = start;
        track.End = end;
        Changed();
    }

    private void DeleteSelected()
    {
        if (_work is not null || Selected() is not { } track) return;
        Remember();
        _plan.Tracks.Remove(track);
        _selected = null;
        Changed();
        Say(Lang.T("Box removed (Ctrl+Z brings it back).", "أُزيل الصندوق (Ctrl+Z يعيده)."));
    }

    private BlurTrack? Selected() => _selected is { } id ? _plan.Tracks.FirstOrDefault(t => t.Id == id) : null;

    private void Changed(bool dirty = true)
    {
        if (dirty) _savedRow.Visibility = _saved is null ? Visibility.Collapsed : _savedRow.Visibility;
        if (_hasFrame) Render();
        Refresh();
    }

    /// <summary>Everything that shows the plan: the overlay, the timeline lanes, the box card and the list.</summary>
    private void Refresh()
    {
        _overlay.SelectedId = _selected;
        _overlay.Locked = _work is not null;
        _overlay.InvalidateVisual();
        _timeline.SelectedId = _selected;
        _timeline.Lanes = _plan.Tracks.Select(t => new TimelineLane(t.Id, t.Start, t.End, t.Mode, BoxName(t), t.Keys.Select(k => k.At).Where(k => k >= t.Start && k <= t.End).ToList())).ToList();
        _timeline.InvalidateMeasure();
        _timeline.InvalidateVisual();
        if (Selected() is { } chosen) foreach (var (m, rb) in _modes) rb.IsChecked = m == chosen.Mode;
        else foreach (var (m, rb) in _modes) rb.IsChecked = m == _mode;
        RefreshCard();

        _listTitle.Text = _plan.Tracks.Count == 0 ? "" : Lang.T($"Hidden in this video ({_plan.Tracks.Count})", $"المخفي في هذا الفيديو ({_plan.Tracks.Count})");
        _list.Children.Clear();
        var accent = BlurColours.Accent(this);
        foreach (var track in _plan.Tracks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            var remove = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 26, Height = 24, Content = SettingsPage.Glyph("close", 9, stroke: 2), ToolTip = Lang.T("Remove", "إزالة") };
            int id = track.Id;
            remove.Click += (_, _) => { _selected = id; DeleteSelected(); };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Margin = new Thickness(2, 0, 8, 0), Background = new SolidColorBrush(BlurColours.For(track.Mode, accent)), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);
            var when = SettingsPage.Ltr(new TextBlock { Text = $"{VideoResultWindow.Format(track.Start)}–{VideoResultWindow.Format(track.End)}", Style = SettingsPage.Res("Lap.Help"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0) });
            DockPanel.SetDock(when, Dock.Right);
            row.Children.Add(when);
            row.Children.Add(new TextBlock { Text = BoxName(track), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = track.Id == _selected ? FontWeights.SemiBold : FontWeights.Normal });
            var item = new Border { Child = row, Padding = new Thickness(6, 3, 2, 3), CornerRadius = new CornerRadius(8) };
            if (track.Id == _selected) item.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
            item.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is DependencyObject d && IsInside(d, remove)) return; Select(id, seek: true); };
            _list.Children.Add(item);
        }
        _save.IsEnabled = _work is null && _plan.Tracks.Count > 0;
        _find.IsEnabled = _work is null || _workName == "find";
        _find.Content = _workName == "find" ? Lang.T("Stop finding", "إيقاف البحث") : Lang.T("Find personal info", "البحث عن معلومات شخصية");
    }

    private void RefreshCard()
    {
        var track = Selected();
        bool on = track?.IsOn(_at) == true;
        if (track is null)
        {
            _boxName.Text = Lang.T("No box chosen", "لم يُختر صندوق");
            _boxInfo.Text = Lang.T("Drag on the picture to add one, or click a box to choose it.", "اسحب على الصورة لإضافة صندوق، أو انقر صندوقًا لاختياره.");
        }
        else
        {
            _boxName.Text = BoxName(track);
            _boxInfo.Text = Lang.T($"On {VideoResultWindow.Format(track.Start, true)} – {VideoResultWindow.Format(track.End, true)} · {track.Keys.Count} key{(track.Keys.Count == 1 ? "" : "s")}", $"ظاهر {VideoResultWindow.Format(track.Start, true)} – {VideoResultWindow.Format(track.End, true)} · {track.Keys.Count} مفاتيح")
                + (on ? "" : Lang.T(". It is off at this moment.", ". إنه مخفي في هذه اللحظة."));
        }
        bool following = _workName == "follow";
        _follow.IsEnabled = following || _work is null && on;
        _follow.Content = following ? Lang.T("Stop following", "إيقاف التتبّع") : Lang.T("Follow it", "تتبّعه");
        _startHere.IsEnabled = _endHere.IsEnabled = _delete.IsEnabled = _work is null && track is not null;
    }

    private static bool IsInside(DependencyObject? d, DependencyObject container)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d == container) return true;
        return false;
    }

    // ---- following, finding, saving ----------------------------------------------------------

    private CancellationToken BeginWork(string name)
    {
        Pause();
        _work = new CancellationTokenSource();
        _workName = name;
        Refresh();
        return _work.Token;
    }

    private void EndWork()
    {
        _work?.Dispose();
        _work = null;
        _workName = null;
        Refresh();
    }

    /// <summary>Follow: the chosen box stays on what is under it from this frame on.</summary>
    internal async Task FollowSelected()
    {
        if (_workName == "follow") { _work?.Cancel(); return; }
        if (_work is not null || Selected() is not { } track || track.BoxAt(_at) is not { } box) return;
        Remember();
        var from = _at;
        var cancel = BeginWork("follow");
        Say(Lang.T("Following… (Stop following ends it here)", "جارٍ التتبّع… («إيقاف التتبّع» ينهيه هنا)"));
        try
        {
            var outcomes = await VideoBlurWork.FollowAsync(_path, [(track.Id, box)], from, (frame, at, keys) =>
                Dispatcher.BeginInvoke(() =>
                {
                    if (_workName != "follow" || !keys.TryGetValue(track.Id, out var soFar)) return;
                    track.ReplaceKeysAfter(from, soFar);
                    Offer(frame, at);
                    Refresh();
                }), cancel);
            if (outcomes.TryGetValue(track.Id, out var outcome))
            {
                track.ReplaceKeysAfter(from, outcome.Keys);
                if (outcome.End == FollowEnd.Gone) track.End = outcome.EndedAt;
                string at = VideoResultWindow.Format(outcome.EndedAt, true);
                Say(outcome.End switch
                {
                    FollowEnd.Gone => Lang.T($"Followed it until it left the picture at {at}. The box ends there.", $"تُتبّع حتى خرج من الصورة عند {at}. ينتهي الصندوق هناك."),
                    FollowEnd.Lost => Lang.T($"Lost it at {at}: what was under the box changed. The box stays where it was from there; check the rest and move it if needed.", $"فُقد عند {at}: تغيّر ما تحت الصندوق. يبقى الصندوق مكانه من هناك؛ راجع الباقي وحرّكه إن لزم."),
                    FollowEnd.Plain => Lang.T("There's nothing to follow in this box (it's one plain colour), so it stays where it is.", "لا شيء يُتتبّع في هذا الصندوق (لون واحد)، فيبقى مكانه."),
                    FollowEnd.Cancelled => Lang.T($"Stopped at {at}. The box keeps what was followed so far.", $"توقف عند {at}. يحتفظ الصندوق بما تُتبّع حتى الآن."),
                    _ => Lang.T("Followed it to the end of the video.", "تُتبّع حتى نهاية الفيديو."),
                });
                Log.Info($"Video blur: followed box {track.Id} from {from} ({outcome.End} at {outcome.EndedAt}, {track.Keys.Count} keys).");
                _pump.SeekTo(outcome.EndedAt);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            Log.Error("Video blur: following failed", ex);
            Say(Lang.T($"Following stopped: {ex.Message}", $"توقف التتبّع: {ex.Message}"));
        }
        finally
        {
            EndWork();
            if (_hasFrame) Render();
        }
    }

    /// <summary>Find personal info: reads the whole video's text and hides what it finds.</summary>
    internal async Task FindPersonalInfo()
    {
        if (_workName == "find") { _work?.Cancel(); return; }
        if (_work is not null) return;
        if (VideoBlurWork.OcrOverride is null && (OcrService.Engine is null || !RuntimeFeatures.IsAvailable(RuntimeFeatures.Ocr)))
        {
            Say(Lang.T("Finding personal info needs Windows text recognition (Settings > Time & language > Language & region > a language with Optical character recognition). Boxes still work.", "يحتاج البحث عن المعلومات الشخصية إلى التعرّف على النص في ويندوز (الإعدادات > الوقت واللغة > اللغة والمنطقة > لغة مع التعرّف الضوئي على الحروف). الصناديق تعمل كالمعتاد."));
            return;
        }
        var cancel = BeginWork("find");
        Say(Lang.T("Reading the video's text…", "قراءة نص الفيديو…"));
        try
        {
            var found = await VideoBlurWork.FindPersonalInfoAsync(_path, _plan.Clone(), _mode, (at, total, count) =>
                Dispatcher.BeginInvoke(() => Say(Lang.T($"Reading the text… {VideoResultWindow.Format(at)} of {VideoResultWindow.Format(total)} · {count} found", $"قراءة النص… {VideoResultWindow.Format(at)} من {VideoResultWindow.Format(total)} · وُجد {count}"))), cancel);
            if (found.Count > 0)
            {
                Remember();
                _plan.Tracks.AddRange(found);
                _selected = found[0].Id;
            }
            Say(found.Count == 0
                ? Lang.T(cancel.IsCancellationRequested ? "Stopped. Nothing was found up to there." : "No e-mails, numbers or keys found in the video's text. Recognition can miss small print, so play it through to check.", cancel.IsCancellationRequested ? "توقف. لم يُعثر على شيء حتى هناك." : "لم يُعثر على بريد أو أرقام أو مفاتيح في نص الفيديو. قد يفوت التعرّف الخط الصغير، فشغّله للتحقق.")
                : Lang.T($"Found {found.Count}. Each is hidden from the frame it first shows and follows the text. Play it through to check: recognition can miss small print.", $"وُجد {found.Count}. كلٌّ منها مخفي من أول إطار يظهر فيه ويتبع النص. شغّله للتحقق: قد يفوت التعرّف الخط الصغير."));
            Log.Info($"Video blur: found {found.Count} pieces of personal info.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
        {
            Log.Warn($"Video blur: finding personal info stopped: {ex.Message}");
            Say(Lang.T($"Finding stopped: {ex.Message}", $"توقف البحث: {ex.Message}"));
        }
        finally
        {
            EndWork();
            if (_hasFrame) Render(); // the new boxes, hidden in the frame shown
        }
    }

    /// <summary>Save: a new MP4 beside the original, with every box hidden.</summary>
    internal async Task<string?> Save()
    {
        if (_workName == "save") { _work?.Cancel(); return null; }
        if (_work is not null || _plan.Tracks.Count == 0) return null;
        var output = VideoFiles.Beside(_path, Lang.T(" blurred", " مموّه"), ".mp4");
        var cancel = BeginWork("save");
        _save.IsEnabled = true;
        _save.Content = Lang.T("Stop saving", "إيقاف الحفظ");
        _savedRow.Visibility = Visibility.Collapsed;
        var progress = new Progress<double>(p => _saveStatus.Text = Lang.T($"Saving… {p:P0}", $"جارٍ الحفظ… {p:P0}"));
        try
        {
            var result = await VideoBlurRenderer.SaveAsync(_path, _plan, output, progress, cancel);
            _saved = result.Path;
            _dirty = false;
            _saveStatus.Text = Lang.T($"Saved {IOPath.GetFileName(result.Path)}.", $"حُفظ {IOPath.GetFileName(result.Path)}.")
                + (result.SoundLeftOut ? Lang.T(" The copy has no sound (not kept yet).", " النسخة بلا صوت (لا يُحفظ بعد).") : "");
            _savedRow.Visibility = Visibility.Visible;
            Log.Info($"Video blur saved {result.Path} ({result.Frames} frames, {_plan.Tracks.Count} boxes).");
            return result.Path;
        }
        catch (OperationCanceledException)
        {
            _saveStatus.Text = Lang.T("Saving stopped. Nothing was written.", "توقف الحفظ. لم يُكتب شيء.");
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            Log.Error("Video blur: saving failed", ex);
            _saveStatus.Text = Lang.T($"The copy couldn't be saved: {ex.Message}", $"تعذّر حفظ النسخة: {ex.Message}");
            return null;
        }
        finally
        {
            _save.Content = Lang.T("Save blurred copy", "حفظ نسخة مموّهة");
            EndWork();
        }
    }

    private void Say(string text) => _status.Text = text;

    // ---- keys and closing ----------------------------------------------------------------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Space when mods == ModifierKeys.None: TogglePlay(); break;
            case Key.Left when mods == ModifierKeys.None: StepFrame(-1); break;
            case Key.Right when mods == ModifierKeys.None: StepFrame(1); break;
            case Key.Delete or Key.Back when mods == ModifierKeys.None: DeleteSelected(); break;
            case Key.F when mods == ModifierKeys.None: _ = FollowSelected(); break;
            case Key.Z when mods == ModifierKeys.Control: Undo(); break;
            case Key.Y when mods == ModifierKeys.Control: Redo(); break;
            case Key.S when mods == ModifierKeys.Control: _ = Save(); break;
            case Key.Escape:
                if (_work is not null) _work.Cancel();
                else if (_selected is not null) Select(null, seek: false);
                else TryClose();
                break;
            default: return;
        }
        e.Handled = true;
    }

    /// <summary>Closing with boxes that were never saved asks once, in the status line.</summary>
    private void TryClose()
    {
        if (_dirty && _plan.Tracks.Count > 0 && !_closeAsked)
        {
            _closeAsked = true;
            Say(Lang.T("The boxes aren't saved into a copy yet. Close again to leave without saving.", "لم تُحفظ الصناديق في نسخة بعد. أغلق مرة أخرى للخروج دون حفظ."));
            return;
        }
        Close();
    }
}
