using System.Windows;
using Toolbox.Core;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Settings;
using Toolbox.Windows.Diagnostics;
using Toolbox.Windows.Drag;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;
using Toolbox.Windows.Wheel;

namespace Toolbox.Windows;

public partial class App : Application
{
    private const string InstanceMutexName = "Local\\FileToolbox.SingleInstance";

    private Mutex? _instanceMutex;
    private SettingsStore _settings = null!;
    private WheelOverlayWindow _overlay = null!;
    private DragActivationService _drag = null!;
    private TrayIcon? _tray;
    private DiagnosticsChannel _diagnostics = null!;

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
        _diagnostics = new DiagnosticsChannel(diag);
        Log.Info($"File Toolbox starting (pid {Environment.ProcessId}, diagnostics {(diag ? "on" : "off")}, data {AppPaths.DataDir}).");

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        _settings = new SettingsStore();
        var settings = _settings.Current;

        _overlay = new WheelOverlayWindow(WheelDiameter(settings.WheelSize));
        _overlay.Show();
        _overlay.MoveOffscreen();

        _drag = new DragActivationService(_overlay, () => _settings.Current, () => [], _diagnostics);
        _drag.Dropped += OnWheelDrop;
        _drag.Start();
        _drag.WarmUp();

        var palette = Palette.Current(settings.Theme);
        _tray = new TrayIcon(AppIcon.CreateTrayIcon(palette.Accent), "File Toolbox — drag a file and tap Shift");
        _tray.SetMenu(
        [
            ("File Toolbox", null),
            ("-", null),
            ("Quit", () => Shutdown()),
        ]);

        _diagnostics.WriteReady(Environment.ProcessId);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _drag?.Dispose();
        _tray?.Dispose();
        Log.Info("File Toolbox exiting.");
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnWheelDrop(WheelDropRequest request)
    {
        // M1 spike: record the real payload; M3 routes it to the job runner and editors.
        Log.Info($"Wheel action requested: {request.ActionId} for {request.Paths.Count} item(s).");
    }

    internal static double WheelDiameter(string size) => size.ToUpperInvariant() switch
    {
        "S" => 270,
        "L" => 390,
        _ => 330,
    };
}
