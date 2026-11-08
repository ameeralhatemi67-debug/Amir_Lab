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
        var settings = _host.Settings.Current;
        ThemeResources.Apply(this, settings.Theme);
        SystemEvents.UserPreferenceChanged += (_, _) => Dispatcher.BeginInvoke(() => ThemeResources.Apply(this, _host.Settings.Current.Theme));
        WicHeicDecoder.Register();
        Ocr.WindowsOcrEngine.Register();

        var toasts = new ToastManager(_host, job => new JobDetailsWindow(job, j => _dispatcher.Retry(j)).Show());
        _dispatcher = new ActionDispatcher(_host, toasts);
        _dispatcher.Editors = new EditorLauncher(_dispatcher);

        _fileToolbox = new FileToolboxTool(_host, _dispatcher, diagnostics);
        _tools = new ToolRegistry(_host.Settings, [_fileToolbox]);

        _channel = new InstanceChannel();
        _channel.Start(args => Dispatcher.BeginInvoke(() => HandleArguments(args, fromAnotherInstance: true)));

        _tray = new TrayIcon(AppIcon.CreateTrayIcon(ThemeResources.Current.Accent), "Amir Lap");
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
            app._home.Closed += (_, _) => app._home = null;
            app._home.Show();
        }
        if (page is not null) app._home.ShowPage(page);
        Foreground.Activate(app._home);
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
        var items = new List<TrayItem> { new("Open Amir Lap", () => ShowHome(), Bold: true), TrayItem.Separator };
        foreach (var tool in _tools.Tools)
        {
            if (_tools.IsEnabled(tool))
            {
                var children = tool.TrayItems(c => _tools.ActiveShortcut(tool, c)).ToList();
                children.Add(TrayItem.Separator);
                children.Add(new TrayItem($"{tool.Name} settings", () => ShowHome(tool.Id)));
                children.Add(new TrayItem($"Turn off {tool.Name}", () => _tools.SetEnabled(tool, false)));
                items.Add(new TrayItem(tool.Name, Children: children));
            }
            else
            {
                items.Add(new TrayItem($"{tool.Name} (off)", Children:
                [
                    new TrayItem($"Turn on {tool.Name}", () => _tools.SetEnabled(tool, true)),
                    new TrayItem($"{tool.Name} settings", () => ShowHome(tool.Id)),
                ]));
            }
        }
        items.Add(TrayItem.Separator);
        items.Add(new TrayItem("Quit Amir Lap", () => Shutdown()));
        _tray.SetMenu(items);
    }

    internal static double WheelDiameter(string size) => size.ToUpperInvariant() switch
    {
        "S" => 270,
        "L" => 390,
        _ => 330,
    };
}
