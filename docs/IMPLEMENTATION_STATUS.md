# Implementation status

States: **Working** (implemented and verified, with the evidence named), **Partial** (some of it works; the gaps are listed), **Blocked** (cannot proceed without something), **Future** (not started).
A capability listed in the seed or the capability map is not working until this table says so.

_Last updated: 20 Oct 2026 — M3 complete._

## Desktop interaction

| Capability | State | Evidence / notes |
|---|---|---|
| Real Explorer file drag → wheel on Shift tap | **Working** | DragHarness `pdf-to-jpg`, `image-to-webp-arabic-name`: the OLE payload equals the dragged paths. |
| Real Desktop drag → wheel | **Working** | DragHarness `desktop-source` (`--desktop`). |
| Multi-file drag | **Working** | DragHarness `two-images-to-pdf` (2 files). |
| Copy-only drop (source never moved) | **Working** | Every scenario: the effect answered is `Copy`; source SHA-256 is unchanged; no files are moved. |
| Ring switch on second Shift tap | **Working** | DragHarness `image-tools-ring`. |
| Escape cancels | **Working** | DragHarness `escape-cancels`. |
| Leaving the wheel closes it, so the drag continues elsewhere | **Working** | DragHarness `leave-wheel-closes`. |
| No wheel without a file drag | **Working** | DragHarness `no-wheel-without-file-drag` (rubber-band selection + Shift). |
| Wheel stays inside the monitor's work area | Partial | The clamping code is in place; the screen-edge case is not exercised by the harness yet. |
| Display scaling | Partial | Tested at 125% (the dev machine). Other scales are untested. |
| Multiple monitors | Partial | Per-monitor DPI handling is coded; **untested** (only one display is available). |
| Reveal latency ≤ 200 ms | **Working** | Measured 27–129 ms (see DECISIONS.md). |
| Drop on a segment runs the real conversion | **Working** | DragHarness `--expect-outputs`: `Report 001.jpg`, an Arabic-named `.webp` and a 2-page PDF were produced from real Explorer drags and verified. |
| Multi-page PDF asks which pages (never silently page 1) | **Working** | DragHarness `multipage-pdf-asks-for-pages`: the picker appears with PDFium thumbnails; confirming exported all 3 pages. |
| Progress toast with cancel, then Open / Show (multi-select) / Remove (Recycle Bin, only if unchanged) / Details | **Working** | Screenshots in harness runs; Remove compares size and timestamp before recycling. |
| Drag a finished output onward from the toast | **Working** (manual) | The toast's file chip starts a real file drag (Copy). Tapping Shift during it opens the wheel again, so you can chain the next step. |
| Job details window (per-file state, outputs, notes, retry failed) | **Working** (manual) | `JobDetailsWindow`. |
| HEIC input | **Working** on computers with Microsoft's HEIF + HEVC extensions | DragHarness `heic-to-jpg-via-windows-codec`, using a real HEIC encoded by Windows. Elsewhere the input is disabled with an explanation. |
| Editors (Crop, Add BG, …) from the Tools ring | Future | M4: currently shows "not available in this build yet". |
| Tray presence | Partial | Icon with Quit only; the full menu arrives in M5. |

## Processing (engines + `ftb` CLI)

| Capability | State | Evidence / notes |
|---|---|---|
| Capability map with stated meanings and limitations | **Working** | `CapabilityMap.cs`; generated [CAPABILITIES.md](CAPABILITIES.md). |
| Content sniffing by magic bytes | **Working** | `FileProbeTests` (JPEG named .png, AVIF vs HEIC brands, RIFF/WAVE vs WebP, PDF after junk). |
| Collision-safe, verified, temp-then-rename outputs | **Working** | `OutputManagerTests`, `Repeated_conversion_never_overwrites`, `Corrupt_input_fails_clearly_and_leaves_nothing_behind`. Every output is re-decoded by an independent verifier before it gets its final name. |
| Read-only destination → fallback folder, reported | **Working** | `Read_only_destination_falls_back_and_says_so` (real ACL deny). |
| Crash-orphaned temp cleanup | **Working** | `Journal_cleanup_removes_orphaned_temp_files_only`. |
| Batch: per-file states, skipped-with-reason, cancel keeps finished outputs | **Working** | `Mixed_batch_reports_skipped_files`, `Cancelled_batch_keeps_finished_outputs_and_cleans_up`. |
| PDF → JPG / PNG (selected pages, 300 DPI default, page-numbered names) | **Working** | `PdfTests` (order, numbering, all pages by default, invalid selection message). Pages above 60 MP are rendered at a lower, reported DPI. |
| PDF → TXT (existing text layer) | **Working** | `Digital_pdf_text_is_extracted_with_page_separators`. Scans fail with an explanation (`Scanned_pdf_without_text_layer_is_explained_not_faked`). |
| PDF → DOCX (text only, RTL paragraphs) | **Working** | `Pdf_text_to_docx_contains_text_and_is_labelled_text_only`. Not layout-preserving, as stated. |
| Image → JPG / PNG / WebP / AVIF / TIFF / BMP | **Working** | `Converts_to_every_supported_format_with_verified_output`, transparency flattening, EXIF orientation, an 8000×6000 image, Arabic and `[bracketed]` names. |
| Images → one PDF (natural order, auto orientation, JPEG pass-through) | **Working** | `Images_become_one_pdf_in_natural_name_order`. |
| Images → DOCX (pictures, no OCR) | **Working** | `Images_to_docx_embeds_pictures`. |
| Password-protected PDFs | Blocked | Rejected with a clear message; supporting them needs a password prompt (future). |
| HEIC input | **Working** (desktop app) | The WIC decoder is registered by the Windows adapter; the CLI has no HEIC. |
| No networking in product code | **Working** | `ArchitectureTests` scan the product assemblies' references. |
| Editors (Crop, Background, Adjust, Compress, Annotate, Redact, Metadata) | Future | M4/M5 |
| Recipes, presets, brand packs | Future | M6 |
| OCR (Arabic/English) | Future | M7 — waiting on the owner's real samples for the evaluation. |
