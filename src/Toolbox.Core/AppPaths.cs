namespace Toolbox.Core;

/// <summary>
/// Local storage locations. Everything lives under %LOCALAPPDATA%\AmirLap unless the AMIRLAP_DATA
/// environment variable (or the older FILETOOLBOX_DATA, still honoured for existing scripts and
/// harnesses) points elsewhere.
/// </summary>
public static class AppPaths
{
    public const string DataEnvironmentVariable = "AMIRLAP_DATA";

    /// <summary>The name used before File Toolbox became a tool inside Amir Lap.</summary>
    public const string LegacyDataEnvironmentVariable = "FILETOOLBOX_DATA";

    public const string AppFolderName = "AmirLap";
    public const string LegacyAppFolderName = "FileToolbox";

    /// <summary>The data folder chosen through an environment variable, or null for the default one.</summary>
    public static string? DataOverride
    {
        get
        {
            foreach (var name in new[] { DataEnvironmentVariable, LegacyDataEnvironmentVariable })
            {
                var value = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            return null;
        }
    }

    public static string DefaultDataDir => Path.Combine(LocalAppData, AppFolderName);

    /// <summary>Where File Toolbox kept its data before the rename (read once, for migration).</summary>
    public static string LegacyDataDir => Path.Combine(LocalAppData, LegacyAppFolderName);

    public static string DataDir
    {
        get
        {
            var dir = DataOverride ?? DefaultDataDir;
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

    /// <summary>Default folder used when the source folder cannot be written to (File Toolbox results).</summary>
    public static string DefaultFallbackOutputDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "File Toolbox");

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
