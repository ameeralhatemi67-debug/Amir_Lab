namespace Toolbox.Core;

/// <summary>
/// Local storage locations. Everything lives under %LOCALAPPDATA%\FileToolbox unless the
/// FILETOOLBOX_DATA environment variable points elsewhere (used by tests and the harness).
/// </summary>
public static class AppPaths
{
    public const string DataEnvironmentVariable = "FILETOOLBOX_DATA";

    public static string DataDir
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DataEnvironmentVariable);
            var dir = !string.IsNullOrWhiteSpace(overridePath)
                ? overridePath
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileToolbox");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.jsonl");
    public static string TempJournalFile => Path.Combine(DataDir, "pending-temp-files.txt");
    public static string LogsDir => Ensure(Path.Combine(DataDir, "logs"));
    public static string RecipesDir => Ensure(Path.Combine(DataDir, "recipes"));
    public static string PresetsDir => Ensure(Path.Combine(DataDir, "presets"));
    public static string DiagnosticsDir => Ensure(Path.Combine(DataDir, "diagnostics"));

    /// <summary>Default folder used when the source folder cannot be written to.</summary>
    public static string DefaultFallbackOutputDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "File Toolbox");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
