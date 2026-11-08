namespace Toolbox.Core.Pulse;

/// <summary>Pulse (the edge dock): where it sits, how it shows, which tiles it holds.</summary>
public sealed class PulseSettings
{
    /// <summary>left, right, top or bottom.</summary>
    public string Edge { get; set; } = "right";

    /// <summary>Where the middle of the dock sits along its edge, from 0 (start) to 1 (end).</summary>
    public double Position { get; set; } = 0.5;

    /// <summary>S, M or L.</summary>
    public string Size { get; set; } = "M";

    /// <summary>always, slide (slides away, a thin line stays) or fade (fades when not used).</summary>
    public string Show { get; set; } = "always";

    /// <summary>main (the main screen) or pointer (the screen the pointer was on when Pulse started).</summary>
    public string Screen { get; set; } = "main";

    /// <summary>A point ("x,y" in pixels) on the screen the user dragged the dock to; null means the main screen.</summary>
    public string? MonitorPoint { get; set; }

    public bool HideInFullScreen { get; set; } = true;

    /// <summary>Id of the tile set in use.</summary>
    public string CurrentSet { get; set; } = "work";

    public List<PulseTileSet> Sets { get; set; } = DefaultSets();

    /// <summary>A light around Claude's ring for what Claude Code is doing.</summary>
    public bool ShowClaudeStatus { get; set; } = true;

    /// <summary>Clicking Claude while a session waits for an answer brings that session's window forward.</summary>
    public bool JumpToTerminal { get; set; } = true;

    public bool ReadCodex { get; set; } = true;

    /// <summary>Share used (percent) at which a quiet warning appears; 0 turns warnings off.</summary>
    public int WarnAt { get; set; } = 80;

    /// <summary>A note when a Claude Code task that took over a minute finishes.</summary>
    public bool NotifyFinished { get; set; } = true;

    /// <summary>The Folder tile's folder (null: Downloads).</summary>
    public string? FolderPath { get; set; }

    /// <summary>The "App or website" tile's target: a file, program or web address.</summary>
    public string? LinkTarget { get; set; }

    public int TimerMinutes { get; set; } = 25;

    public static List<PulseTileSet> DefaultSets() =>
    [
        new() { Id = "work", Tiles = [PulseTiles.Claude, PulseTiles.Codex, PulseTiles.ChatGpt, PulseTiles.Longshot, PulseTiles.Toolbox] },
        new() { Id = "study", Tiles = [PulseTiles.Claude, PulseTiles.ChatGpt, PulseTiles.Timer, PulseTiles.Note, PulseTiles.Folder] },
    ];

    /// <summary>The set in use (the first one when the stored id is unknown).</summary>
    public PulseTileSet Active
    {
        get
        {
            if (Sets.Count == 0) Sets = DefaultSets();
            return Sets.FirstOrDefault(s => string.Equals(s.Id, CurrentSet, StringComparison.OrdinalIgnoreCase)) ?? Sets[0];
        }
    }

    /// <summary>The active set's tiles, known ones only, each once.</summary>
    public IReadOnlyList<string> ActiveTiles => PulseTiles.Clean(Active.Tiles);

    /// <summary>The tile size factor for <see cref="Size"/>.</summary>
    public static double ScaleOf(string size) => size?.ToUpperInvariant() switch { "S" => 0.8, "L" => 1.25, _ => 1.0 };

    /// <summary>The size whose factor is nearest (resizing by dragging snaps to these).</summary>
    public static string SizeNearest(double scale) => scale < 0.9 ? "S" : scale < 1.125 ? "M" : "L";
}

/// <summary>A named list of tiles (the user switches between sets).</summary>
public sealed class PulseTileSet
{
    /// <summary>work, study, or set3, set4... for sets the user adds.</summary>
    public string Id { get; set; } = "work";

    public List<string> Tiles { get; set; } = [];
}

/// <summary>The tiles Pulse knows.</summary>
public static class PulseTiles
{
    public const string Claude = "claude";
    public const string Codex = "codex";
    public const string ChatGpt = "chatgpt";
    public const string Longshot = "longshot";
    public const string Toolbox = "toolbox";
    public const string Timer = "timer";
    public const string Note = "note";
    public const string Colour = "colour";
    public const string Folder = "folder";
    public const string Link = "link";
    public const string System = "system";

    /// <summary>Every tile, in the order the gallery offers them.</summary>
    public static readonly IReadOnlyList<string> All = [Claude, Codex, ChatGpt, Longshot, Toolbox, Timer, Note, Colour, Folder, Link, System];

    /// <summary>Tiles that show a usage ring.</summary>
    public static bool HasRing(string id) => id is Claude or Codex or System;

    public static IReadOnlyList<string> Clean(IEnumerable<string>? tiles) =>
        (tiles ?? []).Where(t => All.Contains(t)).Distinct().ToList();

    /// <summary>Moves a tile one place earlier (-1) or later (+1).</summary>
    public static List<string> Move(IReadOnlyList<string> tiles, string id, int step)
    {
        var list = tiles.ToList();
        int i = list.IndexOf(id), j = i + step;
        if (i < 0 || j < 0 || j >= list.Count) return list;
        (list[i], list[j]) = (list[j], list[i]);
        return list;
    }
}
