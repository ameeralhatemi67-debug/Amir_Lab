using Toolbox.Core.Inputs;

namespace Toolbox.Core.Actions;

/// <summary>Broad class of a dragged payload, used to choose which ring layout to show.</summary>
public enum InputClass
{
    None,
    Pdf,
    Image,
}

/// <summary>
/// The pinned action order of one ring, per input class. Positions run clockwise from the top
/// and never move because of context: actions that do not apply are shown disabled in place.
/// </summary>
public sealed record RingLayout(ActionGroup Group, IReadOnlyList<string> PdfActions, IReadOnlyList<string> ImageActions)
{
    public const int MaxSegments = 8;

    public IReadOnlyList<string> For(InputClass inputClass) => inputClass == InputClass.Pdf ? PdfActions : ImageActions;
}

public static class WheelLayouts
{
    /// <summary>Convert ring. PDF: DOCX top, JPG right, PNG bottom, TXT left (the reference arrangement).</summary>
    public static RingLayout DefaultConvert { get; } = new(
        ActionGroup.Convert,
        [CapabilityMap.Ids.PdfToDocxText, CapabilityMap.Ids.PdfToJpg, CapabilityMap.Ids.PdfToPng, CapabilityMap.Ids.PdfToTxt],
        [
            CapabilityMap.Ids.ImageToPng, CapabilityMap.Ids.ImageToWebp, CapabilityMap.Ids.ImageToJpg, CapabilityMap.Ids.ImageToTiff,
            CapabilityMap.Ids.ImageToAvif, CapabilityMap.Ids.ImageToBmp, CapabilityMap.Ids.ImagesToPdf, CapabilityMap.Ids.ImagesToDocx,
        ]);

    /// <summary>
    /// Tools ring, per kind of file: "same seats, different guests". Tools both kinds have keep the
    /// same seat (Metadata top-right, Add BG bottom, Text (OCR) top-left), so muscle memory carries
    /// over; the other seats hold what fits that kind, so no tile is greyed out (owner, 13 Nov 2026:
    /// the PDF ring used to show Compress and Redact disabled).
    /// </summary>
    public static RingLayout DefaultTools { get; } = new(
        ActionGroup.Tools,
        [
            CapabilityMap.Ids.PdfEdit, CapabilityMap.Ids.Metadata, CapabilityMap.Ids.PdfSign, CapabilityMap.Ids.PdfDark,
            CapabilityMap.Ids.Background, CapabilityMap.Ids.PdfPages, CapabilityMap.Ids.OcrSearchablePdf, CapabilityMap.Ids.OcrImageText,
        ],
        [
            CapabilityMap.Ids.Compress, CapabilityMap.Ids.Metadata, CapabilityMap.Ids.Edit, CapabilityMap.Ids.Annotate,
            CapabilityMap.Ids.Background, CapabilityMap.Ids.Crop, CapabilityMap.Ids.Redact, CapabilityMap.Ids.OcrImageText,
        ]);

    public static InputClass Classify(IReadOnlyList<ProbedFile> files)
    {
        var usable = files.Where(f => f.IsUsable).ToList();
        if (usable.Count == 0) return InputClass.None;
        int pdfs = usable.Count(f => f.Kind == FileKind.Pdf);
        // Majority decides the layout; the per-action applicability still explains skipped files.
        return pdfs * 2 > usable.Count ? InputClass.Pdf : InputClass.Image;
    }
}
