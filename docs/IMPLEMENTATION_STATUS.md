# Implementation status

States: **Working** (implemented and verified, with the evidence named), **Partial** (some of it works; the gaps are listed), **Blocked** (cannot proceed without something), **Future** (not started).
A capability listed in the seed or the capability map is not working until this table says so.

_Last updated: 8 Nov 2026 — Amir Lap phase 1 (the multi-tool shell) complete; File Toolbox is tool #1. M8 measurements: [PERFORMANCE.md](PERFORMANCE.md) (not yet re-measured for the shell)._

## Amir Lap shell (phase 1)

| Capability | State | Evidence / notes |
|---|---|---|
| Tool switch: off stops the tool (keyboard hook, wheel window, shortcut released) | **Working** | `ToolRegistryTests`; the published app starts and quits with File Toolbox on (`publish.ps1`, `test-install.ps1`). Not yet clicked through by the owner. |
| Configurable global shortcuts, conflict checks, pause while recording | **Working** | `KeyChordTests`, `ToolRegistryTests`, `VirtualKeyTests` (every allowed key). The recorder control itself has not been driven by a real key press yet. |
| Amir Lap window: Dashboard, File Toolbox page, General | **Working** | `HomeWindowTests` builds every page in light and dark off-screen; snapshots reviewed. Not yet used live by the owner. |
| Tray menu with a sub-menu per tool | **Partial** | Built and compiled; not yet exercised by a real click. |
| First-start copy of File Toolbox data | **Working** | `LegacyDataMigrationTests` (copy once, never overwrite, old folder untouched, logs not carried over). |
| Installer replaces File Toolbox; integrations carried over | **Working** | `test-install.ps1`: 26 checks, including old folder, shortcut and uninstall entry removed and the startup entry moved to `AmirLap`. |
| Longshot (tool #2) | **Future** | Phase 2. A Python prototype with pixel-exact stitching tests is in `tools/LongshotPrototype`. |

## Desktop interaction

| Capability | State | Evidence / notes |
|---|---|---|
| Real Explorer file drag → wheel on Shift tap | **Working** | DragHarness `pdf-to-jpg`, `image-to-webp-arabic-name`: the OLE payload equals the dragged paths. |
| Real Desktop drag → wheel | **Working** | DragHarness `desktop-source` (`--desktop`). |
| Multi-file drag | **Working** | DragHarness `two-images-to-pdf` (2 files). |
| Copy-only drop (source never moved) | **Working** | Every scenario: the effect answered is `Copy`; source SHA-256 is unchanged; no files are moved. |
| Ring switch on second Shift tap | **Working** | DragHarness `image-tools-ring`. |
| Text recognition from the Tools ring (8th segment) | **Working** | DragHarness `ocr-scan-to-text`: a real drag of an Arabic-named scan onto Text (OCR); the `.txt` holds both lines in reading order. |
| Escape cancels | **Working** | DragHarness `escape-cancels`. |
| Leaving the wheel closes it, so the drag continues elsewhere | **Working** | DragHarness `leave-wheel-closes`. |
| No wheel without a file drag | **Working** | DragHarness `no-wheel-without-file-drag` (rubber-band selection + Shift). |
| Wheel stays inside the monitor's work area | Partial | The clamping code is in place; the screen-edge case is not exercised by the harness yet. |
| Display scaling | Partial | Tested at 125% (the dev machine). Other scales are untested. |
| Multiple monitors | Partial | Placement maths tested on simulated layouts: negative coordinates, mixed scaling, offset monitors, side taskbars, gaps (`MultiMonitorTests`). **Real multi-monitor use is untested** (only one display is available). |
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
| Crash-orphaned temp cleanup | **Working** | `Journal_cleanup_removes_orphaned_temp_files_only`. On exit, running jobs are cancelled (finished outputs kept) and leftover temp/staging files are removed (`CancelAll_stops_running_jobs_…`). |
| Exit and `--quit` | **Working** | `FileToolbox.exe --quit` closes the running copy like the tray's Quit (used by the installer and uninstaller; Bench measured 0.6 s to exit). |
| Nearly full drive | **Working** | Under 32 MB free, results go to the fallback folder with the reason in the note; if that drive is also full, the job fails early naming the drive (`Nearly_full_drive_falls_back_…`, `Full_drives_fail_early_…`). |
| Batch: per-file states, skipped-with-reason, cancel keeps finished outputs | **Working** | `Mixed_batch_reports_skipped_files`, `Cancelled_batch_keeps_finished_outputs_and_cleans_up`. |
| PDF → JPG / PNG (selected pages, 300 DPI default, page-numbered names) | **Working** | `PdfTests` (order, numbering, all pages by default, invalid selection message). Pages above 60 MP are rendered at a lower, reported DPI. |
| PDF → TXT (existing text layer) | **Working** | `Digital_pdf_text_is_extracted_with_page_separators`. Scans fail with an explanation (`Scanned_pdf_without_text_layer_is_explained_not_faked`). |
| PDF → DOCX (text only, RTL paragraphs) | **Working** | `Pdf_text_to_docx_contains_text_and_is_labelled_text_only`. Not layout-preserving, as stated. |
| Image → JPG / PNG / WebP / AVIF / TIFF / BMP | **Working** | `Converts_to_every_supported_format_with_verified_output`, transparency flattening, EXIF orientation, an 8000×6000 image, Arabic and `[bracketed]` names. |
| Images → one PDF (natural order, auto orientation, JPEG pass-through) | **Working** | `Images_become_one_pdf_in_natural_name_order`. |
| Images → DOCX (pictures, no OCR) | **Working** | `Images_to_docx_embeds_pictures`. |
| Password-protected PDFs | Blocked | Rejected with a clear message; supporting them needs a password prompt (future). |
| HEIC input | **Working** (desktop app) | The WIC decoder is registered by the Windows adapter; the CLI has no HEIC. |
| No networking in product code | **Working** | `ArchitectureTests` scan the product assemblies' references, and their WinRT type references for `Windows.Web*` / `Windows.Networking*`. |
| Metadata: inspect (location, camera, dates, author, thumbnail, XMP, IPTC, comments, colour profile) | **Working** | `Inspector_groups_sensitive_metadata`; window with plain-language groups and sensitive items flagged. |
| Metadata removal — JPEG, lossless (compressed picture data byte-identical) | **Working** | `Removing_location_erases_gps_bytes_…` (GPS bytes erased, not just unlinked; camera kept), `Removing_camera_details_erases_serial_numbers`, `Removing_everything_keeps_orientation_…`. Embedded thumbnails and trailing extra images are dropped together with location. |
| Metadata removal — PNG/TIFF (lossless), WebP/AVIF (re-encoded at quality 95, stated) | **Working** | `Png_location_can_be_removed_losslessly`. |
| PDF document-information removal | **Working** | `Pdf_document_information_is_removed`. Attachments, form data and annotations are not touched (stated). |
| Recipes: saved multi-step actions (create, inspect, edit, duplicate, delete, pin to the wheel, import/export JSON) | **Working** | `RecipeTests` (store, import/export, pins); Recipes window screenshot QA. |
| Recipe execution: consecutive image steps fused into one render; staging folder removed; only final results saved | **Working** | `Consecutive_image_steps_fuse_into_one_render`, `Chain_with_combine_makes_one_pdf_and_no_intermediate_files`. |
| "Will produce" summary before running | **Working** | `Will_produce_explains_the_plan_before_running`; shown in the palette and the recipe editor. |
| Result-oriented presets: website images, email (under 2 MB), report graphic, document pages, presentation screenshot, remove location/camera | **Working** | `Website_images_recipe_…`, `Share_pages_recipe_…`; DragHarness `recipe-ring-website-images` (third ring, real drag). |
| Requirements check (size, format, dimensions) reported as met or not met | **Working** | `Email_recipe_reports_when_the_size_cannot_be_met`: the best effort is saved and flagged "✗ Requirement not met". |
| Interactive recipes (editor opens with the recipe's framing and export applied) | **Working** (manual) | Report graphic, presentation screenshot. |
| Brand styles (background, spacing, logo/watermark position, size and opacity, caption with Arabic shaping, font) | **Working** | `Branding_draws_logo_and_caption`; brand editor with live preview (screenshot QA). Used from Studio → Background → Brand style and from the recipe step "Frame". |
| Export packs: 1:1 / 4:5 / 9:16 / 16:9 versions from one session, each previewed, framed so nothing is cut off | **Working** | `Export_pack_frames_every_ratio_without_cutting`; Studio Export → "Also save versions" with mini-previews. |
| "Portal attachment: PDF under 2 MB" | Blocked | PDF compression is not implemented, so a PDF size target cannot be met yet. Image size targets work. |
| OCR engine: Windows built-in (Arabic, English), languages detected at startup, missing ones explained | **Working** | `WindowsOcrTests` (real recogniser, English and Arabic); `Registering_reports_language_availability`; `ftb features`. |
| Image → text in reading order (Arabic right-to-left, Latin/number runs kept, split lines joined) | **Working** | `OcrTests` (bidi, fragments, mixed merge); DragHarness `ocr-scan-to-text`. |
| Scanned PDF → text (selected pages; pages with a text layer reuse it) | **Working** | `Text_from_a_pdf_recognises_scans_and_reuses_existing_text`. |
| Searchable PDF from images or scanned PDFs (original pages kept, text pages copied, rotated pages aligned, read back before saving) | **Working** | `Searchable_pdf_from_an_image_…`, `Searchable_pdf_keeps_original_pages_…` (rendering unchanged, page 2 not re-recognised), `Text_layer_lines_up_on_a_rotated_page`; CLI round trip (searchable PDF → PDF → TXT). |
| OCR accuracy claims | Partial | Measured on a 75-page generated corpus only ([OCR_EVALUATION.md](OCR_EVALUATION.md)): about 0–1% CER for Arabic and English, 0–5% for mixed lines. **Real scans not yet measured** (waiting on the owner's samples). |
| Copying text from a searchable PDF in Chromium-based viewers | Partial | PDFium's own bidi reorders letter-and-digit codes inside Arabic lines (`AB1234` → `1234AB`) and swaps the Arabic words of Latin-heavy lines. A reader limitation; the TXT output is exact. |
| Handwriting, tables, multi-column order, other languages | Future | See ROADMAP "OCR follow-ups". |

## Packaging and resources

| Capability | State | Evidence / notes |
|---|---|---|
| Self-contained win-x64 build (no .NET install needed), ReadyToRun, licences and notices included | **Working** | `tools/publish.ps1`: 235 MB folder, 93 MB zip. The script itself runs `ftb features` and starts and quits the published app. The published CLI made DOCX and searchable PDF files. |
| Per-user install (no admin): Start Menu, Installed apps entry, update in place, existing integrations re-pointed | **Working** | `tools/test-install.ps1` (sandboxed; 21 checks pass). The real `%LOCALAPPDATA%\Programs` install has not been run on this PC; that is the owner's step. |
| Uninstall: integrations, shortcut, entry and folder removed; user data kept unless `-RemoveUserData`; outputs never touched; only File Toolbox's own entries removed | **Working** | `tools/test-install.ps1`. |
| Program icon | **Working** | `app.ico` generated by `tools/IconGen` (classic bitmaps up to 128 px, PNG at 256 px). |
| Idle cost | **Working** | 0.08% of one core in steady state; 112 MB private, flat ([PERFORMANCE.md](PERFORMANCE.md)). libvips and the Windows SDK projection load at start-up for the feature checks; PDFium and Skia load on first use. |
| Large inputs (48 MP images, 60-page PDFs, 24 MP Studio sessions) | **Working** | Bench; peak 770 MB for 48 MP → WebP (61.9 s, worst-case noisy image), about 200 MB for a 60-page PDF export. |
| Code signing | Future | The build is unsigned; SmartScreen may warn on first run. |
| Compatibility beyond the dev PC (Windows 10, other DPI, multiple monitors, ARM64) | Partial | Built for 10.0.17763+ x64; only tested on Windows 11 build 26300 at 125% on one monitor. |
