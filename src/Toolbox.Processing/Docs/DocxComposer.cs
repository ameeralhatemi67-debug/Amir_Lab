using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Drawing.Wordprocessing;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using NetVips;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Imaging;
using A = DocumentFormat.OpenXml.Drawing;
using Pic = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Toolbox.Processing.Docs;

/// <summary>Writes Word documents with the Open XML SDK. A4 pages with 2 cm margins.</summary>
public static class DocxComposer
{
    private const long EmuPerPixel96 = 9525;
    private const int PageWidthTwips = 11906, PageHeightTwips = 16838, MarginTwips = 1134; // A4, 2 cm
    private const long EmuPerTwip = 635;

    /// <summary>One section per PDF page, one paragraph per text line, page breaks between pages.</summary>
    public static void TextToDocx(IReadOnlyList<(int Page, string Text)> pages, string title, string outputPath)
    {
        using var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        AddDefaultStyles(main);
        doc.PackageProperties.Title = title;
        doc.PackageProperties.Creator = "File Toolbox";

        for (int i = 0; i < pages.Count; i++)
        {
            var lines = pages[i].Text.Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
                body.Append(TextParagraph(line.TrimEnd()));
            if (i < pages.Count - 1)
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
        }
        body.Append(SectionProperties());
        main.Document.Save();
    }

    /// <summary>Each image on its own page, scaled down to fit the text area (never enlarged).</summary>
    public static void ImagesToDocx(IReadOnlyList<ProbedFile> images, string title, string outputPath,
        Action<double>? progress, CancellationToken cancellation, ICollection<string>? notes = null)
    {
        using var doc = WordprocessingDocument.Create(outputPath, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        AddDefaultStyles(main);
        doc.PackageProperties.Title = title;
        doc.PackageProperties.Creator = "File Toolbox";

        long maxW = (PageWidthTwips - 2 * MarginTwips) * EmuPerTwip;
        long maxH = (PageHeightTwips - 2 * MarginTwips) * EmuPerTwip - 200000;

        for (int i = 0; i < images.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var file = images[i];
            using var loaded = ImageLoader.Load(file);
            if (notes is not null) foreach (var n in loaded.Notes) notes.Add($"{file.Name}: {n}");

            // Word reliably displays JPEG and PNG; keep JPEGs as JPEG, everything else lossless PNG.
            bool asJpeg = file.Kind == FileKind.Jpeg;
            var bytes = ImageEncoder.SaveToBuffer(loaded.Image, asJpeg ? FileKind.Jpeg : FileKind.Png,
                new EncodeSettings { Quality = 95, KeepMetadata = false });
            var part = main.AddImagePart(asJpeg ? ImagePartType.Jpeg : ImagePartType.Png);
            using (var ms = new MemoryStream(bytes)) part.FeedData(ms);

            long w = loaded.Width * EmuPerPixel96, h = loaded.Height * EmuPerPixel96;
            double scale = Math.Min(1.0, Math.Min((double)maxW / w, (double)maxH / h));
            w = (long)(w * scale);
            h = (long)(h * scale);

            var paragraph = new Paragraph(
                new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                new Run(ImageDrawing(main.GetIdOfPart(part), w, h, (uint)(i + 1), file.Name)));
            body.Append(paragraph);
            if (i < images.Count - 1)
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
            progress?.Invoke((i + 1.0) / images.Count);
        }
        body.Append(SectionProperties());
        main.Document.Save();
    }

    /// <summary>True when most letters are Arabic-script (or other right-to-left) characters.</summary>
    public static bool IsRightToLeft(string text)
    {
        int rtl = 0, ltr = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            if (ch is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') rtl++;
            else ltr++;
        }
        return rtl > ltr;
    }

    private static Paragraph TextParagraph(string line)
    {
        bool rtl = IsRightToLeft(line);
        var runProps = new RunProperties();
        if (rtl)
        {
            runProps.Append(new RightToLeftText());
            runProps.Append(new Languages { Bidi = "ar-SA" });
        }
        var run = new Run(runProps, new Text(line) { Space = SpaceProcessingModeValues.Preserve });
        var paraProps = new ParagraphProperties(new SpacingBetweenLines { After = "0" });
        if (rtl)
        {
            paraProps.Append(new BiDi());
            paraProps.Append(new Justification { Val = JustificationValues.Right });
        }
        return new Paragraph(paraProps, run);
    }

    private static SectionProperties SectionProperties() => new(
        new PageSize { Width = (UInt32Value)(uint)PageWidthTwips, Height = (UInt32Value)(uint)PageHeightTwips },
        new PageMargin
        {
            Top = MarginTwips, Bottom = MarginTwips, Left = (UInt32Value)(uint)MarginTwips, Right = (UInt32Value)(uint)MarginTwips,
            Header = 708, Footer = 708, Gutter = 0,
        });

    private static void AddDefaultStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        styles.Styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Arial", EastAsia = "Calibri" },
                new FontSize { Val = "22" },
                new FontSizeComplexScript { Val = "24" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(new SpacingBetweenLines { After = "0", Line = "259", LineRule = LineSpacingRuleValues.Auto }))));
        styles.Styles.Save();
    }

    private static Drawing ImageDrawing(string relationshipId, long cx, long cy, uint id, string name) => new(
        new Inline(
            new Extent { Cx = cx, Cy = cy },
            new EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DocProperties { Id = id, Name = name },
            new NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new Pic.Picture(
                    new Pic.NonVisualPictureProperties(
                        new Pic.NonVisualDrawingProperties { Id = id, Name = name },
                        new Pic.NonVisualPictureDrawingProperties()),
                    new Pic.BlipFill(
                        new A.Blip { Embed = relationshipId },
                        new A.Stretch(new A.FillRectangle())),
                    new Pic.ShapeProperties(
                        new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        {
            DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0,
        });
}
