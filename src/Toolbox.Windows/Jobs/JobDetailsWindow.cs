using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core;
using Toolbox.Core.Jobs;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Jobs;

/// <summary>Per-file outcomes of a job: state, outputs with size and destination, notes, errors, retry.</summary>
internal sealed class JobDetailsWindow : GlassWindow
{
    private readonly Job _job;
    private readonly Action<Job> _retry;

    public JobDetailsWindow(Job job, Action<Job> retry)
    {
        _job = job;
        _retry = retry;
        Title = $"{job.Title} — details";
        Width = 560;
        Height = 480;
        MinHeight = 260;
        Resizable = true;
        Content = Build();
    }

    private UIElement Build()
    {
        var grid = new Grid { Margin = new Thickness(18, 14, 18, 14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel { Background = Brushes.Transparent, Margin = new Thickness(0, 0, 0, 10) };
        var close = new Button { Style = Res("Toolbox.Button.Icon"), Content = IconPath("close", 12), ToolTip = "Close" };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = _job.Title, Style = Res("Text.Title") });
        titles.Children.Add(new TextBlock { Text = $"{_job.Summary.Describe()} · started {_job.Started:t}", Style = Res("Text.Caption") });
        header.Children.Add(titles);
        MakeDragHandle(header);
        grid.Children.Add(header);

        var list = new StackPanel();
        foreach (var item in _job.Items) list.Children.Add(ItemCard(item));
        var scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroller, 1);
        grid.Children.Add(scroller);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        int failed = _job.Items.Count(i => i.State == JobItemState.Failed);
        if (failed > 0)
        {
            var retry = new Button { Content = $"Retry {failed} failed", Margin = new Thickness(0, 0, 8, 0) };
            retry.Click += (_, _) => { _retry(_job); Close(); };
            buttons.Children.Add(retry);
        }
        var outputs = _job.Items.SelectMany(i => i.Outputs).Select(o => o.Path).ToList();
        if (outputs.Count > 0)
        {
            var reveal = new Button { Content = "Show results", Margin = new Thickness(0, 0, 8, 0) };
            reveal.Click += (_, _) => ShellActions.Reveal(outputs);
            buttons.Children.Add(reveal);
        }
        var ok = new Button { Content = "Close", Style = Res("Toolbox.Button.Accent"), MinWidth = 80, IsDefault = true };
        ok.Click += (_, _) => Close();
        buttons.Children.Add(ok);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);

        var border = new Border { Child = grid };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Surface");
        return border;
    }

    private static UIElement ItemCard(JobItem item)
    {
        var card = new Border { Style = (Style)Application.Current.Resources["Toolbox.Card"], Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(12, 10, 12, 10) };
        var stack = new StackPanel();
        var head = new DockPanel();
        var state = new TextBlock { Text = StateText(item.State), FontSize = 12, FontWeight = FontWeights.SemiBold };
        state.SetResourceReference(TextBlock.ForegroundProperty, item.State switch
        {
            JobItemState.Completed => "Brush.Success",
            JobItemState.Failed => "Brush.Danger",
            JobItemState.Skipped or JobItemState.Cancelled => "Brush.Warning",
            _ => "Brush.TextMuted",
        });
        DockPanel.SetDock(state, Dock.Right);
        head.Children.Add(state);
        head.Children.Add(new TextBlock { Text = item.Label, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        stack.Children.Add(head);

        foreach (var output in item.Outputs)
        {
            var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var show = new Button { Content = "Show", Style = Res("Toolbox.Button.Subtle"), MinHeight = 24, Padding = new Thickness(8, 2, 8, 2) };
            show.Click += (_, _) => ShellActions.Reveal([output.Path]);
            DockPanel.SetDock(show, Dock.Right);
            row.Children.Add(show);
            var dims = output.Width is null ? "" : $" · {output.Width}×{output.Height}px";
            var pages = output.Pages is null ? "" : $" · {output.Pages} pages";
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = "→ " + IOPath.GetFileName(output.Path), TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = $"{Bytes.Format(output.SizeBytes)}{dims}{pages} · {IOPath.GetDirectoryName(output.Path)}", Style = Res("Text.Caption"), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        if (item.Error is not null)
        {
            var error = new TextBlock { Text = item.Error, Style = Res("Text.Caption"), Margin = new Thickness(0, 6, 0, 0) };
            error.SetResourceReference(TextBlock.ForegroundProperty, item.State == JobItemState.Failed ? "Brush.Danger" : "Brush.TextMuted");
            stack.Children.Add(error);
        }
        foreach (var note in item.Notes)
            stack.Children.Add(new TextBlock { Text = "• " + note, Style = Res("Text.Caption"), Margin = new Thickness(0, 4, 0, 0) });
        card.Child = stack;
        return card;
    }

    private static string StateText(JobItemState state) => state switch
    {
        JobItemState.Completed => "Saved",
        JobItemState.Failed => "Failed",
        JobItemState.Cancelled => "Cancelled",
        JobItemState.Skipped => "Skipped",
        JobItemState.Running => "Working…",
        _ => "Waiting",
    };

    private static Path IconPath(string key, double size)
    {
        var p = new Path { Data = Icons.Get(key), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        p.SetResourceReference(Shape.StrokeProperty, "Brush.Text");
        return p;
    }

    private static Style Res(string key) => (Style)Application.Current.Resources[key];
}

/// <summary>Short message near the pointer (an action that cannot run, or information). Never takes focus.</summary>
internal sealed class NoticeToast : GlassWindow
{
    public NoticeToast(string title, string message, bool warning)
    {
        BackdropKind = Backdrop.Solid;
        NoActivate = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CloseOnEscape = false;

        var icon = new Path { Data = Icons.Get(warning ? "warning" : "check"), Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, 1, 10, 0), VerticalAlignment = VerticalAlignment.Top };
        icon.SetResourceReference(Shape.StrokeProperty, warning ? "Brush.Warning" : "Brush.Accent");
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["Text.Title"], FontSize = 14 });
        text.Children.Add(new TextBlock { Text = message, Style = (Style)Application.Current.Resources["Text.Caption"], Margin = new Thickness(0, 3, 0, 0) });
        var row = new DockPanel();
        row.Children.Add(icon);
        row.Children.Add(text);
        var border = new Border { Child = row, Padding = new Thickness(14, 12, 14, 12), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        Content = border;

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(warning ? 7 : 4) };
        timer.Tick += (_, _) => { timer.Stop(); Close(); };
        MouseLeftButtonUp += (_, _) => Close();
        Loaded += (_, _) => timer.Start();
    }
}
