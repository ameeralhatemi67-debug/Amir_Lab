using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Toolbox.Core.Settings;
using Toolbox.Windows.Home;
using Toolbox.Windows.Longshot;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Tool #2: scrolling screenshots. Draw a box, then Extend / Auto / Follow to grow it downward.
/// While on, it only holds its shortcuts; nothing runs until a capture starts.
/// </summary>
internal sealed class LongshotTool : ToolModule
{
    public const string ToolId = "longshot";

    private readonly ToolboxHost _host;

    public LongshotTool(ToolboxHost host)
    {
        _host = host;
        Commands =
        [
            new ToolCommand("capture", "Start a scrolling capture", "Alt+Shift+S", () => Capture()) { TitleAr = "بدء لقطة طويلة", Alternatives = ["Ctrl+Alt+L", "Alt+Shift+L"] },
            new ToolCommand("captureDelayed", "Scrolling capture after a delay", "Ctrl+Alt+Shift+S", () => Capture(delayed: true)) { TitleAr = "لقطة طويلة بعد مهلة", Alternatives = ["Ctrl+Alt+Shift+L"] },
            new ToolCommand("repeat", "Capture the same area again", null, CaptureSameArea) { TitleAr = "التقاط المنطقة نفسها مجددًا" },
        ];
    }

    public override string Id => ToolId;
    public override string Name => Lang.T("Longshot", "اللقطة الطويلة");
    public override string Summary => Lang.T("Scrolling screenshots of a whole page, a part that scrolls (a chat, a list, a panel) or any area. Press the shortcut, then Auto.", "لقطات شاشة طويلة لصفحة كاملة، أو لجزء يتمرّر (محادثة أو قائمة أو لوحة)، أو لأي منطقة. اضغط الاختصار ثم «تلقائي».");
    public override string Icon => "lap-longshot";
    public override IReadOnlyList<ToolCommand> Commands { get; }

    /// <summary>The last area captured (for "Capture the same area again").</summary>
    public PixelRect? LastArea { get; set; }

    /// <summary>Where the wheel turned in the last capture (kept with <see cref="LastArea"/>).</summary>
    public PixelPoint? LastAnchor { get; set; }

    /// <summary>What kind of area the last capture covered.</summary>
    public TargetKind LastKind { get; set; } = TargetKind.Region;

    protected override void OnStart() { } // nothing to load: a capture starts from scratch each time

    protected override void OnStop() => LongshotCapture.CancelActive();

    public void Capture(bool delayed = false) =>
        LongshotCapture.Start(_host, this, delaySeconds: delayed ? Math.Clamp(_host.Settings.Current.Longshot.DelaySeconds, 1, 30) : 0);

    private void CaptureSameArea()
    {
        if (LastArea is { } area) LongshotCapture.Start(_host, this, new CaptureTarget(LastKind, area, LastAnchor is { } a && area.Contains(a) ? a : area.Center));
        else Capture();
    }

    /// <summary>From the Dashboard: get the Amir Lap window out of the way first, then capture.</summary>
    public override void Open()
    {
        App.MinimizeHome();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) => { timer.Stop(); Capture(); };
        timer.Start();
    }

    public override IEnumerable<TrayItem> TrayItems(Func<ToolCommand, string?> shortcutText)
    {
        int delay = _host.Settings.Current.Longshot.DelaySeconds;
        yield return new TrayItem(Commands[0].Label, () => Capture(), Shortcut: shortcutText(Commands[0]));
        yield return new TrayItem(Lang.T($"Capture after {delay} seconds", $"التقاط بعد {delay} ث"), () => Capture(delayed: true), Shortcut: shortcutText(Commands[1]));
        yield return new TrayItem(Commands[2].Label, LastArea is null ? null : CaptureSameArea, Shortcut: shortcutText(Commands[2]));
        yield return new TrayItem(Lang.T("Open screenshots folder", "فتح مجلد اللقطات"), OpenFolder);
    }

    /// <summary>Where captures are saved.</summary>
    public string Folder => _host.Settings.Current.Longshot.SaveFolder is { Length: > 0 } f ? f : LongshotOutput.DefaultFolder;

    private void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Folder}\"") { UseShellExecute = true });
    }

    public override void BuildSettings(SettingsPage page)
    {
        var s = page.Current.Longshot;
        void Edit(Action<LongshotSettings> edit) => page.Change(x => edit(x.Longshot));

        page.Section(Lang.T("Capturing", "الالتقاط"));
        page.Card();
        page.Row(Lang.T("Start on", "البدء على"), SettingsPage.Choice(["pointer", "window"], s.StartOn, v => Edit(x => x.StartOn = v),
                v => v == "pointer" ? Lang.T("What's under the pointer", "ما تحت المؤشر") : Lang.T("The window in front", "النافذة الأمامية")),
            Lang.T("What's under the pointer: the part that scrolls where your pointer is (a chat, a list, a side panel), or the window if the whole page scrolls. The panel's Window, Scroll area and Region buttons switch at any time.",
                   "ما تحت المؤشر: الجزء الذي يتمرّر حيث المؤشر (محادثة أو قائمة أو لوحة جانبية)، أو النافذة إن كانت الصفحة كلها تتمرّر. تبدّل أزرار «نافذة» و«منطقة تمرير» و«منطقة محددة» في اللوحة ذلك في أي وقت."));
        page.Row(Lang.T("When Longshot starts", "عند بدء اللقطة الطويلة"), SettingsPage.Choice(["wait", "auto", "follow"], s.AfterSelect, v => Edit(x => x.AfterSelect = v),
                v => v switch { "wait" => Lang.T("Wait for me", "انتظرني"), "auto" => Lang.T("Auto", "تلقائي"), _ => Lang.T("Follow", "تتبّع") }),
            Lang.T("Wait for me: adjust the frame if needed, then press Auto or Extend. Auto: scrolls to the end by itself. Follow: you scroll, Longshot adds.",
                   "انتظرني: اضبط الإطار إن لزم ثم اضغط «تلقائي» أو «مدّ». تلقائي: يمرّر حتى النهاية وحده. تتبّع: أنت تمرّر واللقطة تُضاف."));
        page.Toggle(Lang.T("Dim the rest of the screen", "تعتيم بقية الشاشة"), s.DimOutside, on => Edit(x => x.DimOutside = on),
            Lang.T("Makes it plain what will be in the shot. Clicks and scrolling still go through.", "يوضح ما سيظهر في اللقطة. النقر والتمرير يعملان كالمعتاد."));
        page.Row(Lang.T("Scroll step", "خطوة التمرير"), SettingsPage.Choice(["small", "auto", "large"], s.ScrollStep, v => Edit(x => x.ScrollStep = v),
                v => v switch { "small" => Lang.T("Small", "صغيرة"), "auto" => Lang.T("Automatic", "تلقائية"), _ => Lang.T("Large", "كبيرة") }),
            Lang.T("Smaller steps line up more reliably on busy pages; larger ones are faster.", "الخطوات الأصغر أدق في الصفحات المزدحمة، والأكبر أسرع."));
        page.Row(Lang.T("Delay for the delayed shortcut", "مهلة الاختصار المؤجَّل"), SettingsPage.Choice(["3", "5", "10"], s.DelaySeconds.ToString(CultureInfo.InvariantCulture),
                v => Edit(x => x.DelaySeconds = int.Parse(v, CultureInfo.InvariantCulture)), v => Lang.T(v + " s", v + " ث")),
            Lang.T("Time to open a menu or hover something before the capture starts.", "وقت لفتح قائمة أو تمرير المؤشر فوق شيء قبل بدء الالتقاط."));

        page.Section(Lang.T("Saving", "الحفظ"));
        page.Card();
        page.Toggle(Lang.T("Copy to the clipboard", "النسخ إلى الحافظة"), s.CopyToClipboard, on => Edit(x => x.CopyToClipboard = on),
            Lang.T("Paste it straight into a chat or document with Ctrl+V.", "الصقها مباشرة في محادثة أو مستند باستخدام Ctrl+V."));
        page.Toggle(Lang.T("Show the result window", "إظهار نافذة النتيجة"), s.ShowResult, on => Edit(x => x.ShowResult = on),
            Lang.T("When it's off, a short notice says where the capture was saved.", "عند إيقافها يظهر إشعار قصير بمكان حفظ اللقطة."));
        page.Row(Lang.T("Format", "الصيغة"), SettingsPage.Choice(["png", "jpg"], s.Format, v => Edit(x => x.Format = v), v => v.ToUpperInvariant()),
            Lang.T("PNG keeps text sharp. JPG files are smaller.", "PNG يحافظ على وضوح النص. ملفات JPG أصغر حجمًا."));

        var folderText = SettingsPage.Ltr(new TextBlock { Text = Folder, Style = SettingsPage.Res("Lap.Help"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(SettingsPage.Button(Lang.T("Choose folder…", "اختيار مجلد…"), () =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = Folder, UseDescriptionForTitle = true, Description = Lang.T("Save long screenshots in", "حفظ اللقطات الطويلة في") };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            Edit(x => x.SaveFolder = dialog.SelectedPath);
            folderText.Text = dialog.SelectedPath;
        }));
        var reset = SettingsPage.Button(Lang.T("Use Pictures\\Screenshots", "استخدام Pictures\\Screenshots"), () => { Edit(x => x.SaveFolder = null); folderText.Text = LongshotOutput.DefaultFolder; }, ghost: true);
        reset.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(reset);
        var open = SettingsPage.Button(Lang.T("Open folder", "فتح المجلد"), OpenFolder, ghost: true);
        open.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(open);
        var where = new StackPanel();
        where.Children.Add(folderText);
        where.Children.Add(buttons);
        page.Stacked(Lang.T("Save to", "الحفظ في"), where,
            Lang.T("Files are named like \"Longshot 2026-11-08 143012.png\". Existing files are never overwritten.", "تُسمّى الملفات مثل «Longshot 2026-11-08 143012.png». لا تُستبدل الملفات الموجودة أبدًا."));

        page.Section(Lang.T("Good to know", "معلومات مفيدة"));
        page.Card();
        page.Note(Lang.T("Sticky headers and footers (menus, cookie bars) are found automatically and kept once, at the top and bottom.",
            "تُكتشف الرؤوس والتذييلات الثابتة (القوائم وأشرطة ملفات تعريف الارتباط) تلقائيًا وتظهر مرة واحدة في الأعلى والأسفل."));
        page.Note(Lang.T("When only part of the window scrolls (a chat beside a sidebar, a list in a page that stays put), Longshot finds that part and captures just it. The round marker in the frame shows where it scrolls: drag it onto another part to capture that instead.",
            "عندما يتمرّر جزء من النافذة فقط (محادثة بجانب شريط جانبي، أو قائمة داخل صفحة ثابتة)، تجد اللقطة الطويلة ذلك الجزء وتلتقطه وحده. تُظهر العلامة الدائرية في الإطار مكان التمرير: اسحبها فوق جزء آخر لالتقاطه بدلًا منه."));
        page.Note(Lang.T("Drag the frame's corners or edges to fit it to what you want, and its Move grip to move it. A moving scrollbar thumb at the edge is left out by itself.",
            "اسحب زوايا الإطار أو حوافه ليناسب ما تريد، ومقبض التحريك لنقله. يُستبعد مقبض شريط التمرير المتحرك على الحافة تلقائيًا."));
        page.Note(Lang.T("Videos, animations and carousels inside the frame can stop it. Resize the frame to leave them out.",
            "قد توقف مقاطع الفيديو والرسوم المتحركة والعروض الدوّارة داخل الإطار عملية الالتقاط. غيّر حجم الإطار لتتجنّبها."));
        page.Note(Lang.T("Longshot's panel and frame never appear in your screenshots, and nothing leaves this computer.",
            "لا تظهر لوحة اللقطة الطويلة وإطارها في لقطاتك أبدًا، ولا يغادر أي شيء هذا الحاسوب."));
        page.Note(Lang.T("Apps running as administrator (Task Manager, for example) can't be scrolled by Longshot: Windows blocks it.",
            "لا تستطيع اللقطة الطويلة تمرير التطبيقات التي تعمل كمسؤول (مثل إدارة المهام)، لأن ويندوز يمنع ذلك."));
    }
}
