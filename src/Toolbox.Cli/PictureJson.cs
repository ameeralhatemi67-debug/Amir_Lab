using System.Text.Json.Nodes;
using Toolbox.Processing.Imaging;

namespace Toolbox.Cli;

/// <summary>The JSON form of a look or a diff, the same from <c>ftb chat … --json</c> and from MCP.</summary>
internal static class PictureJson
{
    public static JsonObject Look(PictureLook look) => new()
    {
        ["picture"] = look.Picture,
        ["width"] = look.Width,
        ["height"] = look.Height,
        ["folder"] = look.Folder,
        ["whole"] = look.Whole,
        ["tiles"] = new JsonArray(look.Tiles.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["path"] = t.Path,
            ["box"] = Box(t.Area),
        }).ToArray()),
    };

    public static JsonObject Diff(PictureDiff diff)
    {
        var o = new JsonObject
        {
            ["ok"] = true,
            ["old"] = diff.Old,
            ["new"] = diff.New,
            ["old_size"] = $"{diff.OldWidth}x{diff.OldHeight}",
            ["new_size"] = $"{diff.NewWidth}x{diff.NewHeight}",
            ["size_changed"] = diff.SizeChanged,
            ["threshold"] = diff.Threshold,
            ["changed_percent"] = diff.ChangedPercent,
            ["mean_delta"] = diff.MeanDelta,
            ["heat_map"] = diff.HeatMap,
            ["boxes"] = new JsonArray(diff.Boxes.Select(b => (JsonNode)Box(b)).ToArray()),
            ["tiles"] = new JsonArray(diff.Tiles.Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["changed_percent"] = t.ChangedPercent }).ToArray()),
        };
        if (diff.ChangedOutsidePercent is { } outside) o["changed_outside_percent"] = outside;
        if (diff.ChangedInsidePercent is { } inside) o["changed_inside_percent"] = inside;
        if (diff.ChangedBounds is { } bounds) o["changed_bounds"] = Box(bounds);
        return o;
    }

    private static JsonObject Box(PixelBox b) => new() { ["x"] = b.X, ["y"] = b.Y, ["width"] = b.Width, ["height"] = b.Height };
}
