# Engineering decisions

Product direction lives in [`sead_idea.md`](../sead_idea.md) (the seed). This file records the engineering choices made while building it, why they were made, and what would change them. Newest entries are at the bottom of each section.

## Fixed by the owner (not up for engineering change)

- Windows first, macOS later. Local processing only: no accounts, no cloud, no telemetry, no payments or licensing. Original files are never modified.
- **Pacing (20 Oct 2026):** build M0–M8 straight through, committing per milestone. The automated drag harness may drive the real mouse and keyboard without warning first.
- Real Arabic/English scans for the OCR evaluation will come from the owner later.

## Stack

| Decision | Choice | Reason | Revisit if |
|---|---|---|---|
| Language/runtime | C# on .NET 9 | The SDK is installed on the dev machine; mature native interop. | — |
| Desktop UI | WPF (WinForms only for `NotifyIcon`) | Mature OLE drop targets, layered no-activate topmost windows, PerMonitorV2 DPI. A WebView overlay would put hit testing on transparent regions at risk. | WinUI 3 gains equivalent layered-window support |
| Layering | `Toolbox.Core` (pure .NET) → `Toolbox.Processing` (engines) → `Toolbox.Windows` (adapter) | The macOS adapter can reuse Core and Processing unchanged. | — |
| Raster engine | NetVips 3.2 + `NetVips.Native.win-x64` 8.18.7 | Fast, low memory, streaming. The bundle includes mozjpeg, libpng, libwebp, libtiff, cgif, libheif + aom (AVIF encode/decode), imagequant, lcms, librsvg. It has **no HEVC**, so it cannot handle HEIC. | — |
| Composition | SkiaSharp 4.x (+HarfBuzz) | One renderer for both preview and export, so the preview matches the export. Shapes Arabic text for captions. | — |
| PDF read/render/text | PDFium (`bblanchon.PDFium.Win32`, Apache-2.0) via in-house P/Invoke | Rendering and text extraction in one permissive engine. Documents load from memory, so Unicode paths are safe. Calls are serialized (PDFium is not thread-safe). | — |
| PDF write | **PDFsharp 6.2.4, pinned** (MIT) | **PDFsharp 6.5.0 relicensed to AGPL-3.0 and adds a runtime licence check** (found 20 Oct 2026 when a fixture build threw `PdfSharpLicenseException`). AGPL would bind the whole app. Stay on 6.2.x. | We need features only in ≥6.5. The alternative is PDFium's own edit/save APIs (`FPDF_CreateNewDocument`, `FPDF_ImportPages`, `FPDF_SaveAsCopy`). |
| DOCX | DocumentFormat.OpenXml 3.5 (MIT) | Official SDK. | — |
| Metadata read | MetadataExtractor 2.9 (Apache-2.0) | Broad EXIF/XMP/IPTC/ICC coverage. | — |
| BMP output | Small managed writer | libvips has no BMP saver. | — |
| HEIC input | Only through the Windows WIC HEIF+HEVC codec, detected at runtime | Matches the seed's rule to add HEIC once validated. It is not shipped by us. | — |
| OCR (M7) | Tesseract 5 CLI as a bundled sidecar, with `eng`+`ara` tessdata | Local and current; a crash cannot take the app down; its `pdf` renderer makes searchable PDFs. | Evaluation results are poor on the owner's documents |
| Storage | JSON settings, JSON-lines history under `%LOCALAPPDATA%\FileToolbox` | Local and inspectable. `FILETOOLBOX_DATA` overrides the location for tests. | — |
| Processing host | In-process workers behind `IActionHandler` + `JobRunner` | Simple and fast. Risk: a native crash in libvips or PDFium takes the tray app down; `Toolbox.Cli` can become an out-of-process worker. | Crash reports from real use |

## Desktop interaction (M1 spike — gate passed 20 Oct 2026)

**Activation:** tap **Shift** (configurable: Shift, Ctrl, Alt, CapsLock) while a mouse button is held.

**Mechanism (proven with real Explorer and Desktop drags):**
1. A `WH_KEYBOARD_LL` hook observes keys. It never swallows keys and does no work in the callback; it posts events to the dispatcher.
2. On a non-repeat activation key while a mouse button is physically down (the swap-buttons setting is honoured; right-drags count too), an always-existing overlay window moves under the pointer. The window is layered, topmost, `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, and was pre-shown off-screen. Its background is alpha 1/255: invisible, but hit-testable for `WindowFromPoint`.
3. A zero-delta `SendInput` mouse move makes the OLE drag loop re-evaluate the window under the pointer. **Result: `DragEnter` arrives without the user moving the mouse** (the harness never needed the fallback movement).
4. The wheel is revealed only when `DragEnter` carries `CF_HDROP` (`DataFormats.FileDrop`). Otherwise the probe closes silently: rubber-band selections, window moves and text drags produce no wheel.
5. `DragOver` and `Drop` answer **`Copy` or `None`, never `Move`**. Explorer offered `Copy, Move, Link` with Shift held, and we answered Copy. Sources were verified byte-identical (SHA-256) after every scenario.
6. Leaving the disc (pointer in the outer catch area) for 320 ms, or `DragLeave`, closes the wheel. Catching stops immediately, so the drag continues to whatever is underneath.
7. After the mouse button is released, closing waits 350 ms, because OLE delivers `Drop` after the button goes up. (An initial 40 ms poll closed the wheel before the drop arrived; the harness caught that race.)
8. Escape cancels the OLE drag at the source; we receive `DragLeave` and close.

**Measured reveal latency** (activation key → wheel revealed, Debug build, Ryzen 5 6600H): cold first use ~128 ms after warm-up, warm 27–90 ms (typically ~30 ms). The target was ≤200 ms.

**Rejected alternatives:** reading the current Explorer selection (forbidden by the seed, and wrong when dragging from other apps); a permanent full-screen drop catcher (it would block every normal drop); a WebView overlay (hit testing on transparent regions is unreliable).

## Product choices made during implementation

- **Wheel ring layouts are pinned:** the Convert ring for PDFs is DOCX/JPG/PNG/TXT (the reference arrangement). The image ring is PNG, WebP, JPG, TIFF, AVIF, BMP, PDF, DOCX: the reference's HEIC slot is taken by JPG, because we cannot encode HEIC and JPG is the most needed target. The Tools ring has the same 7 positions for images and PDFs. Actions that do not apply are shown disabled in place, never rearranged.
- **Ring switching:** each further activation-key tap cycles Convert → Tools (→ Recipes once recipes are pinned).
- **Editing tools accept PDFs:** a chosen page is rendered first. The result is an image, and the action's meaning says so.
- **Same-format conversions** (JPG → JPG) are disabled ("Already JPG") rather than re-encoding silently. Compression is its own tool.
- **Output naming:** `{name}{suffix}{page}` → `Report.jpg`, `Report Cropped.jpg`, `Report - Page 03.jpg`; collisions become `Name (2).jpg`. Outputs go beside the source; when that folder cannot be written, they fall back to `Pictures\File Toolbox` and the job reports it.
- **Colours:** an original neutral glass in light and dark, with the Windows accent colour as the highlight. No reference branding.
- **Windows:** editors and pickers use the Windows 11 acrylic backdrop (`DWMSBT_TRANSIENTWINDOW`). Toasts are never activated, so they use a solid themed surface; acrylic falls back to flat on inactive windows anyway.
- **HEIC detection:** the app checks the user's package repository for `Microsoft.HEIFImageExtension` + `Microsoft.HEVCVideoExtension`. If a decode still fails, the feature is switched off for the session with an explanation. HEIF rotation and mirroring are applied by the Windows codec; EXIF orientation is not applied again on top.
- **PDF page choice:** dropping a multi-page PDF on JPG/PNG opens a picker (thumbnails, ranges such as `1-3, 7`, resolution) with "All pages" preselected. Single-page PDFs run immediately. Several PDFs at once offer "All pages" or "First page of each".
- **Remove output:** the files a job created go to the Recycle Bin (recoverable), and only if they are unchanged since they were saved (same size and write time). It never touches sources.

## Studio (M4)

- **One renderer, two scales:** `EditDocument` stores every distance in full-resolution pixels. The preview renders the same pipeline from a ≤1600 px proxy, multiplying by the proxy scale. Export renders once from the original-quality source (or the PDF page re-rendered at the chosen DPI), so repeated previews never cause repeated lossy saves. A test checks that the preview and a downscaled export agree within 3 levels on average.
- **Pipeline order:** orient → crop → adjust → redact → annotate → background → resize → encode. libvips handles pixel operations; SkiaSharp handles annotations and framing (HarfBuzz shapes Arabic text).
- **Colour:** sources are converted to sRGB through their ICC profile when loading, because the Skia composite carries no profile. Studio exports never carry metadata, which is stated in the UI.
- **Saving goes through the job pipeline** (`studio.export` with the document as JSON). Studio results therefore get the same verification, collision-safe naming, history and toast as conversions. Names follow the reference's pattern: `Page 001 Cropped With Background.png`.
- **Format "auto":** PNG when framed; otherwise the source format where it can be written; JPG when a size target is set.
- **Target size:** quality is reduced first (binary search, never below the minimum quality, default 40), then dimensions are reduced in steps if allowed (never below 480 px on the long edge). The note says whether the target was met.
- **The source is decoded into memory once** when the Studio opens, so the file is not locked while editing.

## Metadata and access (M5)

- **JPEG metadata is removed at segment level:** APP1 EXIF/XMP, APP13 IPTC, COM and vendor APPn are rewritten; the scans are copied byte for byte. Inside EXIF, GPS/camera/date/author entries are unlinked and their bytes overwritten with zeros (`ExifEditor`), and the IFD1 thumbnail is erased. "Remove everything" keeps a minimal EXIF holding only Orientation, so photos stay upright.
- **Other image formats:** libvips re-serialises EXIF from its `exif-ifdN-*` fields and drops entries whose field was removed. That is used for selective removal; the output is re-encoded (lossless for PNG/TIFF; quality 95 for WebP/AVIF, and the note says so).
- **PDF:** PDFsharp rewrites the file without the Info dictionary and the catalog's `/Metadata`. Earlier incremental revisions are not carried over.
- **One window for keyboard and drop-zone access** (the File Toolbox palette). It lists every action with its meaning or the reason it does not apply. A clipboard image is saved as `Pictures\File Toolbox\Clipboard <time>.png`, so its results have a real, visible home.
- **Single instance:** a per-user named pipe (`PipeOptions.CurrentUserOnly`). Further launches forward their arguments and exit.
- **Shell integration is opt-in and per-user** (HKCU `SystemFileAssociations\<ext>\shell\FileToolbox`, a SendTo shortcut, the HKCU Run value). The build does not switch any of it on.
