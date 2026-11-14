using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core.Pulse.Chat;
using Toolbox.Windows.Home;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Pulse.Chat;

/// <summary>
/// Starting a chat: who is in it (Claude, Codex or both), where it works (a project folder, where
/// it can pick up an earlier session as a copy, or a quick chat with no folder), and for a group
/// how they work together, who owns what, how often they may hand work over, and what they may change.
/// </summary>
internal sealed class NewChatWindow : GlassWindow
{
    private const string OptionTemplate = """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
          <Border x:Name="B" CornerRadius="12" BorderThickness="1.5" Padding="{TemplateBinding Padding}" Background="{DynamicResource Brush.LapCard}" BorderBrush="{DynamicResource Brush.Line2}">
            <ContentPresenter />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="B" Property="BorderBrush" Value="{DynamicResource Brush.Text3}" /></Trigger>
            <Trigger Property="IsChecked" Value="True"><Setter TargetName="B" Property="Background" Value="{DynamicResource Brush.AccentWash}" /><Setter TargetName="B" Property="BorderBrush" Value="{DynamicResource Brush.Accent}" /></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="B" Property="BorderBrush" Value="{DynamicResource Brush.Accent}" /></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.5" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """;

    private static ControlTemplate? _template;

    private readonly ChatHub _hub;
    private readonly Func<string, string?> _program;
    private readonly Grid _root = new();
    private readonly StackPanel _left = new();
    private readonly StackPanel _right = new();
    private readonly TextBox _folder;
    private readonly TextBox _name;
    private readonly TextBox _ownsClaude;
    private readonly TextBox _ownsCodex;
    private readonly StackPanel _sessions = new();
    private readonly Button _create;
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Dictionary<string, string?> _versions = [];
    private List<PastSession> _found = [];
    private string _who = "both";
    private bool _quick;
    private string _recipe = ChatRecipe.Builder;
    private string _lead = Agent.Claude;
    private string? _perm;
    private int _hops = 4;
    private PastSession? _pick;
    private bool _nameTouched;
    private int _loadId;

    public NewChatWindow(ChatHub hub, Func<string, string?> program, string? folder)
    {
        _hub = hub;
        _program = program;
        Title = Lang.T("New chat", "محادثة جديدة");
        Width = 880;
        Height = 700;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = Lang.Flow;
        _folder = SettingsPage.TextBox(folder ?? "");
        _folder.FlowDirection = FlowDirection.LeftToRight;
        _folder.TextChanged += (_, _) => { _pick = null; LoadSessions(); Refresh(); };
        _name = SettingsPage.TextBox("");
        _name.TextChanged += (_, _) => { if (_name.IsKeyboardFocusWithin) _nameTouched = true; };
        _ownsClaude = SettingsPage.TextBox("");
        _ownsCodex = SettingsPage.TextBox("");
        _ownsClaude.FlowDirection = _ownsCodex.FlowDirection = FlowDirection.LeftToRight;
        _create = SettingsPage.Button(Lang.T("Create chat", "إنشاء المحادثة"), Create, accent: true);
        foreach (var agent in Agent.All) _versions[agent] = null;
        Content = _root;
        Build();
        ApplyRecipeDefaults();
        Refresh();
        LoadSessions();
        Loaded += (_, _) => _ = LoadVersionsAsync();
    }

    /// <summary>The chat that was made (after Create).</summary>
    public ChatFile? Created { get; private set; }

    private static ControlTemplate OptionLook => _template ??= (ControlTemplate)XamlReader.Parse(OptionTemplate);

    private void Build()
    {
        var layout = new DockPanel();
        var head = new DockPanel { Margin = new Thickness(24, 18, 16, 6) };
        var close = new Button { Style = SettingsPage.Res("Lap.Caption.Close"), Width = 34, Height = 30, Content = SettingsPage.Glyph("close", 12, stroke: 2.2), ToolTip = Lang.T("Close", "إغلاق") };
        close.Click += (_, _) => { DialogResult = false; };
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock { Text = Title, FontSize = 19, FontWeight = FontWeights.SemiBold, Style = SettingsPage.Res("Lap.Text"), VerticalAlignment = VerticalAlignment.Center });
        MakeDragHandle(head);
        DockPanel.SetDock(head, Dock.Top);
        layout.Children.Add(head);

        var foot = new DockPanel { Margin = new Thickness(24, 10, 24, 18), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(SettingsPage.Button(Lang.T("Cancel", "إلغاء"), () => DialogResult = false, ghost: true).With(b => b.Margin = new Thickness(0, 0, 8, 0)));
        buttons.Children.Add(_create);
        DockPanel.SetDock(buttons, Dock.Right);
        foot.Children.Add(buttons);
        foot.Children.Add(_problem);
        DockPanel.SetDock(foot, Dock.Bottom);
        layout.Children.Add(foot);
        var line = new Border { Height = 1, Margin = new Thickness(0) };
        line.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
        DockPanel.SetDock(line, Dock.Bottom);
        layout.Children.Add(line);

        var cols = new Grid { Margin = new Thickness(24, 6, 24, 10) };
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        cols.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        cols.ColumnDefinitions.Add(new ColumnDefinition());
        var l = new ScrollViewer { Content = _left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = SettingsPage.Res("Lap.Scroll") };
        var r = new ScrollViewer { Content = _right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Style = SettingsPage.Res("Lap.Scroll") };
        cols.Children.Add(l);
        Grid.SetColumn(r, 2);
        cols.Children.Add(r);
        layout.Children.Add(cols);
        _root.Children.Add(new Border { Child = layout }.With(b => b.SetResourceReference(Border.BackgroundProperty, "Brush.Content")));
    }

    private static TextBlock Heading(string text, bool first = false) =>
        ChatLook.Text(text.ToUpper(System.Globalization.CultureInfo.CurrentCulture), 11, "Brush.Text3", FontWeights.SemiBold).With(t => t.Margin = new Thickness(0, first ? 4 : 20, 0, 8));

    private RadioButton Option(string group, bool on, UIElement content, Action picked, double pad = 11)
    {
        var rb = new RadioButton { Template = OptionLook, GroupName = group, IsChecked = on, Content = content, Padding = new Thickness(12, pad, 12, pad), Margin = new Thickness(0, 0, 0, 6), Cursor = System.Windows.Input.Cursors.Hand, FocusVisualStyle = null };
        rb.Checked += (_, _) => picked();
        return rb;
    }

    private static FrameworkElement Words(string title, string sub, string subBrush = "Brush.Text3")
    {
        var s = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        s.Children.Add(ChatLook.Text(title, 13.5, "Brush.Text", FontWeights.SemiBold));
        s.Children.Add(ChatLook.Text(sub, 11.5, subBrush).With(t => t.Margin = new Thickness(0, 1, 0, 0)));
        return s;
    }

    private void Refresh()
    {
        bool group = _who == "both";
        _left.Children.Clear();
        _right.Children.Clear();

        // Who
        _left.Children.Add(Heading(Lang.T("Who's in it", "من فيها"), first: true));
        foreach (var key in new[] { Agent.Claude, Agent.Codex, "both" })
        {
            var row = new DockPanel();
            var face = key == "both" ? ChatLook.Pair(34) : ChatLook.Avatar(key, 32);
            face.Margin = new Thickness(0, 0, 11, 0);
            DockPanel.SetDock(face, Dock.Left);
            row.Children.Add(face);
            string title = key == "both" ? Lang.T("Claude and Codex", "Claude وCodex") : Agent.Name(key);
            string sub = key == "both" ? Lang.T("A group: they can talk to each other", "مجموعة: يمكنهما التحدث معًا") : Ready(key);
            row.Children.Add(Words(title, sub, key != "both" && _program(key) is null ? "Brush.Warning" : "Brush.Text3"));
            _left.Children.Add(Option("who", _who == key, row, () => { _who = key; _pick = null; ApplyRecipeDefaults(); Refresh(); }));
        }

        // Where
        _left.Children.Add(Heading(Lang.T("Where", "أين")));
        _left.Children.Add(SettingsPage.Choice(["folder", "quick"], _quick ? "quick" : "folder", v => { _quick = v == "quick"; _pick = null; Refresh(); }, v => v == "folder" ? Lang.T("In a folder", "في مجلد") : Lang.T("Quick chat, no folder", "محادثة سريعة بلا مجلد")));
        if (!_quick)
        {
            var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var choose = SettingsPage.Button(Lang.T("Choose…", "اختيار…"), ChooseFolder);
            choose.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(choose, Dock.Right);
            row.Children.Add(choose);
            if (_folder.Parent is Panel p) p.Children.Remove(_folder);
            row.Children.Add(_folder);
            _left.Children.Add(row);
            _left.Children.Add(_sessions);
            RenderSessions();
        }
        else
        {
            var box = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(14, 11, 14, 11), Margin = new Thickness(0, 10, 0, 0) };
            box.SetResourceReference(Border.BackgroundProperty, "Brush.Line");
            box.Child = ChatLook.Text(Lang.T("Runs in Amir Lap's own folder. The agents can answer, plan and make pictures here, but they can't change files on your PC. Good for ideas, names, questions and drafts.", "تعمل في مجلد أمير لاب نفسه. يمكن للوكلاء الإجابة والتخطيط وصنع الصور هنا، لكن لا يمكنهم تغيير ملفات حاسوبك. مناسبة للأفكار والأسماء والأسئلة والمسودات."), 12.5, "Brush.Text2");
            _left.Children.Add(box);
        }

        // How they work together
        if (group)
        {
            _right.Children.Add(Heading(Lang.T("How they work together", "كيف يعملان معًا"), first: true));
            foreach (var (key, help) in new[]
            {
                (ChatRecipe.Builder, Lang.T("One builds, the other makes the pictures. Like a clothing gallery: Claude builds the site, Codex draws each garment.", "أحدهما يبني والآخر يصنع الصور. مثل معرض ملابس: Claude يبني الموقع وCodex يرسم كل قطعة.")),
                (ChatRecipe.Reviewer, Lang.T("One writes the change, the other checks it and sends notes back. The reviewer never edits.", "أحدهما يكتب التغيير والآخر يراجعه ويرسل ملاحظاته. المراجع لا يعدّل أبدًا.")),
                (ChatRecipe.Free, Lang.T("Both answer and edit; they ask before touching the same file.", "كلاهما يجيب ويعدّل، ويسألان قبل لمس الملف نفسه.")),
            })
            {
                _right.Children.Add(Option("recipe", _recipe == key, Words(ChatWindow.RecipeName(key), help), () => { _recipe = key; ApplyRecipeDefaults(); Refresh(); }, 9));
            }
        }

        // Rules
        _right.Children.Add(Heading(Lang.T("Rules", "القواعد"), first: !group));
        if (group)
        {
            _right.Children.Add(RuleRow(Lang.T("Answers when you don't use @", "يجيب عندما لا تستخدم @"),
                SettingsPage.Choice([Agent.Claude, Agent.Codex, "both"], _lead, v => { _lead = v; ApplyRecipeDefaults(); Refresh(); }, v => v == "both" ? Lang.T("Both", "كلاهما") : Agent.Name(v))));
            if (!_quick && _recipe == ChatRecipe.Builder)
            {
                _right.Children.Add(OwnsRow(Agent.Claude, _ownsClaude));
                _right.Children.Add(OwnsRow(Agent.Codex, _ownsCodex));
            }
            var stepper = new StackPanel { Orientation = Orientation.Horizontal };
            var down = SettingsPage.Button("−", () => { _hops = Math.Max(1, _hops - 1); Refresh(); });
            var up = SettingsPage.Button("+", () => { _hops = Math.Min(12, _hops + 1); Refresh(); });
            down.Width = up.Width = 34;
            down.Padding = up.Padding = new Thickness(0);
            stepper.Children.Add(down);
            stepper.Children.Add(ChatLook.Text(Lang.T($"{_hops} times", $"{_hops} مرات"), 12.5, "Brush.Text", FontWeights.SemiBold).With(t => { t.Width = 70; t.TextAlignment = TextAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }));
            stepper.Children.Add(up);
            _right.Children.Add(RuleRow(Lang.T("Hand-offs before they wait for you", "مرات التبادل قبل أن ينتظراك"), stepper));
        }
        var perms = _quick ? new[] { ChatPermission.Read } : ChatPermission.All.ToArray();
        var perm = _quick ? ChatPermission.Read : Permission;
        _right.Children.Add(RuleRow(Lang.T("Changes on your PC", "التغييرات على حاسوبك"), SettingsPage.Choice(perms, perm, v => { _perm = v; Refresh(); }, ChatWindow.PermissionName)));
        _right.Children.Add(ChatLook.Text(ChatWindow.PermissionHelp(new ChatInfo { Permission = perm }), 11.5, "Brush.Text3").With(t => t.Margin = new Thickness(0, 2, 0, 0)));

        _right.Children.Add(Heading(Lang.T("Chat name", "اسم المحادثة")));
        if (_name.Parent is Panel np) np.Children.Remove(_name);
        if (!_nameTouched) _name.Text = DefaultName();
        _right.Children.Add(new Grid { Children = { _name } });

        Validate();
    }

    private string Ready(string agent)
    {
        if (_program(agent) is not { } exe) return Lang.T("Not found on this PC", "غير موجود على هذا الحاسوب");
        var version = AgentLocator.VersionNumber(_versions.GetValueOrDefault(agent));
        var from = exe.Contains(".sandbox-bin", StringComparison.OrdinalIgnoreCase) ? Lang.T(" · from the Codex app", " · من تطبيق Codex") : "";
        return (version is null ? Lang.T("Ready", "جاهز") : $"{Agent.Name(agent)} {version}") + from;
    }

    private async Task LoadVersionsAsync()
    {
        foreach (var agent in Agent.All)
        {
            if (_program(agent) is not { } exe) continue;
            _versions[agent] = await Task.Run(() => AgentLocator.Version(exe));
        }
        if (IsLoaded) Refresh();
    }

    private string Permission => _perm ?? (_who == "both" && _recipe != ChatRecipe.Reviewer ? ChatPermission.Edit : _who == "both" ? ChatPermission.Edit : ChatPermission.Ask);

    private void ApplyRecipeDefaults()
    {
        var owns = ChatRules.DefaultOwns(_recipe, _lead == "both" ? Agent.Claude : _lead);
        _ownsClaude.Text = string.Join(", ", owns.GetValueOrDefault(Agent.Claude) ?? []);
        _ownsCodex.Text = string.Join(", ", owns.GetValueOrDefault(Agent.Codex) ?? []);
    }

    private string DefaultName()
    {
        if (_pick is { } p) return p.Title;
        if (_who != "both") return Lang.T($"Chat with {Agent.Name(_who)}", $"محادثة مع {Agent.Name(_who)}");
        return _recipe switch
        {
            ChatRecipe.Builder => Lang.T("Build with pictures", "بناء مع صور"),
            ChatRecipe.Reviewer => Lang.T("Write and review", "كتابة ومراجعة"),
            _ => Lang.T("Claude and Codex", "Claude وCodex"),
        };
    }

    private static FrameworkElement RuleRow(string label, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        control.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(ChatLook.Text(label, 12.5, "Brush.Text2", FontWeights.SemiBold).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 12, 0); }));
        return row;
    }

    private static FrameworkElement OwnsRow(string agent, TextBox box)
    {
        if (box.Parent is Panel p) p.Children.Remove(box);
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var label = new TextBlock { Text = Lang.T($"{Agent.Name(agent)} owns", $"يملك {Agent.Name(agent)}"), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = ChatLook.Ink(agent), Width = 104, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left);
        row.Children.Add(label);
        box.ToolTip = Lang.T("Folders and files, separated by commas. The other agent doesn't change these.", "مجلدات وملفات مفصولة بفواصل. لا يغيّرها الوكيل الآخر.");
        row.Children.Add(box);
        return row;
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Lang.T("The folder this chat works in", "المجلد الذي تعمل فيه هذه المحادثة") };
        if (Directory.Exists(_folder.Text.Trim())) dialog.InitialDirectory = _folder.Text.Trim();
        if (dialog.ShowDialog(this) == true) _folder.Text = dialog.FolderName;
    }

    // ---- earlier sessions ----------------------------------------------------------------------

    private void LoadSessions()
    {
        int id = ++_loadId;
        var folder = _folder.Text.Trim();
        _found = [];
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            RenderSessions();
            return;
        }
        Task.Run(() => AgentSessions.Claude(folder, max: 6).Concat(AgentSessions.Codex(folder, max: 6)).OrderByDescending(s => s.Updated).ToList())
            .ContinueWith(t =>
            {
                if (id != _loadId || t.IsFaulted) return;
                _found = t.Result;
                RenderSessions();
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void RenderSessions()
    {
        _sessions.Children.Clear();
        _sessions.Margin = new Thickness(0, 12, 0, 0);
        var folder = _folder.Text.Trim();
        if (folder.Length > 0 && !Directory.Exists(folder))
        {
            _sessions.Children.Add(ChatLook.Text(Lang.T("This folder isn't there.", "هذا المجلد غير موجود."), 12, "Brush.Warning"));
            return;
        }
        var shown = _found.Where(s => _who == "both" || s.Agent == _who).Take(8).ToList();
        if (folder.Length == 0) return;
        _sessions.Children.Add(ChatLook.Text(shown.Count == 0 ? Lang.T("No earlier sessions in this folder. The chat starts fresh.", "لا جلسات سابقة في هذا المجلد. تبدأ المحادثة من جديد.") : Lang.T("Already in this folder · pick one to continue", "موجودة في هذا المجلد · اختر واحدة للمتابعة"), 12, "Brush.Text2", FontWeights.SemiBold).With(t => t.Margin = new Thickness(0, 0, 0, 6)));
        if (shown.Count == 0) return;
        var fresh = new DockPanel();
        fresh.Children.Add(Words(Lang.T("Start fresh", "ابدأ من جديد"), Lang.T("A new chat in this folder", "محادثة جديدة في هذا المجلد")));
        _sessions.Children.Add(Option("past", _pick is null, fresh, () => { _pick = null; Refresh(); }, 7));
        foreach (var s in shown)
        {
            var row = new DockPanel();
            var face = ChatLook.Avatar(s.Agent, 24);
            face.Margin = new Thickness(0, 0, 9, 0);
            DockPanel.SetDock(face, Dock.Left);
            row.Children.Add(face);
            var when = Lang.Ago(s.Updated, DateTimeOffset.UtcNow);
            var where = s.Agent == Agent.Claude ? "Claude Code" : Lang.T("Codex", "Codex");
            var count = s.Messages > 0 ? Lang.T($" · {s.Messages} messages", $" · {s.Messages} رسائل") : "";
            row.Children.Add(Words(s.Title, $"{where} · {when}{count}"));
            var pick = s;
            _sessions.Children.Add(Option("past", _pick == s, row, () => { _pick = pick; if (!_nameTouched) _name.Text = pick.Title; Validate(); }, 7));
        }
        _sessions.Children.Add(ChatLook.Text(Lang.T("Continuing makes a copy, so the original stays as it was in Claude Code or the Codex app.", "المتابعة تصنع نسخة، فتبقى الأصلية كما هي في Claude Code أو تطبيق Codex."), 11.5, "Brush.Text3").With(t => t.Margin = new Thickness(0, 2, 0, 0)));
    }

    // ---- create --------------------------------------------------------------------------------

    private List<string> Agents => _who == "both" ? [Agent.Claude, Agent.Codex] : [_who];

    private void Validate()
    {
        string? problem = null;
        foreach (var a in Agents)
            if (_program(a) is null) problem = Lang.T($"{Agent.Name(a)} isn't on this PC. Install it and sign in once, then come back.", $"{Agent.Name(a)} غير موجود على هذا الحاسوب. ثبّته وسجّل الدخول مرة، ثم عُد.");
        if (problem is null && !_quick && !Directory.Exists(_folder.Text.Trim()))
            problem = Lang.T("Choose the folder this chat works in.", "اختر المجلد الذي تعمل فيه هذه المحادثة.");
        _problem.Text = problem ?? Lang.T("Uses your own Claude Code and Codex sign-ins on this PC. Amir Lap only starts them; it never sees your passwords or tokens.", "يستخدم تسجيل دخولك في Claude Code وCodex على هذا الحاسوب. أمير لاب يشغّلهما فقط ولا يرى كلمات مرورك أو رموزك.");
        _problem.SetResourceReference(TextBlock.ForegroundProperty, problem is null ? "Brush.Text3" : "Brush.Warning");
        _create.IsEnabled = problem is null;
        _create.Content = _pick is null ? Lang.T("Create chat", "إنشاء المحادثة") : Lang.T("Continue in Pulse", "المتابعة في نبض");
    }

    private void Create()
    {
        Validate();
        if (!_create.IsEnabled) return;
        bool group = _who == "both";
        var info = new ChatInfo
        {
            Title = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : DefaultName(),
            Folder = _quick ? null : IOPath.TrimEndingDirectorySeparator(IOPath.GetFullPath(_folder.Text.Trim())),
            Agents = Agents,
            Recipe = group ? _recipe : "",
            Lead = group ? _lead : _who,
            HopLimit = _hops,
            Permission = _quick ? ChatPermission.Read : Permission,
        };
        if (group && !_quick && _recipe == ChatRecipe.Builder)
        {
            info.Owns[Agent.Claude] = Split(_ownsClaude.Text);
            info.Owns[Agent.Codex] = Split(_ownsCodex.Text);
        }
        if (_pick is { } p && info.Has(p.Agent)) info.ForkFrom[p.Agent] = p.Id;
        Created = _hub.Create(info);
        DialogResult = true;
    }

    private static List<string> Split(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(t => t.Length > 0).Distinct().ToList();
}
