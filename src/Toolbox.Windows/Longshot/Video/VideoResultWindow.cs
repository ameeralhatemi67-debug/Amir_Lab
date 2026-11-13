using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Longshot.Video;

/// <summary>
/// The finished recording: it plays straight away (looping the part kept), a trim bar under it
/// with a handle at each end and a strip of frames, and what to do next: save the trimmed part
/// as a new MP4 or as a GIF, copy the file to paste into a chat or a folder, open it, or record
/// again. The recording itself is never changed.
/// </summary>
internal sealed class VideoResultWindow : GlassWindow
{
    private readonly RecordingResult _result;
    private readonly MediaElement _media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform, ScrubbingEnabled = true, IsMuted = true };
    private readonly TrimBar _trim = new() { Height = 52, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _sub = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 12.5 };
    private readonly TextBlock _kept = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5, TextAlignment = TextAlignment.Right };
    private readonly Button _play;
    private readonly Button _saveTrimmed, _saveGif;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private TimeSpan _duration;
    private bool _playing;
    private bool _busy;
    private string _lastSaved;

    public VideoResultWindow(RecordingResult result, Action newRecording, bool fileToolboxOn)
    {
        _result = result;
        _lastSaved = result.Path;
        _duration = result.Duration;
        Title = $"Longshot video · {result.Width} × {result.Height}";
        Resizable = true;
        Width = 820;
        MinWidth = 600;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid { Margin = new Thickness(20, 14, 12, 16), FlowDirection = Lang.Flow };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 8, 10) };
        var close = new Button { Style = SettingsPage.Res("Toolbox.Button.Icon"), Content = SettingsPage.Icon("close", 12), ToolTip = Lang.T("Close (Esc)", "إغلاق (Esc)") };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = Lang.T("Video ready", "الفيديو جاهز"), Style = SettingsPage.Res("Text.Title"), FontSize = 17 });
        _sub.Style = SettingsPage.Res("Text.Caption");
        _sub.FontSize = 12;
        _sub.Text = Summary();
        titles.Children.Add(SettingsPage.Ltr(_sub));
        head.Children.Add(titles);
        MakeDragHandle(head);
        root.Children.Add(head);

        // The player: click it to play or pause.
        var stage = new Border { CornerRadius = new CornerRadius(10), Background = Brushes.Black, ClipToBounds = true, Margin = new Thickness(0, 0, 8, 0), Child = _media, Cursor = Cursors.Hand };
        stage.MouseLeftButtonUp += (_, _) => TogglePlay();
        Grid.SetRow(stage, 1);
        root.Children.Add(stage);

        // Play, time, what is kept; then the trim bar.
        var controls = new DockPanel { Margin = new Thickness(0, 10, 8, 0), FlowDirection = FlowDirection.LeftToRight };
        _play = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 36, Height = 32, Content = SettingsPage.Glyph("lap-play", 15, stroke: 2), ToolTip = Lang.T("Play or pause (Space)", "تشغيل أو إيقاف مؤقت (مسافة)") };
        System.Windows.Automation.AutomationProperties.SetName(_play, Lang.T("Play or pause", "تشغيل أو إيقاف مؤقت"));
        _play.Click += (_, _) => TogglePlay();
        DockPanel.SetDock(_play, Dock.Left);
        controls.Children.Add(_play);
        DockPanel.SetDock(_time, Dock.Left);
        controls.Children.Add(_time);
        controls.Children.Add(_kept);
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);

        _trim.Margin = new Thickness(0, 8, 8, 0);
        _trim.ToolTip = Lang.T("Drag the handles to keep only part of it. I and O set the start and end at the playhead.", "اسحب المقبضين للاحتفاظ بجزء منه فقط. I وO يضبطان البداية والنهاية عند موضع التشغيل.");
        _trim.Seeked += at => { _media.Position = at; UpdateTime(); };
        _trim.RangeChanged += () => { UpdateKept(); if (_media.Position < _trim.Start || _media.Position > _trim.End) _media.Position = _trim.Start; };
        Grid.SetRow(_trim, 3);
        root.Children.Add(_trim);

        var bar = new WrapPanel { Margin = new Thickness(0, 14, 8, 0) };
        void Add(Button b) { b.Margin = new Thickness(0, 0, 6, 6); bar.Children.Add(b); }
        Add(SettingsPage.Button(Lang.T("New recording", "تسجيل جديد"), () => { Close(); newRecording(); }, accent: true));
        var copy = SettingsPage.Button(Lang.T("Copy", "نسخ"), Copy);
        copy.ToolTip = Lang.T("Copies the file: paste it into a chat, an e-mail or a folder (Ctrl+C)", "ينسخ الملف: الصقه في محادثة أو بريد أو مجلد (Ctrl+C)");
        Add(copy);
        _saveTrimmed = SettingsPage.Button(Lang.T("Save trimmed copy", "حفظ نسخة مقصوصة"), () => _ = SaveTrimmed());
        _saveTrimmed.ToolTip = Lang.T("A new MP4 of just the part between the handles", "ملف MP4 جديد للجزء بين المقبضين فقط");
        Add(_saveTrimmed);
        _saveGif = SettingsPage.Button(Lang.T("Save as GIF", "حفظ بصيغة GIF"), () => _ = SaveGif());
        _saveGif.ToolTip = Lang.T("A looping GIF of the part kept, up to 800 px wide: for chats and READMEs", "صورة GIF متكررة للجزء المحتفَظ به بعرض حتى 800 بكسل: للمحادثات وملفات README");
        Add(_saveGif);
        Add(SettingsPage.Button(Lang.T("Open", "فتح"), () => Process.Start(new ProcessStartInfo(_lastSaved) { UseShellExecute = true })));
        Add(SettingsPage.Button(Lang.T("Show in folder", "إظهار في المجلد"), () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastSaved}\"") { UseShellExecute = true })));
        Grid.SetRow(bar, 4);
        root.Children.Add(bar);
        _ = fileToolboxOn; // nothing in File Toolbox takes videos yet

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        Content = border;

        // As tall as the video needs at this width, within the screen.
        double scale = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition()).Scale;
        var work = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition()).WorkArea;
        double videoHeight = (Width - 40) * result.Height / Math.Max(1, result.Width);
        Height = Math.Clamp(videoHeight + 270, MinHeight, work.Height / scale * 0.9);

        _trim.Duration = _duration;
        _trim.Start = TimeSpan.Zero;
        _trim.End = _duration;
        UpdateKept();
        UpdateTime();
        _media.MediaOpened += (_, _) =>
        {
            if (_media.NaturalDuration.HasTimeSpan && _media.NaturalDuration.TimeSpan > TimeSpan.Zero && Math.Abs((_media.NaturalDuration.TimeSpan - _duration).TotalMilliseconds) > 150)
            {
                bool whole = _trim.End >= _duration;
                _duration = _media.NaturalDuration.TimeSpan;
                _trim.Duration = _duration;
                if (whole || _trim.End > _duration) _trim.End = _duration;
                UpdateKept();
            }
            Play();
        };
        _media.MediaFailed += (_, e) => Say(Lang.T($"This video can't be played here ({e.ErrorException?.Message}). It was saved; Open plays it in your video app.", $"لا يمكن تشغيل هذا الفيديو هنا ({e.ErrorException?.Message}). حُفظ، وزر «فتح» يشغّله في تطبيق الفيديو."));
        _clock.Tick += (_, _) => OnClock();
        Loaded += (_, _) =>
        {
            _media.Source = new Uri(result.Path);
            _ = LoadStrip();
        };
        Closed += (_, _) =>
        {
            _clock.Stop();
            _media.Stop();
            _media.Close();
        };
        PreviewKeyDown += OnKey;
    }

    /// <summary>For tests: the trim bar.</summary>
    internal TrimBar Trim => _trim;

    internal string SubText => _sub.Text;

    private string Summary() =>
        $"{_result.Width:N0} × {_result.Height:N0} px   ·   {Format(_duration)}   ·   " + Lang.T($"saved as {IOPath.GetFileName(_result.Path)}", $"حُفظ باسم {IOPath.GetFileName(_result.Path)}");

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; }
        else if (e.Key == Key.I && Keyboard.Modifiers == ModifierKeys.None) { _trim.Start = Clamp(_media.Position, TimeSpan.Zero, _trim.End - TrimBar.MinLength); UpdateKept(); e.Handled = true; }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.None) { _trim.End = Clamp(_media.Position, _trim.Start + TrimBar.MinLength, _duration); UpdateKept(); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { Copy(); e.Handled = true; }
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (_media.Position >= _trim.End - TimeSpan.FromMilliseconds(60) || _media.Position < _trim.Start) _media.Position = _trim.Start;
        _media.Play();
        _playing = true;
        _clock.Start();
        _play.Content = SettingsPage.Glyph("pause", 15, stroke: 2);
    }

    private void Pause()
    {
        _media.Pause();
        _playing = false;
        _clock.Stop();
        _play.Content = SettingsPage.Glyph("lap-play", 15, stroke: 2);
        UpdateTime();
    }

    /// <summary>While playing: the playhead moves, and the part kept loops.</summary>
    private void OnClock()
    {
        if (_playing && _media.Position >= _trim.End) _media.Position = _trim.Start;
        UpdateTime();
    }

    private void UpdateTime()
    {
        _trim.Position = _media.Position;
        _time.Text = $"{Format(_media.Position)} / {Format(_duration)}";
    }

    private void UpdateKept()
    {
        bool trimmed = _trim.Start > TimeSpan.FromMilliseconds(30) || _trim.End < _duration - TimeSpan.FromMilliseconds(30);
        var length = _trim.End - _trim.Start;
        _kept.Text = trimmed
            ? Lang.T($"Keeping {Format(_trim.Start, true)} – {Format(_trim.End, true)}  ({length.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s)", $"الاحتفاظ بـ {Format(_trim.Start, true)} – {Format(_trim.End, true)}  ({length.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} ث)")
            : Lang.T("Drag the ends of the bar to trim", "اسحب طرفي الشريط للقص");
        _saveTrimmed.IsEnabled = trimmed && !_busy;
    }

    internal static string Format(TimeSpan t, bool tenths = false)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        string s = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        return tenths ? s + "." + (t.Milliseconds / 100).ToString(CultureInfo.InvariantCulture) : s;
    }

    /// <summary>A strip of frames along the trim bar, read in the background.</summary>
    private async Task LoadStrip()
    {
        try
        {
            double scale = ScreenGeometry.MonitorAt(ScreenGeometry.CursorPosition()).Scale;
            int h = (int)Math.Ceiling(_trim.Height * scale);
            int w = Math.Max(8, h * _result.Width / Math.Max(1, _result.Height));
            int count = Math.Clamp((int)(Math.Max(_trim.ActualWidth, 600) * scale / w) + 1, 4, 24);
            var frames = new List<BitmapSource>();
            for (int i = 0; i < count; i++)
            {
                var at = TimeSpan.FromTicks((long)(_duration.Ticks * (i + 0.5) / count));
                frames.Add(await VideoFiles.FrameAsync(_result.Path, at, w, h));
                if (!IsLoaded) return;
                _trim.Strip = [.. frames];
                _trim.StripCount = count;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Longshot video: the frame strip could not be read: {ex.Message}");
        }
    }

    private void Copy()
    {
        try
        {
            var files = new StringCollection { _lastSaved };
            Clipboard.SetFileDropList(files);
            Say(Lang.T($"Copied {IOPath.GetFileName(_lastSaved)}. Paste it into a chat, an e-mail or a folder.", $"نُسخ {IOPath.GetFileName(_lastSaved)}. الصقه في محادثة أو بريد أو مجلد."));
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Say(Lang.T("The clipboard is busy. Try again in a moment.", "الحافظة مشغولة. حاول مرة أخرى بعد لحظة."));
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _saveGif.IsEnabled = !busy;
        UpdateKept();
    }

    private async Task SaveTrimmed()
    {
        if (_busy) return;
        var output = VideoFiles.Beside(_result.Path, Lang.T(" trimmed", " مقصوص"), ".mp4");
        SetBusy(true);
        var progress = new Progress<double>(p => Say(Lang.T($"Saving the trimmed copy… {p:P0}", $"حفظ النسخة المقصوصة… {p:P0}")));
        try
        {
            await VideoFiles.TrimAsync(_result.Path, _trim.Start, _trim.End, output, progress);
            _lastSaved = output;
            Say(Lang.T($"Saved {IOPath.GetFileName(output)}. Copy, Open and Show in folder now use it.", $"حُفظ {IOPath.GetFileName(output)}. أزرار النسخ والفتح والإظهار في المجلد تستخدمه الآن."));
        }
        catch (Exception ex)
        {
            Log.Error("Longshot video: trimming failed", ex);
            Say(Lang.T($"The trimmed copy couldn't be saved: {ex.Message}", $"تعذّر حفظ النسخة المقصوصة: {ex.Message}"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task SaveGif()
    {
        if (_busy) return;
        var length = _trim.End - _trim.Start;
        if (length > TimeSpan.FromSeconds(60))
        {
            Say(Lang.T("GIFs are for short clips: trim it to a minute or less first.", "صور GIF للمقاطع القصيرة: قصّه إلى دقيقة أو أقل أولًا."));
            return;
        }
        var output = VideoFiles.Beside(_result.Path, "", ".gif");
        SetBusy(true);
        var progress = new Progress<double>(p => Say(Lang.T($"Making the GIF… {p:P0}", $"إنشاء صورة GIF… {p:P0}")));
        try
        {
            int fps = length > TimeSpan.FromSeconds(20) ? 10 : 12;
            await VideoFiles.GifAsync(_result.Path, _trim.Start, _trim.End, output, maxWidth: 800, fps: fps, progress: progress);
            _lastSaved = output;
            long kb = new FileInfo(output).Length / 1024;
            Say(Lang.T($"Saved {IOPath.GetFileName(output)} ({kb:N0} KB). Copy, Open and Show in folder now use it.", $"حُفظ {IOPath.GetFileName(output)} ({kb:N0} ك.ب). أزرار النسخ والفتح والإظهار في المجلد تستخدمه الآن."));
        }
        catch (Exception ex)
        {
            Log.Error("Longshot video: making the GIF failed", ex);
            Say(Lang.T($"The GIF couldn't be made: {ex.Message}", $"تعذّر إنشاء صورة GIF: {ex.Message}"));
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Say(string text) => _sub.Text = text;
}

/// <summary>
/// The trim bar: a strip of frames, a handle at each end of the part kept (the rest is dimmed),
/// and the playhead. Drag a handle to trim; click or drag anywhere else to move the playhead.
/// </summary>
internal sealed class TrimBar : FrameworkElement
{
    public static readonly TimeSpan MinLength = TimeSpan.FromMilliseconds(300);
    private const double HandleWidth = 12, Radius = 8;
    private enum Drag { None, Start, End, Playhead }
    private Drag _drag;
    private TimeSpan _start, _end, _position;
    private BitmapSource[] _strip = [];

    public TrimBar()
    {
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan Start { get => _start; set { _start = value; InvalidateVisual(); } }

    public TimeSpan End { get => _end; set { _end = value; InvalidateVisual(); } }

    public TimeSpan Position { get => _position; set { _position = value; InvalidateVisual(); } }

    public BitmapSource[] Strip { get => _strip; set { _strip = value; InvalidateVisual(); } }

    /// <summary>How many frames the strip will hold (they arrive one by one).</summary>
    public int StripCount { get; set; }

    /// <summary>The playhead was moved by hand.</summary>
    public event Action<TimeSpan>? Seeked;

    /// <summary>A handle was dragged.</summary>
    public event Action? RangeChanged;

    private Rect Track => new(HandleWidth, 0, Math.Max(1, ActualWidth - 2 * HandleWidth), ActualHeight);

    private double X(TimeSpan t) => Track.X + Track.Width * Math.Clamp(t.TotalMilliseconds / Math.Max(1, Duration.TotalMilliseconds), 0, 1);

    private TimeSpan At(double x) => TimeSpan.FromMilliseconds(Duration.TotalMilliseconds * Math.Clamp((x - Track.X) / Track.Width, 0, 1));

    protected override void OnRender(DrawingContext dc)
    {
        var track = Track;
        var accent = TryFindResource("Brush.Accent") as Brush ?? Brushes.DodgerBlue;
        var line = TryFindResource("Brush.Line2") as Brush ?? Brushes.Gray;
        var back = TryFindResource("Brush.LapCard") as Brush ?? Brushes.DimGray;
        var shape = new RectangleGeometry(track, Radius, Radius);
        dc.DrawGeometry(back, new Pen(line, 1), shape);

        // The frames, side by side, cut to the bar's rounded shape.
        dc.PushClip(shape);
        if (_strip.Length > 0 && StripCount > 0)
        {
            double w = track.Width / StripCount;
            for (int i = 0; i < _strip.Length; i++)
            {
                var img = _strip[i];
                double drawW = Math.Max(w, track.Height * img.PixelWidth / Math.Max(1, img.PixelHeight));
                dc.PushClip(new RectangleGeometry(new Rect(track.X + i * w, 0, w + 0.5, track.Height)));
                dc.DrawImage(img, new Rect(track.X + i * w + (w - drawW) / 2, 0, drawW, track.Height));
                dc.Pop();
            }
        }
        // What is cut away is dimmed.
        var dim = new SolidColorBrush(Color.FromArgb(0xA8, 0, 0, 0));
        double sx = X(_start), ex = X(_end);
        if (sx > track.X) dc.DrawRectangle(dim, null, new Rect(track.X, 0, sx - track.X, track.Height));
        if (ex < track.Right) dc.DrawRectangle(dim, null, new Rect(ex, 0, track.Right - ex, track.Height));
        dc.Pop();

        // The part kept: an accent outline, with a handle at each end.
        dc.DrawRoundedRectangle(null, new Pen(accent, 2.5), new Rect(sx, 1.25, Math.Max(0, ex - sx), track.Height - 2.5), 4, 4);
        DrawHandle(dc, accent, new Rect(sx - HandleWidth, 0, HandleWidth, track.Height), left: true);
        DrawHandle(dc, accent, new Rect(ex, 0, HandleWidth, track.Height), left: false);

        // The playhead.
        double px = X(_position);
        var white = new Pen(Brushes.White, 2);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)), 4), new Point(px, 2), new Point(px, track.Height - 2));
        dc.DrawLine(white, new Point(px, 2), new Point(px, track.Height - 2));
        dc.DrawEllipse(Brushes.White, null, new Point(px, 3), 4, 4);
    }

    private static void DrawHandle(DrawingContext dc, Brush fill, Rect r, bool left)
    {
        var geometry = new RectangleGeometry(r, 5, 5);
        dc.DrawGeometry(fill, null, geometry);
        // Square on the inner side, so the handle and the outline join.
        dc.DrawRectangle(fill, null, left ? new Rect(r.Right - 5, r.Y, 5, r.Height) : new Rect(r.X, r.Y, 5, r.Height));
        var grip = new Pen(Brushes.White, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        double cx = r.X + r.Width / 2, cy = r.Height / 2;
        dc.DrawLine(grip, new Point(cx - 1.6, cy - 7), new Point(cx - 1.6, cy + 7));
        dc.DrawLine(grip, new Point(cx + 1.6, cy - 7), new Point(cx + 1.6, cy + 7));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        double x = e.GetPosition(this).X;
        double sx = X(_start), ex = X(_end);
        if (x >= sx - HandleWidth - 4 && x <= sx + 4) _drag = Drag.Start;
        else if (x >= ex - 4 && x <= ex + HandleWidth + 4) _drag = Drag.End;
        else
        {
            _drag = Drag.Playhead;
            MoveTo(x);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        double x = e.GetPosition(this).X;
        if (_drag == Drag.None)
        {
            double sx = X(_start), ex = X(_end);
            Cursor = x >= sx - HandleWidth - 4 && x <= sx + 4 || x >= ex - 4 && x <= ex + HandleWidth + 4 ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        MoveTo(x);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _drag = Drag.None;
        ReleaseMouseCapture();
    }

    private void MoveTo(double x)
    {
        switch (_drag)
        {
            case Drag.Start:
                // The handle sits left of where the kept part begins.
                _start = Clamp(At(x + HandleWidth / 2), TimeSpan.Zero, _end - MinLength);
                RangeChanged?.Invoke();
                Seeked?.Invoke(_start);
                break;
            case Drag.End:
                _end = Clamp(At(x - HandleWidth / 2), _start + MinLength, Duration);
                RangeChanged?.Invoke();
                Seeked?.Invoke(_end);
                break;
            case Drag.Playhead:
                _position = Clamp(At(x), _start, _end);
                Seeked?.Invoke(_position);
                break;
        }
        InvalidateVisual();
    }

    private static TimeSpan Clamp(TimeSpan v, TimeSpan min, TimeSpan max) => v < min ? min : v > max ? max : v;

    /// <summary>For tests: as if a handle were dragged to <paramref name="x"/> (DIPs from the bar's left).</summary>
    internal void DragHandle(bool start, double x)
    {
        _drag = start ? Drag.Start : Drag.End;
        MoveTo(x);
        _drag = Drag.None;
    }
}
