using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Recipes;
using Toolbox.Core.Settings;

namespace Toolbox.Windows.Wheel;

/// <summary>Builds the ring models for a concrete payload from the capability map and pinned layouts.</summary>
internal static class WheelModels
{
    public static IReadOnlyList<WheelRingModel> Build(IReadOnlyList<ProbedFile> files, AppSettings settings, IReadOnlyList<Recipe>? recipes = null)
    {
        var inputClass = WheelLayouts.Classify(files);
        var convert = WheelLayouts.DefaultConvert;
        var convertIds = inputClass == InputClass.Pdf
            ? settings.ConvertRingPdf ?? convert.PdfActions
            : settings.ConvertRingImage ?? convert.ImageActions;
        var toolIds = settings.ToolsRing ?? WheelLayouts.DefaultTools.For(inputClass);

        var rings = new List<WheelRingModel>
        {
            new(ActionGroup.Convert, "Convert", Segments(convertIds, files), ShowIcons: false),
            new(ActionGroup.Tools, "Tools", Segments(toolIds, files), ShowIcons: true),
        };
        if (recipes is { Count: > 0 })
            rings.Add(new WheelRingModel(ActionGroup.Recipes, "Recipes", RecipeSegments(recipes, files), ShowIcons: true));
        return rings;
    }

    public static (string Primary, string Secondary) IdleText(IReadOnlyList<ProbedFile> files)
    {
        if (files.Count == 0) return ("", "");
        var usable = files.Where(f => f.IsUsable).ToList();
        long total = files.Sum(f => f.SizeBytes);
        if (files.Count == 1)
        {
            var f = files[0];
            return f.IsUsable ? (Bytes.Format(total), f.Name) : ("Not supported", f.Problem ?? f.Name);
        }
        var secondary = usable.Count == files.Count ? $"{files.Count} files" : $"{usable.Count} of {files.Count} supported";
        return ($"{files.Count} files · {Bytes.Format(total)}", secondary);
    }

    /// <summary>Pinned recipes, in their saved order; ones that do not apply are disabled in place.</summary>
    private static List<WheelSegmentModel> RecipeSegments(IReadOnlyList<Recipe> recipes, IReadOnlyList<ProbedFile> files)
    {
        var usable = files.Where(f => f.IsUsable).ToList();
        return recipes.Take(RingLayout.MaxSegments).Select(r =>
        {
            int ok = usable.Count(f => r.Accepts.Contains(f.Kind));
            bool enabled = ok > 0;
            var detail = !enabled ? "Does not apply to these files" : r.Interactive ? "Opens editor" : ok < files.Count ? $"Applies to {ok} of {files.Count} files" : null;
            return new WheelSegmentModel("recipe:" + r.Id, r.WheelLabel, r.Name, "recipe", enabled, detail);
        }).ToList();
    }

    private static List<WheelSegmentModel> Segments(IEnumerable<string> ids, IReadOnlyList<ProbedFile> files)
    {
        var list = new List<WheelSegmentModel>();
        foreach (var id in ids.Take(RingLayout.MaxSegments))
        {
            if (!CapabilityMap.TryGet(id, out var action)) continue;
            var verdict = CapabilityMap.Evaluate(action, files);
            string? detail = verdict switch
            {
                { IsEnabled: false } => verdict.Reason,
                { IsPartial: true } => $"Applies to {verdict.Applicable} of {verdict.Total} files",
                _ when action.Mode == ActionMode.Editor => "Opens editor",
                _ => null,
            };
            list.Add(new WheelSegmentModel(action.Id, action.Label, action.Title, action.Icon, verdict.IsEnabled, detail));
        }
        return list;
    }
}
