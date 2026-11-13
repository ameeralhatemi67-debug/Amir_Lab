using System.Text.Json;
using System.Text.Json.Serialization;
using Toolbox.Core.Output;

namespace Toolbox.Core.Settings;

/// <summary>
/// User preferences, stored locally as JSON. Every field has a sensible default. App-wide (Amir Lap)
/// settings sit next to File Toolbox's own, which predate the multi-tool app and keep their names
/// so existing settings files load unchanged.
/// </summary>
public sealed class AppSettings
{
    // ---- Amir Lap (app-wide) -------------------------------------------------------------------
    /// <summary>What a left click on the tray icon opens: "home" (the Amir Lap window) or "palette".</summary>
    public string TrayClick { get; set; } = "home";

    /// <summary>"en" or "ar": the language of the Amir Lap window and tray menu. Arabic lays out right-to-left.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Accent colour: "blue" (default), "windows" (the Windows accent), "teal" or "violet".</summary>
    public string Accent { get; set; } = "blue";

    /// <summary>system, on or off. On: solid panels instead of glass (system follows Windows' transparency effects).</summary>
    public string ReduceTransparency { get; set; } = "system";

    /// <summary>Set once the "still running in the notification area" notice has been shown.</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Per-tool switch and shortcuts, keyed by tool id (for example "fileToolbox").</summary>
    public Dictionary<string, ToolSettings> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The stored state of a tool, or the defaults when it was never changed.</summary>
    public ToolSettings Tool(string id) => Tools.TryGetValue(id, out var t) ? t : new ToolSettings();

    /// <summary>The tool's entry for editing (created on first use).</summary>
    public ToolSettings EditTool(string id)
    {
        if (!Tools.TryGetValue(id, out var t)) Tools[id] = t = new ToolSettings();
        return t;
    }

    /// <summary>Longshot (scrolling screenshots).</summary>
    public LongshotSettings Longshot { get; set; } = new();

    /// <summary>Pulse (the edge dock with Claude and Codex usage and shortcuts).</summary>
    public Pulse.PulseSettings Pulse { get; set; } = new();

    // ---- File Toolbox: activation & wheel ----------------------------------------------------
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

    /// <summary>The Tools ring for images (the name is older than per-kind rings).</summary>
    public List<string>? ToolsRing { get; set; }

    public List<string>? ToolsRingPdf { get; set; }
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

/// <summary>Longshot's preferences.</summary>
public sealed class LongshotSettings
{
    /// <summary>What happens after the box is drawn: "wait" (Extend by hand), "auto" or "follow".</summary>
    public string AfterSelect { get; set; } = "wait";

    /// <summary>How far each Extend scrolls: "small", "auto" or "large".</summary>
    public string ScrollStep { get; set; } = "auto";

    /// <summary>
    /// What the capture starts on: "pointer" (the scroll area under the pointer, such as a chat, a list
    /// or a panel, or else the window in front) or "window" (always the whole window in front).
    /// </summary>
    public string StartOn { get; set; } = "pointer";

    /// <summary>Dim the rest of the screen while capturing, so it is plain what will be in the shot.</summary>
    public bool DimOutside { get; set; } = true;

    public bool CopyToClipboard { get; set; } = true;

    /// <summary>Show the result window after Done.</summary>
    public bool ShowResult { get; set; } = true;

    /// <summary>Where captures are saved; null means Pictures\Screenshots.</summary>
    public string? SaveFolder { get; set; }

    /// <summary>"png" or "jpg".</summary>
    public string Format { get; set; } = "png";

    /// <summary>Seconds to wait for the delayed-capture shortcut.</summary>
    public int DelaySeconds { get; set; } = 3;

    /// <summary>Share of the scrolling area each Extend aims to move.</summary>
    [JsonIgnore]
    public double StepFraction => ScrollStep switch { "small" => 0.35, "large" => 0.75, _ => 0.55 };
}

/// <summary>A tool's on/off switch and its keyboard shortcuts.</summary>
public sealed class ToolSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Shortcut text per command id ("Ctrl+Alt+F"). A missing entry means the command's default;
    /// an empty string means the user switched that shortcut off.
    /// </summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The chord in effect for a command, or null when it has none.</summary>
    public KeyChord? ShortcutFor(string commandId, string? defaultShortcut) =>
        (Shortcuts.TryGetValue(commandId, out var text) ? text : defaultShortcut) is { Length: > 0 } s && KeyChord.TryParse(s, out var chord)
            ? chord
            : null;
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
