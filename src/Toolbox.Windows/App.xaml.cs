using System.Windows;
using Microsoft.Win32;
using Toolbox.Core;
using Toolbox.Core.Diagnostics;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Drag;
using Toolbox.Windows.Imaging;
using Toolbox.Windows.Jobs;
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

    internal static ToolboxHost Host => ((App)Current)._host;
    internal static ActionDispatcher Actions => ((App)Current)._dispatcher;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out bool isFirst);
        if (!isFirst)
        {
            // M5 forwards command-line files to the running instance; for now just exit quietly.
            Shutdown();
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

        var toasts = new ToastManager(_host, job => new JobDetailsWindow(job, j => _dispatcher.Retry(j)).Show());
        _dispatcher = new ActionDispatcher(_host, toasts);

        _overlay = new WheelOverlayWindow(WheelDiameter(settings.WheelSize));
        _overlay.Show();
        _overlay.MoveOffscreen();

        _drag = new DragActivationService(_overlay, () => _host.Settings.Current, () => [], diagnostics);
        _drag.Dropped += request => _dispatcher.Dispatch(request.ActionId, request.Paths, request.ScreenPoint);
        _drag.Start();
        _drag.WarmUp();

        _tray = new TrayIcon(AppIcon.CreateTrayIcon(ThemeResources.Current.Accent), "File Toolbox — drag a file and tap Shift");
        _tray.SetMenu(
        [
            ("File Toolbox", null),
            ("-", null),
            ("Quit", () => Shutdown()),
        ]);

        diagnostics.WriteReady(Environment.ProcessId);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _drag?.Dispose();
        _tray?.Dispose();
        Log.Info("File Toolbox exiting.");
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    internal static double WheelDiameter(string size) => size.ToUpperInvariant() switch
    {
        "S" => 270,
        "L" => 390,
        _ => 330,
    };
}
