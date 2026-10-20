using System.Text.Json;
using System.Text.Json.Serialization;
using Toolbox.Core.Output;

namespace Toolbox.Core.Settings;

/// <summary>User preferences, stored locally as JSON. Every field has a sensible default.</summary>
public sealed class AppSettings
{
    // ---- Activation & wheel ----------------------------------------------------------------
    /// <summary>Key tapped during a file drag to open the wheel: Shift, Ctrl, Alt or CapsLock.</summary>
    public string ActivationKey { get; set; } = "Shift";

    /// <summary>S, M or L.</summary>
    public string WheelSize { get; set; } = "M";

    /// <summary>system, on or off.</summary>
    public string ReduceMotion { get; set; } = "system";

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>Optional custom ring orders (action ids); null uses the built-in layout.</summary>
    public List<string>? ConvertRingPdf { get; set; }
    public List<string>? ConvertRingImage { get; set; }
    public List<string>? ToolsRing { get; set; }
    public List<string>? RecipesRing { get; set; }

    // ---- Output -------------------------------------------------------------------------------
    public OutputLocation OutputLocation { get; set; } = OutputLocation.BesideSource;
    public string? OutputFolder { get; set; }
    public string? FallbackFolder { get; set; }
    public string NamingPattern { get; set; } = OutputNamer.DefaultPattern;

    // ---- Quality defaults -------------------------------------------------------------------
    public int JpegQuality { get; set; } = 90;
    public int WebpQuality { get; set; } = 85;
    public int AvifQuality { get; set; } = 60;
    public int PdfDpi { get; set; } = 300;
    public string TransparencyBackground { get; set; } = "#FFFFFF";

    // ---- History & integration --------------------------------------------------------------
    public int HistoryRetentionDays { get; set; } = 30;
    public int HistoryMaxEntries { get; set; } = 1000;
    public bool ExplorerContextMenu { get; set; }
    public bool StartWithWindows { get; set; }
    public bool VerboseDiagnostics { get; set; }

    public OutputPolicy ToOutputPolicy() => new()
    {
        Location = OutputLocation,
        Folder = OutputFolder,
        FallbackFolder = string.IsNullOrWhiteSpace(FallbackFolder) ? AppPaths.DefaultFallbackOutputDir : FallbackFolder!,
        NamingPattern = string.IsNullOrWhiteSpace(NamingPattern) ? OutputNamer.DefaultPattern : NamingPattern,
    };

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

/// <summary>Loads and saves <see cref="AppSettings"/> atomically.</summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.SettingsFile;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event Action<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            Current = settings;
        }
        Changed?.Invoke(settings);
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diagnostics.Log.Warn($"Settings could not be read, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }
}
