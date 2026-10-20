using System.Globalization;
using System.Text;

namespace Toolbox.Core.Diagnostics;

/// <summary>
/// Local diagnostic log (never sent anywhere). Records events, timings and errors —
/// not document contents. Files rotate daily and are removed after 14 days.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static bool _pruned;

    /// <summary>When true, also mirror log lines to standard error (CLI and harness use).</summary>
    public static bool MirrorToConsole { get; set; }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}";
        if (MirrorToConsole) Console.Error.WriteLine(line);
        try
        {
            lock (Gate)
            {
                var dir = AppPaths.LogsDir;
                if (!_pruned) { Prune(dir); _pruned = true; }
                var file = Path.Combine(dir, $"toolbox-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Prune(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "toolbox-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-14)) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
