using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Toolbox.Core;
using Toolbox.Core.Diagnostics;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Editors;
using Toolbox.Windows.Home;
using Toolbox.Windows.Imaging;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.CommandPalette;
using Toolbox.Windows.Settings;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Tools;

namespace Toolbox.Windows;

/// <summary>
/// Amir Lap: one tray app hosting several tools (File Toolbox first). Each tool can be switched
/// off, has its own shortcuts and settings, and costs nothing while it is off.
/// </summary>
public partial class App : Application
{
    private const string InstanceMutexName = "Local\\AmirLap.SingleInstance";

    private Mutex? _instanceMutex;
    private ToolboxHost _host = null!;
    private ActionDispatcher _dispatcher = null!;
    private ToolRegistry? _tools;
    private FileToolboxTool _fileToolbox = null!;
    private TrayIcon? _tray;
    private InstanceChannel? _channel;
    private PaletteWindow? _palette;
    private AmirLapWindow? _home;
    private HistoryWindow? _historyWindow;
    private Recipes.RecipesWindow? _recipesWindow;
    private readonly List<string> _pendingPaths = [];
    private DispatcherTimer? _pathBatch;
    private string _look = "";
    private bool _quitting;

    internal static ToolboxHost Host => ((App)Current)._host;
    internal static ActionDispatcher Actions => ((App)Current)._dispatcher;

    /// <summary>
    /// Set by the tests, which create the App only for its shared styles: they pump messages, which
    /// would otherwise run start-up (the single-instance check, the tray, the tools) inside the test.
    /// </summary>
    internal const string StylesOnlyVariable = "AMIRLAP_STYLES_ONLY";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Environment.GetEnvironmentVariable(StylesOnlyVariable) == "1") return;

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            // Hand the request (files from Explorer's menu or "Send to") to the running instance.
            InstanceChannel.TrySend(e.Args.Length == 0 ? ["--show"] : e.Args);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(); // nothing was running
            return;
        }

        // First start after File Toolbox became part of Amir Lap: copy its data over before anything
        // (the log included) creates the new data folder. The old folder is left as it was.
        int migrated = 0;
        string? migrationError = null;
        if (AppPaths.DataOverride is null)
        {
            try { migrated = LegacyDataMigration.Run(AppPaths.LegacyDataDir, AppPaths.DefaultDataDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { migrationError = ex.Message; }
        }

        bool diag = e.Args.Contains("--diag", StringComparer.OrdinalIgnoreCase)
                    || Environment.GetEnvironmentVariable("AMIRLAP_DIAG") == "1"
                    || Environment.GetEnvironmentVariable("FILETOOLBOX_DIAG") == "1";
        var diagnostics = new DiagnosticsChannel(diag);
        Log.Info($"Amir Lap starting (pid {Environment.ProcessId}, diagnostics {(diag ? "on" : "off")}, data {AppPaths.DataDir}).");
        if (migrated > 0) Log.Info($"Copied {migrated} file(s) of File Toolbox data from {AppPaths.LegacyDataDir}.");
        if (migrationError is not null) Log.Warn($"File Toolbox data could not be copied over: {migrationError}");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        _host = new ToolboxHost(diagnostics);
        ApplyLook();
        // Windows' own light/dark, accent, transparency and animation settings can change any time.
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => ApplyLook());
        _host.Settings.Changed += _ => ToolboxHost.OnUi(() => ApplyLook());
        WicHeicDecoder.Register();
        Ocr.WindowsOcrEngine.Register();

        var toasts = new ToastManager(_host, job => new JobDetailsWindow(job, j => _dispatcher.Retry(j)).Show());
        _dispatcher = new ActionDispatcher(_host, toasts);
        _dispatcher.Editors = new EditorLauncher(_dispatcher);

        _fileToolbox = new FileToolboxTool(_host, _dispatcher, diagnostics);
        _tools = new ToolRegistry(_host.Settings, [_fileToolbox, new LongshotTool(_host), new PulseTool(_host)]);

        _channel = new InstanceChannel();
        _channel.Start(args => Dispatcher.BeginInvoke(() => HandleArguments(args, fromAnotherInstance: true)));

        _tray = new TrayIcon(AppIcon.CreateTrayIcon(ThemeResources.Current.Accent), "Amir Lap");
        _tray.SetLanguage(Lang.T("Amir Lap", "أمير لاب"), Lang.IsArabic);
        _tray.PrimaryAction = OnTrayClick;
        _tools.Changed += RefreshTrayMenu;
        _host.Runner.JobFinished += _ => Dispatcher.BeginInvoke(RefreshTrayMenu);
        _tools.Apply(force: true); // starts the tools that are on and registers their shortcuts

        diagnostics.WriteReady(Environment.ProcessId);
        HandleArguments(e.Args, fromAnotherInstance: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tools?.Dispose();
        _tray?.Dispose();
        _channel?.Dispose();
        // A second launch that only forwarded its arguments never started, so it does not log an exit.
        if (_host is not null)
        {
            _host.Shutdown();
            Log.Info("Amir Lap exiting.");
        }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // ---- entry points ---------------------------------------------------------------------------

    /// <summary>
    /// Paths (from Explorer's menu, "Send to" or the command line) open File Toolbox's palette with
    /// those files. Explorer starts one process per selected file, so arrivals are batched.
    /// </summary>
    private void HandleArguments(string[] args, bool fromAnotherInstance)
    {
        // "AmirLap.exe --quit" (used by the uninstaller and scripts): finish like the tray's Quit.
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            Quit();
            return;
        }
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && (File.Exists(a) || Directory.Exists(a))).ToList();
        if (paths.Count > 0)
        {
            _pendingPaths.AddRange(paths);
            _pathBatch ??= new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Normal, (_, _) =>
            {
                _pathBatch!.Stop();
                var batch = _pendingPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _pendingPaths.Clear();
                ShowPalette(batch);
            }, Dispatcher);
            _pathBatch.Stop();
            _pathBatch.Start();
            return;
        }
        if (args.Contains("--palette", StringComparer.OrdinalIgnoreCase))
        {
            ShowPalette(); // File Toolbox's window directly (scripts, the QA harness)
            return;
        }
        bool quiet = args.Any(a => a is "--background" or "--diag");
        if (fromAnotherInstance || (!quiet && args.Length == 0)) ShowHome();
    }

    private void OnTrayClick()
    {
        if (_host.Settings.Current.TrayClick == "palette" && _tools?.IsEnabled(_fileToolbox) == true) ShowPalette();
        else ShowHome();
    }

    /// <summary>Opens the Amir Lap window, on a page (a tool id, "dashboard" or "general").</summary>
    internal static void ShowHome(string? page = null)
    {
        var app = (App)Current;
        if (app._tools is null) return;
        if (app._home is null)
        {
            app._home = new AmirLapWindow(app._host, app._tools);
            app._home.Closed += (_, _) =>
            {
                app._home = null;
                app.ShowTrayHintOnce();
            };
            app._home.Show();
        }
        app._home.RestoreFromMinimized();
        if (page is not null) app._home.ShowPage(page);
        Foreground.Activate(app._home);
    }

    /// <summary>Minimizes the Amir Lap window (before a screen capture started from it).</summary>
    internal static void MinimizeHome()
    {
        if (((App)Current)._home is { } home) home.WindowState = WindowState.Minimized;
    }

    internal static bool IsToolEnabled(string id)
    {
        var app = Current as App;
        return app?._tools?.Find(id) is { } tool && app._tools.IsEnabled(tool);
    }

    /// <summary>A tool by id (null when the app is not running, as in tests).</summary>
    internal static ToolModule? FindTool(string id) => (Current as App)?._tools?.Find(id);

    internal static void SetToolEnabled(string id, bool enabled)
    {
        if (Current is App { _tools: { } tools } && tools.Find(id) is { } tool) tools.SetEnabled(tool, enabled);
    }

    /// <summary>A command's shortcut as text ("Alt+Shift+S"), or null.</summary>
    internal static string? ShortcutText(string toolId, string commandId)
    {
        if (Current is not App { _tools: { } tools } || tools.Find(toolId) is not { } tool) return null;
        return tool.Commands.FirstOrDefault(c => c.Id == commandId) is { } command ? tools.ChordOf(tool, command)?.ToString() : null;
    }

    /// <summary>File Toolbox's palette (its main window). When the tool is off, shows its page instead.</summary>
    internal static void ShowPalette(IReadOnlyList<string>? files = null, string? preselect = null)
    {
        var app = (App)Current;
        if (app._tools is null || !app._tools.IsEnabled(app._fileToolbox))
        {
            ShowHome(FileToolboxTool.ToolId);
            return;
        }
        if (app._palette is null || !app._palette.IsLoaded)
        {
            app._palette = new PaletteWindow(app._dispatcher, app._host);
            app._palette.Closed += (_, _) => app._palette = null;
            app._palette.Show();
        }
        if (files is { Count: > 0 }) app._palette.AddFiles(files);
        if (preselect is not null) app._palette.Preselect(preselect);
        Foreground.Activate(app._palette);
    }

    /// <summary>File Toolbox's settings now live on its page in the Amir Lap window.</summary>
    internal static void ShowSettings() => ShowHome(FileToolboxTool.ToolId);

    internal static void ShowHistory()
    {
        var app = (App)Current;
        if (app._historyWindow is null)
        {
            app._historyWindow = new HistoryWindow(app._host, app._dispatcher);
            app._historyWindow.Closed += (_, _) => app._historyWindow = null;
            app._historyWindow.Show();
        }
        Foreground.Activate(app._historyWindow);
    }

    internal static void ShowRecipes()
    {
        var app = (App)Current;
        if (app._recipesWindow is null)
        {
            app._recipesWindow = new Recipes.RecipesWindow(app._host);
            app._recipesWindow.Closed += (_, _) => app._recipesWindow = null;
            app._recipesWindow.Show();
        }
        Foreground.Activate(app._recipesWindow);
    }

    /// <summary>Closes File Toolbox's own windows (when the tool is switched off).</summary>
    internal static void CloseFileToolboxWindows()
    {
        var app = (App)Current;
        app._palette?.Close();
        app._recipesWindow?.Close();
        app._historyWindow?.Close();
    }

    private void RefreshTrayMenu()
    {
        if (_tray is null || _tools is null) return;
        var items = new List<TrayItem> { new(Lang.T("Open Amir Lap", "فتح أمير لاب"), () => ShowHome(), Bold: true), TrayItem.Separator };
        foreach (var tool in _tools.Tools)
        {
            if (_tools.IsEnabled(tool))
            {
                var children = tool.TrayItems(c => _tools.ActiveShortcut(tool, c)).ToList();
                children.Add(TrayItem.Separator);
                children.Add(new TrayItem(Lang.T($"{tool.Name} settings", $"إعدادات {tool.Name}"), () => ShowHome(tool.Id)));
                children.Add(new TrayItem(Lang.T($"Turn off {tool.Name}", $"إيقاف {tool.Name}"), () => _tools.SetEnabled(tool, false)));
                items.Add(new TrayItem(tool.Name, Children: children));
            }
            else
            {
                items.Add(new TrayItem(Lang.T($"{tool.Name} (off)", $"{tool.Name} (متوقفة)"), Children:
                [
                    new TrayItem(Lang.T($"Turn on {tool.Name}", $"تشغيل {tool.Name}"), () => _tools.SetEnabled(tool, true)),
                    new TrayItem(Lang.T($"{tool.Name} settings", $"إعدادات {tool.Name}"), () => ShowHome(tool.Id)),
                ]));
            }
        }
        items.Add(TrayItem.Separator);
        items.Add(new TrayItem(Lang.T("Quit Amir Lap", "إنهاء أمير لاب"), Quit));
        _tray.SetMenu(items);
    }

    private void Quit()
    {
        _quitting = true;
        Shutdown();
    }

    /// <summary>
    /// Applies theme, accent, glass, language and motion (from Amir Lap's settings and Windows'
    /// own). Cheap when nothing that shows changed; otherwise every window updates and the Amir
    /// Lap window is rebuilt in place, keeping its page.
    /// </summary>
    private void ApplyLook()
    {
        var s = _host.Settings.Current;
        var palette = Palette.Current(s.Theme, s.Accent);
        bool reduced = Motion.Reduced(s.ReduceMotion);
        var look = $"{palette.IsDark}|{palette.Accent}|{ThemeResources.IsGlassOff(s.ReduceTransparency)}|{s.Language}|{reduced}";
        if (look == _look) return;
        bool first = _look.Length == 0;
        bool themeFlipped = !first && palette.IsDark != ThemeResources.Current.IsDark;
        _look = look;

        Lang.Set(s.Language);
        Motion.IsReduced = reduced;
        ThemeResources.Apply(this, s.Theme, s.Accent, s.ReduceTransparency);
        if (first) return;

        _tray?.SetIcon(AppIcon.CreateTrayIcon(ThemeResources.Current.Accent));
        _tray?.SetLanguage(Lang.T("Amir Lap", "أمير لاب"), Lang.IsArabic);
        RefreshTrayMenu();
        // After the click that changed it has finished, so the control is not replaced mid-event.
        if (_home is { } home) Dispatcher.BeginInvoke(() => home.Rebuild(spinThemeIcon: themeFlipped), DispatcherPriority.Background);
    }

    /// <summary>The first time the window is closed: say the app (and its shortcuts) keep running in the tray.</summary>
    private void ShowTrayHintOnce()
    {
        if (_quitting || Dispatcher.HasShutdownStarted || _tray is null || _host.Settings.Current.TrayHintShown) return;
        _tray.ShowBalloon(Lang.T("Amir Lap is still running", "ما زال أمير لاب يعمل"),
            Lang.T("Your tools and shortcuts keep working. Open it again from the notification area.", "أدواتك واختصاراتك تعمل كالمعتاد. افتحه مجددًا من منطقة الإشعارات."));
        var next = _host.Settings.Current.Clone();
        next.TrayHintShown = true;
        _host.Settings.Save(next);
    }

    internal static double WheelDiameter(string size) => size.ToUpperInvariant() switch
    {
        "S" => 270,
        "L" => 390,
        _ => 330,
    };
}
