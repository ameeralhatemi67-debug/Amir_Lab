# Implementation status

States: **Working** (implemented and verified, with the evidence named), **Partial** (some of it works; the gaps are listed), **Blocked** (cannot proceed without something), **Future** (not started).
A capability listed in the seed or the capability map is not working until this table says so.

_Last updated: 20 Oct 2026 — M1 complete._

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

## Processing

| Capability | State | Notes |
|---|---|---|
| Capability map with stated meanings and limitations | **Working** | `Toolbox.Core/Actions/CapabilityMap.cs`. |
| Content sniffing by magic bytes | **Working** | `FileProbe`; unit tests in M2. |
| Collision-safe, verified, temp-then-rename outputs | Partial | Implemented in `OutputManager`; tests arrive with M2. |
| PDF → JPG/PNG, PDF → TXT/DOCX (text only) | Future | M2 |
| Image ↔ JPG/PNG/WebP/AVIF/TIFF/BMP | Future | M2 |
| Images → PDF / DOCX | Future | M2 |
| HEIC input | Future | Only if the Windows HEIF+HEVC codecs are present (detected at runtime). |
| Editors (Crop, Background, Adjust, Compress, Annotate, Redact, Metadata) | Future | M4/M5 |
| Recipes, presets, brand packs | Future | M6 |
| OCR (Arabic/English) | Future | M7 — waiting on the owner's real samples for the evaluation. |
