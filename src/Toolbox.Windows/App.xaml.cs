using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Diagnostics;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Drag;
using Toolbox.Windows.Editors;
using Toolbox.Windows.Imaging;
using Toolbox.Windows.Jobs;
using Toolbox.Windows.CommandPalette;
using Toolbox.Windows.Settings;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Wheel;

namespace Toolbox.Windows;

public partial class App : Application
{
    private const string InstanceMutexName = "Local\\FileToolbox.SingleInstance";

    private Mutex? _instanceMutex;
    private ToolboxHost _host = null!;
    private WheelOverlayWindow _overlay = null!;
    private DragActivationService _drag = null!;
    private ActionDispatcher _dispatcher = null!;
    private TrayIcon? _tray;
    private InstanceChannel? _channel;
    private GlobalHotkey? _hotkey;
    private PaletteWindow? _palette;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private Recipes.RecipesWindow? _recipesWindow;
    private readonly List<string> _pendingPaths = [];
    private DispatcherTimer? _pathBatch;

    internal static ToolboxHost Host => ((App)Current)._host;
    internal static ActionDispatcher Actions => ((App)Current)._dispatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        bool diag = e.Args.Contains("--diag", StringComparer.OrdinalIgnoreCase)
                    || Environment.GetEnvironmentVariable("FILETOOLBOX_DIAG") == "1";
        var diagnostics = new DiagnosticsChannel(diag);
        Log.Info($"File Toolbox starting (pid {Environment.ProcessId}, diagnostics {(diag ? "on" : "off")}, data {AppPaths.DataDir}).");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        _host = new ToolboxHost(diagnostics);
        var settings = _host.Settings.Current;
        ThemeResources.Apply(this, settings.Theme);
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => ThemeResources.Apply(this, _host.Settings.Current.Theme));
        WicHeicDecoder.Register();
        Ocr.WindowsOcrEngine.Register();

        var toasts = new ToastManager(_host, job => new JobDetailsWindow(job, j => _dispatcher.Retry(j)).Show());
        _dispatcher = new ActionDispatcher(_host, toasts);
        _dispatcher.Editors = new EditorLauncher(_dispatcher);

        _overlay = new WheelOverlayWindow(WheelDiameter(settings.WheelSize));
        _overlay.Show();
        _overlay.MoveOffscreen();

        _drag = new DragActivationService(_overlay, () => _host.Settings.Current, () => _host.Recipes.All().Where(r => r.Pinned).ToList(), diagnostics);
        _drag.Dropped += request => _dispatcher.Dispatch(request.ActionId, request.Paths, request.ScreenPoint);
        _drag.Start();
        _drag.WarmUp();

        _channel = new InstanceChannel();
        _channel.Start(args => Dispatcher.BeginInvoke(() => HandleArguments(args, fromAnotherInstance: true)));
        _hotkey = new GlobalHotkey(() => ShowPalette());

        _tray = new TrayIcon(AppIcon.CreateTrayIcon(ThemeResources.Current.Accent), "File Toolbox — drag a file and tap Shift");
        _tray.PrimaryAction = () => ShowPalette();
        RefreshTrayMenu();
        _host.Runner.JobFinished += _ => Dispatcher.BeginInvoke(RefreshTrayMenu);

        diagnostics.WriteReady(Environment.ProcessId);
        HandleArguments(e.Args, fromAnotherInstance: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _drag?.Dispose();
        _tray?.Dispose();
        _channel?.Dispose();
        _hotkey?.Dispose();
        _host?.Shutdown();
        Log.Info("File Toolbox exiting.");
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // ---- entry points ---------------------------------------------------------------------------

    /// <summary>
    /// Paths (from Explorer's menu, "Send to" or the command line) open the palette with those files.
    /// Explorer starts one process per selected file, so arrivals within a short window are batched.
    /// </summary>
    private void HandleArguments(string[] args, bool fromAnotherInstance)
    {
        // "FileToolbox.exe --quit" (used by the uninstaller and scripts): finish like the tray's Quit.
        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            Shutdown();
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
        bool quiet = args.Any(a => a is "--background" or "--diag");
        if (fromAnotherInstance || (!quiet && args.Length == 0)) ShowPalette();
    }

    internal static void ShowPalette(IReadOnlyList<string>? files = null, string? preselect = null)
    {
        var app = (App)Current;
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

    internal static void ShowSettings()
    {
        var app = (App)Current;
        if (app._settingsWindow is null)
        {
            app._settingsWindow = new SettingsWindow(app._host, app._hotkey?.Registered ?? false);
            app._settingsWindow.Closed += (_, _) => app._settingsWindow = null;
            app._settingsWindow.Show();
        }
        Foreground.Activate(app._settingsWindow);
    }

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

    private void RefreshTrayMenu()
    {
        if (_tray is null) return;
        var last = _host.LastAction is { } la && CapabilityMap.TryGet(la.ActionId, out var action) ? action : null;
        _tray.SetMenu(
        [
            ("Open File Toolbox" + (_hotkey?.Registered == true ? "\tCtrl+Alt+F" : ""), () => ShowPalette()),
            (last is null ? "Repeat last action" : $"Repeat: {last.Title}…", last is null ? null : () => ShowPalette(preselect: last.Id)),
            ("Recipes", ShowRecipes),
            ("History", ShowHistory),
            ("Settings", ShowSettings),
            ("-", null),
            ("Quit File Toolbox", () => Shutdown()),
        ]);
    }

    internal static double WheelDiameter(string size) => size.ToUpperInvariant() switch
    {
        "S" => 270,
        "L" => 390,
        _ => 330,
    };
}
