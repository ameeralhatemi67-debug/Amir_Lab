using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Processing.Pdf;
using Toolbox.Windows.Home;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Viewer;

internal sealed partial class ViewerWindow
{
    private OpenPdf? _pdf;
    private PdfView? _view, _view2;
    private PdfView? _active;
    private PdfEditSession? _session;
    private PdfEditController? _editor;
    private readonly List<OpenPdf> _extraPdfs = [];
    private TextBox? _searchBox, _pageBox;
    private TextBlock? _pageCount, _zoomText, _searchCount;
    private Button? _backButton, _forwardButton, _caseButton, _outlineButton, _splitButton, _overviewButton;
    private bool _matchCase;
    private IReadOnlyList<(OpenPdf Pdf, int Page, IReadOnlyList<PdfBox> Boxes)> _hits = [];
    private int _hit = -1;
    private string _searched = "";
    private CancellationTokenSource? _searchCancel;
    private string _themeId = "light";
    private double _level = 0.6;
    private bool _keepPictures = true;
    private Border? _outlinePanel, _pagesPanel;
    private ListBox? _pageList;

    private PageTheme Theme => PageTheme.Find(_themeId, _level, _keepPictures);

    // ---- for tests ----
    internal PdfView? ActiveView => _active;
    internal PdfEditSession? Session => _session;
    internal OpenPdf? Pdf => _pdf;
    internal bool IsReady => _pdf is not null && _toolbar.Content is not null;
    internal string SearchStatus => _searchCount?.Text ?? "";
    internal Task SearchFor(string text) { _searchBox!.Text = text; return SearchStep(1); }
    internal void UseTheme(string id) { _themeId = id; ApplyTheme(); }
    internal void StartEditing() => EnterEdit(EditTool.Select);
    internal void ShowPagesPanel() => TogglePages(true);
    internal void Discard() { _session?.MarkSaved(); }

    private async Task LoadPdf(string path, string size)
    {
        _pdf = await Task.Run(() => OpenPdf.Open(path));
        _subtitle.Text = $"PDF · {_pdf.PageCount} {(_pdf.PageCount == 1 ? Lang.T("page", "صفحة") : Lang.T("pages", "صفحات"))} · {size}";
        var state = Memory.Get(path);
        if (state is not null)
        {
            _themeId = state.Theme;
            _level = state.Level;
            _keepPictures = state.KeepPictures;
        }
        _view = NewView();
        _active = _view;
        BuildPdfLayout();
        _view.SetPages(Enumerable.Range(1, _pdf.PageCount).Select(p => new PageRef(_pdf, p)).ToList(), 0);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        if (state is not null)
        {
            if (state.Zoom > 0) _view.SetZoom(state.Zoom);
            _view.SetPosition(state.Page - 1, state.Offset);
        }
        _toolbar.Content = PdfToolbar();
        SetEdit(Lang.T("Edit", "تعديل"), true);
        ShowPage(_view.Current);
        if (_start != ViewerStart.Preview)
        {
            EnterEdit(_start == ViewerStart.Sign ? EditTool.Picture : EditTool.Select);
            if (_start == ViewerStart.Sign) Sign();
            if (_start == ViewerStart.Pages) TogglePages(true);
            _start = ViewerStart.Preview;
        }
    }

    private PdfView NewView()
    {
        var view = new PdfView { Theme = Theme };
        view.CurrentChanged += i => { if (view == _active) ShowPage(i); };
        view.ZoomChanged += () => { if (view == _active) ShowZoom(); };
        view.HistoryChanged += () => { if (view == _active) ShowHistory(); };
        view.PreviewMouseDown += (_, _) => { if (_active != view) { _active = view; ShowPage(view.Current); ShowZoom(); ShowHistory(); } };
        return view;
    }

    private void UnloadPdf()
    {
        _searchCancel?.Cancel();
        _editor = null;
        _session = null;
        _view = _view2 = _active = null;
        _hits = [];
        _hit = -1;
        _searchBox = null;
        _pdf?.Dispose();
        _pdf = null;
        foreach (var p in _extraPdfs) p.Dispose();
        _extraPdfs.Clear();
        _outlinePanel = _pagesPanel = null;
        _pageList = null;
    }

    /// <summary>Outline | view (or two, split) | pages (in edit mode).</summary>
    private void BuildPdfLayout()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _outlinePanel = new Border { Width = 260, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(0, 0, 1, 0) };
        _outlinePanel.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        grid.Children.Add(_outlinePanel);
        var views = new Grid();
        Grid.SetColumn(views, 1);
        views.Children.Add(_view!);
        grid.Children.Add(views);
        _pagesPanel = new Border { Width = 190, Visibility = Visibility.Collapsed, BorderThickness = new Thickness(1, 0, 0, 0) };
        _pagesPanel.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        Grid.SetColumn(_pagesPanel, 2);
        grid.Children.Add(_pagesPanel);
        _content.Content = grid;
    }

    private Grid? ViewsHost => (_content.Content as Grid)?.Children.OfType<Grid>().FirstOrDefault();

    // ---- the toolbar -------------------------------------------------------------------------------

    private UIElement PdfToolbar()
    {
        var bar = new WrapPanel();
        _outlineButton = IconButton("outline", Lang.T("Contents", "المحتويات"), ToggleOutline);
        bar.Children.Add(_outlineButton);
        _backButton = IconButton("back", Lang.T("Back (Alt ←)", "رجوع (Alt ←)"), () => _active?.Back());
        _forwardButton = IconButton("forward", Lang.T("Forward (Alt →)", "تقدّم (Alt →)"), () => _active?.Forward());
        bar.Children.Add(_backButton);
        bar.Children.Add(_forwardButton);
        bar.Children.Add(Separator());
        bar.Children.Add(IconButton("first", Lang.T("First page (Home)", "الصفحة الأولى (Home)"), () => _active?.GoTo(0)));
        bar.Children.Add(IconButton("lap-chev-up", Lang.T("Previous page", "الصفحة السابقة"), () => _active?.Previous()));
        _pageBox = new TextBox { Width = 46, Style = SettingsPage.Res("Lap.TextBox"), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Lang.T("Type a page number and press Enter", "اكتب رقم صفحة واضغط Enter"), FlowDirection = FlowDirection.LeftToRight };
        _pageBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || _active is null) return;
            if (int.TryParse(_pageBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) _active.GoTo(n - 1);
            Keyboard.ClearFocus();
            e.Handled = true;
        };
        _pageBox.GotKeyboardFocus += (_, _) => _pageBox.SelectAll();
        bar.Children.Add(_pageBox);
        _pageCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 4, 0), FontSize = 12.5, FlowDirection = FlowDirection.LeftToRight };
        _pageCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        bar.Children.Add(_pageCount);
        bar.Children.Add(IconButton("lap-chev-down", Lang.T("Next page", "الصفحة التالية"), () => _active?.Next()));
        bar.Children.Add(IconButton("last", Lang.T("Last page (End)", "الصفحة الأخيرة (End)"), () => _active?.GoTo(int.MaxValue)));
        bar.Children.Add(Separator());
        bar.Children.Add(IconButton("zoom-out", Lang.T("Zoom out (Ctrl −)", "تصغير (Ctrl −)"), () => _active?.ZoomBy(1 / 1.2)));
        _zoomText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, TextAlignment = TextAlignment.Center, FontSize = 12.5, FlowDirection = FlowDirection.LeftToRight };
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        bar.Children.Add(_zoomText);
        bar.Children.Add(IconButton("zoom-in", Lang.T("Zoom in (Ctrl +)", "تكبير (Ctrl +)"), () => _active?.ZoomBy(1.2)));
        bar.Children.Add(IconButton("fit-width", Lang.T("Fit width (Ctrl 0)", "ملاءمة العرض (Ctrl 0)"), () => _active?.FitWidth()));
        bar.Children.Add(IconButton("fit-page", Lang.T("Fit page (Ctrl 9)", "ملاءمة الصفحة (Ctrl 9)"), () => _active?.FitPage()));
        bar.Children.Add(IconButton("rotate-left", Lang.T("Turn the view left", "تدوير العرض لليسار"), () => _active?.Turn(-1)));
        bar.Children.Add(IconButton("rotate", Lang.T("Turn the view right", "تدوير العرض لليمين"), () => _active?.Turn(1)));
        var theme = IconButton("theme", Lang.T("Page colours: light, sepia, grey, dark, night or your own shade", "ألوان الصفحة: فاتح، بني، رمادي، داكن، ليلي أو درجتك"), () => { });
        theme.Click += (_, _) => ShowThemes(theme);
        bar.Children.Add(theme);
        bar.Children.Add(Separator());

        _searchBox = new TextBox { Width = 150, Style = SettingsPage.Res("Lap.TextBox"), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = Lang.T("Search (Ctrl F). Enter: next, Shift+Enter: previous", "بحث (Ctrl F). Enter: التالي، Shift+Enter: السابق") };
        _searchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _ = SearchStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        };
        bar.Children.Add(SettingsPage.Glyph("search", 15).With(g => g.Margin = new Thickness(2, 0, 6, 0)));
        bar.Children.Add(_searchBox);
        _caseButton = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 34, Height = 32, Content = new TextBlock { Text = "Aa", FontWeight = FontWeights.SemiBold, FontSize = 13 }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text")), ToolTip = Lang.T("Match case", "مطابقة حالة الأحرف") };
        _caseButton.Click += (_, _) => { _matchCase = !_matchCase; SetOn(_caseButton, _matchCase); _searched = ""; _ = SearchStep(1); };
        bar.Children.Add(_caseButton);
        bar.Children.Add(IconButton("lap-chev-up", Lang.T("Previous result (Shift F3)", "النتيجة السابقة (Shift F3)"), () => _ = SearchStep(-1)));
        bar.Children.Add(IconButton("lap-chev-down", Lang.T("Next result (F3)", "النتيجة التالية (F3)"), () => _ = SearchStep(1)));
        _searchCount = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0), FontSize = 12.5, MinWidth = 50, FlowDirection = FlowDirection.LeftToRight };
        _searchCount.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2");
        bar.Children.Add(_searchCount);
        bar.Children.Add(Separator());
        _splitButton = IconButton("split", Lang.T("Split: the same PDF twice, side by side", "تقسيم: الملف نفسه مرتين جنبًا إلى جنب"), ToggleSplit);
        bar.Children.Add(_splitButton);
        _overviewButton = IconButton("overview", Lang.T("Overview: all pages small, for fast scrolling (O)", "نظرة عامة: كل الصفحات صغيرة للتمرير السريع (O)"), ToggleOverview);
        bar.Children.Add(_overviewButton);
        bar.Children.Add(IconButton("present", Lang.T("Present (F5)", "عرض تقديمي (F5)"), Present));
        bar.Children.Add(IconButton("fullscreen", Lang.T("Full screen (F11)", "ملء الشاشة (F11)"), ToggleFullScreen));
        bar.Children.Add(IconButton("moon", Lang.T("Save a dark copy (vector and searchable; photos kept)", "حفظ نسخة داكنة (متجهة وقابلة للبحث؛ تبقى الصور)"), DarkCopy));
        ShowZoom();
        ShowHistory();
        _outlineButton.IsEnabled = _pdf?.Outline.Count > 0;
        return bar;
    }

    private void ShowPage(int index)
    {
        if (_pageBox is null || _pageCount is null || _active is null) return;
        if (!_pageBox.IsKeyboardFocused) _pageBox.Text = (index + 1).ToString(CultureInfo.InvariantCulture);
        _pageCount.Text = "/ " + _active.Pages.Count.ToString(CultureInfo.InvariantCulture);
        if (_pageList is not null && _pageList.SelectedIndex != index && index < _pageList.Items.Count) _pageList.SelectedIndex = index;
    }

    private void ShowZoom()
    {
        if (_zoomText is null || _active is null) return;
        _zoomText.Text = _active.IsOverview ? "—" : $"{Math.Round(_active.Zoom * 100)}%";
        if (_overviewButton is not null) SetOn(_overviewButton, _active.IsOverview);
    }

    private void ShowHistory()
    {
        if (_backButton is null || _forwardButton is null || _active is null) return;
        _backButton.IsEnabled = _active.CanGoBack;
        _forwardButton.IsEnabled = _active.CanGoForward;
    }

    private bool OnPdfKeys(KeyEventArgs e, bool typing)
    {
        if (_pdf is null || _active is null) return false;
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F3) { _ = SearchStep(mods.HasFlag(ModifierKeys.Shift) ? -1 : 1); return true; }
        if (key == Key.F5) { Present(); return true; }
        if (mods == ModifierKeys.Alt && key == Key.Left) { _active.Back(); return true; }
        if (mods == ModifierKeys.Alt && key == Key.Right) { _active.Forward(); return true; }
        if (_session is not null)
        {
            if (mods == ModifierKeys.Control && key == Key.Z) { _session.Undo(); return true; }
            if (mods == ModifierKeys.Control && key == Key.Y) { _session.Redo(); return true; }
            if (mods == ModifierKeys.Control && key == Key.S) { SaveEdits(); return true; }
            if (!typing && key == Key.Delete && _editor?.HasSelection == true) { _editor.DeleteSelected(); return true; }
        }
        if (typing) return false;
        if (mods == ModifierKeys.Control && key is Key.OemPlus or Key.Add) { _active.ZoomBy(1.2); return true; }
        if (mods == ModifierKeys.Control && key is Key.OemMinus or Key.Subtract) { _active.ZoomBy(1 / 1.2); return true; }
        if (mods == ModifierKeys.Control && key is Key.D0 or Key.NumPad0) { _active.FitWidth(); return true; }
        if (mods == ModifierKeys.Control && key is Key.D9 or Key.NumPad9) { _active.FitPage(); return true; }
        if (mods == ModifierKeys.Control && key is Key.D1 or Key.NumPad1) { _active.SetZoom(1); return true; }
        if (mods == ModifierKeys.None)
        {
            switch (key)
            {
                case Key.Home: _active.GoTo(0); return true;
                case Key.End: _active.GoTo(int.MaxValue); return true;
                case Key.PageDown or Key.Space: _active.PageDown(); return true;
                case Key.PageUp: _active.PageUp(); return true;
                case Key.O when _session is null: ToggleOverview(); return true;
            }
        }
        return false;
    }

    // ---- search ------------------------------------------------------------------------------------

    /// <summary>Searches when the words changed, otherwise moves to the next (or previous) result.</summary>
    private async Task SearchStep(int direction)
    {
        if (_searchBox is null || _pdf is null) return;
        var what = _searchBox.Text.Trim();
        if (what.Length == 0) { SetHits([], -1); _searchCount!.Text = ""; return; }
        string key = what + (_matchCase ? "|case" : "");
        if (key != _searched)
        {
            _searched = key;
            _searchCancel?.Cancel();
            var cancel = _searchCancel = new CancellationTokenSource();
            _searchCount!.Text = Lang.T("Searching…", "جارٍ البحث…");
            var pdfs = _session?.Sources.ToList() ?? [_pdf];
            bool matchCase = _matchCase;
            var found = await Task.Run(() =>
            {
                var list = new List<(OpenPdf, int, IReadOnlyList<PdfBox>)>();
                foreach (var pdf in pdfs)
                {
                    if (cancel.IsCancellationRequested) break;
                    try { list.AddRange(pdf.Document.Find(what, matchCase).Select(h => (pdf, h.Page, h.Boxes))); }
                    catch (ProcessingException) { }
                }
                return list;
            });
            if (cancel.IsCancellationRequested) return;
            // Only results on pages that are shown, in the order they are shown.
            var view = _active!;
            _hits = found.Where(h => view.IndexOf(h.Item1, h.Item2) >= 0).OrderBy(h => view.IndexOf(h.Item1, h.Item2)).ToList();
            int start = _hits.Select((h, i) => (h, i)).FirstOrDefault(x => view.IndexOf(x.h.Pdf, x.h.Page) >= view.Current, (default, 0)).i;
            _hit = _hits.Count == 0 ? -1 : start;
        }
        else if (_hits.Count > 0) _hit = (_hit + direction + _hits.Count) % _hits.Count;
        SetHits(_hits, _hit);
        _searchCount!.Text = _hits.Count == 0 ? Lang.T("Not found", "غير موجود") : $"{_hit + 1} / {_hits.Count}";
    }

    private void SetHits(IReadOnlyList<(OpenPdf Pdf, int Page, IReadOnlyList<PdfBox> Boxes)> hits, int current)
    {
        _active?.SetHits(hits, current);
        var other = _active == _view ? _view2 : _view;
        other?.SetHits(hits, -1);
    }

    // ---- outline, themes, split, overview, present -------------------------------------------------

    private void ToggleOutline()
    {
        if (_outlinePanel is null || _pdf is null) return;
        bool show = _outlinePanel.Visibility != Visibility.Visible;
        if (show && _outlinePanel.Child is null)
        {
            var tree = new TreeView { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(6, 8, 6, 8) };
            tree.SetResourceReference(ForegroundProperty, "Brush.Text");
            foreach (var item in _pdf.Outline) tree.Items.Add(OutlineItem(item));
            tree.SelectedItemChanged += (_, e) =>
            {
                if (e.NewValue is TreeViewItem { Tag: PdfOutlineItem o } && o.Page is { } page && _active is not null)
                {
                    int index = _active.IndexOf(_pdf, page);
                    if (index < 0) return;
                    double? y = null;
                    if (o.UserY is { } uy) { var map = _pdf.Map(page); y = Math.Max(0, map.ToDisplay(map.ToUser(0, 0).X, uy).Y); }
                    _active.GoTo(index, y);
                }
            };
            _outlinePanel.Child = new ScrollViewer { Content = tree, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }.With(s => s.SetResourceReference(StyleProperty, "Lap.Scroll"));
        }
        _outlinePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_outlineButton is not null) SetOn(_outlineButton, show);
    }

    private static TreeViewItem OutlineItem(PdfOutlineItem item)
    {
        var node = new TreeViewItem { Header = new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 210, ToolTip = item.Title }, Tag = item, IsExpanded = false };
        node.SetResourceReference(ForegroundProperty, "Brush.Text");
        foreach (var child in item.Children) node.Items.Add(OutlineItem(child));
        return node;
    }

    private void ShowThemes(Button anchor)
    {
        var card = new StackPanel { Margin = new Thickness(14) };
        card.Children.Add(new TextBlock { Text = Lang.T("Page colours", "ألوان الصفحة"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var presets = new WrapPanel { MaxWidth = 300 };
        var names = new Dictionary<string, string>
        {
            ["light"] = Lang.T("Light", "فاتح"), ["sepia"] = Lang.T("Sepia", "بني"), ["grey"] = Lang.T("Grey", "رمادي"),
            ["dark"] = Lang.T("Dark", "داكن"), ["night"] = Lang.T("Night", "ليلي"), ["level"] = Lang.T("My shade", "درجتي"),
        };
        var level = new Slider { Minimum = 0, Maximum = 1, Value = _level, Width = 260, Margin = new Thickness(0, 10, 0, 4), IsEnabled = _themeId == "level", ToolTip = Lang.T("From light to dark", "من الفاتح إلى الداكن") };
        foreach (var (id, name) in names)
        {
            var theme = PageTheme.Find(id, _level, false);
            var swatch = new Border { Width = 26, Height = 18, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromRgb((byte)(theme.Paper >> 16), (byte)(theme.Paper >> 8), (byte)theme.Paper)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0), Child = new TextBlock { Text = "a", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb((byte)(theme.Ink >> 16), (byte)(theme.Ink >> 8), (byte)theme.Ink)) } };
            swatch.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(swatch);
            content.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Style = SettingsPage.Res("Lap.Chip"), Content = content, Margin = new Thickness(0, 0, 6, 6) };
            if (id == _themeId) b.SetResourceReference(BackgroundProperty, "Brush.AccentWash");
            b.Click += (_, _) => { _themeId = id; level.IsEnabled = id == "level"; ApplyTheme(); foreach (var other in presets.Children.OfType<Button>()) other.ClearValue(BackgroundProperty); b.SetResourceReference(BackgroundProperty, "Brush.AccentWash"); };
            presets.Children.Add(b);
        }
        card.Children.Add(presets);
        level.ValueChanged += (_, _) => { _level = level.Value; if (_themeId == "level") ApplyTheme(); };
        card.Children.Add(level);
        var keep = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var sw = SettingsPage.Switch(_keepPictures, on => { _keepPictures = on; ApplyTheme(); });
        DockPanel.SetDock(sw, Dock.Right);
        keep.Children.Add(sw);
        keep.Children.Add(new TextBlock { Text = Lang.T("Keep photos as they are", "إبقاء الصور كما هي"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        card.Children.Add(keep);
        var border = new Border { Child = card, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(8), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 4, Direction = 270, Opacity = 0.4 } };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        border.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        new Popup { Child = border, PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, IsOpen = true };
    }

    private void ApplyTheme()
    {
        var theme = Theme;
        if (_view is not null) _view.Theme = theme;
        if (_view2 is not null) _view2.Theme = theme;
    }

    private void ToggleSplit()
    {
        var host = ViewsHost;
        if (host is null || _view is null) return;
        if (_view2 is null)
        {
            _view2 = NewView();
            host.ColumnDefinitions.Clear();
            host.ColumnDefinitions.Add(new ColumnDefinition());
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            host.ColumnDefinitions.Add(new ColumnDefinition());
            var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
            splitter.SetResourceReference(BackgroundProperty, "Brush.Line");
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(_view2, 2);
            host.Children.Add(splitter);
            host.Children.Add(_view2);
            _view2.SetPages(_view.Pages, _view.Current);
            if (_session is not null) AttachEditor(_view2);
            _view2.SetHits(_hits, -1);
        }
        else
        {
            host.Children.Clear();
            host.ColumnDefinitions.Clear();
            host.Children.Add(_view);
            _view2 = null;
            _active = _view;
        }
        if (_splitButton is not null) SetOn(_splitButton, _view2 is not null);
    }

    private void ToggleOverview()
    {
        if (_active is null) return;
        _active.SetOverview(!_active.IsOverview);
    }

    private void Present()
    {
        if (_active is null || _active.Pages.Count == 0) return;
        new PresentWindow(_active.Pages, _active.Current, Theme).Show();
    }

    private void DarkCopy()
    {
        if (_pdf is null) return;
        _dispatcher.Dispatch(CapabilityMap.Ids.PdfDark, [_pdf.Path], ScreenGeometry.CursorPosition());
    }

    // ---- edit mode ---------------------------------------------------------------------------------

    private readonly Dictionary<EditTool, Button> _toolButtons = [];

    private void EnterEdit(EditTool tool)
    {
        if (_pdf is null || _view is null) return;
        if (_session is null)
        {
            _session = new PdfEditSession(_pdf);
            _session.Changed += _ => { RefreshPages(); ShowEditState(); };
            AttachEditor(_view);
            if (_view2 is not null) AttachEditor(_view2);
            _view.SetPages(_session.Refs, _view.Current);
            _view2?.SetPages(_session.Refs, _view2.Current);
        }
        _toolbar.Content = EditToolbar();
        SetEdit(Lang.T("Done", "تم"), true);
        SetTool(tool);
        ShowEditState();
    }

    private void AttachEditor(PdfView view)
    {
        if (_session is null) return;
        var controller = new PdfEditController(view, _session);
        controller.ToolChanged += () => SetTool(controller.Tool);
        view.Editing = true;
        if (view == _view) _editor = controller;
        else _editor2 = controller;
    }

    private PdfEditController? _editor2;

    private void LeaveEdit()
    {
        if (!ConfirmLeaveEdits()) return;
        _session = null;
        _editor = _editor2 = null;
        foreach (var v in new[] { _view, _view2 })
        {
            if (v is null) continue;
            v.Editing = false;
            v.EditCursor = null;
            v.SetPages(Enumerable.Range(1, _pdf!.PageCount).Select(p => new PageRef(_pdf, p)).ToList(), 0);
        }
        TogglePages(false);
        _toolbar.Content = PdfToolbar();
        SetEdit(Lang.T("Edit", "تعديل"), true);
        _subtitle.Text = $"PDF · {_pdf!.PageCount} {(_pdf.PageCount == 1 ? Lang.T("page", "صفحة") : Lang.T("pages", "صفحات"))}";
    }

    /// <summary>True when it is fine to leave the editor: nothing unsaved, or the user chose to save or discard.</summary>
    private bool ConfirmLeaveEdits()
    {
        if (_session is null || !_session.Dirty || _session.IsUnchanged) return true;
        var answer = MessageBox.Show(this, Lang.T("Save your changes to this PDF as a new file before leaving?", "هل تريد حفظ تغييراتك على هذا الملف كملف جديد قبل المغادرة؟"),
            Lang.T("Edit PDF", "تعديل PDF"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) SaveEdits();
        return true;
    }

    private UIElement EditToolbar()
    {
        var bar = new WrapPanel();
        _toolButtons.Clear();
        foreach (var (tool, icon, tip) in new[]
        {
            (EditTool.Select, "pointer", Lang.T("Select: move, resize (corner) or delete what you added; double-click text to change it", "تحديد: تحريك ما أضفته أو تغيير حجمه (الزاوية) أو حذفه؛ انقر مرتين على النص لتعديله")),
            (EditTool.Text, "text", Lang.T("Text: click where it goes (Ctrl+Enter or a click outside to finish)", "نص: انقر حيث يوضع (Ctrl+Enter أو نقرة خارجه للإنهاء)")),
            (EditTool.Pen, "pen", Lang.T("Pen", "قلم")),
            (EditTool.Highlight, "highlighter", Lang.T("Highlighter", "قلم تمييز")),
            (EditTool.Cover, "cover", Lang.T("White cover: drag over something to hide it (then type over it)", "غطاء أبيض: اسحب فوق شيء لإخفائه (ثم اكتب فوقه)")),
        })
        {
            var b = IconButton(icon, tip, () => SetTool(tool));
            _toolButtons[tool] = b;
            bar.Children.Add(b);
        }
        bar.Children.Add(IconButton("image", Lang.T("Picture: choose one, then click where it goes", "صورة: اختر صورة ثم انقر حيث توضع"), ChoosePicture));
        bar.Children.Add(IconButton("sign", Lang.T("Signature: draw, type or choose one, then click where it goes", "توقيع: ارسم أو اكتب أو اختر توقيعًا ثم انقر حيث يوضع"), Sign));
        bar.Children.Add(Separator());
        foreach (var colour in new[] { Color.FromRgb(0x14, 0x14, 0x18), Color.FromRgb(0x1D, 0x4E, 0xD8), Color.FromRgb(0xDC, 0x26, 0x26), Color.FromRgb(0x15, 0x80, 0x3D) })
        {
            var swatch = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 28, Height = 32, Content = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(colour) }, ToolTip = Lang.T("Colour for text and pen", "لون النص والقلم") };
            swatch.Click += (_, _) => { if (_editor is not null) _editor.Colour = colour; if (_editor2 is not null) _editor2.Colour = colour; };
            bar.Children.Add(swatch);
        }
        foreach (var (label, font, pen) in new[] { ("S", 11.0, 1.4), ("M", 14.0, 2.2), ("L", 20.0, 3.6) })
        {
            var b = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 30, Height = 32, Content = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, FontSize = 12 }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text")), ToolTip = Lang.T("Size of text and pen", "حجم النص والقلم") };
            b.Click += (_, _) => { foreach (var c in new[] { _editor, _editor2 }) if (c is not null) { c.FontSize = font; c.PenWidth = pen; } };
            bar.Children.Add(b);
        }
        bar.Children.Add(Separator());
        var pages = IconButton("pages", Lang.T("Pages: reorder, turn, remove, add from another PDF", "الصفحات: ترتيب، تدوير، حذف، إضافة من ملف آخر"), () => TogglePages(_pagesPanel?.Visibility != Visibility.Visible));
        bar.Children.Add(pages);
        _undoButton = IconButton("undo", Lang.T("Undo (Ctrl Z)", "تراجع (Ctrl Z)"), () => _session?.Undo());
        _redoButton = IconButton("repeat", Lang.T("Redo (Ctrl Y)", "إعادة (Ctrl Y)"), () => _session?.Redo());
        bar.Children.Add(_undoButton);
        bar.Children.Add(_redoButton);
        bar.Children.Add(IconButton("zoom-out", Lang.T("Zoom out", "تصغير"), () => _active?.ZoomBy(1 / 1.2)));
        bar.Children.Add(IconButton("zoom-in", Lang.T("Zoom in", "تكبير"), () => _active?.ZoomBy(1.2)));
        bar.Children.Add(IconButton("moon", Lang.T("Save a dark copy of the original", "حفظ نسخة داكنة من الأصل"), DarkCopy));
        bar.Children.Add(Separator());
        var save = SettingsPage.Button(Lang.T("Save as new PDF", "حفظ كملف PDF جديد"), SaveEdits, accent: true);
        save.ToolTip = Lang.T("Saved beside the original (Ctrl S); the original is not changed", "يُحفظ بجانب الأصل (Ctrl S)؛ لا يتغير الأصل");
        bar.Children.Add(save);
        return bar;
    }

    private Button? _undoButton, _redoButton;

    private void SetTool(EditTool tool)
    {
        foreach (var c in new[] { _editor, _editor2 }) if (c is not null) c.Tool = tool;
        foreach (var (t, b) in _toolButtons) SetOn(b, t == tool);
    }

    private void ShowEditState()
    {
        if (_session is null) return;
        if (_undoButton is not null) _undoButton.IsEnabled = _session.CanUndo;
        if (_redoButton is not null) _redoButton.IsEnabled = _session.CanRedo;
        int marks = _session.Pages.Sum(p => p.Marks.Count);
        _subtitle.Text = Lang.T($"Editing · {_session.Pages.Count} pages · {marks} added", $"تعديل · {_session.Pages.Count} صفحات · أُضيف {marks}") + (_session.Dirty && !_session.IsUnchanged ? Lang.T(" · not saved", " · غير محفوظ") : "");
    }

    private void ChoosePicture()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Lang.T("Pictures", "صور") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp", Title = Lang.T("Choose a picture to place", "اختر صورة لوضعها") };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            if (IOPath.GetExtension(dialog.FileName).ToLowerInvariant() is ".webp" or ".gif" or ".bmp") bytes = ToPng(bytes);
            PlacePicture(bytes);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException)
        {
            _dispatcher.Notice(Lang.T("Picture", "صورة"), ex.Message, null, warning: true);
        }
    }

    private void Sign()
    {
        var pad = new SignaturePad { Owner = this };
        pad.ShowDialog();
        if (pad.Result is { } png) PlacePicture(png);
    }

    private void PlacePicture(byte[] bytes)
    {
        foreach (var c in new[] { _editor, _editor2 }) if (c is not null) c.PendingPicture = bytes;
        SetTool(EditTool.Picture);
        _subtitle.Text = Lang.T("Click on a page to place it", "انقر على صفحة لوضعها");
    }

    private static byte[] ToPng(byte[] bytes)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(decoder.Frames[0]));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private void SaveEdits()
    {
        if (_session is null || _pdf is null) return;
        _editor?.CommitText();
        _editor2?.CommitText();
        if (_session.IsUnchanged)
        {
            _dispatcher.Notice(Lang.T("Nothing to save", "لا شيء للحفظ"), Lang.T("Add something or change the pages first.", "أضف شيئًا أو غيّر الصفحات أولًا."), ScreenGeometry.CursorPosition(), warning: false);
            return;
        }
        var options = new ActionOptions { ["plan"] = _session.ToPlan().ToJson() };
        _dispatcher.Start(CapabilityMap.Get(CapabilityMap.Ids.PdfSaveEdits), [_pdf.Path], options, ScreenGeometry.CursorPosition(), Lang.T("Edit PDF", "تعديل PDF"));
        _session.MarkSaved();
        ShowEditState();
    }

    // ---- pages panel -------------------------------------------------------------------------------

    private void TogglePages(bool show)
    {
        if (_pagesPanel is null) return;
        if (show && _session is null) EnterEdit(EditTool.Select);
        _pagesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) BuildPagesPanel();
    }

    private void BuildPagesPanel()
    {
        if (_pagesPanel is null || _session is null) return;
        var root = new DockPanel { Margin = new Thickness(8) };
        var buttons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel.SetDock(buttons, Dock.Bottom);
        int Selected() => Math.Max(0, _pageList?.SelectedIndex ?? 0);
        buttons.Children.Add(IconButton("up", Lang.T("Move up", "نقل لأعلى"), () => MovePage(Selected(), -1)));
        buttons.Children.Add(IconButton("down", Lang.T("Move down", "نقل لأسفل"), () => MovePage(Selected(), 1)));
        buttons.Children.Add(IconButton("rotate", Lang.T("Turn right", "تدوير لليمين"), () => { int i = Selected(); _session.Do(p => p[i].Turns = (p[i].Turns + 1) & 3, structural: true); }));
        buttons.Children.Add(IconButton("trash", Lang.T("Remove page", "حذف الصفحة"), () =>
        {
            int i = Selected();
            if (_session.Pages.Count <= 1) return;
            _session.Do(p => p.RemoveAt(i), structural: true);
        }));
        buttons.Children.Add(IconButton("lap-plus", Lang.T("Add pages from another PDF (after this page)", "إضافة صفحات من ملف PDF آخر (بعد هذه الصفحة)"), () => _ = AddPdf(Selected() + 1)));
        root.Children.Add(buttons);
        _pageList = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, FlowDirection = FlowDirection.LeftToRight };
        ScrollViewer.SetHorizontalScrollBarVisibility(_pageList, ScrollBarVisibility.Disabled);
        _pageList.SelectionChanged += (_, _) => { if (_pageList.SelectedIndex >= 0 && _active is not null && _active.Current != _pageList.SelectedIndex) _active.GoTo(_pageList.SelectedIndex); };
        root.Children.Add(_pageList);
        _pagesPanel.Child = root;
        RefreshPages();
    }

    private void MovePage(int i, int by)
    {
        if (_session is null) return;
        int j = i + by;
        if (j < 0 || j >= _session.Pages.Count) return;
        _session.Do(p => (p[i], p[j]) = (p[j], p[i]), structural: true);
        _pageList!.SelectedIndex = j;
    }

    private async Task AddPdf(int at)
    {
        if (_session is null) return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PDF|*.pdf", Title = Lang.T("Add pages from a PDF", "إضافة صفحات من ملف PDF"), Multiselect = true };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var pdf = await Task.Run(() => OpenPdf.Open(file));
                _extraPdfs.Add(pdf);
                _session.AddSource(pdf, at);
                at += pdf.PageCount;
            }
            catch (ProcessingException ex)
            {
                _dispatcher.Notice(IOPath.GetFileName(file), ex.Message, null, warning: true);
            }
        }
    }

    /// <summary>Thumbnails of the pages being edited, in their order and turns.</summary>
    private async void RefreshPages()
    {
        if (_pageList is null || _session is null || _pagesPanel?.Visibility != Visibility.Visible) return;
        int keep = _pageList.SelectedIndex;
        var pages = _session.Pages.Select(p => (p.Pdf, p.Page, p.Turns, Marks: p.Marks.Count)).ToList();
        _pageList.Items.Clear();
        var images = new List<Image>();
        for (int i = 0; i < pages.Count; i++)
        {
            var image = new Image { Height = 120, Stretch = Stretch.Uniform, LayoutTransform = pages[i].Turns == 0 ? Transform.Identity : new RotateTransform(90 * pages[i].Turns) };
            images.Add(image);
            var label = $"{i + 1}" + (pages[i].Pdf != _session.Main ? $" · {pages[i].Pdf.Name}" : "") + (pages[i].Marks > 0 ? " ✎" : "");
            var item = new StackPanel { Margin = new Thickness(4, 6, 4, 6), HorizontalAlignment = HorizontalAlignment.Center };
            item.Children.Add(new Border { Child = image, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 1, Opacity = 0.3 } });
            item.Children.Add(new TextBlock { Text = label, FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150 }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2")));
            _pageList.Items.Add(item);
        }
        if (keep >= 0 && keep < pages.Count) _pageList.SelectedIndex = keep;
        for (int i = 0; i < pages.Count; i++)
        {
            var (pdf, page, _, _) = pages[i];
            var size = pdf.Size(page);
            int w = 110, h = Math.Max(8, (int)(w * size.Height / size.Width));
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    var px = pdf.Document.RenderBgra(page, w, h);
                    var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, px, w * 4);
                    b.Freeze();
                    return b;
                });
                if (i < images.Count) images[i].Source = bitmap;
            }
            catch (ProcessingException) { }
            if (_pageList is null || _pageList.Items.Count != pages.Count) return; // rebuilt meanwhile
        }
    }
}
