using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Core.Output;

namespace Toolbox.Processing.Pdf;

/// <summary>Job handler for <see cref="CapabilityMap.Ids.PdfDark"/>: option "pictures" is keep (default) or invert.</summary>
internal sealed class PdfDarkHandler : IActionHandler
{
    public string ActionId => CapabilityMap.Ids.PdfDark;

    public Task RunAsync(ItemContext context)
    {
        var input = context.Input;
        bool keep = !string.Equals(context.Options.GetString("pictures", "keep"), "invert", StringComparison.OrdinalIgnoreCase);
        var slot = context.CreateOutput(new OutputSpec(input.BaseName, " Dark", FileKind.Pdf));
        var temp = slot.TempPath + ".building";
        try
        {
            PdfDarkCopy.Make(input.Path, temp, keep, context.Report, context.Cancellation);
            File.Copy(temp, slot.TempPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        if (keep) context.Note("Photos and figures keep their colours.");
        return Task.CompletedTask;
    }
}

/// <summary>Job handler for <see cref="CapabilityMap.Ids.PdfSaveEdits"/>: option "plan" holds the editor's plan as JSON; "suffix" may name the result.</summary>
internal sealed class PdfEditSaveHandler : IActionHandler
{
    public string ActionId => CapabilityMap.Ids.PdfSaveEdits;

    public Task RunAsync(ItemContext context)
    {
        var json = context.Options.GetString("plan", "");
        if (json.Length == 0) throw new ProcessingException("There are no edits to save.");
        var plan = PdfEditPlan.FromJson(json);
        var suffix = context.Options.GetString("suffix", " Edited");
        var slot = context.CreateOutput(new OutputSpec(context.Input.BaseName, suffix, FileKind.Pdf));
        PdfEditor.Save(plan, slot.TempPath, context.Report, context.Cancellation, overwrite: true);
        int marks = plan.Pages.Sum(p => p.Marks.Count);
        context.Note($"{plan.Pages.Count} {(plan.Pages.Count == 1 ? "page" : "pages")}{(marks > 0 ? $", {marks} {(marks == 1 ? "addition" : "additions")}" : "")}.");
        return Task.CompletedTask;
    }
}
