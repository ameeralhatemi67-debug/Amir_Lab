using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Studio;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.CommandPalette;

/// <summary>
/// The keyboard-first way into every action, and a conventional drop zone: add files by dropping,
/// browsing or pasting, type to filter actions, Enter to run. Same processing as the wheel.
/// </summary>
internal sealed class PaletteWindow : GlassWindow
{
    private readonly ActionDispatcher _dispatcher;
    private readonly ToolboxHost _host;
    private readonly List<string> _files = [];
    private readonly WrapPanel _chips = new();
    private readonly TextBlock _dropHint = new();
    private readonly TextBox _search = new();
    private readonly ListBox _list = new();
    private readonly TextBlock _status = new();
    private readonly Border _dropZone = new();
    private string? _preselect;

    public PaletteWindow(ActionDispatcher dispatcher, ToolboxHost host)
    {
        _dispatcher = dispatcher;
        _host = host;
        Title = "File Toolbox";
        Width = 600;
        Height = 620;
        MinHeight = 420;
        Resizable = true;
        AllowDrop = true;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = Shell.AppIcon.Render(64, ThemeResources.Current.Accent);
        Content = Build();
        DragEnter += OnDragOverWindow;
        DragOver += OnDragOverWindow;
        DragLeave += (_, _) => HighlightDrop(false);
        Drop += OnDrop;
        PreviewKeyDown += OnKeys;
        Loaded += (_, _) => { Refresh(); _search.Focus(); };
    }

    public IReadOnlyList<string> Files => _files;

    public void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (!_files.Contains(path, StringComparer.OrdinalIgnoreCase) && (File.Exists(path) || Directory.Exists(path)))
                _files.Add(path);
        Refresh();
    }

    /// <summary>Puts this action first and selects it (used by "Repeat last action").</summary>
    public void Preselect(string? actionId)
    {
        _preselect = actionId;
        Refresh();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 12) };
        var close = new Button { Style = StudioWindow.Res("Toolbox.Button.Icon"), Content = StudioWindow.IconPath("close", 12), ToolTip = "Close (Esc)" };
        close.Click += (_, _) => Close();
        var settings = new Button { Style = StudioWindow.Res("Toolbox.Button.Subtle"), Content = "Settings", Margin = new Thickness(0, 0, 4, 0) };
        settings.Click += (_, _) => App.ShowSettings();
        var history = new Button { Style = StudioWindow.Res("Toolbox.Button.Subtle"), Content = "History", Margin = new Thickness(0, 0, 4, 0) };
        history.Click += (_, _) => App.ShowHistory();
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(history);
        right.Children.Add(settings);
        right.Children.Add(close);
        DockPanel.SetDock(right, Dock.Right);
        header.Children.Add(right);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "File Toolbox", Style = StudioWindow.Res("Text.Title"), FontSize = 16 });
        titles.Children.Add(new TextBlock { Text = "Tip: drag a file anywhere and tap Shift for the wheel.", Style = StudioWindow.Res("Text.Caption") });
        header.Children.Add(titles);
        MakeDragHandle(header);
        root.Children.Add(header);

        // Drop zone
        _dropZone.CornerRadius = new CornerRadius(10);
        _dropZone.BorderThickness = new Thickness(1.5);
        _dropZone.Padding = new Thickness(14, 12, 14, 12);
        _dropZone.SetResourceReference(Border.BorderBrushProperty, "Brush.BorderStrong");
        _dropZone.SetResourceReference(Border.BackgroundProperty, "Brush.Card");
        var dropStack = new StackPanel();
        _dropHint.Text = "Drop files here";
        _dropHint.FontWeight = FontWeights.SemiBold;
        var links = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        var browse = new Button { Content = "Browse…", Margin = new Thickness(0, 0, 8, 0), ToolTip = "Ctrl+O" };
        browse.Click += (_, _) => Browse();
        var paste = new Button { Content = "Paste", Margin = new Thickness(0, 0, 8, 0), ToolTip = "Ctrl+V: files or an image from the clipboard" };
        paste.Click += (_, _) => Paste();
        var clear = new Button { Content = "Clear", Style = StudioWindow.Res("Toolbox.Button.Subtle") };
        clear.Click += (_, _) => { _files.Clear(); Refresh(); };
        links.Children.Add(browse);
        links.Children.Add(paste);
        links.Children.Add(clear);
        dropStack.Children.Add(_dropHint);
        dropStack.Children.Add(_chips);
        dropStack.Children.Add(links);
        _dropZone.Child = dropStack;
        Grid.SetRow(_dropZone, 1);
        root.Children.Add(_dropZone);

        // Search
        _search.Margin = new Thickness(0, 12, 0, 8);
        _search.Padding = new Thickness(10, 7, 10, 7);
        _search.FontSize = 14;
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search actions");
        _search.TextChanged += (_, _) => Refresh();
        var searchGrid = new Grid();
        searchGrid.Children.Add(_search);
        var placeholder = new TextBlock { Text = "Type an action — webp, crop, compress, pages…", IsHitTestVisible = false, Margin = new Thickness(13, 19, 0, 0), Style = StudioWindow.Res("Text.Caption") };
        _search.TextChanged += (_, _) => placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        searchGrid.Children.Add(placeholder);
        Grid.SetRow(searchGrid, 2);
        root.Children.Add(searchGrid);

        // Actions
        _list.MouseDoubleClick += (_, _) => RunSelected();
        System.Windows.Automation.AutomationProperties.SetName(_list, "Actions");
        Grid.SetRow(_list, 3);
        root.Children.Add(_list);

        _status.Style = StudioWindow.Res("Text.Caption");
        _status.Margin = new Thickness(0, 8, 0, 0);
        _status.Text = "Enter runs the selected action · Esc closes";
        Grid.SetRow(_status, 4);
        root.Children.Add(_status);

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private void Refresh()
    {
        _chips.Children.Clear();
        var probed = FileProbe.ProbeAll(_files);
        foreach (var file in probed) _chips.Children.Add(Chip(file));
        _dropHint.Text = _files.Count == 0
            ? "Drop files here"
            : $"{_files.Count} file{(_files.Count == 1 ? "" : "s")} · {Bytes.Format(probed.Sum(f => f.SizeBytes))}";
        _chips.Margin = new Thickness(0, _files.Count > 0 ? 8 : 0, 0, 0);

        var query = _search.Text.Trim();
        var rows = new List<(ActionDefinition Action, Applicability Verdict)>();
        foreach (var action in CapabilityMap.All.Where(a => a.Id != CapabilityMap.Ids.StudioExport))
        {
            if (query.Length > 0 && !Matches(action, query)) continue;
            var verdict = _files.Count == 0 ? new Applicability(action, 0, 0, "Add files first") : CapabilityMap.Evaluate(action, probed);
            rows.Add((action, verdict));
        }
        var ordered = rows
            .OrderByDescending(r => r.Action.Id == _preselect)
            .ThenByDescending(r => r.Action.Id == _host.LastAction?.ActionId)
            .ThenByDescending(r => r.Verdict.IsEnabled)
            .ThenBy(r => r.Action.Group)
            .ToList();

        _list.Items.Clear();
        foreach (var (action, verdict) in ordered) _list.Items.Add(Row(action, verdict));
        var firstEnabled = _list.Items.Cast<ListBoxItem>().FirstOrDefault(i => i.IsEnabled);
        if (firstEnabled is not null) _list.SelectedItem = firstEnabled;
    }

    private static bool Matches(ActionDefinition action, string query)
    {
        var haystack = $"{action.Label} {action.Title} {action.Meaning} {action.Id} {Synonyms(action.Id)}";
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(word => haystack.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    private static string Synonyms(string id) => id switch
    {
        CapabilityMap.Ids.Compress => "shrink smaller reduce size email upload limit",
        CapabilityMap.Ids.Background => "frame border padding shadow gradient post slide",
        CapabilityMap.Ids.Crop => "trim cut aspect square rotate",
        CapabilityMap.Ids.Metadata => "exif gps location privacy strip clean",
        CapabilityMap.Ids.Redact => "hide blur censor black box",
        CapabilityMap.Ids.Annotate => "arrow draw markup text highlight",
        CapabilityMap.Ids.PdfToJpg or CapabilityMap.Ids.PdfToPng => "pages export image picture",
        CapabilityMap.Ids.ImagesToPdf => "combine scan merge",
        _ => "",
    };

    private ListBoxItem Row(ActionDefinition action, Applicability verdict)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Path { Data = Icons.Get(action.Icon), Width = 16, Height = 16, Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        icon.SetResourceReference(Shape.StrokeProperty, verdict.IsEnabled ? "Brush.Accent" : "Brush.TextDisabled");
        grid.Children.Add(icon);
        var text = new StackPanel();
        var title = new TextBlock { Text = action.Title, FontWeight = FontWeights.SemiBold };
        if (!verdict.IsEnabled) title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.TextDisabled");
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = verdict.IsEnabled ? action.Meaning : verdict.Reason ?? action.Meaning,
            Style = StudioWindow.Res("Text.Caption"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var tag = new TextBlock
        {
            Text = action.Id == _host.LastAction?.ActionId ? "Last used" : verdict.IsPartial ? $"{verdict.Applicable} of {verdict.Total}" : action.Mode == ActionMode.Editor ? "Editor" : action.Group.ToString(),
            Style = StudioWindow.Res("Text.Caption"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tag, 2);
        grid.Children.Add(tag);
        var item = new ListBoxItem { Content = grid, Tag = action.Id, IsEnabled = verdict.IsEnabled, ToolTip = action.Limitations.Count > 0 ? string.Join("\n", action.Limitations) : null };
        System.Windows.Automation.AutomationProperties.SetName(item, action.Title);
        return item;
    }

    private Border Chip(ProbedFile file)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = file.Name, FontSize = 12, MaxWidth = 200, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var remove = new Button { Style = StudioWindow.Res("Toolbox.Button.Icon"), Width = 20, Height = 20, Content = StudioWindow.IconPath("close", 8), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Remove" };
        remove.Click += (_, _) => { _files.Remove(file.Path); Refresh(); };
        panel.Children.Add(remove);
        var chip = new Border { Child = panel, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 2, 2), Margin = new Thickness(0, 0, 6, 6), BorderThickness = new Thickness(1), ToolTip = file.Problem ?? file.Path };
        chip.SetResourceReference(Border.BackgroundProperty, file.IsUsable ? "Brush.AccentSoft" : "Brush.Control");
        chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return chip;
    }

    private void RunSelected()
    {
        if (_list.SelectedItem is not ListBoxItem { IsEnabled: true, Tag: string id }) return;
        var files = _files.ToList();
        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        var anchor = new Native.POINT { X = (int)center.X, Y = (int)center.Y };
        Close();
        _dispatcher.Dispatch(id, files, anchor);
    }

    private void OnKeys(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Enter:
                RunSelected();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when _list.Items.Count > 0:
                MoveSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.V when ctrl && _search.Text.Length == 0:
                Paste();
                e.Handled = true;
                break;
            case Key.O when ctrl:
                Browse();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var items = _list.Items.Cast<ListBoxItem>().ToList();
        int index = _list.SelectedItem is ListBoxItem current ? items.IndexOf(current) : -1;
        for (int i = index + delta; i >= 0 && i < items.Count; i += delta)
        {
            if (!items[i].IsEnabled) continue;
            _list.SelectedItem = items[i];
            _list.ScrollIntoView(items[i]);
            return;
        }
    }

    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Supported files|*.pdf;*.jpg;*.jpeg;*.png;*.webp;*.avif;*.heic;*.heif;*.tif;*.tiff;*.gif;*.bmp;*.svg|All files|*.*",
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private void Paste()
    {
        var files = ClipboardImport.Read(out var note);
        if (note is not null) _status.Text = note;
        else if (files.Count == 0) _status.Text = "The clipboard has no files or image.";
        AddFiles(files);
    }

    private void OnDragOverWindow(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        HighlightDrop(files);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        HighlightDrop(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
        e.Effects = DragDropEffects.Copy; // never Move: the originals stay where they are
        e.Handled = true;
    }

    private void HighlightDrop(bool on)
    {
        _dropZone.SetResourceReference(Border.BorderBrushProperty, on ? "Brush.Accent" : "Brush.BorderStrong");
        _dropZone.SetResourceReference(Border.BackgroundProperty, on ? "Brush.AccentSoft" : "Brush.Card");
    }
}
