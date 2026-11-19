using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Toolbox.Core.Pulse.Chat;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse.Chat;

/// <summary>The chat's colours and small pieces: each agent's ink and tint, avatars, spinners.</summary>
internal static class ChatLook
{
    private static bool Dark => ThemeResources.Current.IsDark;

    public static Brush Ink(string agent) => Frozen(agent == Agent.Codex ? (Dark ? "#B4BCFF" : "#4338CA") : (Dark ? "#F2A688" : "#B93D0E"));

    public static Brush Soft(string agent) => Frozen(agent == Agent.Codex ? (Dark ? "#272A52" : "#E6E6FB") : (Dark ? "#43271D" : "#FBE7DE"));

    public static Brush Bubble => Frozen(Dark ? "#1E3366" : "#E6EEFD");

    public static Brush Mention => Frozen(Dark ? "#9DBBFF" : "#1D4ED8");

    public static Brush AmberSoft => Frozen(Dark ? "#3A2C10" : "#FEF3DA");

    public static Brush AmberLine => Frozen(Dark ? "#8A6417" : "#F3C46B");

    public static Brush AmberInk => Frozen(Dark ? "#FCD34D" : "#92400E");

    public static Brush WashSoft => Frozen(Dark ? "#16244A" : "#EAF1FE");

    public static Brush WashLine => Frozen(Dark ? "#2F4A8C" : "#BCD0F8");

    public static Brush RedInk => Frozen(Dark ? "#FCA5A5" : "#B91C1C");

    public static Brush GreenInk => Frozen(Dark ? "#4ADE80" : "#15803D");

    public static FontFamily Mono { get; } = new("Cascadia Code, Cascadia Mono, Consolas");

    public static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public static string Icon(string agent) => agent == Agent.Codex ? "lap-codex" : "lap-claude";

    public static FrameworkElement Avatar(string agent, double size = 28)
    {
        var b = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.32),
            Background = Soft(agent),
            Child = SettingsPage.Glyph(Icon(agent), size * 0.54, stroke: agent == Agent.Codex ? 1.8 : 2, brush: Ink(agent)),
            VerticalAlignment = VerticalAlignment.Top,
        };
        return b;
    }

    /// <summary>Both agents' marks, overlapping (a group chat).</summary>
    public static FrameworkElement Pair(double size = 30)
    {
        double s = size * 0.72;
        var grid = new Grid { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        var a = Avatar(Agent.Claude, s);
        a.HorizontalAlignment = HorizontalAlignment.Left;
        a.VerticalAlignment = VerticalAlignment.Top;
        var b = (Border)Avatar(Agent.Codex, s);
        b.HorizontalAlignment = HorizontalAlignment.Right;
        b.VerticalAlignment = VerticalAlignment.Bottom;
        b.BorderThickness = new Thickness(2);
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.LapCard");
        grid.Children.Add(a);
        grid.Children.Add(b);
        return grid;
    }

    public static FrameworkElement Faces(ChatInfo chat, double size = 30) =>
        chat.Members.Count > 1 ? Pair(size) : Avatar(chat.Agents.FirstOrDefault() ?? Agent.Claude, size * 0.93);

    private static readonly Dictionary<string, (DateTime At, IReadOnlyList<AgentModel> List)> ModelCache = [];

    /// <summary>An agent's models (Codex's are read from its own files, so they are kept for a minute).</summary>
    public static IReadOnlyList<AgentModel> Models(string agent)
    {
        if (ModelCache.TryGetValue(agent, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(1)) return hit.List;
        var list = AgentModels.For(agent);
        ModelCache[agent] = (DateTime.UtcNow, list);
        return list;
    }

    /// <summary>A drop-down of an agent's models; <paramref name="picked"/> gets the id ("" for its default).</summary>
    public static ComboBox ModelPicker(string agent, string current, Action<string> picked)
    {
        var box = new ComboBox { MinWidth = 170, MaxWidth = 240, ToolTip = Lang.T($"{Agent.Name(agent)}'s model", $"نموذج {Agent.Name(agent)}") };
        var list = Models(agent).ToList();
        if (current.Length > 0 && !list.Any(m => string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase))) list.Add(new AgentModel(current, current));
        foreach (var m in list)
        {
            var name = m.Id.Length == 0 ? Lang.T(m.Name, m.Name.Replace("Default", "الافتراضي", StringComparison.Ordinal)) : m.Name;
            box.Items.Add(new ComboBoxItem { Content = name, Tag = m.Id, IsSelected = string.Equals(m.Id, current, StringComparison.OrdinalIgnoreCase) });
        }
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string id }) picked(id); };
        return box;
    }

    /// <summary>A model's name for showing ("Opus", "GPT-6.2-Sol", "Default").</summary>
    public static string ModelName(string agent, string id) =>
        id.Length == 0 ? Lang.T("Default", "الافتراضي") : Models(agent).FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? id;

    /// <summary>A small turning arc (still when motion is reduced).</summary>
    public static FrameworkElement Spinner(double size = 12)
    {
        var arc = new Path
        {
            Data = Geometry.Parse(FormattableString.Invariant($"M{size / 2},1 A{size / 2 - 1},{size / 2 - 1} 0 0 1 {size - 1},{size / 2}")),
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        arc.SetResourceReference(Shape.StrokeProperty, "Brush.Accent");
        var ring = new Ellipse { Width = size, Height = size, StrokeThickness = 2 };
        ring.SetResourceReference(Shape.StrokeProperty, "Brush.Line2");
        var spin = new RotateTransform();
        var grid = new Grid { Width = size, Height = size, RenderTransform = spin, RenderTransformOrigin = new Point(0.5, 0.5), VerticalAlignment = VerticalAlignment.Center, Children = { ring, new Canvas { Width = size, Height = size, Children = { arc } } } };
        if (!Motion.IsReduced) spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        return grid;
    }

    public static TextBlock Text(string text, double size = 13, string brush = "Brush.Text", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, FontWeight = weight ?? FontWeights.Normal };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    /// <summary>A small button for cards and pictures.</summary>
    public static Button Small(string text, Action click, bool accent = false, bool ghost = false)
    {
        var b = SettingsPage.Button(text, click, accent, ghost);
        b.Height = 30;
        b.Padding = new Thickness(12, 0, 12, 0);
        b.FontSize = 12.5;
        b.Margin = new Thickness(0, 0, 8, 0);
        return b;
    }

    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// The agents write Markdown. This draws the parts that matter in a chat: paragraphs, lists,
/// headings, bold, <c>code</c>, code blocks (selectable) and @names. Everything else stays text.
/// </summary>
internal static partial class ChatText
{
    [GeneratedRegex(@"(\*\*[^*\n]+\*\*|`[^`\n]+`|(?<![\w@])@(?:claude|codex|both|all)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex InlinePattern();

    [GeneratedRegex(@"^\s*(?:[-*•]|\d+[.)])\s+")]
    private static partial Regex BulletPattern();

    public static FrameworkElement Render(string text, double size = 13.5)
    {
        var stack = new StackPanel();
        var blocks = Regex.Split(text.Replace("\r\n", "\n"), "(```[\\s\\S]*?(?:```|$))");
        foreach (var block in blocks)
        {
            if (block.Length == 0) continue;
            if (block.StartsWith("```", StringComparison.Ordinal))
            {
                var body = block.Trim('`');
                int nl = body.IndexOf('\n');
                if (nl >= 0 && !body[..nl].Contains(' ')) body = body[(nl + 1)..];
                stack.Children.Add(Code(body.TrimEnd('\n')));
                continue;
            }
            foreach (var para in Regex.Split(block, "\n\\s*\n"))
            {
                var lines = para.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
                if (lines.Count == 0) continue;
                var p = new StackPanel { Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 6, 0, 0) };
                var run = new List<string>();
                void FlushRun()
                {
                    if (run.Count == 0) return;
                    p.Children.Add(Line(string.Join(" ", run), size));
                    run.Clear();
                }
                foreach (var line in lines)
                {
                    if (BulletPattern().Match(line) is { Success: true } m)
                    {
                        FlushRun();
                        var marker = m.Value.Trim();
                        var row = new DockPanel { Margin = new Thickness(2, 2, 0, 0), FlowDirection = DirectionOf(line[m.Length..]) };
                        var dot = new TextBlock { Text = char.IsDigit(marker[0]) ? marker : "•", FontSize = size, Width = char.IsDigit(marker[0]) ? 22 : 14, FlowDirection = FlowDirection.LeftToRight };
                        dot.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
                        DockPanel.SetDock(dot, Dock.Left);
                        row.Children.Add(dot);
                        row.Children.Add(Line(line[m.Length..], size));
                        p.Children.Add(row);
                    }
                    else if (line.TrimStart().StartsWith('#'))
                    {
                        FlushRun();
                        var head = Line(line.TrimStart().TrimStart('#').Trim(), size + 0.5);
                        head.FontWeight = FontWeights.SemiBold;
                        head.Margin = new Thickness(0, 2, 0, 0);
                        p.Children.Add(head);
                    }
                    else run.Add(line.Trim());
                }
                FlushRun();
                stack.Children.Add(p);
            }
        }
        return stack;
    }

    /// <summary>
    /// Which way a piece of text reads, from its first letter: Arabic (or Hebrew) right to left,
    /// anything else left to right. A message keeps its own direction inside either layout.
    /// </summary>
    public static FlowDirection DirectionOf(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return FlowDirection.RightToLeft;
            if (char.IsLetter(c)) return FlowDirection.LeftToRight;
        }
        return Lang.Flow;
    }

    private static TextBlock Line(string text, double size)
    {
        var t = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.5, FlowDirection = DirectionOf(text) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        int last = 0;
        foreach (Match m in InlinePattern().Matches(text))
        {
            if (m.Index > last) t.Inlines.Add(new Run(text[last..m.Index]));
            var token = m.Value;
            if (token.StartsWith("**", StringComparison.Ordinal)) t.Inlines.Add(new Bold(new Run(token[2..^2])));
            else if (token.StartsWith('`'))
            {
                var code = new Run(token[1..^1]) { FontFamily = ChatLook.Mono, FontSize = size - 1 };
                code.SetResourceReference(TextElement.BackgroundProperty, "Brush.Line");
                t.Inlines.Add(code);
            }
            else t.Inlines.Add(new Run(token) { FontWeight = FontWeights.SemiBold, Foreground = ChatLook.Mention });
            last = m.Index + m.Length;
        }
        if (last < text.Length) t.Inlines.Add(new Run(text[last..]));
        return t;
    }

    private static FrameworkElement Code(string code)
    {
        var box = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontFamily = ChatLook.Mono,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Thickness(0),
        };
        box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        var border = new Border { Child = box, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 6, 0, 0), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
        border.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return border;
    }
}

/// <summary>Callbacks the message views need from the chat window.</summary>
internal sealed record ChatActions(
    Action<ChatMessage, string> Answer,
    Action<string, string, bool> Approve,
    Action<string, string> Redo,
    Action<string> OpenFile,
    Action<string> Copy,
    Func<string, bool> IsExpanded,
    Action<string> ToggleSteps,
    Func<string, ImageSource?> Thumbnail,
    Action<string, string>? SetModel = null);

/// <summary>Draws each kind of message: the user's bubble, an agent's reply, cards and notes.</summary>
internal static class ChatViews
{
    /// <summary>The most picture thumbnails one message shows (the rest are counted).</summary>
    public const int MaxThumbnails = 24;

    public static FrameworkElement For(ChatFile chat, ChatMessage m, ChatActions act) => m.Kind switch
    {
        ChatKind.Denied => Denied(m, act),
        ChatKind.Limit => Limit(chat, m, act),
        ChatKind.Consent => Consent(chat, m, act),
        ChatKind.Approval => Approval(m),
        ChatKind.Note => Note(m),
        _ => m.Author == ChatAuthor.User ? User(m, act) : AgentReply(chat, m, act),
    };

    private static FrameworkElement User(ChatMessage m, ChatActions act)
    {
        var stack = new StackPanel();
        stack.Children.Add(ChatText.Render(m.Text));
        var time = ChatLook.Text(ChatLook.Time(m.At), 10.5, "Brush.Text3");
        time.HorizontalAlignment = HorizontalAlignment.Right;
        time.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(time);
        var bubble = new Border
        {
            Child = stack,
            Background = ChatLook.Bubble,
            CornerRadius = Lang.IsArabic ? new CornerRadius(14, 14, 14, 4) : new CornerRadius(14, 14, 4, 14),
            Padding = new Thickness(12, 9, 12, 7),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 560,
            Margin = new Thickness(60, 0, 0, 0),
        };
        bubble.ContextMenu = CopyMenu(m.Text, act);
        return bubble;
    }

    private static ContextMenu CopyMenu(string text, ChatActions act)
    {
        var item = new MenuItem { Header = Lang.T("Copy", "نسخ") };
        item.Click += (_, _) => act.Copy(text);
        return new ContextMenu { Items = { item } };
    }

    private static FrameworkElement AgentReply(ChatFile chat, ChatMessage m, ChatActions act)
    {
        var agent = m.Author;
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 40, 0) };
        var face = ChatLook.Avatar(agent);
        face.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(face, Dock.Left);
        row.Children.Add(face);

        var body = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        head.Children.Add(new TextBlock { Text = Agent.Name(agent), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ChatLook.Ink(agent), VerticalAlignment = VerticalAlignment.Center });
        if (chat.Info.IsDirected && agent == chat.Info.Director)
        {
            var role = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Background = ChatLook.Soft(agent) };
            role.Child = new TextBlock { Text = Lang.T("directs", "يوجّه"), FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = ChatLook.Ink(agent) };
            role.ToolTip = chat.Info.DirectorInPulse
                ? Lang.T("Directs this chat. Pulse carries it on from a copy of its session.", "يوجّه هذه المحادثة. يتابعه نبض من نسخة من جلسته.")
                : Lang.T("Directs this chat from its own session with you.", "يوجّه هذه المحادثة من جلسته الخاصة معك.");
            head.Children.Add(role);
        }
        if (m.To is { } to)
        {
            var chip = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            chip.SetResourceReference(Border.BackgroundProperty, "Brush.Line2");
            chip.Child = ChatLook.Text(Lang.T($"to {Agent.Name(to)}", $"إلى {Agent.Name(to)}"), 10.5, "Brush.Text2", FontWeights.SemiBold);
            head.Children.Add(chip);
        }
        var when = ChatLook.Text(m.Working ? Lang.T("working…", "يعمل…") : ChatLook.Time(m.At), 11, "Brush.Text3");
        when.Margin = new Thickness(8, 0, 0, 0);
        when.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(when);
        if (m.Working)
        {
            var spin = ChatLook.Spinner(11);
            spin.Margin = new Thickness(7, 0, 0, 0);
            head.Children.Add(spin);
        }
        body.Children.Add(head);

        if (m.Text.Length > 0) body.Children.Add(ChatText.Render(m.Text));
        else if (m.Working)
        {
            var last = m.Steps.LastOrDefault(s => s.Kind != "say");
            body.Children.Add(ChatLook.Text(last is null ? Lang.T("Starting…", "يبدأ…") : last.Text + "…", 12.5, "Brush.Text3"));
        }
        if (m.Error is { } error && !(m.Working))
        {
            var e = ChatLook.Text(error == "Stopped." ? Lang.T("Stopped.", "أُوقف.") : error, 12.5);
            e.Foreground = error == "Stopped." ? (Brush)Application.Current.Resources["Brush.Text3"] : ChatLook.RedInk;
            e.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(e);
        }

        var work = m.Steps.Where(s => s.Kind != "say").ToList();
        if (m.Steps.Count > 0)
        {
            bool open = act.IsExpanded(m.Id);
            var toggle = new Button { Style = SettingsPage.Res("Lap.Chip"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0), Height = 26, Padding = new Thickness(10, 0, 10, 0) };
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(SettingsPage.Glyph("lap-steps", 12, stroke: 2).With(g => g.Margin = new Thickness(0, 0, 6, 0)));
            label.Children.Add(ChatLook.Text(StepsSummary(work) + " · " + (open ? Lang.T("Hide steps", "إخفاء الخطوات") : Lang.T("Show steps", "عرض الخطوات")), 11.5, "Brush.Text2", FontWeights.SemiBold));
            toggle.Content = label;
            toggle.Click += (_, _) => act.ToggleSteps(m.Id);
            body.Children.Add(toggle);
            if (open)
            {
                var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
                foreach (var s in m.Steps.TakeLast(80))
                {
                    var t = ChatLook.Text(s.Text, 11.5, s.Kind == "say" ? "Brush.Text3" : "Brush.Text2");
                    t.FontFamily = s.Kind == "say" ? t.FontFamily : ChatLook.Mono;
                    t.FlowDirection = FlowDirection.LeftToRight;
                    t.Margin = new Thickness(0, 1, 0, 1);
                    list.Children.Add(t);
                }
                var box = new Border { Child = list, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 7, 10, 7) };
                box.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
                body.Children.Add(box);
            }
        }

        if (m.Images.Count > 0) body.Children.Add(Pictures(chat, m, act));
        row.Children.Add(body);
        if (m.Text.Length > 0) row.ContextMenu = CopyMenu(m.Text, act);
        return row;
    }

    private static string StepsSummary(IReadOnlyList<ChatStep> steps)
    {
        if (steps.Count == 0) return Lang.T("Its thinking", "تفكيره");
        int read = steps.Count(s => s.Kind == "read"), edit = steps.Count(s => s.Kind == "edit"), run = steps.Count(s => s.Kind == "run"), web = steps.Count(s => s.Kind == "web"), other = steps.Count - read - edit - run - web;
        var parts = new List<string>();
        if (edit > 0) parts.Add(Lang.T($"edited {edit}", $"عدّل {edit}"));
        if (run > 0) parts.Add(Lang.T(run == 1 ? "ran 1 command" : $"ran {run} commands", $"شغّل {run}"));
        if (read > 0) parts.Add(Lang.T($"read {read}", $"قرأ {read}"));
        if (web > 0) parts.Add(Lang.T($"searched {web}", $"بحث {web}"));
        if (other > 0) parts.Add(Lang.T($"{other} more", $"{other} أخرى"));
        var text = string.Join(" · ", parts);
        return text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    private static FrameworkElement Pictures(ChatFile chat, ChatMessage m, ChatActions act)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        // A rendered sequence can be hundreds of frames: show the first ones and say how many more.
        foreach (var path in m.Images.Take(MaxThumbnails))
        {
            bool approved = ChatHub.IsApproved(chat, path);
            var tile = new StackPanel { Width = 140 };
            var image = new Image { Height = 118, Stretch = Stretch.Uniform, Source = act.Thumbnail(path), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = path };
            image.MouseLeftButtonUp += (_, _) => act.OpenFile(path);
            var frame = new Border { Child = image, CornerRadius = new CornerRadius(10), Padding = new Thickness(4), BorderThickness = new Thickness(approved ? 2 : 1) };
            frame.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
            if (approved) frame.BorderBrush = ChatLook.GreenInk;
            else frame.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
            tile.Children.Add(frame);
            // Pictures are often front.png, back.png... in a folder per item: show both.
            var folder = IOPath.GetFileName(IOPath.GetDirectoryName(path) ?? "");
            var name = ChatLook.Text(folder.Length > 0 ? folder + "/" + IOPath.GetFileName(path) : IOPath.GetFileName(path), 11, "Brush.Text2");
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.FlowDirection = FlowDirection.LeftToRight;
            name.Margin = new Thickness(2, 4, 2, 4);
            name.ToolTip = ChatRules.Relative(chat.Info, path);
            tile.Children.Add(name);
            var buttons = new UniformGrid2();
            var ok = ChatLook.Small(approved ? Lang.T("Approved", "معتمدة") : Lang.T("Approve", "اعتماد"), () => act.Approve(path, m.Author, !approved), accent: approved);
            ok.Height = 26;
            ok.FontSize = 11;
            ok.Padding = new Thickness(4, 0, 4, 0);
            ok.Margin = new Thickness(0, 0, 3, 0);
            var redo = ChatLook.Small(Lang.T("Redo", "إعادة"), () => act.Redo(m.Author, path));
            redo.Height = 26;
            redo.FontSize = 11;
            redo.Padding = new Thickness(4, 0, 4, 0);
            redo.Margin = new Thickness(3, 0, 0, 0);
            buttons.Add(ok, redo);
            tile.Children.Add(buttons);
            wrap.Children.Add(new Border { Child = tile, Margin = new Thickness(0, 0, 8, 8) });
        }
        var note = ChatLook.Text(Lang.T("Approve the ones you like. The agent that made them sees your approvals on its next turn and uses them as the reference.", "اعتمد ما يعجبك. يرى الوكيل الذي صنعها اعتمادك في دوره التالي ويستخدمها مرجعًا."), 11.5, "Brush.Text3");
        var stack = new StackPanel();
        stack.Children.Add(wrap);
        if (m.Images.Count > MaxThumbnails)
        {
            int more = m.Images.Count - MaxThumbnails;
            stack.Children.Add(ChatLook.Text(Lang.T($"And {more} more pictures ({m.Images.Count} in all), in the folder.", $"و{more} صورة أخرى ({m.Images.Count} في المجموع)، في المجلد."), 11.5, "Brush.Text2", FontWeights.SemiBold).With(t => t.Margin = new Thickness(0, 0, 0, 6)));
        }
        if (m.Overwrote is { Count: > 0 } over)
            stack.Children.Add(ChatLook.Text(Lang.T($"Saved over an earlier version instead of a new one: {string.Join(", ", over.Take(4).Select(IOPath.GetFileName))}{(over.Count > 4 ? "…" : "")}", $"حُفظت فوق نسخة سابقة بدل نسخة جديدة: {string.Join("، ", over.Take(4).Select(IOPath.GetFileName))}{(over.Count > 4 ? "…" : "")}"), 11.5, "Brush.Text2", FontWeights.SemiBold).With(t => { t.Foreground = ChatLook.AmberInk; t.Margin = new Thickness(0, 0, 0, 6); }));
        stack.Children.Add(note);
        var card = new Border { Child = stack, CornerRadius = new CornerRadius(14), Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(1) };
        card.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        card.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        return card;
    }

    private static FrameworkElement Denied(ChatMessage m, ChatActions act)
    {
        var agent = m.About ?? Agent.Claude;
        var what = m.Denied.Select(d => d.Summary).ToList();
        if (m.State != CardState.Open)
        {
            var said = m.State switch
            {
                CardState.Allowed => Lang.T("Allowed once: ", "سُمح مرة واحدة: "),
                CardState.Always => Lang.T("Always allowed in this chat: ", "مسموح دائمًا في هذه المحادثة: "),
                _ => Lang.T("Not allowed: ", "غير مسموح: "),
            };
            return Done(said + string.Join("; ", what), m.State != CardState.Skipped);
        }
        var stack = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(SettingsPage.Glyph("lap-shield", 16, stroke: 2, brush: ChatLook.AmberInk).With(g => g.Margin = new Thickness(0, 0, 8, 0)));
        title.Children.Add(ChatLook.Text(Lang.T($"{Agent.Name(agent)} needs your OK", $"{Agent.Name(agent)} يحتاج موافقتك"), 13.5, "Brush.Text", FontWeights.SemiBold));
        stack.Children.Add(title);
        stack.Children.Add(ChatLook.Text(Lang.T("It wasn't allowed to do this, so it stopped there:", "لم يُسمح له بهذا، فتوقف عنده:"), 12.5, "Brush.Text2").With(t => t.Margin = new Thickness(0, 4, 0, 6)));
        foreach (var d in m.Denied.Take(6))
        {
            var line = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 0, 4), BorderThickness = new Thickness(1) };
            line.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
            line.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
            var t = ChatLook.Text(d.Summary, 12);
            t.FontFamily = ChatLook.Mono;
            t.FlowDirection = FlowDirection.LeftToRight;
            line.Child = t;
            stack.Children.Add(line);
        }
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(ChatLook.Small(Lang.T("Allow once", "اسمح مرة"), () => act.Answer(m, CardState.Allowed), accent: true));
        row.Children.Add(ChatLook.Small(Lang.T("Always in this chat", "دائمًا في هذه المحادثة"), () => act.Answer(m, CardState.Always)));
        row.Children.Add(ChatLook.Small(Lang.T("Don't allow", "لا تسمح"), () => act.Answer(m, CardState.Skipped), ghost: true));
        stack.Children.Add(row);
        return new Border { Child = stack, Background = ChatLook.AmberSoft, BorderBrush = ChatLook.AmberLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12) };
    }

    private static FrameworkElement Limit(ChatFile chat, ChatMessage m, ChatActions act)
    {
        if (m.State != CardState.Open)
            return Done(m.State == CardState.More
                ? Lang.T("You let them keep going.", "سمحت لهما بالمتابعة.")
                : Lang.T("Paused. They wait for you.", "متوقف مؤقتًا. ينتظرانك."), m.State == CardState.More);
        var stack = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(SettingsPage.Glyph("lap-loop", 16, stroke: 2, brush: ChatLook.Mention).With(g => g.Margin = new Thickness(0, 0, 8, 0)));
        int n = int.TryParse(m.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hops) ? hops : chat.Info.HopLimit;
        var info = chat.Info;
        if (info.DirectorOutside)
        {
            string director = Agent.Name(info.Director), guest = Agent.Name(info.Guest ?? Agent.Other(info.Director));
            title.Children.Add(ChatLook.Text(Lang.T($"{director} wrote to {guest} {n} times without you", $"كتب {director} إلى {guest} {n} مرات دونك"), 13.5, "Brush.Text", FontWeights.SemiBold));
            stack.Children.Add(title);
            stack.Children.Add(ChatLook.Text(Lang.T($"Its next message is waiting. Let {director} go on, or write below to steer them both.", $"رسالته التالية تنتظر. دع {director} يتابع، أو اكتب في الأسفل لتوجيههما معًا."), 12.5, "Brush.Text2").With(t => t.Margin = new Thickness(0, 4, 0, 8)));
            var go = new WrapPanel();
            go.Children.Add(ChatLook.Small(Lang.T($"Let {director} go on ({info.DirectorBudget} more)", $"دع {director} يتابع ({info.DirectorBudget} أخرى)"), () => act.Answer(m, CardState.More), accent: true));
            go.Children.Add(ChatLook.Small(Lang.T("Pause here", "توقف هنا"), () => act.Answer(m, CardState.Paused), ghost: true));
            stack.Children.Add(go);
            return new Border { Child = stack, Background = ChatLook.WashSoft, BorderBrush = ChatLook.WashLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12) };
        }
        title.Children.Add(ChatLook.Text(Lang.T($"Claude and Codex handed work to each other {n} times without you", $"تبادل Claude وCodex العمل {n} مرات دونك"), 13.5, "Brush.Text", FontWeights.SemiBold));
        stack.Children.Add(title);
        stack.Children.Add(ChatLook.Text(Lang.T($"{Agent.Name(m.About ?? Agent.Codex)} is next. Let them keep going, or write below to steer them.", $"الدور التالي لـ {Agent.Name(m.About ?? Agent.Codex)}. دعهما يتابعان، أو اكتب في الأسفل لتوجيههما."), 12.5, "Brush.Text2").With(t => t.Margin = new Thickness(0, 4, 0, 8)));
        var row = new WrapPanel();
        row.Children.Add(ChatLook.Small(Lang.T($"Keep going ({chat.Info.HopLimit} more)", $"تابعا ({chat.Info.HopLimit} أخرى)"), () => act.Answer(m, CardState.More), accent: true));
        row.Children.Add(ChatLook.Small(Lang.T("Pause here", "توقف هنا"), () => act.Answer(m, CardState.Paused), ghost: true));
        stack.Children.Add(row);
        return new Border { Child = stack, Background = ChatLook.WashSoft, BorderBrush = ChatLook.WashLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 12, 14, 12) };
    }

    /// <summary>
    /// An agent opened this chat from its own session: what it asks for (who, where, what the
    /// guest may change, its model and the director's budget), and the owner's answer.
    /// </summary>
    private static FrameworkElement Consent(ChatFile chat, ChatMessage m, ChatActions act)
    {
        var info = chat.Info;
        var directorId = info.IsDirected ? info.Director : info.TakenFrom ?? Agent.Claude;
        var guestId = m.About ?? info.Guest ?? Agent.Other(directorId);
        string director = Agent.Name(directorId), guest = Agent.Name(guestId);
        if (m.State != CardState.Open)
        {
            return m.State switch
            {
                CardState.AllowRead => Done(Lang.T($"You allowed this chat. {guest} reads only.", $"سمحت بهذه المحادثة. {guest} يقرأ فقط."), true),
                CardState.AllowEdit => Done(Lang.T($"You allowed this chat. {guest} can edit files in this folder.", $"سمحت بهذه المحادثة. يمكن لـ {guest} تعديل الملفات في هذا المجلد."), true),
                CardState.Paused => Done(Lang.T("You took over this chat.", "توليت هذه المحادثة."), true),
                _ => Done(Lang.T("You didn't allow this chat.", "لم تسمح بهذه المحادثة."), false),
            };
        }
        var stack = new StackPanel();
        var title = new DockPanel();
        var faces = ChatLook.Pair(30);
        faces.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(faces, Dock.Left);
        title.Children.Add(faces);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(ChatLook.Text(Lang.T($"{director} wants to work with {guest} here", $"يريد {director} العمل مع {guest} هنا"), 14, "Brush.Text", FontWeights.SemiBold));
        words.Children.Add(ChatLook.Text(Lang.T($"{director} opened this chat from its own session with you. {guest} starts when you allow it.", $"فتح {director} هذه المحادثة من جلسته الخاصة معك. يبدأ {guest} عندما تسمح."), 12, "Brush.Text2"));
        title.Children.Add(words);
        stack.Children.Add(title);

        var facts = new StackPanel { Margin = new Thickness(0, 10, 0, 4) };
        void Fact(string key, UIElement value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var k = ChatLook.Text(key, 12, "Brush.Text3", FontWeights.SemiBold);
            k.Width = 120;
            k.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(k, Dock.Left);
            row.Children.Add(k);
            row.Children.Add(value);
            facts.Children.Add(row);
        }
        TextBlock Value(string text, bool ltr = false)
        {
            var t = ChatLook.Text(text, 12, "Brush.Text");
            t.VerticalAlignment = VerticalAlignment.Center;
            if (ltr) t.FlowDirection = FlowDirection.LeftToRight;
            return t;
        }
        Fact(Lang.T("Folder", "المجلد"), Value(info.Folder ?? "", ltr: true));
        Fact(Lang.T($"{guest} would", $"ما سيفعله {guest}"), Value(m.Text == ChatPermission.Edit
            ? Lang.T("change files in this folder (asked for)", "يغيّر الملفات في هذا المجلد (مطلوب)")
            : Lang.T("read only (asked for)", "يقرأ فقط (مطلوب)")));
        if (info.Outputs is { Length: > 0 } outputs) Fact(Lang.T("Saves to", "يحفظ في"), Value(outputs.TrimEnd('/') + "/", ltr: true));
        if (act.SetModel is { } setModel)
        {
            var picker = ChatLook.ModelPicker(guestId, info.ModelOf(guestId), id => setModel(guestId, id));
            picker.HorizontalAlignment = HorizontalAlignment.Left;
            Fact(Lang.T($"{guest}'s model", $"نموذج {guest}"), picker);
        }
        else Fact(Lang.T($"{guest}'s model", $"نموذج {guest}"), Value(ChatLook.ModelName(guestId, info.ModelOf(guestId))));
        Fact(Lang.T("Without you", "دونك"), Value(Lang.T($"{director} may write {info.DirectorBudget} times, then Pulse asks you", $"يمكن لـ {director} الكتابة {info.DirectorBudget} مرات، ثم يسألك نبض")));
        Fact(Lang.T($"{guest} gets", $"يحصل {guest} على"), Value(info.DirectorNotes is null
            ? Lang.T($"{director}'s brief", $"موجز {director}")
            : Lang.T($"{director}'s brief, and a copy of your conversation to read if needed", $"موجز {director}، ونسخة من محادثتكما ليقرأها عند الحاجة")));
        stack.Children.Add(facts);

        var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        bool wantsEdit = m.Text == ChatPermission.Edit;
        buttons.Children.Add(ChatLook.Small(wantsEdit ? Lang.T($"Allow, {guest} can edit", $"اسمح، يمكن لـ {guest} التعديل") : Lang.T("Allow, read only", "اسمح، قراءة فقط"), () => act.Answer(m, wantsEdit ? CardState.AllowEdit : CardState.AllowRead), accent: true));
        buttons.Children.Add(ChatLook.Small(wantsEdit ? Lang.T("Allow, read only", "اسمح، قراءة فقط") : Lang.T($"Allow, {guest} can edit", $"اسمح، يمكن لـ {guest} التعديل"), () => act.Answer(m, wantsEdit ? CardState.AllowRead : CardState.AllowEdit)));
        buttons.Children.Add(ChatLook.Small(Lang.T("Don't allow", "لا تسمح"), () => act.Answer(m, CardState.Skipped), ghost: true));
        stack.Children.Add(buttons);
        stack.Children.Add(ChatLook.Text(Lang.T("Commands still ask you first. You can write to both of them below, take the chat over, or stop it any time.", "الأوامر تسألك أولًا دائمًا. يمكنك الكتابة لكليهما في الأسفل، أو تولّي المحادثة، أو إيقافها في أي وقت."), 11.5, "Brush.Text3").With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        return new Border { Child = stack, Background = ChatLook.AmberSoft, BorderBrush = ChatLook.AmberLine, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 14, 16, 14) };
    }

    private static FrameworkElement Done(string text, bool positive)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(SettingsPage.Glyph(positive ? "lap-check" : "lap-x", 13, stroke: 2.4, brush: positive ? ChatLook.GreenInk : null).With(g => g.Margin = new Thickness(0, 0, 7, 0)));
        var t = ChatLook.Text(text, 12, "Brush.Text3");
        t.TextWrapping = TextWrapping.Wrap;
        row.Children.Add(t);
        return row;
    }

    private static FrameworkElement Approval(ChatMessage m)
    {
        var withdrawn = m.State == CardState.Skipped;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Opacity = withdrawn ? 0.6 : 1 };
        row.Children.Add(SettingsPage.Glyph(withdrawn ? "lap-x" : "lap-check", 12, stroke: 2.4, brush: withdrawn ? null : ChatLook.GreenInk).With(g => g.Margin = new Thickness(0, 0, 6, 0)));
        var t = ChatLook.Text(withdrawn ? Lang.T($"Approval taken back: {IOPath.GetFileName(m.Text)}", $"سُحب الاعتماد: {IOPath.GetFileName(m.Text)}") : Lang.T($"You approved {IOPath.GetFileName(m.Text)}", $"اعتمدتَ {IOPath.GetFileName(m.Text)}"), 11.5, "Brush.Text3");
        t.ToolTip = m.Text;
        row.Children.Add(t);
        return row;
    }

    private static FrameworkElement Note(ChatMessage m)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 40, 0) };
        bool fork = m.Text.StartsWith("Continues ", StringComparison.Ordinal);
        var glyph = SettingsPage.Glyph(fork ? "lap-history" : "lap-warning", 15, "Brush.Text2", 1.9);
        glyph.Margin = new Thickness(0, 1, 9, 0);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        DockPanel.SetDock(glyph, Dock.Left);
        row.Children.Add(glyph);
        var text = fork && m.About is { } agent
            ? Lang.T($"Picked up from {Agent.Name(agent)}'s earlier session. Pulse continues it as a copy, so the original stays as it was.", $"استُكملت من جلسة {Agent.Name(agent)} السابقة. يتابعها نبض كنسخة، فتبقى الأصلية كما هي.")
            : m.Text;
        row.Children.Add(ChatLook.Text(text, 12.5, "Brush.Text2"));
        var box = new Border { Child = row, CornerRadius = new CornerRadius(14), Padding = new Thickness(14, 10, 14, 10) };
        box.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
        return box;
    }

    /// <summary>Thumbnails, read small and once per version of a file.</summary>
    public static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            // Read from a stream (so the file is never held open), small, and fully at once.
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 236;
            using (var stream = new FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete))
            {
                bmp.StreamSource = stream;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>Two buttons side by side, each half the width.</summary>
internal sealed class UniformGrid2 : Grid
{
    public UniformGrid2()
    {
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition());
    }

    public void Add(UIElement left, UIElement right)
    {
        Children.Add(left);
        SetColumn(right, 1);
        Children.Add(right);
    }
}
