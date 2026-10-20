using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;

namespace Toolbox.Processing.Metadata;

/// <summary>Job handler for "tool.metadata": option "remove" lists categories (all, location, camera, dates, author, xmp, iptc, thumbnail, comments).</summary>
internal sealed class MetadataCleanHandler : IActionHandler
{
    public string ActionId => CapabilityMap.Ids.Metadata;

    public Task RunAsync(ItemContext context)
    {
        var input = context.Input;
        var remove = MetadataCategoryNames.Parse(context.Options.GetString("remove", "all"));
        if (remove == MetadataCategories.None) throw new ProcessingException("Choose at least one kind of metadata to remove.");
        var slot = context.CreateOutput(new OutputSpec(input.BaseName, " Clean", input.Kind));
        foreach (var note in MetadataCleaner.Clean(input, remove, slot.TempPath)) context.Note(note);
        context.Note("Metadata removal does not change what is visible in the picture or document.");
        context.Report(1);
        return Task.CompletedTask;
    }
}
