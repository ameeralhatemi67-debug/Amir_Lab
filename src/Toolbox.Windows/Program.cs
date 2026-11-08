using Toolbox.Core.Pulse;
using Toolbox.Windows.Pulse;

namespace Toolbox.Windows;

/// <summary>
/// Entry point. Claude Code runs "AmirLap.exe --pulse-status / --pulse-event" (when the user
/// connected Pulse to it) many times a minute; those runs only update Pulse's state file and finish
/// at once, without starting the app, its windows or the single-instance check.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] is ClaudeCodeConnection.StatusArg or ClaudeCodeConnection.EventArg)
            return PulseEntry.Run(args[0]);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
