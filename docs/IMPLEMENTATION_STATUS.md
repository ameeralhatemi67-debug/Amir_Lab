# Implementation status

States: **Working** (implemented and verified, with the evidence named), **Partial** (some of it works; the gaps are listed), **Blocked** (cannot proceed without something), **Future** (not started).
A capability listed in the seed or the capability map is not working until this table says so.

_Last updated: 20 Oct 2026 — M5 complete._

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
| Studio: Crop / Adjust / Background / Annotate / Redact / Export in one non-destructive session | **Working** | DragHarness `studio-crop-background-save`: a real drag onto Crop, 1:1, background and a real-mouse arrow saved `… Cropped Annotated With Background.png` at the exact expected 1040×1040. `StudioTests` (16) cover geometry, framing, the preview matching the export, redaction, annotations and naming. |
| Crop editor (8 handles, shaded outside, Free/Original/1:1/16:9/9:16/4:3/3:4, W/H px fields, rotate/flip, arrow-key nudge) | **Working** | Screenshot QA plus the harness. |
| Background (10 original gradients, 10 solids + custom colour, picture background with blur, padding/corners/shadow, Auto/1:1/16:9/4:5/9:16) | **Working** | `Background_*` tests and screenshot QA. |
| Compress (format, quality, max edge, target size with met/not-met report) | **Working** | `Compress_with_target_reports_the_outcome`, `Target_size_*`. Opened from the Tools ring as a compact Export-only editor. |
| Annotate (arrow, box, ellipse, line, pen, highlight, text with Arabic shaping, numbered steps) | **Working** | `Annotations_draw_into_the_pixels` (includes Arabic text). Marks can be removed (last or all) but not moved after placing. |
| Redact (image) — solid boxes flattened into the pixels; pixelate/blur labelled "obscure, not secure" | **Working** | `Solid_redaction_replaces_the_pixels`. PDF redaction remains out of scope; a PDF page is redacted as an image. |
| Undo/redo, hold-to-compare, unsaved-changes guard, PDF page switcher | **Working** (manual) | |
| Edit a PDF page (render → crop → background) without intermediate files | **Working** | `Pdf_page_can_be_edited_and_saved_as_an_image`. |
| Tray presence (open, repeat last action, history, settings, quit) | **Working** (manual) | Left click opens the File Toolbox window. |
| Keyboard palette + drop zone (drop, Browse, Ctrl+V files or image, type to filter, Enter to run) | **Working** | DragHarness `palette-from-second-launch` (search "webp", Enter, output verified). |
| Single instance; Explorer menu / Send to / second launch hand files to the running app | **Working** | `InstanceChannelTests`, harness `palette-from-second-launch`. Explorer's one-process-per-file launches are batched within 450 ms. |
| Explorer context menu (opt-in, HKCU, supported types only) | **Working** (registration) | `ShellIntegrationTests` against a throwaway registry root. Not enabled on this PC by the build; switch it on in Settings. Windows 11 shows it under "Show more options". |
| "Send to" entry and start with Windows (opt-in) | Partial | Implemented; switching them on and off has not been tested automatically. |
| Global shortcut Ctrl+Alt+F for the palette | **Working** (manual) | Reported in Settings if another program owns it. |
| Settings (activation key, wheel size, theme, motion, destination, naming pattern with preview, quality defaults, PDF DPI, history retention, integrations, privacy) | **Working** (manual) | Saved immediately to `settings.json`. A wheel size change needs a restart. |
| History (recent jobs, show results, run again, clear) | **Working** (manual) | JSON lines, retention by days and count (`HistoryStore`). |

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
| Metadata: inspect (location, camera, dates, author, thumbnail, XMP, IPTC, comments, colour profile) | **Working** | `Inspector_groups_sensitive_metadata`; window with plain-language groups and sensitive items flagged. |
| Metadata removal — JPEG, lossless (compressed picture data byte-identical) | **Working** | `Removing_location_erases_gps_bytes_…` (GPS bytes erased, not just unlinked; camera kept), `Removing_camera_details_erases_serial_numbers`, `Removing_everything_keeps_orientation_…`. Embedded thumbnails and trailing extra images are dropped together with location. |
| Metadata removal — PNG/TIFF (lossless), WebP/AVIF (re-encoded at quality 95, stated) | **Working** | `Png_location_can_be_removed_losslessly`. |
| PDF document-information removal | **Working** | `Pdf_document_information_is_removed`. Attachments, form data and annotations are not touched (stated). |
| Editors (Crop, Background, Adjust, Compress, Annotate, Redact, Metadata) | Future | M4/M5 |
| Recipes, presets, brand packs | Future | M6 |
| OCR (Arabic/English) | Future | M7 — waiting on the owner's real samples for the evaluation. |
