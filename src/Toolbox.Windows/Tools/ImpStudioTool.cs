using System.Globalization;
using System.Windows;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Sound;
using Toolbox.Windows.Audio;
using Toolbox.Windows.Home;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Sound;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Tool #4: Imp Studio, every app's sound. A mixer with each app's volume, level and mute,
/// remembered per app and per output; hotkeys for the app in front with a small level bar; and the
/// Sound tile on Pulse's dock. While on, it keeps one thread that listens to Windows' sound system
/// (no polling); off, nothing runs.
/// </summary>
internal sealed class ImpStudioTool : ToolModule
{
    public const string ToolId = "impStudio";

    private readonly ToolboxHost _host;
    private SoundEngine? _engine;
    private SoundMemory? _memory;
    private MixerWindow? _mixer;

    public ImpStudioTool(ToolboxHost host)
    {
        _host = host;
        Commands =
        [
            new ToolCommand("louder", "Louder for the app in front", "Alt+Shift+Up", () => Nudge(up: true)) { TitleAr = "رفع صوت التطبيق الأمامي", Alternatives = ["Ctrl+Alt+Up", "Ctrl+Shift+Up"] },
            new ToolCommand("quieter", "Quieter for the app in front", "Alt+Shift+Down", () => Nudge(up: false)) { TitleAr = "خفض صوت التطبيق الأمامي", Alternatives = ["Ctrl+Alt+Down", "Ctrl+Shift+Down"] },
            new ToolCommand("mute", "Mute or unmute the app in front", "Alt+Shift+M", ToggleFront) { TitleAr = "كتم التطبيق الأمامي أو إلغاء كتمه", Alternatives = ["Ctrl+Alt+M", "Ctrl+Shift+M"] },
            new ToolCommand("mixer", "Open the mixer", null, Open) { TitleAr = "فتح المازج" },
        ];
    }

    public override string Id => ToolId;
    public override string Name => Lang.T("Imp Studio", "استوديو إمب");
    public override string Summary => Lang.T("Every app's sound, your way: each app's volume and level, remembered per app and output, and hotkeys for the app in front, even in a game.", "صوت كل تطبيق كما تريد: مستوى صوت كل تطبيق ومؤشره، يُحفظ لكل تطبيق ومخرج، واختصارات للتطبيق الأمامي حتى داخل لعبة.");
    public override string Icon => "lap-mixer";
    public override IReadOnlyList<ToolCommand> Commands { get; }

    private SoundSettings S => _host.Settings.Current.Sound;

    internal ISoundControl? Engine => _engine;

    internal MixerWindow? MixerView => _mixer;

    // ---- start / stop -----------------------------------------------------------------------------

    protected override void OnStart()
    {
        _memory = new SoundMemory(SoundMemory.DefaultPath);
        _engine = new SoundEngine(_memory, () => _host.Settings.Current.Sound.Remember);
        SoundHub.Attach(_engine);
    }

    protected override void OnStop()
    {
        _mixer?.Close();
        _mixer = null;
        SoundHub.Attach(null);
        _engine?.Dispose();
        _engine = null;
        _memory?.Dispose();
        _memory = null;
    }

    /// <summary>Opens the mixer (or brings it to the front).</summary>
    public override void Open()
    {
        if (_engine is null)
        {
            App.ShowHome(ToolId);
            return;
        }
        if (_mixer is null)
        {
            _mixer = new MixerWindow(_engine, _memory, id => App.ShortcutText(ToolId, id));
            _mixer.Closed += (_, _) => _mixer = null;
            _mixer.Show();
        }
        if (_mixer.WindowState == WindowState.Minimized) _mixer.WindowState = WindowState.Normal;
        Foreground.Activate(_mixer);
    }

    // ---- hotkeys for the app in front ------------------------------------------------------------

    private void Nudge(bool up)
    {
        if (Front() is not { } front) return;
        var (window, app, name, path) = front;
        if (app is null)
        {
            if (S.LevelBar) LevelBar.Say(name, path, Lang.T($"{name} hasn't made a sound yet", $"لم يُصدر {name} صوتًا بعد"), window);
            return;
        }
        var volume = SoundApps.Step(app.Volume, S.Step, up);
        bool muted = app.Muted && !up; // Louder unmutes
        _engine!.SetAppVolume(app.Key, volume);
        if (app.Muted && !muted) _engine.SetAppMute(app.Key, false);
        if (S.LevelBar) LevelBar.Show(app with { Volume = volume, Muted = muted }, window);
    }

    private void ToggleFront()
    {
        if (Front() is not { } front) return;
        var (window, app, name, path) = front;
        if (app is null)
        {
            if (S.LevelBar) LevelBar.Say(name, path, Lang.T($"{name} hasn't made a sound yet", $"لم يُصدر {name} صوتًا بعد"), window);
            return;
        }
        _engine!.SetAppMute(app.Key, !app.Muted);
        if (S.LevelBar) LevelBar.Show(app with { Muted = !app.Muted }, window);
    }

    /// <summary>The window in front, the app it is in the mixer (null when it has no sound yet), and its name.</summary>
    private (IntPtr Window, SoundApp? App, string Name, string? Path)? Front()
    {
        if (_engine is null || ForegroundApp.Find() is not { } front) return null;
        var apps = SoundApps.Group(_engine.Current.Sessions);
        var app = ForegroundApp.Match(apps, front.Key, front.ProcessId);
        var name = app is not null ? SoundHub.NameOf(app) : SoundApps.NameFrom(FileDescription(front.Path), null, front.Path);
        return (front.Window, app, name, front.Path);
    }

    private static string? FileDescription(string? path)
    {
        try { return path is null ? null : System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // ---- for Pulse's Sound tile -------------------------------------------------------------------

    /// <summary>Turns the volume of everything by wheel notches (each notch one step).</summary>
    public void Wheel(int delta)
    {
        if (_engine is null || delta == 0) return;
        var snap = _engine.Current;
        int notches = Math.Max(1, Math.Abs(delta) / 120);
        float v = snap.Master;
        for (int i = 0; i < notches; i++) v = SoundApps.Step(v, Math.Clamp(S.Step, 1, 10), delta > 0);
        _engine.SetMaster(v);
        if (snap.MasterMuted && delta > 0) _engine.SetMasterMute(false);
    }

    public void ToggleMute()
    {
        if (_engine is null) return;
        _engine.SetMasterMute(!_engine.Current.MasterMuted);
    }

    // ---- settings and tray ------------------------------------------------------------------------

    public override IEnumerable<TrayItem> TrayItems(Func<ToolCommand, string?> shortcutText)
    {
        yield return new TrayItem(Commands[3].Label, Open, Shortcut: shortcutText(Commands[3]));
        bool muted = _engine?.Current.MasterMuted == true;
        yield return new TrayItem(muted ? Lang.T("Unmute everything", "إلغاء كتم كل شيء") : Lang.T("Mute everything", "كتم كل شيء"), _engine is null ? null : ToggleMute);
    }

    public override void BuildSettings(SettingsPage page)
    {
        var s = page.Current.Sound;
        void Edit(Action<SoundSettings> edit) => page.Change(x => edit(x.Sound));

        page.Section(Lang.T("Mixer", "المازج"));
        page.Card();
        page.Toggle(Lang.T("Remember each app's level", "تذكّر مستوى كل تطبيق"), s.Remember, on => Edit(x => x.Remember = on),
            Lang.T("When an app starts a sound, it goes back to the level you gave it last time on that output. Handy when an app's update makes Windows forget.",
                   "عندما يبدأ تطبيق صوتًا، يعود إلى المستوى الذي ضبطته له آخر مرة على ذلك المخرج. مفيد عندما يجعل تحديثُ التطبيق ويندوز ينساه."));
        page.Row(Lang.T("Hotkey step", "خطوة الاختصار"), SettingsPage.Choice(SoundSettings.Steps.Select(v => v.ToString(CultureInfo.InvariantCulture)).ToArray(), s.Step.ToString(CultureInfo.InvariantCulture),
                v => Edit(x => x.Step = int.Parse(v, CultureInfo.InvariantCulture)), v => v + "%"),
            Lang.T("How much Louder and Quieter change the app in front with each press. The wheel over Pulse's Sound tile uses the same step.",
                   "مقدار تغيير «أعلى» و«أخفض» للتطبيق الأمامي مع كل ضغطة. تستخدم العجلة فوق مربع الصوت في نبض الخطوة نفسها."));
        page.Toggle(Lang.T("Show the level bar", "إظهار شريط المستوى"), s.LevelBar, on => Edit(x => x.LevelBar = on),
            Lang.T("A small bar near the top of the screen shows the new level for a second. It never takes the keyboard from a game.",
                   "يُظهر شريط صغير قرب أعلى الشاشة المستوى الجديد لثانية. لا يأخذ لوحة المفاتيح من اللعبة أبدًا."));
        page.Stacked(Lang.T("Sound check", "فحص الصوت"), SettingsPage.Button(Lang.T("Copy a sound check", "نسخ فحص الصوت"), CopyCheck),
            Lang.T("Copies what Windows shows about your outputs and apps as text, to paste where you ask for help. Nothing is changed.",
                   "ينسخ ما يُظهره ويندوز عن مخارجك وتطبيقاتك كنص، لتلصقه حيث تطلب المساعدة. لا يتغيّر شيء."));

        page.Section(Lang.T("Good to know", "معلومات مفيدة"));
        page.Card();
        page.Note(Lang.T("Imp Studio needs no sound driver: it uses the same per-app volumes as Windows' own volume mixer, so the two always agree.",
            "لا يحتاج استوديو إمب إلى برنامج تشغيل للصوت: يستخدم مستويات الصوت نفسها لكل تطبيق كما في مازج ويندوز، لذا يتفقان دائمًا."));
        page.Note(Lang.T("Each app's level is a share of the main volume: an app at 50% with everything at 60% plays at about 30%.",
            "مستوى كل تطبيق جزء من مستوى الصوت الرئيسي: تطبيق عند 50% مع كل شيء عند 60% يعمل عند نحو 30%."));
        page.Note(Lang.T("A few games take the sound device for themselves (exclusive mode). Their volume can then only be set inside the game.",
            "قليل من الألعاب تأخذ جهاز الصوت لنفسها (الوضع الحصري). عندها لا يُضبط صوتها إلا من داخل اللعبة."));
        page.Note(Lang.T("Rules, auto-duck, profiles, an output per app and effects come in the next updates.",
            "القواعد والخفض التلقائي وملفات التعريف ومخرج لكل تطبيق والمؤثرات تأتي في التحديثات القادمة."));
    }

    private void CopyCheck()
    {
        try
        {
            var text = SoundCheck.Run(_engine);
            Clipboard.SetText(text);
            Pulse.PulseToast.Show(Pulse.PulseTileView.Green, "lap-check", Lang.T("Sound check copied", "نُسخ فحص الصوت"), Lang.T("Paste it with Ctrl+V.", "الصقه باستخدام Ctrl+V."));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log.Warn($"The sound check could not be copied: {ex.Message}");
        }
    }
}
