# OCR evaluation (Arabic / English)

_Measured 20 Oct 2026 on the development PC (Windows 11 build 26300, Windows OCR languages ar-SA, en-GB, en-US)._

The app recognises text with the OCR engine built into Windows (`Windows.Media.Ocr`). This page records what was measured, how the pipeline settings were chosen, and which cases the app may claim to handle. **Every number below comes from generated pages. Real scans have not been measured yet** (see "Your samples").

## How it was measured

`tools/OcrEval` renders pages with WPF text, which does full Arabic shaping. It degrades some of them with libvips and runs them through the **same `OcrService` pipeline the app uses**. It then compares the result with the exact source text:

- **CER** (character error rate) is the edit distance divided by the length of the true text, after Unicode NFC and whitespace collapsing. **Lenient CER** also ignores Arabic diacritics and tatweel and treats the hamza/madda alef forms as plain alef.
- **WER** (word error rate) is the same at word level.
- **The corpus has 75 pages, all written for this evaluation:**
  - English in 6 fonts.
  - Arabic in 7 fonts, including Sakkal Majalla, Traditional Arabic and Simplified Arabic.
  - Mixed Arabic and English lines in 4 fonts. These include an order code, a product code, an e-mail address, "PDF", "Windows" and a place name.
  - Western and Arabic-Indic numbers.
  - Low resolution (150 DPI at 10 pt).
  - Screenshot size (96 DPI at 9 pt).
  - Seven degradations: blur, noise, JPEG at quality 20, a 3° rotation, low contrast, uneven lighting, and a phone-like combination of tilt, shading, noise and JPEG.
- **Codes inside Arabic lines are laid out the way Word and browsers place them** (the Unicode bidi algorithm). WPF's own bidi orders hyphenated codes differently, which would have made the true text ambiguous.

Run it yourself:

```
dotnet run --project tools/OcrEval -- --out artifacts/ocr-eval
dotnet run --project tools/OcrEval -- --samples "D:\my scans"   # adds your own pages
```

Images, recognised texts and `results.md`/`results.json` are written to the output folder.

## What the evaluation changed

1. **Reading order.** Windows OCR lists Arabic words left to right, in the order they appear on the page, so every Arabic line came back reversed: 71% CER on clean text. `OcrLayout` now restores reading order at word level. Runs of Latin words and numbers keep their own left-to-right order, and the page's main script decides lines that mix both. Clean Arabic went from 71% to 0.1% CER.
2. **Line fragments.** The recogniser splits a line at wide gaps (seen with Courier New) and lists the pieces as separate lines, which scrambles reading order. Pieces on the same baseline with a gap under 1.5 line heights are now joined. Wider gaps are treated as columns.
3. **Combining the two languages** (`ara+eng`, the default):
   - The Arabic model alone misreads Latin text: `INV-2026-0042` became `2026-0O42-lNV`, and at the end of English sentences the last word's first letter was replaced by a full stop (`regions.` → `.egions.`).
   - The English model cannot read Arabic.
   - **Chosen: per word.** Latin-only lines come from the English model. In Arabic lines, runs of Latin words and numbers are replaced by the English reading of the same area, but only when the English words cover the run completely without splitting it. Next to Arabic, the English model splits `AB1234` into `ABI 234`.
4. **Upscaling.** Pictures whose long edge is under 1600 px are enlarged before recognition, by up to ×4. Without upscaling, screenshot-size Arabic fails (57% CER). Enlarging to 2400 or 3200 px gave no further gain and took 1.5–2× longer.

## Results (app default: `ara+eng`, per word, upscale to 1600 px)

| Group | Pages | CER (lenient) |
|---|---|---|
| Arabic, clean 300 DPI, 7 fonts | 7 | 0.1% (0.1%) |
| Arabic, 150 DPI at 10 pt | 2 | 0.2% |
| Arabic, each degradation (blur, noise, JPEG q20, rotated 3°, low contrast, lighting, phone-like) | 14 | 0.0–0.7% (0.0%) |
| Arabic-Indic numbers | 2 | 2.0% (1.5%) |
| Arabic, screenshot size (96 DPI at 9 pt) | 2 | 7.2% |
| English, clean 300 DPI, 6 fonts | 6 | 0.0% |
| English, all degradations and 150 DPI | 16 | 0.0–0.3% |
| Western numbers (dates, amounts, phone numbers) | 2 | 0.0% |
| English, screenshot size | 2 | 1.1% |
| Mixed Arabic and English, clean 300 DPI | 4 | 0.2% (0.1%) |
| Mixed, 150 DPI | 2 | 2.0% |
| Mixed, degradations | 14 | 2.7–5.2% |
| Mixed, screenshot size | 2 | 7.2% |

| Strategy (all 75 pages) | Mean WER | Pages with CER < 2% | Time per page |
|---|---|---|---|
| English only | 61.3% | 25/75 | 155 ms |
| Arabic only | 9.2% | 24/75 | 333 ms |
| Both, per line | 5.1% | 49/75 | 451 ms |
| **Both, per word (default)** | **4.5%** | **58/75** | **436 ms** |
| Per word, upscaling off | 8.3% | 56/75 | 334 ms |
| Per word, upscale to 2400 px | 4.9% | 58/75 | 641 ms |

Times are for pages of about 1000–1700 px on a Ryzen 5 6600H in a Debug build. Each page runs both recognisers.

## What the app claims

- **Supported:**
  - Printed Arabic and English in common document fonts, from clean scans or phone photos of reasonable quality.
  - Measured error on generated pages: Arabic and English about 0–1% of characters; mixed lines about 0–5%.
- **Check these:**
  - Codes that mix letters and digits, and hyphenated references, inside Arabic text (most of the mixed-line errors).
  - Arabic-Indic digits (about 2% error).
  - Very small text, such as a 9 pt screenshot at 96 DPI (about 7% error).
- **Not supported or not measured:**
  - Handwriting.
  - Diacritised (fully vowelled) Arabic. Harakat are not reliably returned, which is why the lenient score exists.
  - Tables and multi-column reading order.
  - Rotated pages beyond a few degrees.
  - Languages other than Arabic and English.
  - Calligraphic styles.
  - Real scanner and camera noise.

## Searchable PDFs

The text layer is invisible text placed over each recognised word with a "glyphless" font, as in Tesseract's PDF output. A test reads it back with PDFium after saving. Arabic is written in visual order, as Word and browsers write it, and PDF readers rebuild reading order themselves.

Measured with PDFium, which Chrome and Edge's Chromium viewer use:

- Plain Arabic lines and English lines are read back correctly.
- Inside Arabic lines, PDFium reorders letter-and-digit codes when copying: `AB1234` becomes `1234AB`.
- A line that is mostly Latin, such as an e-mail address after two Arabic words, comes back with its Arabic words swapped.

These are limits of PDFium's bidi handling and affect PDFs from any producer. **Text (OCR) to TXT gives the exact recognised text.**

## Your samples

Please put real scans or photos in a folder. Use the kinds you actually handle: invoices, letters, receipts, forms and screenshots. Blank out personal details first if you prefer; nothing leaves this PC either way. Next to each file, put a `.txt` file with the same name containing its exact text in UTF-8, one line per printed line. Then run:

```
dotnet run --project tools/OcrEval -- --samples "<folder>"
```

The report adds a "Your samples" group. The claims above will be revised from those results.
