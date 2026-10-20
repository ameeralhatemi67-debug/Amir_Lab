# Implementation status

States: **Working** (implemented and verified, with the evidence named), **Partial** (some of it works; the gaps are listed), **Blocked** (cannot proceed without something), **Future** (not started).
A capability listed in the seed or the capability map is not working until this table says so.

_Last updated: 20 Oct 2026 — M2 complete._

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
| Drop on a segment runs the action | Future | M3: drops are currently only recorded. |
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
| HEIC input | Future | M3: Windows WIC decoder, enabled only if the HEIF+HEVC codecs are present. |
| No networking in product code | **Working** | `ArchitectureTests` scan the product assemblies' references. |
| Editors (Crop, Background, Adjust, Compress, Annotate, Redact, Metadata) | Future | M4/M5 |
| Recipes, presets, brand packs | Future | M6 |
| OCR (Arabic/English) | Future | M7 — waiting on the owner's real samples for the evaluation. |
