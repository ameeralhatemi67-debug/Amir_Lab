using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Output;
using Toolbox.Core.Settings;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Drag;
using Toolbox.Windows.Home;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Wheel;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Tool #1: the file wheel (drag a file, tap the activation key), the keyboard palette, recipes,
/// editors and history. While it is off, the keyboard watcher is removed and the wheel window closed.
/// </summary>
internal sealed class FileToolboxTool : ToolModule
{
    public const string ToolId = "fileToolbox";

    private readonly ToolboxHost _host;
    private readonly ActionDispatcher _dispatcher;
    private readonly DiagnosticsChannel _diagnostics;
    private WheelOverlayWindow? _overlay;
    private DragActivationService? _drag;

    public FileToolboxTool(ToolboxHost host, ActionDispatcher dispatcher, DiagnosticsChannel diagnostics)
    {
        _host = host;
        _dispatcher = dispatcher;
        _diagnostics = diagnostics;
        // No keyboard shortcut (owner's decision): the tool answers when a file is being dragged and
        // the activation key is tapped. Its window stays reachable from the tray and the Dashboard.
        Commands = [];
    }

    public override string Id => ToolId;
    public override string Name => Lang.T("File Toolbox", "أدوات الملفات");
    public override string Summary => Lang.T("Convert, edit and read files. Drag a file and tap a key to open the wheel.", "حوّل الملفات وعدّلها واقرأها. اسحب ملفًا واضغط مفتاحًا لفتح العجلة.");
    public override string Icon => "lap-toolbox";
    public override IReadOnlyList<ToolCommand> Commands { get; }

    public override IReadOnlyList<ToolGesture> Gestures(AppSettings settings) =>
        [new ToolGesture(Lang.T("Open the wheel while dragging a file", "فتح العجلة أثناء سحب ملف"), [settings.ActivationKey, Lang.T("tap", "نقرة")])];

    public override string RestartKey(AppSettings settings) => settings.WheelSize;

    protected override void OnStart()
    {
        var settings = _host.Settings.Current;
        _overlay = new WheelOverlayWindow(App.WheelDiameter(settings.WheelSize));
        _overlay.Show();
        _overlay.MoveOffscreen();
        _drag = new DragActivationService(_overlay, () => _host.Settings.Current, () => _host.Recipes.All().Where(r => r.Pinned).ToList(), _diagnostics);
        _drag.Dropped += request => _dispatcher.Dispatch(request.ActionId, request.Paths, request.ScreenPoint);
        _drag.Start();
        _drag.WarmUp();
    }

    protected override void OnStop()
    {
        _drag?.Dispose();
        _drag = null;
        _overlay?.Close();
        _overlay = null;
        App.CloseFileToolboxWindows();
    }

    public override void Open() => App.ShowPalette();

    public override IEnumerable<TrayItem> TrayItems(Func<ToolCommand, string?> shortcutText)
    {
        var last = _host.LastAction is { } la && CapabilityMap.TryGet(la.ActionId, out var action) ? action : null;
        yield return new TrayItem(Lang.T("Open File Toolbox", "فتح أدوات الملفات"), () => App.ShowPalette());
        yield return last is null
            ? new TrayItem(Lang.T("Repeat last action", "تكرار آخر إجراء"))
            : new TrayItem(Lang.T($"Repeat: {last.Title}…", $"تكرار: {last.Title}…"), () => App.ShowPalette(preselect: last.Id));
        yield return new TrayItem(Lang.T("Recipes", "الوصفات"), App.ShowRecipes);
        yield return new TrayItem(Lang.T("History", "السجل"), App.ShowHistory);
    }

    public override void BuildSettings(SettingsPage page)
    {
        var s = page.Current;

        page.Section(Lang.T("Wheel", "العجلة"));
        page.Card();
        page.Row(Lang.T("Open the wheel with", "فتح العجلة بالمفتاح"), SettingsPage.Choice(["Shift", "Ctrl", "Alt", "CapsLock"], s.ActivationKey, v => page.Change(x => x.ActivationKey = v)),
            Lang.T("Tap this key while dragging a file. The wheel always copies, so originals are never moved.", "اضغط هذا المفتاح أثناء سحب ملف. العجلة تنسخ دائمًا، فلا يتحرك الملف الأصلي أبدًا."));
        page.Row(Lang.T("Wheel size", "حجم العجلة"), WheelSizeChoice(page), Lang.T("How big the wheel opens around the pointer.", "حجم العجلة حول المؤشر."));

        page.Section(Lang.T("Results", "النتائج"));
        page.Card();
        page.Stacked(Lang.T("Save results", "حفظ النتائج"), ResultLocation(page),
            Lang.T($"If that place can't be written to, results go to {s.ToOutputPolicy().FallbackFolder}. Existing files are never overwritten: a number is added instead.",
                   $"إذا تعذّرت الكتابة هناك، تُحفظ النتائج في {s.ToOutputPolicy().FallbackFolder}. لا تُستبدل الملفات الموجودة أبدًا، بل يُضاف رقم إلى الاسم."));

        var example = SettingsPage.Ltr(new TextBlock { Style = SettingsPage.Res("Lap.Help"), Margin = new Thickness(0, 6, 0, 0) });
        void UpdateExample() => example.Text = Lang.T("Example: ", "مثال: ") + OutputNamer.Compose(page.Current.NamingPattern, new NameParts("Report", " Cropped", ".png", 3, 12, 1920, 1080));
        var pattern = SettingsPage.TextBox(s.NamingPattern);
        pattern.FlowDirection = FlowDirection.LeftToRight;
        pattern.TextChanged += (_, _) => { page.Change(x => x.NamingPattern = pattern.Text); UpdateExample(); };
        UpdateExample();
        var naming = new StackPanel();
        naming.Children.Add(pattern);
        naming.Children.Add(example);
        page.Stacked(Lang.T("File names", "أسماء الملفات"), naming,
            Lang.T("Tokens: {name} {suffix} {page} {w} {h} {date} {time}. Default: {name}{suffix}{page}", "الرموز: {name} {suffix} {page} {w} {h} {date} {time}. الافتراضي: {name}{suffix}{page}"));

        page.Section(Lang.T("Default quality", "الجودة الافتراضية"));
        page.Card();
        page.Row(Lang.T("JPG quality", "جودة JPG"), SettingsPage.Slider(40, 100, s.JpegQuality, v => page.Change(x => x.JpegQuality = v)));
        page.Row(Lang.T("WebP quality", "جودة WebP"), SettingsPage.Slider(40, 100, s.WebpQuality, v => page.Change(x => x.WebpQuality = v)));
        page.Row(Lang.T("AVIF quality", "جودة AVIF"), SettingsPage.Slider(30, 100, s.AvifQuality, v => page.Change(x => x.AvifQuality = v)));
        page.Row(Lang.T("PDF pages at", "دقة صفحات PDF"), SettingsPage.Choice(["150", "200", "300", "600"], s.PdfDpi.ToString(CultureInfo.InvariantCulture), v => page.Change(x => x.PdfDpi = int.Parse(v, CultureInfo.InvariantCulture))),
            Lang.T("DPI used when turning PDF pages into pictures.", "الدقة (DPI) عند تحويل صفحات PDF إلى صور."));
        var fill = SettingsPage.TextBox(s.TransparencyBackground, 110);
        fill.FlowDirection = FlowDirection.LeftToRight;
        fill.LostKeyboardFocus += (_, _) => page.Change(x => x.TransparencyBackground = fill.Text.Trim());
        page.Row(Lang.T("Fill for transparency", "لون ملء الشفافية"), fill,
            Lang.T("Colour used where a transparent picture is saved as JPG or BMP (#RRGGBB).", "اللون المستخدم مكان الشفافية عند الحفظ بصيغة JPG أو BMP (#RRGGBB)."));

        page.Section(Lang.T("History", "السجل"));
        page.Card();
        page.Row(Lang.T("Keep for", "الاحتفاظ لمدة"), SettingsPage.Choice(["7", "30", "90", "365"], s.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture),
                v => page.Change(x => x.HistoryRetentionDays = int.Parse(v, CultureInfo.InvariantCulture)),
                d => Lang.T(d + " days", d == "7" ? "7 أيام" : d + " يومًا")),
            Lang.T("History stores file paths, settings and outcomes, never file contents.", "يحفظ السجل مسارات الملفات والإعدادات والنتائج، ولا يحفظ محتوى الملفات أبدًا."));
        var historyButtons = new StackPanel { Orientation = Orientation.Horizontal };
        historyButtons.Children.Add(SettingsPage.Button(Lang.T("Open history", "فتح السجل"), App.ShowHistory));
        var clear = SettingsPage.Button(Lang.T("Clear history", "مسح السجل"), () => { }, ghost: true);
        clear.Margin = new Thickness(8, 0, 0, 0);
        clear.Click += (_, _) => { _host.History.Clear(); clear.Content = Lang.T("History cleared", "تم مسح السجل"); clear.IsEnabled = false; };
        historyButtons.Children.Add(clear);
        page.Row(Lang.T("Past jobs", "المهام السابقة"), historyButtons);

        page.Section(Lang.T("Recipes and brand styles", "الوصفات وأنماط العلامة"));
        page.Card();
        page.Row(Lang.T("Saved steps you can run on any file from the wheel's third ring", "خطوات محفوظة تشغّلها على أي ملف من الحلقة الثالثة في العجلة"),
            SettingsPage.Button(Lang.T("Open recipes", "فتح الوصفات"), App.ShowRecipes));

        page.Section(Lang.T("Windows", "ويندوز"));
        page.Card();
        page.Toggle(Lang.T("Add \"File Toolbox…\" to Explorer's right-click menu", "إضافة «File Toolbox…» إلى قائمة النقر بزر الماوس الأيمن في المستكشف"), ShellIntegration.IsContextMenuRegistered(),
            on => { ShellIntegration.SetContextMenu(on); page.Change(x => x.ExplorerContextMenu = on); },
            Lang.T("On Windows 11 it appears under \"Show more options\". Only for supported file types.", "في ويندوز 11 تظهر ضمن «عرض المزيد من الخيارات». للأنواع المدعومة فقط."));
        page.Toggle(Lang.T("Add File Toolbox to \"Send to\"", "إضافة أدوات الملفات إلى «إرسال إلى»"), ShellIntegration.IsSendToRegistered, ShellIntegration.SetSendTo);
    }

    /// <summary>Small / Medium / Large, with a wheel beside it that grows or shrinks to match.</summary>
    private static FrameworkElement WheelSizeChoice(SettingsPage page)
    {
        static double Scale(string size) => size == "S" ? 0.8 : size == "L" ? 1.2 : 1;
        var preview = new WheelPreview(56) { Margin = new Thickness(16, 0, 0, 0) };
        var scale = Fx.Transforms(preview).Scale;
        scale.ScaleX = scale.ScaleY = Scale(page.Current.WheelSize);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(SettingsPage.Choice(["S", "M", "L"], page.Current.WheelSize, v =>
            {
                Motion.To(scale, ScaleTransform.ScaleXProperty, Scale(v), 450, Motion.Spring);
                Motion.To(scale, ScaleTransform.ScaleYProperty, Scale(v), 450, Motion.Spring);
                page.Change(x => x.WheelSize = v);
            },
            v => v switch { "S" => Lang.T("Small", "صغيرة"), "L" => Lang.T("Large", "كبيرة"), _ => Lang.T("Medium", "متوسطة") }));
        row.Children.Add(preview);
        return row;
    }

    /// <summary>Next to the original, or in a chosen folder (with the folder and a chooser).</summary>
    private static FrameworkElement ResultLocation(SettingsPage page)
    {
        var s = page.Current;
        var folderText = SettingsPage.Ltr(new TextBlock { Text = s.OutputFolder ?? Lang.T("No folder chosen yet", "لم يُختر مجلد بعد"), Style = SettingsPage.Res("Lap.Help"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        var pick = SettingsPage.Button(Lang.T("Choose folder…", "اختيار مجلد…"), () => { });
        var folderRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0), Visibility = s.OutputLocation == OutputLocation.Folder ? Visibility.Visible : Visibility.Collapsed };
        DockPanel.SetDock(pick, Dock.Right);
        folderRow.Children.Add(pick);
        folderRow.Children.Add(folderText);
        pick.Click += (_, _) =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = page.Current.OutputFolder ?? AppPaths.DefaultFallbackOutputDir, UseDescriptionForTitle = true, Description = Lang.T("Save results in", "حفظ النتائج في") };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            folderText.Text = dialog.SelectedPath;
            page.Change(x => { x.OutputFolder = dialog.SelectedPath; x.OutputLocation = OutputLocation.Folder; });
        };
        var choice = SettingsPage.Choice(["beside", "folder"], s.OutputLocation == OutputLocation.Folder ? "folder" : "beside", v =>
        {
            folderRow.Visibility = v == "folder" ? Visibility.Visible : Visibility.Collapsed;
            if (v == "beside") page.Change(x => x.OutputLocation = OutputLocation.BesideSource);
            else if (!string.IsNullOrWhiteSpace(page.Current.OutputFolder)) page.Change(x => x.OutputLocation = OutputLocation.Folder);
        }, v => v == "beside" ? Lang.T("Next to the original", "بجانب الأصلي") : Lang.T("In a folder", "في مجلد"));
        var stack = new StackPanel();
        stack.Children.Add(choice);
        stack.Children.Add(folderRow);
        return stack;
    }
}
