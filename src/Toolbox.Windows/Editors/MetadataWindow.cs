using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Metadata;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Studio;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Editors;

/// <summary>
/// Shows what a file carries besides its content, grouped in plain language with the sensitive
/// parts highlighted, and saves a copy without the chosen categories.
/// </summary>
internal sealed class MetadataWindow : GlassWindow
{
    private readonly IReadOnlyList<string> _paths;
    private readonly ActionDispatcher _dispatcher;
    private readonly Native.POINT? _anchor;
    private readonly StackPanel _groups = new();
    private readonly Dictionary<MetadataCategories, CheckBox> _checks = new();
    private readonly RadioButton _selected = new() { Content = "Remove the selected kinds", IsChecked = true };
    private readonly RadioButton _everything = new() { Content = "Remove everything (keeps the colour profile and orientation)" };
    private readonly Button _save = new() { Content = "Save clean copy", MinWidth = 120, IsEnabled = false };
    private readonly TextBlock _footer = new();

    public MetadataWindow(IReadOnlyList<string> paths, ActionDispatcher dispatcher, Native.POINT? anchor)
    {
        _paths = paths;
        _dispatcher = dispatcher;
        _anchor = anchor;
        Title = "Metadata";
        Width = 560;
        Height = 640;
        Resizable = true;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build();
        Loaded += async (_, _) => await LoadAsync();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Style = StudioWindow.Res("Toolbox.Button.Icon"), Content = StudioWindow.IconPath("close", 12), ToolTip = "Close" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "Metadata", Style = StudioWindow.Res("Text.Title"), FontSize = 16 });
        titles.Children.Add(new TextBlock
        {
            Text = _paths.Count == 1 ? IOPath.GetFileName(_paths[0]) : $"{_paths.Count} files (showing the first)",
            Style = StudioWindow.Res("Text.Caption"),
        });
        header.Children.Add(titles);
        MakeDragHandle(header);
        root.Children.Add(header);

        var modes = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var rb in new[] { _selected, _everything })
        {
            rb.Style = StudioWindow.Res("Toolbox.Radio");
            rb.GroupName = "mode";
            rb.Margin = new Thickness(0, 0, 0, 6);
            rb.Checked += (_, _) => UpdateState();
            modes.Children.Add(rb);
        }
        Grid.SetRow(modes, 1);
        root.Children.Add(modes);

        var scroll = new ScrollViewer { Content = _groups, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _groups.Children.Add(new TextBlock { Text = "Reading metadata…", Style = StudioWindow.Res("Text.Caption") });
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0), MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        _save.Style = StudioWindow.Res("Toolbox.Button.Accent");
        _save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(_save);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        _footer.Style = StudioWindow.Res("Text.Caption");
        _footer.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_footer);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private async Task LoadAsync()
    {
        var file = FileProbe.Probe(_paths[0]);
        try
        {
            var groups = await Task.Run(() => MetadataInspector.Inspect(file));
            Render(file, groups);
        }
        catch (ProcessingException ex)
        {
            _groups.Children.Clear();
            _groups.Children.Add(new TextBlock { Text = ex.Message, Style = StudioWindow.Res("Text.Caption") });
        }
    }

    private void Render(ProbedFile file, IReadOnlyList<MetadataGroup> groups)
    {
        _groups.Children.Clear();
        bool removable = groups.Any(g => g.RemovedBy != MetadataCategories.None);
        if (!removable)
        {
            _groups.Children.Add(new TextBlock { Text = "No removable metadata was found in this file.", Margin = new Thickness(0, 6, 0, 6) });
        }
        foreach (var group in groups)
        {
            var card = new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Margin = new Thickness(0, 0, 0, 8) };
            var stack = new StackPanel();
            var head = new DockPanel();
            if (group.RemovedBy is not MetadataCategories.None and not MetadataCategories.All)
            {
                var check = new CheckBox { Content = "Remove", IsChecked = group.Sensitive, VerticalAlignment = VerticalAlignment.Center };
                check.Checked += (_, _) => UpdateState();
                check.Unchecked += (_, _) => UpdateState();
                _checks[group.RemovedBy] = check;
                DockPanel.SetDock(check, Dock.Right);
                head.Children.Add(check);
            }
            else
            {
                var note = new TextBlock { Text = group.RemovedBy == MetadataCategories.None ? "Kept" : "Removed by \"everything\"", Style = StudioWindow.Res("Text.Caption"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(note, Dock.Right);
                head.Children.Add(note);
            }
            var title = new TextBlock { Text = group.Title, FontWeight = FontWeights.SemiBold };
            if (group.Sensitive) title.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Warning");
            head.Children.Add(title);
            stack.Children.Add(head);
            stack.Children.Add(new TextBlock { Text = group.Explanation, Style = StudioWindow.Res("Text.Caption"), Margin = new Thickness(0, 2, 0, 6) });
            foreach (var item in group.Items.Take(12))
            {
                var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(new TextBlock { Text = item.Name, Style = StudioWindow.Res("Text.Label"), TextTrimming = TextTrimming.CharacterEllipsis });
                var value = new TextBlock { Text = item.Value, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                stack.Children.Add(row);
            }
            card.Child = stack;
            _groups.Children.Add(card);
        }
        _groups.Children.Add(new TextBlock
        {
            Text = "Metadata removal does not change what is visible. Names, faces or addresses shown in the picture or document stay as they are.",
            Style = StudioWindow.Res("Text.Caption"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        _footer.Text = file.Kind switch
        {
            FileKind.Jpeg => "JPEG: the picture data is copied unchanged.",
            FileKind.Png or FileKind.Tiff => "Lossless format: the picture is unchanged.",
            FileKind.WebP or FileKind.Avif => $"{file.Kind.Label()}: re-encoded at high quality to remove metadata.",
            FileKind.Pdf => "PDF: document information and XMP are removed; pages are unchanged.",
            _ => "",
        };
        if (file.Kind == FileKind.Pdf)
        {
            _everything.IsChecked = true;
            _selected.IsEnabled = false;
        }
        UpdateState();
    }

    private string RemoveSpec()
    {
        if (_everything.IsChecked == true) return "all";
        var parts = new List<string>();
        foreach (var (category, check) in _checks)
            if (check.IsChecked == true) parts.Add(category.ToString().ToLowerInvariant());
        return string.Join(",", parts);
    }

    private void UpdateState()
    {
        foreach (var check in _checks.Values) check.IsEnabled = _everything.IsChecked != true;
        var spec = RemoveSpec();
        _save.IsEnabled = spec.Length > 0;
    }

    private void Save()
    {
        var spec = RemoveSpec();
        if (spec.Length == 0) return;
        _dispatcher.Start(CapabilityMap.Get(CapabilityMap.Ids.Metadata), _paths, new ActionOptions { ["remove"] = spec }, _anchor ?? ScreenGeometry.CursorPosition(), "Remove metadata");
        Close();
    }
}
