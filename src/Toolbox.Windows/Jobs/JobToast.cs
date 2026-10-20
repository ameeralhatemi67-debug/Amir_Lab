using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Toolbox.Core;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;
using Toolbox.Windows.Interop;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Jobs;

/// <summary>
/// Compact, never-activated notification for one job: live progress and cancel while running,
/// then the actual result (names, sizes, notes) with Open, Show in folder, Remove and Details.
/// </summary>
internal sealed class JobToast : GlassWindow
{
    private const double ToastWidth = 360;

    private readonly Job _job;
    private readonly ToolboxHost _host;
    private readonly Action<Job> _openDetails;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private readonly DispatcherTimer _autoClose = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly Dictionary<string, (long Size, DateTime Written)> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextBlock _title = new() { Style = Res("Text.Title"), FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { Style = Res("Text.Caption"), Margin = new Thickness(0, 2, 0, 0) };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _notes = new() { Style = Res("Text.Caption"), Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed, MaxHeight = 48, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _chip = new();
    private readonly Path _icon = new() { Width = 18, Height = 18, Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
    private bool _finishedRendered;
    private bool _removed;

    public JobToast(Job job, ToolboxHost host, Action<Job> openDetails)
    {
        _job = job;
        _host = host;
        _openDetails = openDetails;
        BackdropKind = Backdrop.Solid;
        NoActivate = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Width = ToastWidth;
        SizeToContent = SizeToContent.Height;
        CloseOnEscape = false;
        Title = job.Title;

        Content = BuildLayout();
        AutomationPropertiesHelper.Name(this, $"{job.Title} progress");

        _refresh.Tick += (_, _) => Refresh();
        _autoClose.Tick += (_, _) => FadeOutAndClose();
        MouseEnter += (_, _) => _autoClose.Stop();
        MouseLeave += (_, _) => { if (_finishedRendered && IsCleanSuccess) _autoClose.Start(); };
        Loaded += (_, _) =>
        {
            Refresh();
            _refresh.Start();
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
            BeginAnimation(OpacityProperty, fade);
        };
        Closed += (_, _) => { _refresh.Stop(); _autoClose.Stop(); };
    }

    public Job Job => _job;

    private bool IsCleanSuccess => _job.IsFinished && _job.Summary is { Failed: 0, Cancelled: 0, Completed: > 0 };

    private UIElement BuildLayout()
    {
        var close = new Button { Style = Res("Toolbox.Button.Icon"), Width = 26, Height = 26, Content = IconPath("close", 12), ToolTip = "Cancel or dismiss" };
        close.Click += (_, _) =>
        {
            if (!_job.IsFinished) _job.Cancel();
            else FadeOutAndClose();
        };

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _icon.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        _icon.Margin = new Thickness(0, 2, 10, 0);
        _icon.VerticalAlignment = VerticalAlignment.Top;
        header.Children.Add(_icon);
        var titles = new StackPanel();
        titles.Children.Add(_title);
        titles.Children.Add(_status);
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        Grid.SetColumn(close, 2);
        close.VerticalAlignment = VerticalAlignment.Top;
        header.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(_progress);
        body.Children.Add(_notes);
        body.Children.Add(_actions);

        var border = new Border
        {
            Padding = new Thickness(14, 12, 10, 12),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = body,
        };
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");
        return border;
    }

    private void Refresh()
    {
        var summary = _job.Summary;
        if (!_job.IsFinished)
        {
            _title.Text = JobText.Progressive(_job.Title);
            _icon.Data = Icons.Get("export");
            int processable = summary.Total - summary.Skipped;
            _status.Text = processable > 1
                ? $"{summary.Completed} of {processable} done · {_job.Progress:P0}"
                : $"{_job.Progress:P0}";
            _progress.Value = _job.Progress;
            if (_actions.Children.Count == 0)
            {
                var cancel = new Button { Content = "Cancel" };
                cancel.Click += (_, _) => _job.Cancel();
                _actions.Children.Add(cancel);
            }
            ShowNotes();
            return;
        }

        if (_finishedRendered) return;
        _finishedRendered = true;
        _refresh.Stop();
        RenderFinished(summary);
        if (IsCleanSuccess && !IsMouseOver) _autoClose.Start();
    }

    private void RenderFinished(JobSummary summary)
    {
        var outputs = _job.Items.SelectMany(i => i.Outputs).ToList();
        foreach (var o in outputs)
        {
            try
            {
                var fi = new FileInfo(o.Path);
                _fingerprints[o.Path] = (fi.Length, fi.LastWriteTimeUtc);
            }
            catch (IOException) { }
        }

        _progress.Visibility = Visibility.Collapsed;
        bool failed = summary.Failed > 0 || summary.Completed == 0;
        _icon.Data = Icons.Get(failed ? "warning" : "check");
        _icon.SetResourceReference(Shape.StrokeProperty, failed ? "Brush.Warning" : "Brush.Success");
        _title.Text = _job.Title;
        _status.Text = summary.Describe() + (outputs.Count > 0 ? " · " + Bytes.Format(outputs.Sum(o => o.SizeBytes)) : "");

        ShowNotes();
        _actions.Children.Clear();
        if (outputs.Count > 0)
        {
            _actions.Children.Add(BuildChip(outputs));
            var open = new Button { Content = "Open", Margin = new Thickness(6, 0, 0, 0), ToolTip = outputs.Count > 1 ? "Open the first result" : "Open the result" };
            open.Click += (_, _) => ShellActions.Open(outputs[0].Path);
            var reveal = new Button { Content = "Show", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Show in File Explorer" };
            reveal.Click += (_, _) => ShellActions.Reveal(outputs.Select(o => o.Path).ToList());
            var remove = new Button { Content = "Remove", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Move the files this job created to the Recycle Bin (only if they are unchanged)" };
            remove.Click += (_, _) => RemoveOutputs(outputs, remove);
            _actions.Children.Add(open);
            _actions.Children.Add(reveal);
            _actions.Children.Add(remove);
        }
        bool details = _job.Items.Count > 1 || failed || _job.Items.Sum(i => i.Notes.Count) > 1;
        if (details)
        {
            var more = new Button { Content = "Details", Margin = new Thickness(6, 0, 0, 0), Style = Res("Toolbox.Button.Subtle") };
            more.Click += (_, _) => _openDetails(_job);
            _actions.Children.Add(more);
        }
    }

    private void ShowNotes()
    {
        var lines = new List<string>();
        var errors = _job.Items.Where(i => i.State == JobItemState.Failed && i.Error is not null).ToList();
        if (errors.Count == 1) lines.Add(errors[0].Error!);
        else if (errors.Count > 1) lines.Add($"{errors.Count} files failed — see Details.");
        var skipped = _job.Items.Where(i => i.State == JobItemState.Skipped).ToList();
        if (skipped.Count > 0)
            lines.Add(skipped.Count == 1 ? $"Skipped {skipped[0].Label}: {skipped[0].Error}" : $"Skipped {skipped.Count} files that do not apply.");
        lines.AddRange(_job.Items.SelectMany(i => i.Notes).Distinct().Take(2));
        _notes.Text = string.Join(Environment.NewLine, lines.Take(3));
        _notes.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _notes.SetResourceReference(TextBlock.ForegroundProperty, errors.Count > 0 ? "Brush.Danger" : "Brush.TextMuted");
    }

    /// <summary>Chip that can be dragged onward into any app (or back onto the wheel with Shift).</summary>
    private Border BuildChip(IReadOnlyList<OutputRecord> outputs)
    {
        var label = outputs.Count == 1 ? IOPath.GetFileName(outputs[0].Path) : $"{outputs.Count} files";
        var text = new TextBlock { Text = label, FontSize = 12, MaxWidth = 120, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = IconPath(outputs[0].Kind == Core.Inputs.FileKind.Pdf ? "pdf" : outputs[0].Kind.IsRaster() ? "image" : "doc", 14);
        icon.Margin = new Thickness(0, 0, 6, 0);
        panel.Children.Add(icon);
        panel.Children.Add(text);
        _chip.Child = panel;
        _chip.Padding = new Thickness(8, 5, 10, 5);
        _chip.CornerRadius = new CornerRadius(6);
        _chip.BorderThickness = new Thickness(1);
        _chip.Cursor = Cursors.SizeAll;
        _chip.ToolTip = "Drag the result into another app — or tap Shift while dragging to open the wheel for the next step";
        _chip.SetResourceReference(Border.BackgroundProperty, "Brush.AccentSoft");
        _chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border");

        Point? down = null;
        _chip.PreviewMouseLeftButtonDown += (_, e) => down = e.GetPosition(_chip);
        _chip.PreviewMouseLeftButtonUp += (_, _) => down = null;
        _chip.PreviewMouseMove += (_, e) =>
        {
            if (down is null || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(_chip) - down.Value;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            down = null;
            _autoClose.Stop();
            var existing = outputs.Select(o => o.Path).Where(File.Exists).ToArray();
            if (existing.Length == 0) return;
            DragDrop.DoDragDrop(_chip, new DataObject(DataFormats.FileDrop, existing), DragDropEffects.Copy);
        };
        return _chip;
    }

    private void RemoveOutputs(IReadOnlyList<OutputRecord> outputs, Button button)
    {
        if (_removed) return;
        var changed = outputs.Where(o =>
        {
            if (!File.Exists(o.Path)) return false;
            var fi = new FileInfo(o.Path);
            return !_fingerprints.TryGetValue(o.Path, out var fp) || fp.Size != fi.Length || fp.Written != fi.LastWriteTimeUtc;
        }).ToList();
        if (changed.Count > 0)
        {
            _notes.Text = $"Not removed: {IOPath.GetFileName(changed[0].Path)} was changed after it was saved.";
            _notes.Visibility = Visibility.Visible;
            return;
        }
        int moved = ShellActions.Recycle(outputs.Select(o => o.Path));
        _removed = true;
        button.IsEnabled = false;
        foreach (var child in _actions.Children.OfType<Button>()) if (child.Content as string is "Open" or "Show") child.IsEnabled = false;
        _chip.IsEnabled = false;
        _chip.Opacity = 0.4;
        _status.Text = $"Moved {moved} file{(moved == 1 ? "" : "s")} to the Recycle Bin";
        _autoClose.Start();
    }

    private void FadeOutAndClose()
    {
        _autoClose.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private static Path IconPath(string key, double size)
    {
        var p = new Path { Data = Icons.Get(key), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        p.SetResourceReference(Shape.StrokeProperty, "Brush.Text");
        return p;
    }

    private static Style Res(string key) => (Style)Application.Current.Resources[key];
}

internal static class JobText
{
    private static readonly Dictionary<string, string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Convert"] = "Converting", ["Render"] = "Rendering", ["Extract"] = "Extracting", ["Place"] = "Placing",
        ["Compress"] = "Compressing", ["Crop"] = "Cropping", ["Add"] = "Adding", ["Export"] = "Exporting",
        ["Make"] = "Making", ["Recognise"] = "Recognising", ["Inspect"] = "Inspecting", ["Annotate"] = "Annotating",
        ["Adjust"] = "Adjusting", ["Redact"] = "Redacting", ["Edit"] = "Editing", ["Remove"] = "Removing",
        ["Resize"] = "Resizing", ["Apply"] = "Applying", ["Prepare"] = "Preparing", ["Run"] = "Running",
    };

    /// <summary>"Convert to JPG" → "Converting to JPG".</summary>
    public static string Progressive(string title)
    {
        var space = title.IndexOf(' ');
        var verb = space > 0 ? title[..space] : title;
        return Verbs.TryGetValue(verb, out var ing) ? ing + title[verb.Length..] : title;
    }
}

internal static class AutomationPropertiesHelper
{
    public static void Name(DependencyObject element, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(element, name);
}

/// <summary>Places toasts near where the work started and stacks concurrent ones.</summary>
internal sealed class ToastManager
{
    private readonly ToolboxHost _host;
    private readonly List<JobToast> _open = [];
    private readonly Action<Job> _openDetails;

    public ToastManager(ToolboxHost host, Action<Job> openDetails)
    {
        _host = host;
        _openDetails = openDetails;
    }

    public void Show(Job job, Native.POINT? anchor)
    {
        var toast = new JobToast(job, _host, _openDetails);
        var point = anchor ?? DefaultAnchor();
        // Below the pointer for the first toast; further toasts stack beneath the previous ones.
        double offset = 28 + _open.Count * 118;
        toast.PlaceNear(point, offset);
        toast.Closed += (_, _) => _open.Remove(toast);
        _open.Add(toast);
        toast.Show();
    }

    private static Native.POINT DefaultAnchor()
    {
        var work = SystemParameters.WorkArea;
        var monitor = ScreenGeometry.MonitorAt(new Native.POINT { X = 1, Y = 1 });
        return new Native.POINT
        {
            X = (int)(monitor.WorkArea.Right - 200 * monitor.Scale),
            Y = (int)(monitor.WorkArea.Bottom - 160 * monitor.Scale),
        };
    }
}
