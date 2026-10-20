using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.History;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Studio;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Settings;

/// <summary>Recent jobs: what ran, on which files, what it produced; show the results or run it again.</summary>
internal sealed class HistoryWindow : GlassWindow
{
    private readonly ToolboxHost _host;
    private readonly ActionDispatcher _dispatcher;
    private readonly StackPanel _list = new();

    public HistoryWindow(ToolboxHost host, ActionDispatcher dispatcher)
    {
        _host = host;
        _dispatcher = dispatcher;
        Title = "File Toolbox history";
        Width = 640;
        Height = 620;
        MinHeight = 320;
        Resizable = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = Build();
        Loaded += (_, _) => Fill();
    }

    private UIElement Build()
    {
        var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Style = StudioWindow.Res("Toolbox.Button.Icon"), Content = StudioWindow.IconPath("close", 12), ToolTip = "Close" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = "History", Style = StudioWindow.Res("Text.Title"), FontSize = 16 });
        titles.Children.Add(new TextBlock { Text = $"Kept for {_host.Settings.Current.HistoryRetentionDays} days on this computer. Paths and outcomes only — never file contents.", Style = StudioWindow.Res("Text.Caption") });
        header.Children.Add(titles);
        MakeDragHandle(header);
        root.Children.Add(header);

        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var clear = new Button { Content = "Clear history", Margin = new Thickness(0, 0, 8, 0) };
        clear.Click += (_, _) => { _host.History.Clear(); Fill(); };
        var ok = new Button { Content = "Close", Style = StudioWindow.Res("Toolbox.Button.Accent"), MinWidth = 80 };
        ok.Click += (_, _) => Close();
        footer.Children.Add(clear);
        footer.Children.Add(ok);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        var border = new Border { Child = root };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private void Fill()
    {
        _list.Children.Clear();
        var entries = _host.History.ReadRecent(200);
        if (entries.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "Nothing yet. Results of conversions and edits appear here.", Style = StudioWindow.Res("Text.Caption") });
            return;
        }
        foreach (var entry in entries) _list.Children.Add(Card(entry));
    }

    private UIElement Card(HistoryEntry entry)
    {
        var card = new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 10, 12, 10) };
        var stack = new StackPanel();
        var head = new DockPanel();
        var when = new TextBlock { Text = entry.Started.LocalDateTime.ToString("g"), Style = StudioWindow.Res("Text.Caption"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(when, Dock.Right);
        head.Children.Add(when);
        head.Children.Add(new TextBlock { Text = entry.Title, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(head);

        var outputs = entry.Items.SelectMany(i => i.Outputs).ToList();
        int failed = entry.Items.Count(i => i.State == "Failed");
        var inputs = entry.Items.SelectMany(i => i.Inputs).Distinct().ToList();
        stack.Children.Add(new TextBlock
        {
            Text = $"{string.Join(", ", inputs.Take(3).Select(IOPath.GetFileName))}{(inputs.Count > 3 ? $" + {inputs.Count - 3} more" : "")}  →  " +
                   (outputs.Count == 0 ? "no output" : $"{outputs.Count} file{(outputs.Count == 1 ? "" : "s")}, {Bytes.Format(outputs.Sum(o => o.SizeBytes))}") +
                   (failed > 0 ? $" · {failed} failed" : ""),
            Style = StudioWindow.Res("Text.Caption"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var error = entry.Items.FirstOrDefault(i => i.Error is not null && i.State == "Failed")?.Error;
        if (error is not null)
        {
            var e = new TextBlock { Text = error, Style = StudioWindow.Res("Text.Caption") };
            e.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
            stack.Children.Add(e);
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var existing = outputs.Select(o => o.Path).Where(File.Exists).ToList();
        var show = new Button { Content = "Show results", IsEnabled = existing.Count > 0, Margin = new Thickness(0, 0, 8, 0), ToolTip = existing.Count < outputs.Count ? "Some results were moved or deleted since." : null };
        show.Click += (_, _) => ShellActions.Reveal(existing);
        var available = inputs.Where(File.Exists).ToList();
        bool canRepeat = CapabilityMap.TryGet(entry.ActionId, out var action) && available.Count > 0;
        var repeat = new Button { Content = "Run again", IsEnabled = canRepeat, ToolTip = "Runs the same action with the same settings on the same files (results get new names)." };
        repeat.Click += (_, _) =>
        {
            var options = new ActionOptions(entry.Options.ToDictionary(k => k.Key, v => v.Value));
            _dispatcher.Start(action!, available, options, ScreenGeometry.CursorPosition(), entry.Title);
        };
        buttons.Children.Add(show);
        buttons.Children.Add(repeat);
        stack.Children.Add(buttons);
        card.Child = stack;
        return card;
    }
}
