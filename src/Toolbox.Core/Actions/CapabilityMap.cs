using Toolbox.Core.Inputs;

namespace Toolbox.Core.Actions;

/// <summary>How an action relates to a concrete set of inputs.</summary>
public sealed record Applicability(ActionDefinition Action, int Applicable, int Total, string? Reason)
{
    public bool IsEnabled => Applicable > 0 && Reason is null;
    public bool IsPartial => IsEnabled && Applicable < Total;
}

/// <summary>
/// The explicit capability map: every supported input, operation, output, setting and limitation.
/// It is the single source of truth for the wheel, the command palette, the CLI and docs/CAPABILITIES.md.
/// </summary>
public static class CapabilityMap
{
    public static class Ids
    {
        public const string PdfToJpg = "pdf.render-jpg";
        public const string PdfToPng = "pdf.render-png";
        public const string PdfToTxt = "pdf.text-txt";
        public const string PdfToDocxText = "pdf.text-docx";

        public const string ImageToJpg = "image.to-jpg";
        public const string ImageToPng = "image.to-png";
        public const string ImageToWebp = "image.to-webp";
        public const string ImageToAvif = "image.to-avif";
        public const string ImageToTiff = "image.to-tiff";
        public const string ImageToBmp = "image.to-bmp";
        public const string ImagesToPdf = "image.to-pdf";
        public const string ImagesToDocx = "image.to-docx";

        public const string Compress = "tool.compress";
        public const string Metadata = "tool.metadata";
        public const string Edit = "tool.edit";
        public const string Annotate = "tool.annotate";
        public const string Background = "tool.background";
        public const string Crop = "tool.crop";
        public const string Redact = "tool.redact";

        public const string OcrImageText = "ocr.image-text";
        public const string OcrSearchablePdf = "ocr.searchable-pdf";

        /// <summary>The PDF editor (opens the viewer in edit mode).</summary>
        public const string PdfEdit = "pdf.edit";

        /// <summary>The PDF editor, starting with the signature tool.</summary>
        public const string PdfSign = "pdf.sign";

        /// <summary>The PDF editor, starting with the pages (order, turn, remove, add from another PDF).</summary>
        public const string PdfPages = "pdf.pages";

        /// <summary>A dark copy for reading at night (vector and searchable; pictures kept by default).</summary>
        public const string PdfDark = "pdf.dark";

        /// <summary>Final save of a PDF editing session (option "plan" holds the edit plan JSON).</summary>
        public const string PdfSaveEdits = "pdf.save-edits";

        /// <summary>Final render of a Studio editing session (crop/adjust/background/annotate/redact stack).</summary>
        public const string StudioExport = "studio.export";

        /// <summary>Runs a saved recipe on each file (option "recipe" holds the recipe JSON).</summary>
        public const string RunRecipe = "recipe.run";

        /// <summary>Runs a saved recipe that combines all files into one result.</summary>
        public const string RunRecipeCombined = "recipe.run-combined";
    }

    public static readonly IReadOnlySet<FileKind> ImageInputs = new HashSet<FileKind>
    {
        FileKind.Jpeg, FileKind.Png, FileKind.WebP, FileKind.Avif, FileKind.Tiff,
        FileKind.Gif, FileKind.Bmp, FileKind.Svg, FileKind.Heic,
    };

    public static readonly IReadOnlySet<FileKind> PdfInputs = new HashSet<FileKind> { FileKind.Pdf };

    private static readonly OptionDefinition PagesOption = new(
        "pages", "Pages", OptionType.PageSet, "all",
        "\"all\" or a list such as 1,3,5-7. Pages are numbered from 1.");

    private static readonly OptionDefinition DpiOption = new(
        "dpi", "Resolution (DPI)", OptionType.Choice, "300",
        "Render resolution. 300 DPI suits print and reading; 150 DPI suits screens.",
        Choices: ["72", "96", "150", "200", "300", "600"]);

    private static OptionDefinition Quality(int fallback, string help) =>
        new("quality", "Quality", OptionType.Integer, fallback.ToString(), help, 1, 100);

    private static readonly OptionDefinition TransparencyOption = new(
        "background", "Background for transparent areas", OptionType.Color, "#FFFFFF",
        "JPG and BMP cannot store transparency; transparent pixels are filled with this colour.");

    private static readonly OptionDefinition MetadataOption = new(
        "metadata", "Metadata", OptionType.Choice, "keep",
        "keep: copy EXIF/XMP/IPTC and colour profile. strip: drop everything except the colour profile.",
        Choices: ["keep", "strip"]);

    public static IReadOnlyList<ActionDefinition> All { get; } = Build();

    private static readonly Dictionary<string, ActionDefinition> ById =
        All.ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);

    public static ActionDefinition Get(string id) =>
        ById.TryGetValue(id, out var action) ? action : throw new KeyNotFoundException($"Unknown action '{id}'.");

    public static bool TryGet(string id, out ActionDefinition action) => ById.TryGetValue(id, out action!);

    /// <summary>Evaluates an action against the given inputs, explaining why it is unavailable when it is.</summary>
    public static Applicability Evaluate(ActionDefinition action, IReadOnlyList<ProbedFile> files)
    {
        if (action.RequiresFeature is { } feature && !RuntimeFeatures.IsAvailable(feature))
            return new Applicability(action, 0, files.Count, RuntimeFeatures.Describe(feature));

        int ok = 0;
        string? reason = null;
        foreach (var file in files)
        {
            if (!file.IsUsable) { reason ??= file.Problem; continue; }
            if (file.Kind == FileKind.Heic && !RuntimeFeatures.IsAvailable(RuntimeFeatures.HeicDecode))
            {
                reason ??= "HEIC: " + RuntimeFeatures.Describe(RuntimeFeatures.HeicDecode);
                continue;
            }
            if (action.RedundantFor.Contains(file.Kind)) { reason ??= $"Already {file.Kind.Label()}"; continue; }
            if (!action.Inputs.Contains(file.Kind)) { reason ??= $"Not available for {file.Kind.Label()}"; continue; }
            ok++;
        }

        return ok > 0
            ? new Applicability(action, ok, files.Count, null)
            : new Applicability(action, 0, files.Count, reason ?? "Not available for these files");
    }

    private static IReadOnlyList<ActionDefinition> Build()
    {
        var list = new List<ActionDefinition>();

        // ---- PDF conversions -------------------------------------------------------------
        list.Add(new ActionDefinition
        {
            Id = Ids.PdfToJpg,
            Label = "JPG",
            Title = "Render pages as JPG",
            Meaning = "A JPEG picture of each selected page. Text in the picture is no longer selectable.",
            Group = ActionGroup.Convert,
            Inputs = PdfInputs,
            Output = FileKind.Jpeg,
            Mode = ActionMode.Options,
            Icon = "image",
            Lossy = true,
            Options = [PagesOption, DpiOption, Quality(90, "JPEG quality (1-100).")],
            Limitations =
            [
                "One image per page; multi-page documents ask which pages to export.",
                "Password-protected PDFs are not supported yet.",
            ],
        });
        list.Add(new ActionDefinition
        {
            Id = Ids.PdfToPng,
            Label = "PNG",
            Title = "Render pages as PNG",
            Meaning = "A lossless PNG picture of each selected page. Text in the picture is no longer selectable.",
            Group = ActionGroup.Convert,
            Inputs = PdfInputs,
            Output = FileKind.Png,
            Mode = ActionMode.Options,
            Icon = "image",
            Options = [PagesOption, DpiOption],
            Limitations = ["One image per page; multi-page documents ask which pages to export."],
        });
        list.Add(new ActionDefinition
        {
            Id = Ids.PdfToTxt,
            Label = "TXT",
            Title = "Extract existing text to TXT",
            Meaning = "The text already stored in the PDF, saved as a UTF-8 text file. Pages are separated by a form-feed character.",
            Group = ActionGroup.Convert,
            Inputs = PdfInputs,
            Output = FileKind.Text,
            Icon = "text",
            Options = [PagesOption],
            Limitations =
            [
                "Scanned pages have no text layer and produce no text; use Text (OCR) in the Tools ring to recognise scans.",
                "Reading order follows the PDF's internal order and may differ from the visual order in complex layouts.",
            ],
        });
        list.Add(new ActionDefinition
        {
            Id = Ids.PdfToDocxText,
            Label = "DOCX",
            Title = "Extract text to Word (text only)",
            Meaning = "A Word document containing the PDF's existing text as plain paragraphs, one section per page. Layout, fonts, images and tables are not reproduced.",
            Group = ActionGroup.Convert,
            Inputs = PdfInputs,
            Output = FileKind.Docx,
            Icon = "doc",
            Options = [PagesOption],
            Limitations =
            [
                "Not a layout-preserving PDF-to-Word conversion.",
                "Scanned pages without a text layer produce empty pages.",
                "Arabic paragraphs are marked right-to-left; mixed-direction lines keep the PDF's character order.",
            ],
        });

        // ---- Image conversions -----------------------------------------------------------
        list.Add(ImageConvert(Ids.ImageToJpg, FileKind.Jpeg, "JPG", "A JPEG image. Transparent areas are filled with the background colour.", lossy: true,
            [Quality(90, "JPEG quality (1-100)."), TransparencyOption, MetadataOption],
            ["JPEG has no transparency.", "Animated images keep only the first frame."]));
        list.Add(ImageConvert(Ids.ImageToPng, FileKind.Png, "PNG", "A lossless PNG image. Transparency is preserved.", lossy: false,
            [MetadataOption],
            ["Animated images keep only the first frame.", "PNG files of photos are usually much larger than JPEG."]));
        list.Add(ImageConvert(Ids.ImageToWebp, FileKind.WebP, "WebP", "A WebP image for the web. Transparency is preserved.", lossy: true,
            [Quality(85, "WebP quality (1-100). Use 100 with lossless=true for lossless WebP."),
             new OptionDefinition("lossless", "Lossless", OptionType.Boolean, "false"), MetadataOption],
            ["Animated images keep only the first frame."]));
        list.Add(ImageConvert(Ids.ImageToAvif, FileKind.Avif, "AVIF", "An AVIF (AV1) image: small files at good quality. Transparency is preserved.", lossy: true,
            [Quality(60, "AVIF quality (1-100); 50-65 is typical for photos."), MetadataOption],
            ["Encoding is slower than JPEG or WebP.", "Older software may not open AVIF files."],
            requires: RuntimeFeatures.AvifEncode));
        list.Add(ImageConvert(Ids.ImageToTiff, FileKind.Tiff, "TIFF", "A TIFF image with lossless LZW compression. Transparency is preserved.", lossy: false,
            [MetadataOption],
            ["Animated images keep only the first frame."]));
        list.Add(ImageConvert(Ids.ImageToBmp, FileKind.Bmp, "BMP", "An uncompressed 24-bit BMP image. Transparent areas are filled with the background colour.", lossy: false,
            [TransparencyOption],
            ["BMP has no transparency and no metadata.", "Files are large."]));
        list.Add(new ActionDefinition
        {
            Id = Ids.ImagesToPdf,
            Label = "PDF",
            Title = "Place images in a PDF",
            Meaning = "One PDF with each image on its own page, in file-name order. The pictures are embedded; their text is not recognised.",
            Group = ActionGroup.Convert,
            Inputs = ImageInputs,
            Output = FileKind.Pdf,
            Arity = InputArity.Combined,
            Icon = "pdf",
            Options =
            [
                new("page", "Page size", OptionType.Choice, "a4", "a4 / letter: fit the image within margins. fit: page matches the image.", Choices: ["a4", "letter", "fit"]),
                new("margin", "Margin (mm)", OptionType.Integer, "10", Min: 0, Max: 50),
                new("order", "Order", OptionType.Choice, "name", "name: natural file-name order. given: the order the files were provided.", Choices: ["name", "given"]),
            ],
            Limitations =
            [
                "JPEG images are embedded without re-encoding; other formats are embedded losslessly.",
                "The PDF is not searchable; use Make a searchable PDF (Tools ring or palette) for that.",
            ],
        });
        list.Add(new ActionDefinition
        {
            Id = Ids.ImagesToDocx,
            Label = "DOCX",
            Title = "Place images in a Word document",
            Meaning = "A Word document containing the images, one per page, scaled to fit. This does not convert pictures of text into editable text.",
            Group = ActionGroup.Convert,
            Inputs = ImageInputs,
            Output = FileKind.Docx,
            Arity = InputArity.Combined,
            Icon = "doc",
            Options = [new("order", "Order", OptionType.Choice, "name", Choices: ["name", "given"])],
            Limitations = ["Images are embedded as pictures; no OCR is performed."],
        });

        // ---- Tools (focused editors) --------------------------------------------------------
        // Editing tools also accept a PDF: the chosen page is rendered first and the result is an image.
        const string pdfPageNote = "For a PDF, one chosen page is rendered and the result is an image, not a PDF.";
        list.Add(Tool(Ids.Compress, "Compress", "Compress image", "compress", " Compressed",
            "A smaller copy using lossless optimisation, a chosen quality, or a target file size. The result reports whether the target was met.", lossy: true,
            limitations: ["PDF compression is not available yet."]));
        list.Add(Tool(Ids.Metadata, "Metadata", "Inspect or remove metadata", "tag", " Clean",
            "Shows EXIF, GPS, XMP, IPTC and colour-profile data (for PDFs: the document information) and saves a copy without the categories you remove. Visible content is not changed.", lossy: false,
            inputs: WithPdf(ImageInputs),
            limitations: ["For PDFs, removes the document-information dictionary and XMP stream only; attachments, form data and annotations are kept.",
                          "Metadata removal does not remove identifying information visible in the content itself."]));
        list.Add(Tool(Ids.Edit, "Edit", "Adjust and resize", "sliders", " Edited",
            "A copy with brightness, contrast, saturation, sharpening, rotation or size changes applied.", lossy: true,
            inputs: WithPdf(ImageInputs), limitations: [pdfPageNote]));
        list.Add(Tool(Ids.Annotate, "Annotate", "Annotate", "pen", " Annotated",
            "A copy with arrows, shapes, highlights, numbered markers or text drawn into the pixels.", lossy: true,
            inputs: WithPdf(ImageInputs), limitations: [pdfPageNote]));
        list.Add(Tool(Ids.Background, "Add BG", "Add background", "frame", " With Background",
            "The image placed on a styled canvas (gradient, colour or picture) with padding, rounded corners and shadow. This is decorative framing, not subject cut-out.", lossy: true,
            output: FileKind.Png, inputs: WithPdf(ImageInputs), limitations: [pdfPageNote]));
        list.Add(Tool(Ids.Crop, "Crop", "Crop", "crop", " Cropped",
            "A copy containing only the selected rectangle, optionally constrained to an aspect ratio.", lossy: true,
            inputs: WithPdf(ImageInputs), limitations: [pdfPageNote]));
        list.Add(Tool(Ids.Redact, "Blur", "Blur or hide", "eye-off", " Redacted",
            "A copy with chosen areas blurred, pixelated or covered with solid colour in the exported pixels. Click a word to hide it, drag across text, or let it find e-mails, phone numbers, card numbers, IBANs, IP addresses and keys. Blur and pixelate hide from a glance; Solid is the secure choice.", lossy: true,
            limitations: ["Redacts pictures only. PDF redaction (removing underlying text) is not available.", "Metadata is not changed by redaction; use Metadata to remove it."]));

        // ---- OCR (Windows' built-in recogniser; languages are installed in Windows) ----------
        list.Add(new ActionDefinition
        {
            Id = Ids.OcrImageText,
            Label = "Text (OCR)",
            Title = "Recognise text (Arabic/English)",
            Meaning = "Text recognised on this computer from the picture or scanned pages, in reading order (Arabic right-to-left), saved as UTF-8 text. PDF pages that already have selectable text use it as is.",
            Group = ActionGroup.Tools,
            Inputs = new HashSet<FileKind>(ImageInputs.Append(FileKind.Pdf)),
            Output = FileKind.Text,
            Icon = "scan",
            RequiresFeature = RuntimeFeatures.Ocr,
            Options = [new("languages", "Languages", OptionType.Choice, "ara+eng", Choices: ["ara+eng", "eng", "ara"]), PagesOption],
            Limitations =
            [
                "Recognition is not perfect; check names, numbers and codes. Measured accuracy is in docs/OCR_EVALUATION.md.",
                "Printed text only; handwriting is not supported.",
                "Tables and multi-column layouts may be read in the wrong order; no layout is reproduced.",
            ],
        });
        list.Add(new ActionDefinition
        {
            Id = Ids.OcrSearchablePdf,
            Label = "Searchable",
            Title = "Make a searchable PDF",
            Meaning = "A copy of the PDF (or a new PDF from a picture) with an invisible recognised-text layer, so it can be searched and copied. PDF pages are kept as they are; pages that already have selectable text are not recognised again.",
            Group = ActionGroup.Tools,
            Inputs = new HashSet<FileKind>(ImageInputs.Append(FileKind.Pdf)),
            Output = FileKind.Pdf,
            Icon = "scan",
            OutputSuffix = " Searchable",
            RequiresFeature = RuntimeFeatures.Ocr,
            Options = [new("languages", "Languages", OptionType.Choice, "ara+eng", Choices: ["ara+eng", "eng", "ara"]), DpiOption],
            Limitations =
            [
                "The visible page is unchanged, so recognition mistakes are only in the hidden text layer; search and copy to check it.",
                "PDF viewers rebuild right-to-left reading order themselves: some (including Chromium-based ones) reorder codes such as AB1234 inside Arabic lines when copying. The TXT action gives the exact recognised text.",
                "PDFs whose pages cannot be copied are recreated from page images (noted when it happens). Password-protected PDFs are not supported.",
            ],
        });

        // ---- PDF editing (the viewer's edit mode) and the dark copy ---------------------------------
        const string pdfEditNote = "Saved as a new PDF beside the original, which is never changed. Pages keep their text, links and quality.";
        list.Add(Tool(Ids.PdfEdit, "Edit PDF", "Edit PDF", "pen", " Edited",
            "Add text, drawings, highlights, pictures, signatures and white cover; turn, reorder, remove pages or add pages from other PDFs. The result is a PDF.", lossy: false,
            output: FileKind.Pdf, inputs: PdfInputs,
            limitations: [pdfEditNote, "Existing text cannot be retyped in place: cover it with white and type over it.", "Password-protected PDFs are not supported yet."]));
        list.Add(Tool(Ids.PdfSign, "Sign", "Sign PDF", "sign", " Signed",
            "Place a drawn, typed or picture signature on any page. Signatures you make are kept for next time on this PC.", lossy: false,
            output: FileKind.Pdf, inputs: PdfInputs,
            limitations: [pdfEditNote, "A drawn signature is a picture of a signature, not a certificate-based digital signature."]));
        list.Add(Tool(Ids.PdfPages, "Pages", "Organise pages", "pages", " Edited",
            "Reorder, turn and remove pages, or add pages from other PDFs, then save the result as one PDF.", lossy: false,
            output: FileKind.Pdf, inputs: PdfInputs, limitations: [pdfEditNote]));
        list.Add(new ActionDefinition
        {
            Id = Ids.PdfDark,
            Label = "Dark copy",
            Title = "Dark copy",
            Meaning = "A copy for reading in the dark: white pages become black and black text white. The pages stay vector and searchable; photos and figures keep their colours unless you choose otherwise.",
            Group = ActionGroup.Tools,
            Inputs = PdfInputs,
            Output = FileKind.Pdf,
            Icon = "moon",
            OutputSuffix = " Dark",
            Options = [new("pictures", "Photos and figures", OptionType.Choice, "keep", "keep: as they are. invert: like the rest of the page.", Choices: ["keep", "invert"])],
            Limitations =
            [
                "For reading on a screen; printing it uses a lot of ink.",
                "Comments and form fields drawn on top of the page keep their colours.",
                "It relies on PDF transparency: very old viewers may show the page unchanged.",
            ],
        });

        foreach (var (id, arity) in new[] { (Ids.RunRecipe, InputArity.PerFile), (Ids.RunRecipeCombined, InputArity.Combined) })
        {
            list.Add(new ActionDefinition
            {
                Id = id,
                Label = "Recipe",
                Title = "Run recipe",
                Meaning = "Runs a saved sequence of steps. Intermediate files are temporary and removed; only the final results are saved, then checked against the recipe's requirements.",
                Group = ActionGroup.Recipes,
                Inputs = new HashSet<FileKind>(ImageInputs.Append(FileKind.Pdf)),
                Arity = arity,
                Icon = "recipe",
            });
        }

        list.Add(new ActionDefinition
        {
            Id = Ids.StudioExport,
            Label = "Export",
            Title = "Export edited image",
            Meaning = "Renders the editing session (crop, adjustments, background, annotations, redactions) from the original-quality source.",
            Group = ActionGroup.Tools,
            Inputs = new HashSet<FileKind>(ImageInputs.Append(FileKind.Pdf)),
            Output = FileKind.Png,
            Icon = "export",
        });

        list.Add(new ActionDefinition
        {
            Id = Ids.PdfSaveEdits,
            Label = "Save",
            Title = "Save edited PDF",
            Meaning = "Writes the PDF editor's result as a new PDF beside the original: the pages in their new order and turns, with what was added drawn on them. The original is not changed.",
            Group = ActionGroup.Tools,
            Inputs = PdfInputs,
            Output = FileKind.Pdf,
            Icon = "export",
            OutputSuffix = " Edited",
        });

        return list;
    }

    private static ActionDefinition ImageConvert(
        string id, FileKind output, string label, string meaning, bool lossy,
        IReadOnlyList<OptionDefinition> options, IReadOnlyList<string> limitations, string? requires = null) => new()
    {
        Id = id,
        Label = label,
        Title = $"Convert to {label}",
        Meaning = meaning,
        Group = ActionGroup.Convert,
        Inputs = ImageInputs,
        Output = output,
        Icon = "image",
        Lossy = lossy,
        Options = options,
        Limitations = limitations,
        RequiresFeature = requires,
        RedundantFor = new HashSet<FileKind> { output },
    };

    private static IReadOnlySet<FileKind> WithPdf(IReadOnlySet<FileKind> kinds) => new HashSet<FileKind>(kinds.Append(FileKind.Pdf));

    private static ActionDefinition Tool(
        string id, string label, string title, string icon, string suffix, string meaning, bool lossy,
        FileKind? output = null, IReadOnlySet<FileKind>? inputs = null, IReadOnlyList<string>? limitations = null) => new()
    {
        Id = id,
        Label = label,
        Title = title,
        Meaning = meaning,
        Group = ActionGroup.Tools,
        Inputs = inputs ?? ImageInputs,
        Output = output,
        Mode = ActionMode.Editor,
        Icon = icon,
        OutputSuffix = suffix,
        Lossy = lossy,
        Limitations = limitations ?? [],
    };
}
