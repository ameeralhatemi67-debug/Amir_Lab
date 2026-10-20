# Capability map

Generated from `Toolbox.Core/Actions/CapabilityMap.cs` with `ftb caps --markdown`. Do not edit by hand.
Each entry is a defined operation; the *Meaning* column states exactly what the result contains.
HEIC input is accepted only when Windows' HEIF and HEVC codecs are installed (checked at runtime by the desktop app).
*Status* is the state of the build that generated this file; see IMPLEMENTATION_STATUS.md for evidence.

## Convert

| Action | Accepts | Produces | Meaning | Limitations | Status |
|---|---|---|---|---|---|
| **Render pages as JPG**<br>`pdf.render-jpg` | PDF | JPG | A JPEG picture of each selected page. Text in the picture is no longer selectable. | One image per page; multi-page documents ask which pages to export.<br>Password-protected PDFs are not supported yet. | Working |
| **Render pages as PNG**<br>`pdf.render-png` | PDF | PNG | A lossless PNG picture of each selected page. Text in the picture is no longer selectable. | One image per page; multi-page documents ask which pages to export. | Working |
| **Extract existing text to TXT**<br>`pdf.text-txt` | PDF | TXT | The text already stored in the PDF, saved as a UTF-8 text file. Pages are separated by a form-feed character. | Scanned pages have no text layer and produce no text; recognising text in scans is a separate OCR action.<br>Reading order follows the PDF's internal order and may differ from the visual order in complex layouts. | Working |
| **Extract text to Word (text only)**<br>`pdf.text-docx` | PDF | DOCX | A Word document containing the PDF's existing text as plain paragraphs, one section per page. Layout, fonts, images and tables are not reproduced. | Not a layout-preserving PDF-to-Word conversion.<br>Scanned pages without a text layer produce empty pages.<br>Arabic paragraphs are marked right-to-left; mixed-direction lines keep the PDF's character order. | Working |
| **Convert to JPG**<br>`image.to-jpg` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | JPG | A JPEG image. Transparent areas are filled with the background colour. | JPEG has no transparency.<br>Animated images keep only the first frame. | Working |
| **Convert to PNG**<br>`image.to-png` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | PNG | A lossless PNG image. Transparency is preserved. | Animated images keep only the first frame.<br>PNG files of photos are usually much larger than JPEG. | Working |
| **Convert to WebP**<br>`image.to-webp` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | WebP | A WebP image for the web. Transparency is preserved. | Animated images keep only the first frame. | Working |
| **Convert to AVIF**<br>`image.to-avif` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | AVIF | An AVIF (AV1) image: small files at good quality. Transparency is preserved. | Encoding is slower than JPEG or WebP.<br>Older software may not open AVIF files. | Working |
| **Convert to TIFF**<br>`image.to-tiff` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | TIFF | A TIFF image with lossless LZW compression. Transparency is preserved. | Animated images keep only the first frame. | Working |
| **Convert to BMP**<br>`image.to-bmp` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | BMP | An uncompressed 24-bit BMP image. Transparent areas are filled with the background colour. | BMP has no transparency and no metadata.<br>Files are large. | Working |
| **Place images in a PDF**<br>`image.to-pdf` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | PDF | One PDF with each image on its own page, in file-name order. The pictures are embedded; their text is not recognised. | JPEG images are embedded without re-encoding; other formats are embedded losslessly.<br>The PDF is not searchable; text recognition is a separate OCR action. | Working |
| **Place images in a Word document**<br>`image.to-docx` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | DOCX | A Word document containing the images, one per page, scaled to fit. This does not convert pictures of text into editable text. | Images are embedded as pictures; no OCR is performed. | Working |

## Tools

| Action | Accepts | Produces | Meaning | Limitations | Status |
|---|---|---|---|---|---|
| **Compress image**<br>`tool.compress` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | same as input | A smaller copy using lossless optimisation, a chosen quality, or a target file size. The result reports whether the target was met. | PDF compression is not available yet. | Editor (desktop app) |
| **Inspect or remove metadata**<br>`tool.metadata` | PDF, JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | same as input | Shows EXIF, GPS, XMP, IPTC and colour-profile data (for PDFs: the document information) and saves a copy without the categories you remove. Visible content is not changed. | For PDFs, removes the document-information dictionary and XMP stream only; attachments, form data and annotations are kept.<br>Metadata removal does not remove identifying information visible in the content itself. | Working |
| **Adjust and resize**<br>`tool.edit` | PDF, JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | same as input | A copy with brightness, contrast, saturation, sharpening, rotation or size changes applied. | For a PDF, one chosen page is rendered and the result is an image, not a PDF. | Editor (desktop app) |
| **Annotate**<br>`tool.annotate` | PDF, JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | same as input | A copy with arrows, shapes, highlights, numbered markers or text drawn into the pixels. | For a PDF, one chosen page is rendered and the result is an image, not a PDF. | Editor (desktop app) |
| **Add background**<br>`tool.background` | PDF, JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | PNG | The image placed on a styled canvas (gradient, colour or picture) with padding, rounded corners and shadow. This is decorative framing, not subject cut-out. | For a PDF, one chosen page is rendered and the result is an image, not a PDF. | Editor (desktop app) |
| **Crop**<br>`tool.crop` | PDF, JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | same as input | A copy containing only the selected rectangle, optionally constrained to an aspect ratio. | For a PDF, one chosen page is rendered and the result is an image, not a PDF. | Editor (desktop app) |
| **Redact (image)**<br>`tool.redact` | JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | same as input | A copy with chosen areas permanently covered with solid colour in the exported pixels. Blur and pixelate are offered as obscuring only, not secure removal. | Redacts pictures only. PDF redaction (removing underlying text) is not available.<br>Metadata is not changed by redaction; use Metadata to remove it. | Editor (desktop app) |
| **Recognise text (Arabic/English)**<br>`ocr.image-text` | PDF, JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | TXT | Text recognised from the picture with the local OCR engine, saved as UTF-8 text. Accuracy depends on scan quality. | Recognition is not perfect; check important text.<br>Tables and multi-column layouts may be read in the wrong order. | Needs ocr |
| **Make a searchable PDF**<br>`ocr.searchable-pdf` | PDF, JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | PDF | A PDF that looks like the scan but has an invisible recognised-text layer, so it can be searched and copied. | Pages are re-rendered as images; existing selectable text is replaced by the recognised layer. | Needs ocr |
| **Export edited image**<br>`studio.export` | PDF, JPG, PNG, WebP, AVIF, HEIC, TIFF, GIF, BMP, SVG | PNG | Renders the editing session (crop, adjustments, background, annotations, redactions) from the original-quality source. | — | Working |

