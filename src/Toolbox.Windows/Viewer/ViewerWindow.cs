using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Inputs;
using Toolbox.Core.Viewer;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Viewer;

/// <summary>What the viewer opens into: just looking, or the PDF editor (optionally with a tool or the pages ready).</summary>
internal enum ViewerStart
{
    Preview,
    Edit,
    Sign,
    Pages,
}

/// <summary>
/// Preview: a file shown large, in the same kind of window as the editor. PDFs get the full viewer
/// (pages, zoom, search, outline, themes, split, present) and Edit turns the same window into the
/// PDF editor; a picture opens in the image editor; Word and other documents open in their own
/// app. Several files can be stepped through with the arrows in the title. Where each file was left
/// (page, zoom, theme) is remembered.
/// </summary>
internal sealed partial class ViewerWindow : GlassWindow
{
    private static readonly ViewerMemory Memory = new();
    private static readonly HashSet<string> MovingPictureTypes = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm" };
    private static readonly HashSet<string> VideoTypes = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".mp3", ".wav", ".m4a", ".wma", ".flac", ".aac", ".ogg" };
    private static readonly HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".log", ".ini", ".cfg", ".yaml", ".yml", ".toml", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".c", ".cpp", ".h", ".css", ".scss", ".html", ".htm", ".sql", ".ps1", ".sh", ".bat", ".cmd", ".astro", ".vue", ".go", ".rs", ".kt", ".swift", ".srt", ".vtt" };

    private readonly ActionDispatcher _dispatcher;
    private readonly IReadOnlyList<string> _paths;
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _subtitle = new() { FontSize = 12 };
    private readonly Border _tile = new() { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 0, 12, 0) };
    private readonly Button _editButton = new() { MinWidth = 92 };
    private readonly Button _openButton = new();
    private readonly Button _prevFile = new(), _nextFile = new();
    private readonly ContentControl _toolbar = new();
    private readonly ContentControl _content = new();
    private readonly Grid _body = new();
    private readonly Grid _chrome = new();
    private readonly Border _header = new();
    private readonly TextBlock _busy = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
    private ViewerStart _start;
    private int _index;
    private string _kindLabel = "";
    private ImageView? _image;
    private TextView? _text;
    private MediaView? _media;
    private bool _fullScreen;
    private WindowState _stateBeforeFullScreen;

    public ViewerWindow(IReadOnlyList<string> paths, int index, ViewerStart start, ActionDispatcher dispatcher, Native.POINT? anchor)
    {
        _paths = paths;
        _index = Math.Clamp(index, 0, paths.Count - 1);
        _start = start;
        _dispatcher = dispatcher;
        Resizable = true;
        MinWidth = 720;
        MinHeight = 480;
        CloseOnEscape = false;
        var monitor = ScreenGeometry.MonitorAt(anchor ?? ScreenGeometry.CursorPosition());
        double workW = monitor.WorkArea.Width / monitor.Scale, workH = monitor.WorkArea.Height / monitor.Scale;
        Width = Math.Min(1240, workW * 0.9);
        Height = Math.Min(900, workH * 0.92);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = BuildChrome();
        PreviewKeyDown += OnKeys;
        Closing += OnClosingWindow;
        Closed += (_, _) => Unload();
        Loaded += (_, _) => Load(_index);
    }

    /// <summary>Opens the viewer on files (the wheel's centre, Edit PDF, Sign, Pages).</summary>
    public static void Open(IReadOnlyList<string> paths, ViewerStart start, ActionDispatcher dispatcher, Native.POINT? anchor)
    {
        var existing = paths.Where(File.Exists).ToList();
        if (existing.Count == 0) return;
        var window = new ViewerWindow(existing, 0, start, dispatcher, anchor);
        window.Show();
        Toolbox.Windows.Shell.Foreground.Activate(window);
    }

    private string Path => _paths[_index];

    // ---- chrome ------------------------------------------------------------------------------------

    private UIElement BuildChrome()
    {
        // Right-to-left on the content, not the window (a window-level mirror also mirrors pictures and pages).
        _chrome.FlowDirection = Lang.Flow;
        _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chrome.RowDefinitions.Add(new RowDefinition());

        var header = new DockPanel { Margin = new Thickness(16, 12, 10, 8), Background = Brushes.Transparent };
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        ConfigureIcon(_prevFile, "back", Lang.T("Previous file", "الملف السابق"));
        ConfigureIcon(_nextFile, "forward", Lang.T("Next file", "الملف التالي"));
        _prevFile.Click += (_, _) => Step(-1);
        _nextFile.Click += (_, _) => Step(1);
        right.Children.Add(_prevFile);
        right.Children.Add(_nextFile);
        _openButton.Style = SettingsPage.Res("Lap.Button");
        _openButton.Margin = new Thickness(8, 0, 6, 0);
        _openButton.Click += (_, _) => ShellActions.Open(Path);
        right.Children.Add(_openButton);
        _editButton.Style = SettingsPage.Res("Lap.Button.Accent");
        _editButton.Click += (_, _) => EditClicked();
        right.Children.Add(_editButton);
        var close = new Button { Style = SettingsPage.Res("Lap.Caption.Close"), Width = 40, Height = 32, Content = SettingsPage.Glyph("close", 12, stroke: 2.2), Margin = new Thickness(8, 0, 0, 0), ToolTip = Lang.T("Close (Esc)", "إغلاق (Esc)") };
        close.Click += (_, _) => Close();
        right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        _tile.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
        DockPanel.SetDock(_tile, Dock.Left);
        header.Children.Add(_tile);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        titles.Children.Add(_title);
        titles.Children.Add(_subtitle);
        header.Children.Add(titles);
        MakeDragHandle(header);
        _header.Child = header;
        _chrome.Children.Add(_header);

        _toolbar.Margin = new Thickness(12, 0, 12, 8);
        Grid.SetRow(_toolbar, 1);
        _chrome.Children.Add(_toolbar);

        var stage = new Border { CornerRadius = new CornerRadius(12), Margin = new Thickness(12, 0, 12, 12), ClipToBounds = true };
        stage.SetResourceReference(Border.BackgroundProperty, "Brush.Content");
        _body.Children.Add(_content);
        _busy.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        _body.Children.Add(_busy);
        stage.Child = _body;
        Grid.SetRow(stage, 2);
        _chrome.Children.Add(stage);
        return _chrome;
    }

    internal static void ConfigureIcon(Button b, string icon, string tip, double size = 15)
    {
        b.Style = SettingsPage.Res("Lap.Caption");
        b.Width = 30;
        b.Height = 32;
        b.Content = SettingsPage.Glyph(icon, size, stroke: 1.9);
        b.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
    }

    internal static Button IconButton(string icon, string tip, Action click, double size = 15)
    {
        var b = new Button();
        ConfigureIcon(b, icon, tip, size);
        b.Click += (_, _) => click();
        return b;
    }

    internal static Border Separator() => new Border { Width = 1, Height = 20, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }.With(b => b.SetResourceReference(Border.BackgroundProperty, "Brush.Line2"));

    /// <summary>Shows a toggle-like icon button as on (a light wash behind it) or off.</summary>
    internal static void SetOn(Button b, bool on)
    {
        if (on) b.SetResourceReference(BackgroundProperty, "Brush.AccentWash");
        else b.ClearValue(BackgroundProperty);
    }

    // ---- files -------------------------------------------------------------------------------------

    private void Step(int by)
    {
        if (_paths.Count < 2 || !ConfirmLeaveEdits()) return;
        Unload();
        Load((_index + by + _paths.Count) % _paths.Count);
    }

    private async void Load(int index)
    {
        _index = index;
        _content.Content = null;
        _toolbar.Content = null;
        _busy.Text = Lang.T("Opening…", "جارٍ الفتح…");
        _busy.Visibility = Visibility.Visible;
        _prevFile.Visibility = _nextFile.Visibility = _paths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var path = Path;
        _title.Text = IOPath.GetFileName(path);
        Title = _title.Text;
        var probed = await Task.Run(() => FileProbe.Probe(path));
        string ext = IOPath.GetExtension(path);
        string size = Bytes.Format(probed.SizeBytes);
        _openButton.Content = Lang.T("Open in app", "فتح في التطبيق");
        _openButton.Visibility = Visibility.Visible;
        try
        {
            if (probed.Kind == FileKind.Pdf)
            {
                _kindLabel = "PDF";
                SetTile("pdf");
                await LoadPdf(path, size);
            }
            else if (probed.Kind.IsImage())
            {
                _kindLabel = probed.Kind.Label();
                SetTile("image");
                _image = new ImageView();
                _content.Content = _image;
                bool ok = _image.Load(path);
                var (w, h) = _image.PixelSize;
                _subtitle.Text = ok ? $"{_kindLabel} · {w} × {h} px · {size}" : $"{_kindLabel} · {size}";
                _toolbar.Content = ImageToolbar();
                SetEdit(Lang.T("Edit", "تعديل"), true);
            }
            else if (VideoTypes.Contains(ext))
            {
                _kindLabel = ext.TrimStart('.').ToUpperInvariant();
                SetTile("lap-play");
                _media = new MediaView();
                _media.Failed += message => _busy.Text = Lang.T("This can't be played here: ", "لا يمكن تشغيله هنا: ") + message;
                _content.Content = _media;
                _media.Load(path);
                _subtitle.Text = $"{_kindLabel} · {size}";
                if (MovingPictureTypes.Contains(ext)) _toolbar.Content = VideoToolbar();
                SetEdit(null, false);
            }
            else if (probed.Kind == FileKind.Text || TextTypes.Contains(ext))
            {
                _kindLabel = ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() : "TXT";
                SetTile("doc");
                _text = new TextView();
                _text.Load(path);
                _content.Content = _text;
                _subtitle.Text = $"{_kindLabel} · {size}" + (_text.Truncated ? Lang.T(" · first 4 MB shown", " · تُعرض أول 4 ميغابايت") : "");
                _toolbar.Content = TextToolbar();
                SetEdit(Lang.T("Edit", "تعديل"), true);
            }
            else
            {
                _kindLabel = probed.Kind == FileKind.Docx ? "Word" : ext.Length > 0 ? ext.TrimStart('.').ToUpperInvariant() : Lang.T("File", "ملف");
                SetTile("doc");
                _content.Content = new ShellPreview(path, _kindLabel, size);
                _subtitle.Text = $"{_kindLabel} · {size}";
                bool word = probed.Kind == FileKind.Docx || ext.Equals(".doc", StringComparison.OrdinalIgnoreCase);
                SetEdit(word ? Lang.T("Edit in Word", "تعديل في Word") : null, word);
                _openButton.Content = Lang.T("Open", "فتح");
            }
            _busy.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is ProcessingException or IOException or UnauthorizedAccessException)
        {
            _busy.Text = ex.Message;
            Log.Warn($"Viewer could not open {path}: {ex.Message}");
        }
    }

    private void SetTile(string icon) => _tile.Child = SettingsPage.Glyph(icon, 17, "Brush.Accent");

    private void SetEdit(string? text, bool visible)
    {
        _editButton.Visibility = visible && text is not null ? Visibility.Visible : Visibility.Collapsed;
        if (text is not null) _editButton.Content = text;
    }

    /// <summary>One click from looking to editing: the PDF editor here, a picture in the image editor, Word in Word.</summary>
    private void EditClicked()
    {
        if (_pdf is not null)
        {
            if (_session is null) EnterEdit(EditTool.Select);
            else LeaveEdit();
            return;
        }
        if (_image is not null)
        {
            _dispatcher.Dispatch(CapabilityMap.Ids.Edit, [Path], ScreenGeometry.CursorPosition());
            Close();
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(Path) { UseShellExecute = true, Verb = _text is not null ? "edit" : "open" });
        }
        catch (Exception)
        {
            ShellActions.Open(Path);
        }
    }

    private void Unload()
    {
        SavePosition();
        _content.Content = null;
        _image = null;
        _text = null;
        _media = null;
        UnloadPdf();
    }

    private void SavePosition()
    {
        if (_pdf is null || _view is null || _session is not null) return;
        var (i, f) = _view.Position;
        Memory.Set(_pdf.Path, new ViewerState(i + 1, f, _view.IsFitWidth ? 0 : _view.Zoom, _themeId, _level, _keepPictures));
    }

    // ---- images and text ---------------------------------------------------------------------------

    private UIElement ImageToolbar()
    {
        var bar = new WrapPanel();
        var zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 50, TextAlignment = TextAlignment.Center, FontSize = 12.5 };
        zoomText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        void ShowZoom() => zoomText.Text = $"{Math.Round((_image?.ZoomLevel ?? 1) * 100)}%";
        _image!.ZoomChanged += ShowZoom;
        bar.Children.Add(IconButton("zoom-out", Lang.T("Zoom out (Ctrl −)", "تصغير (Ctrl −)"), () => _image?.Zoom(1 / 1.25)));
        bar.Children.Add(zoomText);
        bar.Children.Add(IconButton("zoom-in", Lang.T("Zoom in (Ctrl +)", "تكبير (Ctrl +)"), () => _image?.Zoom(1.25)));
        bar.Children.Add(IconButton("fit-page", Lang.T("Fit (Ctrl 0)", "ملاءمة (Ctrl 0)"), () => _image?.Fit()));
        bar.Children.Add(Separator());
        bar.Children.Add(IconButton("rotate-left", Lang.T("Turn left", "تدوير لليسار"), () => _image?.Turn(-1)));
        bar.Children.Add(IconButton("rotate", Lang.T("Turn right", "تدوير لليمين"), () => _image?.Turn(1)));
        bar.Children.Add(Separator());
        bar.Children.Add(IconButton("eye-off", Lang.T("Blur or hide parts of it", "تمويه أجزاء منها أو إخفاؤها"), () => { _dispatcher.Editors?.OpenBlur(Path, false, ScreenGeometry.CursorPosition()); Close(); }));
        bar.Children.Add(IconButton("scan", Lang.T("Find and hide personal info", "البحث عن المعلومات الشخصية وإخفاؤها"), () => { _dispatcher.Editors?.OpenBlur(Path, true, ScreenGeometry.CursorPosition()); Close(); }));
        ShowZoom();
        return bar;
    }

    /// <summary>Videos: blur parts of it, or find personal info in it (Longshot's video blur editor).</summary>
    private UIElement VideoToolbar()
    {
        var bar = new WrapPanel();
        bar.Children.Add(IconButton("eye-off", Lang.T("Blur or hide parts of it", "تمويه أجزاء منه أو إخفاؤها"), () => OpenVideoBlur(false)));
        bar.Children.Add(IconButton("scan", Lang.T("Find and hide personal info", "البحث عن المعلومات الشخصية وإخفاؤها"), () => OpenVideoBlur(true)));
        return bar;
    }

    private void OpenVideoBlur(bool findPersonalInfo)
    {
        if (Longshot.Video.VideoBlurWindow.Open(Path, findPersonalInfo) is not null) Close();
    }

    private UIElement TextToolbar()
    {
        var bar = new WrapPanel();
        var find = new TextBox { Width = 220, Style = SettingsPage.Res("Lap.TextBox"), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Lang.T("Find (Ctrl F, Enter for the next)", "بحث (Ctrl F، Enter للتالي)") };
        var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12.5 };
        count.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        find.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _text is null) return;
            bool found = _text.Find(find.Text, false, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
            int n = _text.Count(find.Text, false);
            count.Text = found ? Lang.T($"{n} found", $"وُجد {n}") : Lang.T("Not found", "غير موجود");
            find.Focus();
            e.Handled = true;
        };
        _findBox = find;
        bar.Children.Add(SettingsPage.Glyph("search", 15).With(g => g.Margin = new Thickness(4, 0, 8, 0)));
        bar.Children.Add(find);
        bar.Children.Add(count);
        bar.Children.Add(Separator());
        var wrap = new Button();
        ConfigureIcon(wrap, "fit-width", Lang.T("Wrap long lines", "التفاف الأسطر الطويلة"));
        SetOn(wrap, true);
        wrap.Click += (_, _) => { if (_text is null) return; _text.Wrap = !_text.Wrap; SetOn(wrap, _text.Wrap); };
        bar.Children.Add(wrap);
        return bar;
    }

    private TextBox? _findBox;

    // ---- keys, full screen, closing ----------------------------------------------------------------

    private void OnKeys(object sender, KeyEventArgs e)
    {
        bool typing = Keyboard.FocusedElement is TextBox;
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Escape)
        {
            if (_fullScreen) { ToggleFullScreen(); e.Handled = true; return; }
            if (_editor?.HasSelection == true || typing) { _editor?.CommitText(); Keyboard.ClearFocus(); e.Handled = true; return; }
            Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F11) { ToggleFullScreen(); e.Handled = true; return; }
        if (e.Key == Key.F && mods == ModifierKeys.Control) { (_searchBox ?? _findBox)?.Focus(); (_searchBox ?? _findBox)?.SelectAll(); e.Handled = true; return; }
        if (OnPdfKeys(e, typing)) { e.Handled = true; return; }
        if (typing) return;
        if (_image is not null)
        {
            if (mods == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add) { _image.Zoom(1.25); e.Handled = true; }
            else if (mods == ModifierKeys.Control && e.Key is Key.OemMinus or Key.Subtract) { _image.Zoom(1 / 1.25); e.Handled = true; }
            else if (mods == ModifierKeys.Control && e.Key is Key.D0 or Key.NumPad0) { _image.Fit(); e.Handled = true; }
        }
        if (_media is not null && e.Key == Key.Space) { _media.Toggle(); e.Handled = true; }
        if (_pdf is null && _paths.Count > 1 && mods == ModifierKeys.None && e.Key is Key.Left or Key.Right)
        {
            Step((e.Key == Key.Right) ^ Lang.IsArabic ? 1 : -1);
            e.Handled = true;
        }
    }

    private void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            _header.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Maximized;
        }
        else
        {
            _header.Visibility = Visibility.Visible;
            WindowState = _stateBeforeFullScreen;
        }
    }

    private void OnClosingWindow(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmLeaveEdits()) e.Cancel = true;
    }
}
