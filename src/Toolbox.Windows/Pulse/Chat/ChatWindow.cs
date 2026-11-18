using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Pulse.Chat;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse.Chat;

/// <summary>
/// Pulse's chats: the list of chats by folder on the left, the open chat in the middle (messages,
/// what the agents are doing, the message box with @ names), and the chat's board on the right
/// (folder, who owns what, rules, the agents' own sessions).
/// </summary>
internal sealed partial class ChatWindow : GlassWindow
{
    private const double TitleHeight = 44;

    private readonly ChatHub _hub;
    private readonly Func<string, string?> _program;
    private readonly Grid _root = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _thread = new() { Margin = new Thickness(18, 14, 18, 14) };
    private readonly ScrollViewer _scroll = new();
    private readonly Border _head = new();
    private readonly Border _lanes = new();
    private readonly WrapPanel _status = new() { Margin = new Thickness(14, 8, 14, 0) };
    private readonly Border _board = new();
    private readonly TextBox _box = new();
    private readonly TextBlock _hint = new();
    private readonly Popup _mentions = new();
    private readonly StackPanel _mentionList = new();
    private readonly Border _toHost = new();
    private readonly Button _send = new();
    private readonly Grid _content = new();
    private readonly Dictionary<string, (DateTime Written, ImageSource? Image)> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expanded = [];
    private readonly DispatcherTimer _redraw = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _current;
    private string _to = "";
    private bool _boardOpen = true;
    private bool _listDirty, _threadDirty;

    public ChatWindow(ChatHub hub, Func<string, string?> program)
    {
        _hub = hub;
        _program = program;
        UseNativeFrame(TitleHeight);
        FollowsGlassSetting = true;
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1160, work.Width * 0.9);
        Height = Math.Min(780, work.Height * 0.9);
        MinWidth = 760;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = Lang.T("Pulse · Chats", "نبض · المحادثات");
        Content = _root;
        CloseOnEscape = false;
        _redraw.Tick += (_, _) => Redraw();
        _tick.Tick += (_, _) => { if (_current is { } id && _hub.WorkingIn(id) is not null) RenderStatus(); };
        _hub.Changed += OnChanged;
        ThemeResources.Changed += Rebuild;
        Closed += (_, _) =>
        {
            _hub.Changed -= OnChanged;
            ThemeResources.Changed -= Rebuild;
            _redraw.Stop();
            _tick.Stop();
        };
        BuildBox();
        Rebuild();
        _tick.Start();
        _current = _hub.Chats.FirstOrDefault()?.Info.Id;
        Select(_current);
    }

    public string? Current => _current;

    /// <summary>Opens a chat (or the newest one).</summary>
    public void Select(string? id)
    {
        if (id is not null && _hub.Get(id) is null) id = null;
        _current = id ?? _hub.Chats.FirstOrDefault()?.Info.Id;
        _to = "";
        if (_current is { } c) _hub.MarkRead(c);
        RenderList();
        RenderChat();
        Dispatcher.BeginInvoke(() => _box.Focus(), DispatcherPriority.Input);
    }

    private void OnChanged(string id)
    {
        if (id.Length == 0 || id != _current) _listDirty = true;
        if (id == _current)
        {
            _threadDirty = true;
            _listDirty = true;
        }
        if (!_redraw.IsEnabled) _redraw.Start();
    }

    private void Redraw()
    {
        _redraw.Stop();
        if (_current is { } id && _hub.Get(id) is null) Select(null);
        if (_listDirty) RenderList();
        if (_threadDirty)
        {
            if (_current is { } c && IsActive) _hub.MarkRead(c);
            RenderThread();
            RenderStatus();
            RenderHead();
        }
        _listDirty = _threadDirty = false;
    }

    // ---- layout ------------------------------------------------------------------------------

    private void Rebuild()
    {
        _root.Children.Clear();
        _root.RowDefinitions.Clear();
        _root.FlowDirection = Lang.Flow;
        var tint = new Border();
        tint.SetResourceReference(Border.BackgroundProperty, "Brush.Window");
        _root.Children.Add(tint);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleHeight) });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(TitleBar());
        var body = new Grid { Margin = new Thickness(8, 0, 8, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(Sidebar());
        Grid.SetColumn(_content, 2);
        body.Children.Add(_content);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);
        _root.Children.Add(layout);
        _thumbs.Clear();
        RenderList();
        RenderChat();
    }

    private UIElement TitleBar()
    {
        var bar = new DockPanel { Margin = new Thickness(16, 0, 8, 0), LastChildFill = false };
        var captions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var min = Caption("Lap.Caption", "M3,8 H13", Lang.T("Minimize", "تصغير"));
        min.Click += (_, _) => WindowState = WindowState.Minimized;
        var max = Caption("Lap.Caption", "M3.5,3.5 H12.5 V12.5 H3.5 Z", Lang.T("Maximize", "تكبير"));
        max.Click += (_, _) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        var close = Caption("Lap.Caption.Close", "M4,4 L12,12 M12,4 L4,12", Lang.T("Close", "إغلاق"));
        close.Click += (_, _) => Close();
        captions.Children.Add(min);
        captions.Children.Add(max);
        captions.Children.Add(close);
        DockPanel.SetDock(captions, Dock.Right);
        bar.Children.Add(captions);
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(SettingsPage.AccentTile("lap-chats", 24, 14).With(t => { t.Margin = new Thickness(0, 0, 10, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        brand.Children.Add(new TextBlock { Text = Title, Style = SettingsPage.Res("Lap.Text"), FontSize = 13.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(brand, Dock.Left);
        bar.Children.Add(brand);
        return bar;
    }

    private static Button Caption(string style, string data, string name)
    {
        var p = new Path { Data = Geometry.Parse(data), Width = 16, Height = 16, StrokeThickness = 1.25, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, SnapsToDevicePixels = true };
        p.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding { Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty), RelativeSource = System.Windows.Data.RelativeSource.Self });
        var b = new Button { Style = SettingsPage.Res(style), Content = p, ToolTip = name };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        WindowChrome.SetIsHitTestVisibleInChrome(b, true);
        return b;
    }

    private UIElement Sidebar()
    {
        var dock = new DockPanel();
        var add = SettingsPage.Button(Lang.T("New chat", "محادثة جديدة"), NewChat, accent: true);
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        add.Margin = new Thickness(4, 4, 4, 10);
        DockPanel.SetDock(add, Dock.Top);
        dock.Children.Add(add);
        var foot = SidebarFoot();
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);
        var scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = SettingsPage.Res("Lap.Scroll") };
        dock.Children.Add(scroll);
        return dock;
    }

    private UIElement SidebarFoot()
    {
        var stack = new StackPanel { Margin = new Thickness(6, 8, 6, 2) };
        foreach (var agent in Agent.All)
        {
            bool found = _program(agent) is not null;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = found ? ChatLook.GreenInk : ChatLook.AmberInk, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(ChatLook.Text(found ? Lang.T($"{Agent.Name(agent)} is ready on this PC", $"{Agent.Name(agent)} جاهز على هذا الحاسوب") : Lang.T($"{Agent.Name(agent)} not found", $"لم يُعثر على {Agent.Name(agent)}"), 11.5, "Brush.Text3"));
            stack.Children.Add(row);
        }
        return stack;
    }

    // ---- the list ----------------------------------------------------------------------------

    private void RenderList()
    {
        _list.Children.Clear();
        var chats = _hub.Chats;
        if (chats.Count == 0)
        {
            _list.Children.Add(ChatLook.Text(Lang.T("No chats yet.", "لا محادثات بعد."), 12.5, "Brush.Text3").With(t => t.Margin = new Thickness(10, 8, 10, 0)));
            return;
        }
        var groups = chats.GroupBy(c => c.Info.Folder ?? "").OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenByDescending(g => g.Max(c => c.Info.Updated));
        foreach (var group in groups)
        {
            bool quick = group.Key.Length == 0;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, _list.Children.Count == 0 ? 2 : 14, 6, 4), ToolTip = quick ? null : group.Key };
            head.Children.Add(SettingsPage.Glyph(quick ? "lap-bolt" : "lap-folder", 13, "Brush.Text3", 1.9).With(g => g.Margin = new Thickness(0, 0, 6, 0)));
            head.Children.Add(ChatLook.Text((quick ? Lang.T("Quick chats", "محادثات سريعة") : IOPath.GetFileName(IOPath.TrimEndingDirectorySeparator(group.Key))).ToUpper(System.Globalization.CultureInfo.CurrentCulture), 11, "Brush.Text3", FontWeights.SemiBold));
            _list.Children.Add(head);
            foreach (var chat in group.OrderByDescending(c => c.Info.Updated)) _list.Children.Add(Row(chat));
        }
    }

    private FrameworkElement Row(ChatFile chat)
    {
        var info = chat.Info;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var face = ChatLook.Faces(info, 30);
        face.Margin = new Thickness(0, 0, 9, 0);
        grid.Children.Add(face);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = ChatLook.Text(info.Title.Length > 0 ? info.Title : Lang.T("New chat", "محادثة جديدة"), 13, "Brush.Text", FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(title);
        var sub = ChatLook.Text(Subtitle(chat), 11.5, "Brush.Text3");
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(sub);
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        if (ChatHub.NeedsAnswer(chat) || info.Unread || _hub.WorkingIn(info.Id) is not null)
        {
            FrameworkElement mark = _hub.WorkingIn(info.Id) is not null
                ? ChatLook.Spinner(10)
                : new Ellipse { Width = 8, Height = 8, Fill = ChatHub.NeedsAnswer(chat) ? ChatLook.Frozen("#F59E0B") : (Brush)Application.Current.Resources["Brush.Accent"], VerticalAlignment = VerticalAlignment.Center };
            mark.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }
        var item = new RadioButton { Style = SettingsPage.Res("Lap.Nav"), Content = grid, GroupName = "chats", Height = 52, Padding = new Thickness(10, 0, 8, 0), Margin = new Thickness(0, 0, 0, 2), IsChecked = info.Id == _current };
        item.Checked += (_, _) => { if (_current != info.Id) Select(info.Id); };
        return item;
    }

    private string Subtitle(ChatFile chat)
    {
        if (_hub.WorkingIn(chat.Info.Id) is { } agent) return Lang.T($"{Agent.Name(agent)} is working…", $"{Agent.Name(agent)} يعمل…");
        if (ChatHub.NeedsAnswer(chat)) return Lang.T("Needs you", "يحتاجك");
        var last = chat.Messages.LastOrDefault(m => m.Kind == ChatKind.Text);
        if (last is null) return chat.Info.IsQuick ? Lang.T("Quick chat", "محادثة سريعة") : Lang.T("No messages yet", "لا رسائل بعد");
        var who = last.Author == ChatAuthor.User ? Lang.T("You", "أنت") : Agent.Name(last.Author);
        var text = Regex.Replace(Regex.Replace(last.Text.Length > 0 ? last.Text : last.Error ?? "", @"\*\*|`|^#+\s*", ""), @"\s+", " ").Trim();
        return $"{who}: {text}";
    }

    // ---- the open chat -----------------------------------------------------------------------

    /// <summary>Takes a reused part out of the layout it was in, so it can be placed again.</summary>
    private static void Detach(FrameworkElement e)
    {
        switch (e.Parent)
        {
            case Panel p: p.Children.Remove(e); break;
            case Decorator d: d.Child = null; break;
            case ContentControl c: c.Content = null; break;
        }
    }

    private void RenderChat()
    {
        foreach (var part in new FrameworkElement[] { _head, _lanes, _status, _scroll, _board }) Detach(part);
        _content.Children.Clear();
        _content.RowDefinitions.Clear();
        _content.ColumnDefinitions.Clear();
        var chat = _current is { } id ? _hub.Get(id) : null;
        if (chat is null)
        {
            _content.Children.Add(Panel(Empty()));
            return;
        }
        _content.ColumnDefinitions.Add(new ColumnDefinition());
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_boardOpen ? 8 : 0) });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_boardOpen ? 262 : 0) });

        var main = new DockPanel();
        DockPanel.SetDock(_head, Dock.Top);
        main.Children.Add(_head);
        DockPanel.SetDock(_lanes, Dock.Top);
        main.Children.Add(_lanes);
        var bottom = new StackPanel();
        bottom.Children.Add(_status);
        bottom.Children.Add(Composer(chat));
        DockPanel.SetDock(bottom, Dock.Bottom);
        main.Children.Add(bottom);
        _scroll.Content = _thread;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Style = SettingsPage.Res("Lap.Scroll");
        main.Children.Add(_scroll);
        _content.Children.Add(Panel(main));

        if (_boardOpen)
        {
            _board.Child = null;
            var board = Panel(_board);
            Grid.SetColumn(board, 2);
            _content.Children.Add(board);
        }
        RenderHead();
        RenderThread(toEnd: true);
        RenderStatus();
    }

    private static Border Panel(UIElement child)
    {
        var b = new Border { Child = child, CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), ClipToBounds = false };
        b.SetResourceReference(Border.BackgroundProperty, "Brush.Content");
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return b;
    }

    private UIElement Empty()
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 460 };
        stack.Children.Add(ChatLook.Pair(56).With(p => { p.HorizontalAlignment = HorizontalAlignment.Center; p.Margin = new Thickness(0, 0, 0, 14); }));
        stack.Children.Add(ChatLook.Text(Lang.T("Chat with Claude and Codex", "تحدّث مع Claude وCodex"), 18, "Brush.Text", FontWeights.SemiBold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center));
        stack.Children.Add(ChatLook.Text(Lang.T("One at a time, or both in one chat. In a group they hand work to each other with @, and you can steer either with @claude or @codex. A chat can work in a project folder and pick up the sessions you already have there.", "مع واحد منهما، أو كليهما في محادثة واحدة. في المجموعة يتبادلان العمل بـ @، ويمكنك توجيه أي منهما بـ @claude أو @codex. يمكن أن تعمل المحادثة في مجلد مشروع وتكمل الجلسات الموجودة فيه."), 13, "Brush.Text2").With(t => { t.TextAlignment = TextAlignment.Center; t.Margin = new Thickness(0, 8, 0, 16); }));
        stack.Children.Add(SettingsPage.Button(Lang.T("New chat", "محادثة جديدة"), NewChat, accent: true).With(b => b.HorizontalAlignment = HorizontalAlignment.Center));
        return stack;
    }

    private void RenderHead()
    {
        var chat = _current is { } id ? _hub.Get(id) : null;
        if (chat is null) return;
        var info = chat.Info;
        var dock = new DockPanel { Margin = new Thickness(16, 10, 10, 10), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var board = new Button { Style = SettingsPage.Res("Lap.Chip"), ToolTip = Lang.T("Board: folder, who owns what, rules", "اللوحة: المجلد ومن يملك ماذا والقواعد") };
        board.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { SettingsPage.Glyph("lap-board", 15, stroke: 1.8).With(g => g.Margin = new Thickness(0, 0, 6, 0)), ChatLook.Text(Lang.T("Board", "اللوحة"), 12.5, "Brush.Text2", FontWeights.SemiBold) } };
        board.Padding = new Thickness(10, 0, 12, 0);
        board.Click += (_, _) => { _boardOpen = !_boardOpen; RenderChat(); };
        buttons.Children.Add(board);
        DockPanel.SetDock(buttons, Dock.Right);
        dock.Children.Add(buttons);
        var face = ChatLook.Faces(info, 32);
        face.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(face, Dock.Left);
        dock.Children.Add(face);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = ChatLook.Text(info.Title.Length > 0 ? info.Title : Lang.T("New chat", "محادثة جديدة"), 15, "Brush.Text", FontWeights.SemiBold);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(title);
        var line = new List<string> { info.Folder ?? Lang.T("Quick chat · no folder", "محادثة سريعة · بلا مجلد") };
        if (info.IsDirected)
        {
            string director = Agent.Name(info.Director), guest = Agent.Name(info.Guest ?? Agent.Other(info.Director));
            line.Add(info.DirectorInPulse
                ? Lang.T($"{director} directs (carried on by Pulse) · with {guest}", $"{director} يوجّه (يتابعه نبض) · مع {guest}")
                : Lang.T($"{director} directs from its session · with {guest}", $"{director} يوجّه من جلسته · مع {guest}"));
        }
        else line.Add(info.IsGroup ? "Claude + Codex" : Agent.Name(info.Agents[0]));
        if (info.IsGroup && !info.IsDirected && RecipeName(info.Recipe) is { Length: > 0 } recipe) line.Add(recipe);
        var sub = ChatLook.Text(string.Join(" · ", line), 11.5, "Brush.Text3");
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        words.Children.Add(sub);
        dock.Children.Add(words);
        _head.Child = dock;
        _head.BorderThickness = new Thickness(0, 0, 0, 1);
        _head.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");

        var lanes = new WrapPanel { Margin = new Thickness(16, 7, 16, 7) };
        foreach (var agent in info.Agents)
        {
            var owns = ChatRules.OwnsOf(info, agent);
            if (owns.Count == 0) continue;
            var chip = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(9, 3, 9, 3), Margin = new Thickness(0, 0, 6, 0), Background = ChatLook.Soft(agent) };
            var t = new TextBlock { FontSize = 11.5, Foreground = ChatLook.Ink(agent) };
            t.Inlines.Add(new System.Windows.Documents.Run(Agent.Name(agent) + "  ") { FontWeight = FontWeights.SemiBold });
            t.Inlines.Add(new System.Windows.Documents.Run(string.Join("  ", owns)) { FontFamily = ChatLook.Mono, FontSize = 11 });
            chip.Child = t;
            lanes.Children.Add(chip);
        }
        if (info.IsGroup && info.Recipe == ChatRecipe.Reviewer)
            lanes.Children.Add(ChatLook.Text(Lang.T($"{Agent.Name(info.Lead)} writes · {Agent.Name(Agent.Other(info.Lead))} reviews and never edits", $"{Agent.Name(info.Lead)} يكتب · {Agent.Name(Agent.Other(info.Lead))} يراجع ولا يعدّل"), 11.5, "Brush.Text3"));
        _lanes.Child = lanes.Children.Count == 0 ? null : lanes;
        _lanes.BorderThickness = new Thickness(0, 0, 0, lanes.Children.Count == 0 ? 0 : 1);
        _lanes.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        if (_boardOpen) RenderBoard(chat);
    }

    public static string RecipeName(string recipe) => recipe switch
    {
        ChatRecipe.Builder => Lang.T("Builder + Artist", "بانٍ + فنان"),
        ChatRecipe.Reviewer => Lang.T("Coder + Reviewer", "مبرمج + مراجع"),
        ChatRecipe.Free => Lang.T("Free talk", "حديث حر"),
        _ => "",
    };

    public static string PermissionName(string permission) => permission switch
    {
        ChatPermission.Edit => Lang.T("Can edit", "يمكنه التعديل"),
        ChatPermission.Read => Lang.T("Read only", "قراءة فقط"),
        _ => Lang.T("Ask first", "اسأل أولًا"),
    };

    private void RenderThread(bool toEnd = false)
    {
        var chat = _current is { } id ? _hub.Get(id) : null;
        if (chat is null) return;
        bool atEnd = toEnd || _scroll.ScrollableHeight - _scroll.VerticalOffset < 60;
        _thread.Children.Clear();
        var act = new ChatActions(
            (m, state) => _hub.Answer(chat.Info.Id, m.Id, state),
            (path, maker, on) => _hub.Approve(chat.Info.Id, path, maker, on),
            (maker, path) => { _box.Text = $"@{maker} {Lang.T("redo", "أعد")} {ChatRules.Relative(chat.Info, path)}: "; _box.CaretIndex = _box.Text.Length; _box.Focus(); },
            path => PulseLaunch.Open(path),
            text => { try { Clipboard.SetText(text); } catch (System.Runtime.InteropServices.COMException) { } },
            _expanded.Contains,
            msg => { if (!_expanded.Remove(msg)) _expanded.Add(msg); RenderThread(); },
            Thumb,
            (agent, model) => _hub.SetModel(chat.Info.Id, agent, model));
        if (chat.Messages.Count == 0)
        {
            _thread.Children.Add(ChatLook.Text(chat.Info.IsGroup
                ? Lang.T("Write to both, or name one with @claude or @codex. When one of them needs the other, it says so with @, and Pulse passes it on.", "اكتب لكليهما، أو سمِّ واحدًا بـ @claude أو @codex. عندما يحتاج أحدهما الآخر يقول ذلك بـ @، فيمرره نبض.")
                : Lang.T($"Write to {Agent.Name(chat.Info.Agents[0])} below.", $"اكتب إلى {Agent.Name(chat.Info.Agents[0])} في الأسفل."), 12.5, "Brush.Text3").With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        }
        foreach (var m in chat.Messages.TakeLast(300))
        {
            var view = ChatViews.For(chat, m, act);
            view.Margin = new Thickness(view.Margin.Left, 0, view.Margin.Right, 14);
            _thread.Children.Add(view);
        }
        if (atEnd) Dispatcher.BeginInvoke(() => _scroll.ScrollToEnd(), DispatcherPriority.Loaded);
    }

    private ImageSource? Thumb(string path)
    {
        DateTime written;
        try { written = File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        if (_thumbs.TryGetValue(path, out var cached) && cached.Written == written) return cached.Image;
        var image = ChatViews.LoadThumbnail(path);
        _thumbs[path] = (written, image);
        return image;
    }

    private void RenderStatus()
    {
        _status.Children.Clear();
        if (_current is not { } id) return;
        if (_hub.WorkingIn(id) is { } agent)
        {
            var chat = _hub.Get(id);
            var reply = chat?.Messages.LastOrDefault(m => m.Working);
            var seconds = reply is null ? 0 : (int)Math.Max(0, (DateTimeOffset.UtcNow - reply.At).TotalSeconds);
            var chip = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(10, 3, 4, 3), Margin = new Thickness(0, 0, 6, 0) };
            chip.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ChatLook.Spinner(11).With(s => s.Margin = new Thickness(0, 0, 8, 0)));
            var last = reply?.Steps.LastOrDefault(s => s.Kind != "say")?.Text;
            var text = Lang.T($"{Agent.Name(agent)} · {Elapsed(seconds)}", $"{Agent.Name(agent)} · {Elapsed(seconds)}") + (last is null ? "" : " · " + ClaudeTrim(last));
            var t = ChatLook.Text(text, 11.5, "Brush.Text2", FontWeights.SemiBold);
            t.TextWrapping = TextWrapping.NoWrap;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.MaxWidth = 420;
            t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
            var stop = new Button { Style = SettingsPage.Res("Lap.Button"), Content = Lang.T("Stop", "إيقاف"), Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11.5, Margin = new Thickness(8, 0, 0, 0) };
            stop.Click += (_, _) => _hub.Stop(id);
            row.Children.Add(stop);
            chip.Child = row;
            _status.Children.Add(chip);
        }
        if (_hub.Get(id) is { } c && c.Info.IsGroup && c.Info.Hops > 0)
        {
            var hops = ChatLook.Text(Lang.T($"Handed over {c.Info.Hops} of {c.Info.HopLimit} times", $"تبادلا العمل {c.Info.Hops} من {c.Info.HopLimit} مرات"), 11.5, "Brush.Text3");
            hops.VerticalAlignment = VerticalAlignment.Center;
            hops.Margin = new Thickness(4, 0, 0, 0);
            _status.Children.Add(hops);
        }
        if (_hub.Get(id) is { } d) DirectorStatus(d);
        _status.Visibility = _status.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// In a chat an agent directs from its own session: whether it is waiting for the guest or
    /// hasn't checked in for a while, its budget, and the owner's two ways on when its session is
    /// gone: Pulse carries it on (Resume director) or the owner directs (Take over).
    /// </summary>
    private void DirectorStatus(ChatFile chat)
    {
        var info = chat.Info;
        if (!info.DirectorOutside && !(info.Closed && info.IsDirected)) return;
        if (info.Consent == ChatConsent.Open) return; // the card says it all
        string director = Agent.Name(info.Director), guest = Agent.Name(info.Guest ?? Agent.Other(info.Director));
        var chip = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(6, 3, 4, 3), Margin = new Thickness(0, 0, 6, 0) };
        chip.Background = ChatLook.Soft(info.Director);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(ChatLook.Avatar(info.Director, 18).With(a => { a.Margin = new Thickness(0, 0, 7, 0); a.VerticalAlignment = VerticalAlignment.Center; }));
        string text;
        bool offer = false, resumeFirst = false;
        if (info.Closed)
        {
            text = info.Consent == ChatConsent.Declined ? Lang.T("You didn't allow this chat", "لم تسمح بهذه المحادثة") : Lang.T($"{director} closed this chat", $"أغلق {director} هذه المحادثة");
        }
        else if (_hub.DirectorWaiting(info.Id))
        {
            text = _hub.WorkingIn(info.Id) is not null
                ? Lang.T($"{director} waits for {guest}'s reply", $"{director} ينتظر رد {guest}")
                : Lang.T($"{director} is reading what's new", $"{director} يقرأ الجديد");
        }
        else
        {
            var alive = ChatHub.DirectorAlive(info);
            var seen = info.DirectorSeen is { } at ? Lang.Ago(at, DateTimeOffset.UtcNow) : Lang.T("a while", "مدة");
            text = alive == false
                ? Lang.T($"{director}'s session is closed", $"جلسة {director} مغلقة")
                : _hub.WorkingIn(info.Id) is not null
                    ? Lang.T($"{guest} is working for {director}", $"{guest} يعمل لـ {director}")
                    : Lang.T($"Waiting for {director} · last heard {seen}", $"بانتظار {director} · آخر تواصل {seen}");
            offer = _hub.WorkingIn(info.Id) is null;
            resumeFirst = alive == false;
        }
        var t = ChatLook.Text(text, 11.5, "Brush.Text2", FontWeights.SemiBold);
        t.Foreground = ChatLook.Ink(info.Director);
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(0, 0, 6, 0);
        row.Children.Add(t);
        Button Mini(string label, Action click, bool accent)
        {
            var b = new Button { Style = SettingsPage.Res(accent ? "Lap.Button.Accent" : "Lap.Button"), Content = label, Height = 24, Padding = new Thickness(10, 0, 10, 0), FontSize = 11.5, Margin = new Thickness(4, 0, 0, 0) };
            b.Click += (_, _) => click();
            return b;
        }
        var id = info.Id;
        if (offer && info.DirectorSession is { Length: > 0 } && !info.Closed)
        {
            var resume = Mini(Lang.T("Resume director", "استئناف الموجّه"), () => _hub.ResumeDirector(id), resumeFirst);
            resume.ToolTip = Lang.T($"Pulse carries {director} on from a copy of its session (its own window stays as it was). {director} still directs.", $"يتابع نبض {director} من نسخة من جلسته (تبقى نافذته كما هي). يبقى {director} الموجّه.");
            row.Children.Add(resume);
        }
        if (offer || info.Closed)
        {
            var take = Mini(Lang.T("Take over", "تولَّ المحادثة"), () => _hub.TakeOver(id), false);
            take.ToolTip = Lang.T($"You direct this chat from now on. {director}'s session can't write here any more.", $"توجّه هذه المحادثة من الآن. لا تستطيع جلسة {director} الكتابة هنا بعد ذلك.");
            row.Children.Add(take);
        }
        chip.Child = row;
        _status.Children.Add(chip);
        if (!info.Closed && info.DirectorTurns > 0)
        {
            var budget = ChatLook.Text(Lang.T($"{director} wrote {info.DirectorTurns} of {info.DirectorBudget} times without you", $"كتب {director} {info.DirectorTurns} من {info.DirectorBudget} مرات دونك"), 11.5, "Brush.Text3");
            budget.VerticalAlignment = VerticalAlignment.Center;
            budget.Margin = new Thickness(4, 0, 0, 0);
            _status.Children.Add(budget);
        }
    }

    private static string ClaudeTrim(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private static string Elapsed(int seconds) => seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";

    // ---- the message box ---------------------------------------------------------------------

    private void BuildBox()
    {
        _box.Style = SettingsPage.Res("Lap.TextBox");
        _box.AcceptsReturn = true;
        _box.TextWrapping = TextWrapping.Wrap;
        _box.MinHeight = 40;
        _box.MaxHeight = 160;
        _box.Height = double.NaN;
        _box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _box.VerticalContentAlignment = VerticalAlignment.Top;
        _box.Padding = new Thickness(12, 9, 12, 9);
        _box.BorderThickness = new Thickness(0);
        _box.Background = Brushes.Transparent;
        _box.PreviewKeyDown += OnBoxKey;
        _box.TextChanged += (_, _) =>
        {
            UpdateMentions();
            _send.IsEnabled = _box.Text.Trim().Length > 0;
            _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        _hint.FontSize = 13.5;
        _hint.IsHitTestVisible = false;
        _hint.Margin = new Thickness(14, 10, 14, 0);
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
        _box.SelectionChanged += (_, _) => UpdateMentions();
        _mentions.Child = new Border { Child = _mentionList, CornerRadius = new CornerRadius(12), Padding = new Thickness(6), BorderThickness = new Thickness(1), MinWidth = 240 };
        _mentions.Placement = PlacementMode.Top;
        _mentions.PlacementTarget = _box;
        _mentions.AllowsTransparency = true;
        _mentions.StaysOpen = true;
        _send.Style = SettingsPage.Res("Lap.Button.Accent");
        _send.Width = 36;
        _send.Height = 36;
        _send.Padding = new Thickness(0);
        _send.Content = SettingsPage.Glyph("lap-send", 16, stroke: 2.2, brush: Brushes.White);
        _send.ToolTip = Lang.T("Send (Enter)", "إرسال (Enter)");
        _send.IsEnabled = false;
        _send.Click += (_, _) => Send();
    }

    private FrameworkElement Composer(ChatFile chat)
    {
        if (_mentions.Child is Border mb)
        {
            mb.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
            mb.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        }
        var holder = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Margin = new Thickness(12, 8, 12, 12), Padding = new Thickness(2, 2, 8, 8) };
        holder.SetResourceReference(Border.BackgroundProperty, "Brush.LapCard");
        holder.SetResourceReference(Border.BorderBrushProperty, "Brush.Line2");
        var stack = new StackPanel();
        if (_box.Parent is Panel old) old.Children.Remove(_box);
        if (_hint.Parent is Panel oldHint) oldHint.Children.Remove(_hint);
        var info = chat.Info;
        _hint.Text = info.Closed
            ? Lang.T("This chat is closed. Take over to carry on.", "هذه المحادثة مغلقة. تولَّها للمتابعة.")
            : info.DirectorOutside
                ? Lang.T($"Write to {Agent.Name(info.Guest ?? "")} and {Agent.Name(info.Director)}. Your word comes first", $"اكتب إلى {Agent.Name(info.Guest ?? "")} و{Agent.Name(info.Director)}. كلمتك أولًا")
                : info.IsGroup ? Lang.T("Message Claude and Codex. Type @ to pick one", "راسل Claude وCodex. اكتب @ لاختيار أحدهما") : Lang.T($"Message {Agent.Name(info.Agents[0])}", $"راسل {Agent.Name(info.Agents[0])}");
        _box.IsEnabled = !info.Closed;
        _hint.Visibility = _box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var field = new Grid { Children = { _box, _hint } };
        stack.Children.Add(field);
        var tools = new DockPanel { Margin = new Thickness(10, 2, 0, 0), LastChildFill = false };
        DockPanel.SetDock(_send, Dock.Right);
        if (_send.Parent is Panel sp) sp.Children.Remove(_send);
        tools.Children.Add(_send);
        if (chat.Info.IsGroup)
        {
            var label = ChatLook.Text(Lang.T("To", "إلى"), 11.5, "Brush.Text3", FontWeights.SemiBold);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 8, 0);
            tools.Children.Add(label);
            var current = _to.Length > 0 ? _to : chat.Info.Lead;
            var choice = SettingsPage.Choice(["both", Agent.Claude, Agent.Codex], current, v => _to = v, v => v == "both" ? Lang.T("Both", "كلاهما") : Agent.Name(v));
            tools.Children.Add(choice);
        }
        var mode = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 3, 10, 3), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = PermissionHelp(chat.Info) };
        mode.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
        mode.Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { SettingsPage.Glyph("lap-shield", 12, "Brush.Text2", 2).With(g => g.Margin = new Thickness(0, 0, 5, 0)), ChatLook.Text(PermissionName(chat.Info.Permission), 11.5, "Brush.Text2", FontWeights.SemiBold) } };
        tools.Children.Add(mode);
        stack.Children.Add(tools);
        holder.Child = stack;
        return holder;
    }

    public static string PermissionHelp(ChatInfo chat) => chat.Permission switch
    {
        ChatPermission.Edit => Lang.T("They change files in this folder (in a group, each in its own folders). Commands still ask you first.", "يغيّران الملفات في هذا المجلد (في المجموعة كلٌّ في مجلداته). الأوامر تسألك أولًا."),
        ChatPermission.Read => Lang.T("They read, explain and plan, but change nothing.", "يقرآن ويشرحان ويخططان، لكن لا يغيّران شيئًا."),
        _ => Lang.T("Claude asks before every edit and command. Codex can't stop to ask, so here it only reads.", "يسأل Claude قبل كل تعديل وأمر. لا يستطيع Codex التوقف ليسأل، لذا يقرأ فقط هنا."),
    };

    private void OnBoxKey(object sender, KeyEventArgs e)
    {
        if (_mentions.IsOpen && _mentionList.Children.Count > 0)
        {
            if (e.Key is Key.Tab or Key.Enter && _mentionList.Children[0] is Button first)
            {
                first.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                _mentions.IsOpen = false;
                e.Handled = true;
                return;
            }
        }
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Send();
        }
    }

    private void UpdateMentions()
    {
        var chat = _current is { } id ? _hub.Get(id) : null;
        var before = _box.Text[..Math.Min(_box.CaretIndex, _box.Text.Length)];
        var m = Regex.Match(before, @"(^|\s)@(\w*)$");
        if (chat is null || !chat.Info.IsGroup || !m.Success)
        {
            _mentions.IsOpen = false;
            return;
        }
        var typed = m.Groups[2].Value.ToLowerInvariant();
        _mentionList.Children.Clear();
        foreach (var (key, label, help) in new[]
        {
            (Agent.Claude, "@claude", Lang.T("Only Claude answers", "يجيب Claude فقط")),
            (Agent.Codex, "@codex", Lang.T("Only Codex answers", "يجيب Codex فقط")),
            ("both", "@both", Lang.T("Both answer, one at a time", "يجيب كلاهما، واحدًا تلو الآخر")),
        })
        {
            if (!key.StartsWith(typed, StringComparison.Ordinal)) continue;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(key == "both" ? ChatLook.Pair(24) : ChatLook.Avatar(key, 24));
            var words = new StackPanel { Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(ChatLook.Text(label, 12.5, "Brush.Text", FontWeights.SemiBold).With(t => t.FlowDirection = FlowDirection.LeftToRight));
            words.Children.Add(ChatLook.Text(help, 11, "Brush.Text3"));
            row.Children.Add(words);
            var b = new Button { Style = SettingsPage.Res("Lap.Button.Ghost"), Content = row, HorizontalContentAlignment = HorizontalAlignment.Left, Height = 44, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 2), Padding = new Thickness(6, 0, 10, 0) };
            var start = m.Index + m.Groups[1].Length;
            b.Click += (_, _) =>
            {
                int caret = _box.CaretIndex;
                _box.Text = _box.Text[..start] + label + " " + _box.Text[caret..];
                _box.CaretIndex = start + label.Length + 1;
                _mentions.IsOpen = false;
                _box.Focus();
            };
            _mentionList.Children.Add(b);
        }
        _mentions.IsOpen = _mentionList.Children.Count > 0;
    }

    private void Send()
    {
        if (_current is not { } id) return;
        var text = _box.Text.Trim();
        if (text.Length == 0) return;
        var chat = _hub.Get(id);
        string? to = chat is { Info.IsGroup: true } ? (_to.Length > 0 ? _to : null) : null;
        _box.Clear();
        _mentions.IsOpen = false;
        _hub.Send(id, text, to);
        RenderThread(toEnd: true);
    }

    // ---- the board ---------------------------------------------------------------------------

    private void RenderBoard(ChatFile chat)
    {
        var info = chat.Info;
        var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        void Section(string title)
        {
            stack.Children.Add(ChatLook.Text(title.ToUpper(System.Globalization.CultureInfo.CurrentCulture), 11, "Brush.Text3", FontWeights.SemiBold).With(t => t.Margin = new Thickness(0, stack.Children.Count == 0 ? 0 : 18, 0, 8)));
        }
        void Line(string key, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var v = ChatLook.Text(value, 12, "Brush.Text", FontWeights.SemiBold);
            v.TextAlignment = TextAlignment.Right;
            v.MaxWidth = 130;
            DockPanel.SetDock(v, Dock.Right);
            row.Children.Add(v);
            row.Children.Add(ChatLook.Text(key, 12, "Brush.Text2"));
            stack.Children.Add(row);
        }

        Section(Lang.T("Folder", "المجلد"));
        var folder = ChatLook.Text(info.Folder ?? Lang.T("Quick chat: Amir Lap's own folder", "محادثة سريعة: مجلد أمير لاب نفسه"), 12, "Brush.Text");
        folder.FlowDirection = FlowDirection.LeftToRight;
        stack.Children.Add(folder);
        stack.Children.Add(ChatLook.Small(Lang.T("Open folder", "فتح المجلد"), () => PulseLaunch.Open(_hub.Store.WorkFolder(info))).With(b => { b.Margin = new Thickness(0, 8, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Left; }));

        if (info.IsDirected || info.TakenFrom is not null)
        {
            var directorId = info.IsDirected ? info.Director : info.TakenFrom!;
            string director = Agent.Name(directorId);
            Section(Lang.T("Director", "الموجّه"));
            var who = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
            who.Children.Add(ChatLook.Avatar(directorId, 22).With(a => a.Margin = new Thickness(0, 0, 8, 0)));
            who.Children.Add(ChatLook.Text(info.IsDirected
                ? info.DirectorInPulse
                    ? Lang.T($"{director}, carried on by Pulse from a copy of its session", $"{director}، يتابعه نبض من نسخة من جلسته")
                    : Lang.T($"{director}, from its own session with you", $"{director}، من جلسته الخاصة معك")
                : Lang.T($"You (taken over from {director})", $"أنت (توليتها من {director})"), 12, "Brush.Text").With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.MaxWidth = 200; }));
            stack.Children.Add(who);
            if (info.DirectorSession is { Length: > 0 } ds)
                stack.Children.Add(ChatLook.Text(ds, 11, "Brush.Text3").With(t => { t.FontFamily = ChatLook.Mono; t.FlowDirection = FlowDirection.LeftToRight; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; t.ToolTip = ds; }));
            // A path reads left to right in either language (the marks keep its slashes in place).
            if (info.Outputs is { Length: > 0 } outputs) Line(Lang.T("Outputs go to", "تُحفظ المخرجات في"), "‎" + outputs.TrimEnd('/') + "/‎");
            if (info.IsDirected) Line(Lang.T("Messages without you", "رسائل دونك"), $"{info.DirectorTurns} / {info.DirectorBudget}");
            if (info.DirectorNotes is { Length: > 0 } notes && File.Exists(notes))
                stack.Children.Add(ChatLook.Small(Lang.T("Read the conversation copy", "قراءة نسخة المحادثة"), () => PulseLaunch.Open(notes)).With(b => { b.Margin = new Thickness(0, 6, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Left; b.ToolTip = Lang.T($"What {director} and you said before it opened this chat, as the guest can read it", $"ما قلتماه أنت و{director} قبل فتح هذه المحادثة، كما يقرؤه الضيف"); }));
        }

        if (info.IsGroup)
        {
            Section(Lang.T("Who owns what", "من يملك ماذا"));
            bool any = false;
            foreach (var agent in info.Agents)
            {
                var owns = ChatRules.OwnsOf(info, agent);
                if (owns.Count == 0) continue;
                any = true;
                var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                row.Children.Add(new TextBlock { Text = Agent.Name(agent), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ChatLook.Ink(agent), Width = 56, VerticalAlignment = VerticalAlignment.Center });
                foreach (var o in owns)
                {
                    var chip = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 0, 4, 4) };
                    chip.SetResourceReference(Border.BackgroundProperty, "Brush.Line2");
                    chip.Child = new TextBlock { Text = o, FontFamily = ChatLook.Mono, FontSize = 11, FlowDirection = FlowDirection.LeftToRight }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text"));
                    row.Children.Add(chip);
                }
                stack.Children.Add(row);
            }
            if (!any) stack.Children.Add(ChatLook.Text(info.Recipe == ChatRecipe.Reviewer
                ? Lang.T($"{Agent.Name(info.Lead)} writes; {Agent.Name(Agent.Other(info.Lead))} reviews and never edits.", $"{Agent.Name(info.Lead)} يكتب؛ {Agent.Name(Agent.Other(info.Lead))} يراجع ولا يعدّل.")
                : Lang.T("Both may change files; they ask before touching the same one.", "يمكن لكليهما تغيير الملفات؛ ويسألان قبل لمس الملف نفسه."), 12, "Brush.Text2"));
        }

        Section(Lang.T("Rules", "القواعد"));
        Line(Lang.T("Changes on your PC", "التغييرات على حاسوبك"), PermissionName(info.Permission));
        Line(Lang.T("Commands", "الأوامر"), Lang.T("Ask me", "اسألني"));
        foreach (var agent in info.Agents)
            Line(Lang.T($"{Agent.Name(agent)}'s model", $"نموذج {Agent.Name(agent)}"), ChatLook.ModelName(agent, info.ModelOf(agent)));
        if (info.IsGroup && !info.IsDirected)
        {
            Line(Lang.T("Answers without @", "يجيب دون @"), info.Lead == "both" ? Lang.T("Both", "كلاهما") : Agent.Name(info.Lead));
            Line(Lang.T("Hand-offs before asking you", "مرات التبادل قبل سؤالك"), info.HopLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (info.AllowedTools.Count > 0)
            stack.Children.Add(ChatLook.Text(Lang.T("Always allowed: ", "مسموح دائمًا: ") + string.Join(", ", info.AllowedTools), 11.5, "Brush.Text3").With(t => t.Margin = new Thickness(0, 4, 0, 0)));

        Section(Lang.T("Their own sessions", "جلساتهما الخاصة"));
        foreach (var agent in info.Agents)
        {
            var session = info.Sessions.GetValueOrDefault(agent);
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            if (session is { Length: > 0 } && info.Folder is not null)
            {
                var open = new Button { Style = SettingsPage.Res("Lap.Chip"), Content = SettingsPage.Glyph("lap-terminal", 15, stroke: 1.8), ToolTip = Lang.T($"Continue this session in {Agent.Name(agent)} in a terminal", $"تابع هذه الجلسة في {Agent.Name(agent)} داخل الطرفية") };
                open.Click += (_, _) => OpenInTerminal(info, agent, session);
                DockPanel.SetDock(open, Dock.Right);
                row.Children.Add(open);
            }
            var words = new StackPanel();
            words.Children.Add(new TextBlock { Text = Agent.Name(agent), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ChatLook.Ink(agent) });
            words.Children.Add(ChatLook.Text(session is { Length: > 0 } ? session : Lang.T("Starts on its first turn", "تبدأ في دوره الأول"), 11, "Brush.Text3").With(t => { t.FontFamily = ChatLook.Mono; t.FlowDirection = FlowDirection.LeftToRight; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }));
            row.Children.Add(words);
            stack.Children.Add(row);
        }
        stack.Children.Add(ChatLook.Text(Lang.T("Each agent keeps its own session, so you can carry on in Claude Code or Codex later.", "يحتفظ كل وكيل بجلسته، فيمكنك المتابعة لاحقًا في Claude Code أو Codex."), 11.5, "Brush.Text3"));

        var delete = ChatLook.Small(Lang.T("Delete chat", "حذف المحادثة"), () => ConfirmDelete(info), ghost: true);
        delete.Margin = new Thickness(0, 20, 0, 0);
        delete.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(delete);
        _board.Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = SettingsPage.Res("Lap.Scroll") };
    }

    private void ConfirmDelete(ChatInfo info)
    {
        var answer = MessageBox.Show(this,
            Lang.T($"Delete “{info.Title}” from Pulse? Claude's and Codex's own sessions stay on this PC.", $"حذف “{info.Title}” من نبض؟ تبقى جلسات Claude وCodex الخاصة على هذا الحاسوب."),
            Lang.T("Delete chat", "حذف المحادثة"), MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;
        _hub.Delete(info.Id);
        Select(null);
    }

    private void OpenInTerminal(ChatInfo info, string agent, string session)
    {
        if (_program(agent) is not { } exe || info.Folder is not { } folder) return;
        var args = agent == Agent.Codex ? $"resume {session}" : $"--resume {session}";
        try
        {
            var command = $"\"{exe}\" {args}";
            var wt = AgentLocatorPath("wt.exe");
            var start = wt is not null
                ? new ProcessStartInfo(wt) { ArgumentList = { "-d", folder, "cmd", "/k", command }, UseShellExecute = false }
                : new ProcessStartInfo("cmd.exe", $"/k \"{command}\"") { WorkingDirectory = folder, UseShellExecute = true };
            Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Warn($"Pulse could not open a terminal: {ex.Message}");
        }
    }

    private static string? AgentLocatorPath(string file)
    {
        var local = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", file);
        return File.Exists(local) ? local : null;
    }

    // ---- new chat ----------------------------------------------------------------------------

    private void NewChat()
    {
        var dialog = new NewChatWindow(_hub, _program, _current is { } id ? _hub.Get(id)?.Info.Folder : null) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Created is { } created) Select(created.Info.Id);
    }
}
