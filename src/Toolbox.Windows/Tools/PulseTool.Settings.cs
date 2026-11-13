using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Toolbox.Core.Pulse;
using Toolbox.Windows.Home;
using Toolbox.Windows.Pulse;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Tools;

internal sealed partial class PulseTool
{
    public override void BuildSettings(SettingsPage page)
    {
        void Edit(Action<PulseSettings> edit) => page.Change(x => edit(x.Pulse));

        // ---- your tiles ----
        page.Section(Lang.T("Your tiles", "مربعاتك"));
        var tilesHost = new StackPanel();
        page.Add(tilesHost);
        void RenderTiles()
        {
            tilesHost.Children.Clear();
            var s = page.Current.Pulse;
            var card = new StackPanel();
            tilesHost.Children.Add(LapCard.Create(card, new Thickness(0)));

            // Sets
            var setRow = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
            var setControls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            setControls.Children.Add(SettingsPage.Choice([.. s.Sets.Select(x => x.Id)], s.Active.Id, id => { Edit(p => p.CurrentSet = id); RenderTiles(); }, SetName));
            setControls.Children.Add(SettingsPage.Button("+ " + Lang.T("New set", "مجموعة جديدة"), () =>
            {
                Edit(p =>
                {
                    int n = 3;
                    while (p.Sets.Any(x => x.Id == "set" + n)) n++;
                    p.Sets.Add(new PulseTileSet { Id = "set" + n, Tiles = [PulseTiles.Claude, PulseTiles.Codex] });
                    p.CurrentSet = "set" + n;
                });
                RenderTiles();
            }, ghost: true).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
            if (s.Sets.Count > 1 && !(s.Active.Id is "work" or "study"))
                setControls.Children.Add(SettingsPage.Button(Lang.T("Remove set", "إزالة المجموعة"), () =>
                {
                    Edit(p =>
                    {
                        p.Sets.RemoveAll(x => x.Id == p.Active.Id);
                        p.CurrentSet = p.Sets[0].Id;
                    });
                    RenderTiles();
                }, ghost: true).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
            DockPanel.SetDock(setControls, Dock.Right);
            setRow.Children.Add(setControls);
            setRow.Children.Add(Label(Lang.T("Tile set", "مجموعة المربعات"), Lang.T("Each set keeps its own tiles. Switch sets from the dock's right-click menu.", "لكل مجموعة مربعاتها. بدّل بينها من قائمة النقر بزر الماوس الأيمن على الشريط.")));
            card.Children.Add(setRow);

            // Preview
            var preview = new PulsePreview(220);
            preview.Update(s);
            var previewBox = new StackPanel { Margin = new Thickness(18, 0, 18, 14) };
            previewBox.Children.Add(preview);
            previewBox.Children.Add(new TextBlock { Text = Lang.T("The dock as it will look. Changes apply right away.", "الشريط كما سيبدو. تُطبَّق التغييرات فورًا."), Style = SettingsPage.Res("Lap.Help"), Margin = new Thickness(0, 8, 0, 0) });
            card.Children.Add(Lined(previewBox));

            // Active tiles, with order and remove
            var tiles = s.ActiveTiles;
            foreach (var id in tiles)
            {
                var row = new DockPanel { Margin = new Thickness(18, 10, 12, 10) };
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                buttons.Children.Add(IconButton("lap-chev-up", Lang.T("Move up", "تحريك لأعلى"), tiles[0] != id, () => { Edit(p => p.Active.Tiles = PulseTiles.Move(p.ActiveTiles, id, -1)); RenderTiles(); }));
                buttons.Children.Add(IconButton("lap-chev-down", Lang.T("Move down", "تحريك لأسفل"), tiles[^1] != id, () => { Edit(p => p.Active.Tiles = PulseTiles.Move(p.ActiveTiles, id, 1)); RenderTiles(); }));
                buttons.Children.Add(IconButton("lap-x", Lang.T("Remove", "إزالة"), true, () => { Edit(p => p.Active.Tiles = p.ActiveTiles.Where(t => t != id).ToList()); RenderTiles(); }));
                DockPanel.SetDock(buttons, Dock.Right);
                row.Children.Add(buttons);
                var tile = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 12, 0), Child = SettingsPage.Glyph(DockWindow.IconOf(id), 18, PulseTiles.HasRing(id) ? "Brush.Accent" : "Brush.Text") };
                tile.SetResourceReference(Border.BackgroundProperty, PulseTiles.HasRing(id) ? "Brush.AccentWash" : "Brush.Card2");
                tile.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
                DockPanel.SetDock(tile, Dock.Left);
                row.Children.Add(tile);
                row.Children.Add(Label(TileName(id), TileHelp(id)));
                card.Children.Add(Lined(row));
            }

            // Gallery
            var gallery = new WrapPanel { Margin = new Thickness(18, 12, 18, 14) };
            var head = new TextBlock { Text = Lang.T("Add a tile", "إضافة مربع"), FontSize = 12, FontWeight = FontWeights.SemiBold, Width = 2000, Margin = new Thickness(0, 0, 0, 8) };
            head.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text3");
            gallery.Children.Add(head);
            var more = PulseTiles.All.Where(t => !tiles.Contains(t)).ToList();
            foreach (var id in more)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(SettingsPage.Glyph(DockWindow.IconOf(id), 16).With(g => g.Margin = new Thickness(0, 0, 8, 0)));
                content.Children.Add(new TextBlock { Text = TileName(id), VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(new TextBlock { Text = "+", FontWeight = FontWeights.Bold, FontSize = 15, Margin = new Thickness(8, -1, 0, 0), VerticalAlignment = VerticalAlignment.Center }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent")));
                var chip = new Button { Style = SettingsPage.Res("Lap.Button"), Content = content, Margin = new Thickness(0, 0, 8, 8), ToolTip = TileHelp(id) };
                chip.Click += (_, _) => { Edit(p => p.Active.Tiles = [.. p.ActiveTiles, id]); RenderTiles(); };
                gallery.Children.Add(chip);
            }
            if (more.Count == 0) gallery.Children.Add(new TextBlock { Text = Lang.T("Every tile is already on the dock.", "كل المربعات موجودة على الشريط."), Style = SettingsPage.Res("Lap.Help") });
            card.Children.Add(Lined(gallery));
        }
        RenderTiles();

        // ---- place ----
        var s0 = page.Current.Pulse;
        page.Section(Lang.T("Place", "المكان"));
        page.Card();
        page.Row(Lang.T("Edge", "الحافة"), EdgePicker(s0.Edge, edge => { Edit(p => { p.Edge = edge; p.Position = 0.5; }); RenderTiles(); }),
            Lang.T("Or drag the dock by its dots, along the edge or to another one.", "أو اسحب الشريط من نقاطه، على طول الحافة أو إلى حافة أخرى."));
        page.Row(Lang.T("Screen", "الشاشة"), SettingsPage.Choice(["main", "pointer"], s0.Screen, v => Edit(p => { p.Screen = v; if (v == "main") p.MonitorPoint = null; }),
                v => v == "main" ? Lang.T("Main screen", "الشاشة الرئيسية") : Lang.T("Where the pointer is", "حيث يوجد المؤشر")),
            Lang.T("With more than one monitor: stay on one, or follow the pointer.", "مع أكثر من شاشة: البقاء على شاشة واحدة، أو اتباع المؤشر."));
        page.Row(Lang.T("Size", "الحجم"), SettingsPage.Choice(["S", "M", "L"], s0.Size, v => { Edit(p => p.Size = v); RenderTiles(); },
                v => v switch { "S" => Lang.T("Small", "صغير"), "L" => Lang.T("Large", "كبير"), _ => Lang.T("Medium", "متوسط") }),
            Lang.T("Or drag the dock's inner edge. Small hides the labels.", "أو اسحب الحافة الداخلية للشريط. الصغير يخفي الأسماء."));
        page.Row(Lang.T("Show", "الظهور"), SettingsPage.Choice(["always", "slide", "fade", "tap"], s0.Show, v => { Edit(p => p.Show = v); RenderTiles(); },
                v => v switch { "slide" => Lang.T("Slide away", "ينزلق جانبًا"), "fade" => Lang.T("Fade", "يخفت"), "tap" => Lang.T("Tap", "نقر"), _ => Lang.T("Always", "دائمًا") }),
            Lang.T("Slide away and Tap leave a thin line on the edge. Slide away comes back when the pointer touches the line; Tap only when you click it, so reaching for a scrollbar never opens it.",
                   "ينزلق جانبًا ونقر يتركان خطًا رفيعًا على الحافة. ينزلق جانبًا يعود عندما يلمس المؤشر الخط، ونقر لا يعود إلا عند النقر عليه، فلا يفتح عند الوصول إلى شريط التمرير."));
        page.Toggle(Lang.T("Hide during full-screen apps", "الإخفاء أثناء ملء الشاشة"), s0.HideInFullScreen, on => Edit(p => p.HideInFullScreen = on),
            Lang.T("Games, videos and presentations.", "الألعاب والفيديو والعروض التقديمية."));

        // ---- Claude Code ----
        page.Section("Claude Code");
        var claudeHost = new StackPanel();
        page.Add(claudeHost);
        void RenderClaude()
        {
            claudeHost.Children.Clear();
            var stack = new StackPanel();
            claudeHost.Children.Add(LapCard.Create(stack, new Thickness(0)));
            var connection = Connection();
            bool linked;
            string? error = null;
            try { linked = connection.IsLinked; }
            catch (InvalidOperationException ex) { linked = false; error = ex.Message; }
            _linked = null;

            if (!linked)
            {
                var consent = new DockPanel { Margin = new Thickness(18) };
                var mark = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top, Child = SettingsPage.Glyph("lap-claude", 20, "Brush.Accent", stroke: 2) };
                mark.SetResourceReference(Border.BackgroundProperty, "Brush.AccentWash");
                DockPanel.SetDock(mark, Dock.Left);
                consent.Children.Add(mark);
                var body = new StackPanel();
                body.Children.Add(new TextBlock { Text = Lang.T("Show Claude's limits and what it is doing", "إظهار حدود Claude وما يفعله"), FontSize = 14.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(new TextBlock
                {
                    Text = Lang.T("Amir Lap adds a small status line and a few hooks to Claude Code's settings on this PC. They only write to a file in Amir Lap's folder, and nothing goes online. You can remove them here at any time.",
                                  "يضيف أمير لاب سطر حالة صغيرًا وبعض الخطافات إلى إعدادات Claude Code على هذا الحاسوب. لا تكتب إلا في ملف داخل مجلد أمير لاب، ولا يُرسَل شيء عبر الإنترنت. يمكنك إزالتها من هنا في أي وقت."),
                    Style = SettingsPage.Res("Lap.Text"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left,
                }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text2")));
                var changes = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed, FlowDirection = FlowDirection.LeftToRight };
                changes.SetResourceReference(Border.BackgroundProperty, "Brush.Card2");
                changes.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
                var lines = new StackPanel();
                foreach (var (text, add) in new[]
                {
                    (connection.SettingsPath, false),
                    ("+ \"statusLine\": " + connection.Command(ClaudeCodeConnection.StatusArg), true),
                    ("+ \"hooks\": " + string.Join(", ", ClaudeCodeConnection.Events), true),
                    ("    " + Lang.T("each runs ", "كلٌّ منها يشغّل ") + connection.Command(ClaudeCodeConnection.EventArg), true),
                })
                {
                    var t = new TextBlock { Text = text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                    t.SetResourceReference(TextBlock.ForegroundProperty, add ? "Brush.Success" : "Brush.Text2");
                    lines.Children.Add(t);
                }
                changes.Child = lines;
                body.Children.Add(changes);
                var acts = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                var connect = SettingsPage.Button(Lang.T("Connect", "ربط"), () =>
                {
                    try
                    {
                        connection.Link();
                        _monitor?.Refresh();
                        RenderClaude();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                    {
                        acts.ToolTip = ex.Message;
                        error = ex.Message;
                    }
                }, accent: true);
                connect.IsEnabled = error is null;
                acts.Children.Add(connect);
                Button? see = null;
                see = SettingsPage.Button(Lang.T("See what changes", "عرض التغييرات"), () =>
                {
                    bool show = changes.Visibility != Visibility.Visible;
                    changes.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                    see!.Content = show ? Lang.T("Hide the changes", "إخفاء التغييرات") : Lang.T("See what changes", "عرض التغييرات");
                }, ghost: true);
                see.Margin = new Thickness(8, 0, 0, 0);
                acts.Children.Add(see);
                body.Children.Add(acts);
                if (error is not null) body.Children.Add(Warn(error));
                else if (!connection.ClaudeCodeFound) body.Children.Add(Muted(Lang.T("Claude Code hasn't been used on this PC yet. You can still connect it now.", "لم يُستخدم Claude Code على هذا الحاسوب بعد. يمكنك ربطه الآن مع ذلك.")));
                consent.Children.Add(body);
                stack.Children.Add(consent);
            }
            else
            {
                var p = page.Current.Pulse;
                var ok = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                ok.Children.Add(SettingsPage.Glyph("lap-check", 15, "Brush.Success", stroke: 2.2).With(g => g.Margin = new Thickness(0, 0, 6, 0)));
                ok.Children.Add(new TextBlock { Text = Lang.T("Connected", "مرتبط"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Success")));
                ok.Children.Add(SettingsPage.Button(Lang.T("Remove", "إزالة"), () =>
                {
                    try { connection.Unlink(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { ok.ToolTip = ex.Message; }
                    _monitor?.Refresh();
                    RenderClaude();
                }, ghost: true).With(b => b.Margin = new Thickness(12, 0, 0, 0)));
                stack.Children.Add(Lined(SettingRow(Lang.T("Claude limits and status", "حدود Claude وحالته"),
                    Lang.T("Limits update while Claude Code is open. When it is closed, the dock shows the last numbers and when they were read.", "تتحدّث الحدود أثناء عمل Claude Code. وعند إغلاقه يعرض الشريط آخر الأرقام ووقت قراءتها."), ok), first: true));
                stack.Children.Add(Lined(SettingRow(Lang.T("Show what Claude is doing", "إظهار ما يفعله Claude"),
                    Lang.T("Working, needs you, or done, as a light around the ring.", "يعمل، أو يحتاجك، أو انتهى، كضوء حول الحلقة."), SettingsPage.Switch(p.ShowClaudeStatus, on => Edit(x => x.ShowClaudeStatus = on)))));
                stack.Children.Add(Lined(SettingRow(Lang.T("Jump to the waiting terminal", "الانتقال إلى الطرفية المنتظرة"),
                    Lang.T("Clicking Claude's tile while a session needs you brings its window forward.", "النقر على مربع Claude عندما تحتاجك جلسة يُظهر نافذتها."), SettingsPage.Switch(p.JumpToTerminal, on => Edit(x => x.JumpToTerminal = on)))));
            }
            var note = new DockPanel { Margin = new Thickness(18, 12, 18, 12) };
            var info = SettingsPage.Glyph("lap-about", 15, "Brush.Text3");
            info.Margin = new Thickness(0, 1, 8, 0);
            info.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(info, Dock.Left);
            note.Children.Add(info);
            note.Children.Add(new TextBlock { Text = Lang.T("If you already have a status line, it keeps working as before. Limits are reported for claude.ai Pro and Max plans.", "إن كان لديك سطر حالة من قبل، فسيبقى يعمل كما هو. تُعرض الحدود لخطط Pro وMax في claude.ai."), Style = SettingsPage.Res("Lap.Help"), Margin = new Thickness(0) });
            stack.Children.Add(Lined(note));
        }
        RenderClaude();

        // ---- Codex ----
        page.Section("Codex");
        page.Card();
        var found = CodexUsageReader.LatestSessionFile(CodexUsageReader.DefaultHome) is not null;
        page.Toggle(Lang.T("Codex limits", "حدود Codex"), s0.ReadCodex, on => { Edit(p => p.ReadCodex = on); _monitor?.Refresh(); },
            (found ? Lang.T("Found on this PC. ", "موجود على هذا الحاسوب. ") : Lang.T("No Codex sessions on this PC yet. ", "لا توجد جلسات Codex على هذا الحاسوب بعد. "))
            + Lang.T("Read from Codex's own session files; nothing is changed.", "تُقرأ من ملفات جلسات Codex نفسه؛ ولا يتغيّر شيء."));

        // ---- warnings ----
        page.Section(Lang.T("Warnings", "التنبيهات"));
        page.Card();
        page.Row(Lang.T("Warn me at", "نبّهني عند"), SettingsPage.Choice(["70", "80", "90", "0"], s0.WarnAt.ToString(CultureInfo.InvariantCulture), v => Edit(p => p.WarnAt = int.Parse(v, CultureInfo.InvariantCulture)),
                v => v == "0" ? Lang.T("Off", "إيقاف") : v + "%"),
            Lang.T("A quiet note, once per limit window. A used-up limit always says when it comes back.", "إشعار هادئ مرة واحدة في كل فترة. والحد المستنفَد يخبرك دائمًا متى يعود."));
        page.Toggle(Lang.T("When Claude finishes", "عندما ينتهي Claude"), s0.NotifyFinished, on => Edit(p => p.NotifyFinished = on),
            Lang.T("A note when a task that took over a minute ends (needs Claude Code connected).", "إشعار عند انتهاء مهمة استغرقت أكثر من دقيقة (يتطلب ربط Claude Code)."));

        // ---- tile options ----
        page.Section(Lang.T("Tile options", "خيارات المربعات"));
        page.Card();
        page.Row(TileName(PulseTiles.Timer), SettingsPage.Choice(["15", "25", "50"], s0.TimerMinutes.ToString(CultureInfo.InvariantCulture), v => Edit(p => p.TimerMinutes = int.Parse(v, CultureInfo.InvariantCulture)),
            v => Lang.T(v + " min", v + " د")), Lang.T("How long a focus round lasts.", "مدة جولة التركيز."));
        page.Row(TileName(PulseTiles.Colour), SettingsPage.Choice(["basic", "pro"], s0.ColourMode == "pro" ? "pro" : "basic", v => Edit(p => p.ColourMode = v),
                v => v == "pro" ? Lang.T("Pro", "احترافي") : Lang.T("Basic", "أساسي")),
            Lang.T("Basic copies the HEX code. Pro copies the code you star and opens a window beside the pick with every code, lighter and darker steps, contrast, saved and recent colours. In Basic, Shift+click opens it once.",
                   "الأساسي ينسخ رمز HEX. الاحترافي ينسخ الرمز الذي تميّزه بنجمة ويفتح نافذة بجانب الالتقاط فيها كل الرموز ودرجات أفتح وأغمق والتباين والألوان المحفوظة والأخيرة. في الأساسي، Shift+نقر يفتحها مرة واحدة."));
        page.Row(Lang.T("Recent colours", "الألوان الأخيرة"), SettingsPage.Choice(["10", "20", "30"], Colours.HistorySize(s0.ColourHistorySize).ToString(CultureInfo.InvariantCulture),
                v => Edit(p =>
                {
                    p.ColourHistorySize = int.Parse(v, CultureInfo.InvariantCulture);
                    if (p.ColourHistory.Count > p.ColourHistorySize) p.ColourHistory = p.ColourHistory.Take(p.ColourHistorySize).ToList();
                })),
            Lang.T("How many recent picks the colour window keeps. Saved colours don't count.", "عدد الالتقاطات الأخيرة التي تحتفظ بها نافذة اللون. الألوان المحفوظة لا تُحتسب."));
        var folderText = SettingsPage.Ltr(new TextBlock { Text = FolderPath, Style = SettingsPage.Res("Lap.Help"), TextTrimming = TextTrimming.CharacterEllipsis });
        var folderButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        folderButtons.Children.Add(SettingsPage.Button(Lang.T("Choose folder…", "اختيار مجلد…"), () =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = FolderPath, UseDescriptionForTitle = true, Description = Lang.T("The Folder tile opens", "مربع المجلد يفتح") };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            Edit(p => p.FolderPath = dialog.SelectedPath);
            folderText.Text = dialog.SelectedPath;
        }));
        var folderStack = new StackPanel();
        folderStack.Children.Add(folderText);
        folderStack.Children.Add(folderButtons);
        page.Stacked(TileName(PulseTiles.Folder), folderStack, Lang.T("Downloads until you choose another.", "مجلد التنزيلات حتى تختار غيره."));
        var link = SettingsPage.TextBox(s0.LinkTarget ?? "", 420);
        link.FlowDirection = FlowDirection.LeftToRight;
        link.LostFocus += (_, _) => Edit(p => p.LinkTarget = string.IsNullOrWhiteSpace(link.Text) ? null : link.Text.Trim());
        var linkRow = new StackPanel { Orientation = Orientation.Horizontal };
        linkRow.Children.Add(link);
        linkRow.Children.Add(SettingsPage.Button(Lang.T("Browse…", "استعراض…"), () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = Lang.T("Programs and shortcuts", "البرامج والاختصارات") + "|*.exe;*.lnk;*.url;*.bat;*.cmd|" + Lang.T("All files", "كل الملفات") + "|*.*" };
            if (dialog.ShowDialog() != true) return;
            link.Text = dialog.FileName;
            Edit(p => p.LinkTarget = dialog.FileName);
        }, ghost: true).With(b => b.Margin = new Thickness(8, 0, 0, 0)));
        page.Stacked(TileName(PulseTiles.Link), linkRow, Lang.T("A program, file, folder or web address (https://…).", "برنامج أو ملف أو مجلد أو عنوان ويب (https://…)."));

        // ---- privacy ----
        page.Section(Lang.T("Privacy", "الخصوصية"));
        page.Card();
        page.Note(Lang.T("Nothing leaves this PC. Pulse reads files Claude Code and Codex already keep on this computer: no sign-in, no internet. The dock stays out of screenshots.",
            "لا شيء يغادر هذا الحاسوب. يقرأ نبض ملفات يحتفظ بها Claude Code وCodex على هذا الحاسوب أصلًا: بلا تسجيل دخول ولا إنترنت. ولا يظهر الشريط في لقطات الشاشة."));
    }

    // ---- small pieces ----------------------------------------------------------------------------

    private static StackPanel Label(string label, string? help)
    {
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock { Text = label, Style = SettingsPage.Res("Lap.Text"), FontWeight = FontWeights.Medium });
        if (help is not null) left.Children.Add(new TextBlock { Text = help, Style = SettingsPage.Res("Lap.Help") });
        return left;
    }

    private static FrameworkElement SettingRow(string label, string help, FrameworkElement control)
    {
        var row = new DockPanel { Margin = new Thickness(18, 14, 18, 14) };
        control.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        row.Children.Add(Label(label, help));
        return row;
    }

    /// <summary>A card row with the separator line above it.</summary>
    private static Border Lined(UIElement content, bool first = false)
    {
        var b = new Border { Child = content, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0) };
        b.SetResourceReference(Border.BorderBrushProperty, "Brush.Line");
        return b;
    }

    private static Button IconButton(string icon, string name, bool enabled, Action click)
    {
        var b = new Button { Style = SettingsPage.Res("Lap.Caption"), Width = 32, Height = 30, Content = SettingsPage.Glyph(icon, 15, stroke: 2), ToolTip = name, IsEnabled = enabled, Opacity = enabled ? 1 : 0.3, Margin = new Thickness(2, 0, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(b, name);
        b.Click += (_, _) => click();
        return b;
    }

    private static TextBlock Muted(string text) => new TextBlock { Text = text, Style = SettingsPage.Res("Lap.Help"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };

    private static TextBlock Warn(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");
        return t;
    }

    /// <summary>Four little screens, each with a bar on one edge.</summary>
    private static FrameworkElement EdgePicker(string current, Action<string> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, FlowDirection = FlowDirection.LeftToRight };
        var group = Guid.NewGuid().ToString("N");
        foreach (var edge in new[] { "left", "right", "top", "bottom" })
        {
            var screen = new Grid { Width = 56, Height = 36 };
            screen.Children.Add(new Border { CornerRadius = new CornerRadius(6), Background = new LinearGradientBrush(ThemeResources.Lap.IsDark ? Color.FromRgb(0x1C, 0x25, 0x50) : Color.FromRgb(0xC3, 0xD2, 0xF6), ThemeResources.Lap.IsDark ? Color.FromRgb(0x45, 0x19, 0x3A) : Color.FromRgb(0xF2, 0xD3, 0xE2), 30) });
            var bar = new Rectangle { RadiusX = 2, RadiusY = 2 };
            bar.SetResourceReference(Shape.FillProperty, "Brush.Accent");
            switch (edge)
            {
                case "left": bar.Width = 5; bar.Margin = new Thickness(0, 9, 0, 9); bar.HorizontalAlignment = HorizontalAlignment.Left; break;
                case "right": bar.Width = 5; bar.Margin = new Thickness(0, 9, 0, 9); bar.HorizontalAlignment = HorizontalAlignment.Right; break;
                case "top": bar.Height = 5; bar.Margin = new Thickness(14, 0, 14, 0); bar.VerticalAlignment = VerticalAlignment.Top; break;
                default: bar.Height = 5; bar.Margin = new Thickness(14, 0, 14, 0); bar.VerticalAlignment = VerticalAlignment.Bottom; break;
            }
            screen.Children.Add(bar);
            var content = new StackPanel();
            content.Children.Add(screen);
            content.Children.Add(new TextBlock
            {
                Text = edge switch { "left" => Lang.T("Left", "يسار"), "right" => Lang.T("Right", "يمين"), "top" => Lang.T("Top", "أعلى"), _ => Lang.T("Bottom", "أسفل") },
                FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0), FlowDirection = Lang.Flow,
            });
            var button = new RadioButton { Style = SettingsPage.Res("Lap.Segment"), GroupName = group, Content = content, IsChecked = edge == current, Padding = new Thickness(8, 8, 8, 6), Height = double.NaN, Margin = new Thickness(0, 0, 6, 0) };
            var e = edge;
            button.Checked += (_, _) => pick(e);
            row.Children.Add(button);
        }
        var bar2 = new Border { Style = SettingsPage.Res("Lap.SegmentBar"), Child = row };
        return bar2;
    }
}
