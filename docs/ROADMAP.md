# Roadmap

Milestones for the Windows build (from the approved plan). Every item from the seed's planned improvements (§8) and future candidates (§12) is kept here, even when it is not scheduled yet. Current state is tracked in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md).

## Windows milestones

| # | Milestone | Scope |
|---|---|---|
| M0 | Scaffold | Repo, solution, docs, architecture guard against network code. |
| M1 | Native drag-and-drop spike (gate) | Keyboard hook → invisible probe → real OLE `DragEnter` with `CF_HDROP` → wheel. Copy-only drops. Real-Explorer harness. |
| M2 | Processing core + CLI | Capability map; PDF render/text/DOCX; image conversions; image→PDF/DOCX; verified, collision-safe outputs; job runner; tests. |
| M3 | Wheel, toasts, batches | Polished rings, page picker, progress toast with Open / Show in Explorer / Remove output / Repeat, job panel, retry. |
| M4 | Studio editors | Crop, Adjust/Resize, Background, Compress (target size), Export panel; non-destructive multi-step session from the original-quality source. |
| M5 | Remaining tools & access | Metadata, Annotate, raster Redact, drop zone, command palette, clipboard, tray, settings, history, Explorer context menu (opt-in), single-instance forwarding. |
| M6 | Recipes & presets | Saved multi-step actions with a preview of what they will produce, result-oriented presets (email/web/portal), brand presets, export packs. |
| M7 | Arabic/English OCR | Windows' built-in recogniser (instead of the planned Tesseract sidecar); image/scanned PDF → text, → searchable PDF; generated evaluation corpus with CER/WER; claims limited to measured cases. **Done.** Follow-ups below. |
| M8 | Hardening & packaging | Performance measurements, temp cleanup, permission/disk-space handling, multi-monitor maths tests, self-contained publish, per-user installer. **Done.** Follow-ups below. |

## Amir Lap (owner decision, 8 Nov 2026)

File Toolbox becomes tool #1 of **Amir Lap**, a multi-tool tray app ([DECISIONS.md](DECISIONS.md#amir-lap-shell-phase-1--8-nov-2026)).

| Phase | Scope | State |
|---|---|---|
| 1. Shell | Rename and data migration; tool modules with on/off switches; configurable global shortcuts with conflict checks; sidebar window (Dashboard, tool pages, General); tray menu per tool; installer that replaces File Toolbox. | Done |
| 2. Longshot | Rewrite the Python prototype (`tools/LongshotPrototype`) in C#: stitching engine with its pixel-exact tests, box selection, Extend / Auto / Follow / Undo panel kept out of captures, result window, Longshot page and shortcuts (default Ctrl+Alt+S, and Ctrl+Alt+Shift+S after a delay). | Done (live use by the owner pending) |
| 3. Finish | Live check of both tools, installer and uninstaller for both tools, idle-cost measurement, 100/125/150% scaling and mixed-DPI monitors, docs. | Next |

Ideas kept for later, owner to choose:

- **Longshot:** pick a window or scroll area by hovering; open the result in Studio (crop, redact, annotate); copy text from the capture with the existing OCR, or save as a searchable PDF; split into A4 PDF pages or image parts for messengers; delay timer and "capture the same area again"; sideways scrolling; ordinary region/window/full-screen captures and "pin to screen"; capture history.
- **File Toolbox:** PDF compression (unblocks "PDF under 2 MB"); batch rename with a live preview using the naming tokens. PDF merge/split/reorder and watched folders are already listed under future candidates below.
- **Amir Lap:** a quick-launcher flyout on tray click; a shortcut cheat sheet while holding Ctrl+Alt; export and import of settings; a brand icon of its own. New tools should build on what is unique here (Arabic/English OCR, the file wheel, scroll capture) rather than repeat PowerToys.

## Planned improvements preserved from the seed (§8)

- Saved multi-step actions (recipes): define, inspect, name, save, edit, duplicate, reuse. Deterministic, no LLM. → M6
- Result-oriented presets: prepare for email or a website, presentation graphic, make text searchable, export selected pages, fit an upload limit, with verified met/unmet conditions. → M4/M6/M7
- Arabic and English handling: OCR, mixed text, searchable PDFs, RTL behaviour, Arabic-safe filenames and sorting. → M2 (filenames, sorting), M7 (OCR)
- Brand presets and export packs with per-variant preview and cut-off warnings. → M6
- Clear quality and output information before export (dimensions, estimated vs actual size, transparency, metadata, destination), zoom and before/after view. → M4
- Useful batch processing: per-file states, partial success, retry, cancellation that keeps finished outputs, bounded concurrency. → M2/M3
- Local history and repeat actions, with clear-history and retention controls; precisely scoped "remove output". → M3/M5
- Clipboard and naming workflows: paste an image, repeat the last action, naming tokens, destination per recipe, drag an output onward. → M5/M6
- Metadata and sharing preparation, explaining what changed. → M5

## Future candidates (seed §12) — not scheduled

| Candidate | Boundary kept |
|---|---|
| PDF merge, split, reorder | Validate page order and output integrity (PDFsharp 6.2 or PDFium import APIs). |
| More image formats (HEIC output, JPEG XL, …) | Add codec paths deliberately and record their licences. |
| Shared recipe files | Local import/export before any sync. |
| Watched folders | Opt-in; never reprocess the app's own outputs. |
| Video/audio processing | Separate codec, performance and FFmpeg licensing work. |
| Collages, QR reading | When real use justifies them. |
| Archives (ZIP/TAR/GZIP, RAR input) | Own file-handling scope and validation. |
| Broader Office/EPUB/subtitle support | Every transformation a defined capability. |
| Subject background removal | Separate from decorative backgrounds. |
| Sensitive-content suggestions | Review required; no promise of perfect detection. |
| Natural-language action selection | Optional; execution stays deterministic and local. |
| Filename suggestions | User keeps control. |
| Extension system | After the action model is stable. |
| PDF redaction (true content removal) | Needs an engine and extraction-based verification. |
| Editable document reconstruction (PDF → layout-faithful DOCX) | Document-specific validation; never promise perfection. |
| Windows 11 modern context-menu entry | Needs package identity (MSIX / sparse package). |
| macOS application | Reuse Core and Processing; native drag destination and permissions. |
| Commercial release | No payments, accounts, quotas or licence activation until the owner decides. |

## OCR follow-ups (after M7)

- Measure the owner's real Arabic/English scans (`tools/OcrEval --samples`) and revise the claims in OCR_EVALUATION.md.
- OCR → Word (editable paragraphs, RTL), and recognised text straight to the clipboard.
- Embed a glyphless font program in searchable PDFs for PDF/A-style consumers.
- Deskew and orientation detection for photos rotated by more than a few degrees.
- Table and multi-column reading order.
- A second engine behind `IOcrEngine` (Tesseract or PaddleOCR sidecar) if real samples need more languages, layout analysis or better accuracy; Apple Vision for the macOS adapter.

## After M8

- Code signing (the build is unsigned, so SmartScreen may warn), and MSIX or a signed installer when there is a distribution channel.
- Load libvips and the Windows SDK projection lazily, using cached feature checks, to cut idle memory.
- Faster WebP for very large images (lower effort above about 24 MP), or a warning before starting.
- Test on Windows 10, other display scalings, real multiple monitors, and ARM64 (no ARM64 build yet).
- Crash isolation: run heavy jobs in `ftb` as an out-of-process worker, so a native crash cannot take the tray app down.
