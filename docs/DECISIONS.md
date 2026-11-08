# Engineering decisions

Product direction lives in [`sead_idea.md`](../sead_idea.md) (the seed). This file records the engineering choices made while building it, why they were made, and what would change them. Newest entries are at the bottom of each section.

## Fixed by the owner (not up for engineering change)

- Windows first, macOS later. Local processing only: no accounts, no cloud, no telemetry, no payments or licensing. Original files are never modified.
- **Pacing (20 Oct 2026):** build M0–M8 straight through, committing per milestone. The automated drag harness may drive the real mouse and keyboard without warning first.
- Real Arabic/English scans for the OCR evaluation will come from the owner later. Until then, OCR is measured on a generated corpus ([OCR_EVALUATION.md](OCR_EVALUATION.md)).
- **Amir Lap (8 Nov 2026):** the app becomes **Amir Lap**, a PowerToys-style home for several tools, and File Toolbox becomes tool #1 inside it. One tray app and one window with a sidebar (Dashboard, one page per tool, General). Every tool has an on/off switch, its own global shortcuts and its own settings. Tool #2 is **Longshot** (scrolling "extend downward" screenshots), to be rewritten in C# from the working Python prototype in `tools/LongshotPrototype`. Work is phased: 1. the shell (this entry), 2. Longshot, 3. packaging, measurements and docs. See [Amir Lap shell](#amir-lap-shell-phase-1--8-nov-2026).

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
| OCR (M7) | **Windows' built-in recogniser (`Windows.Media.Ocr`)** behind a portable `IOcrEngine` interface (changed from the planned Tesseract sidecar on 20 Oct 2026) | Arabic (ar-SA) and English were already installed on this PC. No engine or language data to download, bundle, license or update. It recognises on-device and measured well (0.1% CER on clean Arabic once reading order was fixed). The cost is the Windows 10 SDK target (`net9.0-windows10.0.19041.0`) and its projection assembly: `Microsoft.Windows.SDK.NET.dll` is 23.7 MB, which M8 can trim. Tesseract with `ara`+`eng` data would have been a similar size. | The owner's real samples score poorly, or more languages or layout analysis are needed (then add a Tesseract or PaddleOCR sidecar as a second `IOcrEngine`). macOS will use Apple Vision. |
| Storage | JSON settings, JSON-lines history under `%LOCALAPPDATA%\AmirLap` (was `%LOCALAPPDATA%\FileToolbox` before 8 Nov 2026) | Local and inspectable. `AMIRLAP_DATA` overrides the location for tests; the older `FILETOOLBOX_DATA` is still honoured. | — |
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

- **Wheel ring layouts are pinned:** the Convert ring for PDFs is DOCX/JPG/PNG/TXT (the reference arrangement). The image ring is PNG, WebP, JPG, TIFF, AVIF, BMP, PDF, DOCX: the reference's HEIC slot is taken by JPG, because we cannot encode HEIC and JPG is the most needed target. The Tools ring has the same 7 positions for images and PDFs, plus an eighth for text recognition: Text (OCR) for pictures, Searchable for PDFs. Actions that do not apply are shown disabled in place, never rearranged.
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
- **Accessibility:** the custom TabControl template must name its content presenter `PART_SelectedContentHost`, or screen readers cannot see tab contents. The harness found this when its UI Automation could not reach the "New style" button.

## Recipes (M6)

- **User-facing steps** (`RecipeOp`): PDF pages → images, centred crop to a ratio, resize, frame/brand, format, fit under a size, remove metadata, combine into one PDF. They are stored as JSON in `%LOCALAPPDATA%\FileToolbox\recipes`. Built-in recipes are code, read-only, and can be pinned.
- **The compiler fuses consecutive image steps** into one Studio render, so a recipe decodes and encodes each picture once (no generation loss). A format change straight after rendering PDF pages is folded into the render itself. Metadata removal is implicit for rendered images; it becomes a separate lossless stage only when nothing else re-renders the pictures.
- **Execution is a normal job** (`recipe.run`, or `recipe.run-combined` when the recipe combines files into one result). Intermediate files go to `…\FileToolbox\staging\<id>`, which is always deleted afterwards and swept at startup. The final stage writes through the ordinary verified output pipeline, using the original name plus the recipe's suffix.
- **Requirements** (max size, formats, max dimensions) are checked on the real results and reported per file. A result that misses a requirement is still saved (it is the best effort) but clearly flagged ✗, which avoids both a silent failure and a false success.
- **Interactive recipes** open the Studio on the crop step with the recipe's framing and export applied. Export-pack ratios from the recipe preselect "Also save versions".
- **Brand styles** live in `…\presets\brand-*.json`. Logo and caption are drawn by the same renderer after framing; logo size is relative to the canvas, so every variant stays consistent.
- **Export packs reframe and never crop:** each ratio is a background frame around the full content, so text at the edges is never cut off silently.

## OCR (M7)

- **Engine:** `Toolbox.Windows.Ocr.WindowsOcrEngine` adapts `Windows.Media.Ocr`. Pixels go in as BGRA `SoftwareBitmap`s and words come back with boxes. Languages are whatever the user installed in Windows ("Optical character recognition" under a language's options). Missing languages are reported in the feature status and in job notes, with instructions. The app downloads nothing.
- **Pipeline (`OcrService`):**
  1. Flatten, then greyscale.
  2. Enlarge to a 1600 px long edge (at most ×4).
  3. Recognise once per language.
  4. Map the boxes back to source pixels.
  5. Restore reading order (`OcrLayout`).
  6. Merge the languages.

  Each setting was chosen from the evaluation; [OCR_EVALUATION.md](OCR_EVALUATION.md) has the numbers.
- **Reading order is our job:**
  - Windows returns Arabic words in visual (left-to-right) order.
  - `OcrLayout` orders words by position, then applies word-level bidi:
    - An RTL line is reversed.
    - Runs of Latin words or numbers inside it keep their order.
    - Neutral tokens take their neighbours' direction.
    - A line that mixes both scripts follows the page's majority script.
  - Fragments of one visual line (split at wide gaps) are joined when the gap is under 1.5 line heights.
- **Mixed Arabic/English = per word:**
  - Latin-dominant lines come from the English model.
  - In Arabic lines, Latin/number runs are replaced by the English reading, but only if it covers the same extent, has no more words, and has at least 80% of the characters.
  - This fixes hyphenated codes, which the Arabic model scrambles, without letting the English model split codes like `AB1234`.
- **Searchable PDF:**
  - Text layer:
    - A Tesseract-style invisible layer: a Type0/Identity-H "GlyphLessFont" (each UTF-16 unit is its own CID, DW 500, identity ToUnicode in 256 compliant ranges), drawn in text render mode 3.
    - One text object per word, stretched by its text matrix over the recognised box. The box comes through PDFium's `FPDF_DeviceToPage`, so rotated pages and offset boxes line up; a test checks a `/Rotate 90` page.
    - The font is not embedded. Nothing is painted, and PDFium, PDFsharp and the verifier need no font program. PDF/A would need an embedded glyphless font (roadmap).
  - Word order inside the layer:
    - Words are written in visual order, with Arabic characters reversed and digit runs kept. That is how Word and browsers write Arabic, and what PDF readers expect when they rebuild reading order.
    - An experiment showed PDFium orders by position anyway: content order is irrelevant, character order within a word matters.
    - PDFium's own bidi still reorders `AB1234`-style codes in Arabic lines and Latin-heavy RTL lines. That is documented as a reader limitation, and TXT is the exact output.
  - Pages:
    - **PDF pages are copied, not re-rendered** (PDFsharp import). The original content stream is wrapped in `q … Q` so its graphics state cannot leak into the text layer.
    - Pages that already have selectable text (3 or more letters or digits) are copied unchanged and not recognised again.
    - If PDFsharp cannot import a PDF, its pages are recreated from images at the chosen DPI, and the note says so.
    - Image inputs become one page at the image's DPI. Images without a usable DPI are sized to an A4 long edge.
  - **Verification:** the saved PDF is reopened with PDFium, and every page that received words must return text. Otherwise nothing is saved.
- **Text output:** UTF-8 without BOM, lines in reading order, a blank line between blocks that are far apart, and a form feed between PDF pages (the same convention as PDF → TXT). For PDFs, pages that already have a text layer use it instead of recognition, and the note says which pages.
- **Target framework:** the Windows adapter, CLI, Windows tests and `OcrEval` target `net9.0-windows10.0.19041.0` (supported from 10.0.17763). Core and Processing stay `net9.0`. An architecture test forbids product code from using WinRT networking namespaces (`Windows.Web*`, `Windows.Networking*`) as well as `System.Net.*`.

## Hardening and packaging (M8)

- **Distribution:** a self-contained win-x64 folder, so no .NET install is needed, installed per user under `%LOCALAPPDATA%\Programs\FileToolbox` by a PowerShell script. No administrator rights, no MSI or MSIX.
  - **Why:** nothing about the app needs machine-wide state, the owner runs it on one PC, and a script is transparent about exactly what it changes.
  - **Revisit:** MSIX or a signed installer once there is a distribution channel; code signing is not set up.
- **One folder, two programs:** `FileToolbox.exe` and `ftb.exe` share the runtime. Each is published on its own and then merged: the CLI first, then the app with a forced overwrite.
  - **Why the order:** where both carry a file, the app's copy is the newer one (WPF's `System.IO.Packaging`, `WindowsBase` and three others).
  - **What went wrong before:** publishing both into one folder kept the CLI's older `System.IO.Packaging` (newer timestamp), and the app crashed at start-up. `publish.ps1` now starts and quits the published app to catch this.
- **Size:**
  - ReadyToRun for everything except the Windows SDK projection and OpenXML; compiling those ahead of time would roughly triple their size for code that only OCR and Word export use.
  - Third-party native `.pdb` files are dropped, which saves 110 MB. Our own are kept so logged errors have line numbers.
  - Result: a 235 MB folder and a 93 MB zip.
- **Install:**
  - **Safe copy:** it mirrors the build into the destination only if that folder is empty or holds an earlier install (mirroring deletes extra files). It asks a running copy to quit with `--quit` first.
  - **Shortcut and entry:** it adds a Start Menu shortcut and an Installed apps entry under HKCU.
  - **Re-pointing:** Explorer menu, Send to and startup entries that another copy of File Toolbox registered are pointed at the installed one. It never switches on anything that was off.
- **Uninstall:**
  - It quits the app, then removes only entries whose command or target is `FileToolbox.exe`, plus the shortcut, the entry and the folder (only if it contains `FileToolbox.exe`).
  - User data stays unless `-RemoveUserData` is passed. Outputs are never touched.
  - The sandboxed `test-install.ps1` exercises all of this with parameterised registry roots and folders, so the real Start Menu and registry are never touched by tests.
- **Exit:** quitting cancels running jobs (finished outputs are kept), waits up to 4 s, then removes listed temp files and staging folders. Anything a crash leaves behind is removed at the next start.
- **Low disk space:** when the destination drive has under 32 MB free, the result goes to the fallback folder, with the reason in the note. If that drive is full too, the job fails before writing and names the drive. Running out of space mid-write still fails cleanly: the temp file is discarded and the message says so.
- **Idle cost:** 0.08% of one core and 112 MB private in steady state ([PERFORMANCE.md](PERFORMANCE.md)).
  - **Loaded at start-up:** libvips and the Windows SDK projection, because the wheel must know at once whether AVIF encoding and OCR are available, and libvips' untrusted loaders must be blocked before any file is opened.
  - **Loaded on first use:** PDFium and Skia.
  - **Possible change:** caching the feature checks would let both load lazily (roadmap).
- **Icon:** `app.ico` is generated by `tools/IconGen` from the same drawing code as the tray icon, with a fixed Windows-blue accent. Sizes up to 128 px are classic 32-bit bitmaps, because .NET Framework's reader and some shell components misread PNG entries; 256 px is PNG.

## Amir Lap shell (phase 1 — 8 Nov 2026)

- **One process, tools as modules.** Each tool is a `ToolModule` (id, name, commands with default shortcuts, start/stop, its settings page, its tray items). `ToolRegistry` keeps them in line with the settings: a tool that is switched off is stopped, so File Toolbox's keyboard hook and wheel window go away and its shortcut is released. Idle cost while off is zero by construction. Changing a start-only setting (the wheel size) restarts just that tool instead of asking for an app restart.
- **Shortcuts.** Registered with `RegisterHotKey` on one message-only window (`HotkeyManager`). They are stored as text per tool and command (`"Ctrl+Alt+F"`, `""` = switched off, missing = the default), parsed by the platform-neutral `KeyChord` in Core. A global shortcut must include Ctrl, Alt or Win (F13–F24 may stand alone), so typing is never taken over. The recorder checks the app's other commands, then probes Windows to see whether another program holds the chord. While recording, the app's own shortcuts are released, so pressing the current one reaches the recorder.
- **Settings.** Still one `settings.json`. New fields: `tools` (per tool: `enabled`, `shortcuts`) and `trayClick`. Every older settings file loads unchanged, with all tools on.
- **Data migration.** On the first start, if `%LOCALAPPDATA%\AmirLap` has no settings, settings, history, recipes and brand styles are copied from `%LOCALAPPDATA%\FileToolbox`. Logs, diagnostics and temp bookkeeping are not copied. The old folder is never changed, and a marker file prevents a second copy. It is skipped when a data folder is set through the environment (tests, harness).
- **What changed name and what didn't.** The app is Amir Lap: `AmirLap.exe`, its mutex and pipe, the Start Menu entry, the Installed apps entry, the `Run` value `AmirLap` and the data folder. The tool keeps its name: Explorer's "File Toolbox…" entry (verb key `FileToolbox`), "Send to › File Toolbox", `Pictures\File Toolbox`, the `ftb` CLI and the creator tag in PDF/DOCX outputs. Internal namespaces (`Toolbox.*`) and `FileToolbox.sln` also stay, because renaming them would be churn with no user benefit.
- **Installer replaces File Toolbox.** `install.ps1` quits a running File Toolbox and removes its program folder, Start Menu shortcut and Installed apps entry, but never its user data. It moves start-with-Windows from the `FileToolbox` value to `AmirLap`, and re-points File Toolbox's Explorer and Send-to entries at `AmirLap.exe`. `test-install.ps1` checks all of this in its sandbox.
- **Launching.** A plain launch, or a second launch, opens the Amir Lap window; `--background` stays quiet; `--palette` opens File Toolbox's window directly (scripts, the QA harness). Files from Explorer still open the palette, or File Toolbox's page if the tool is off. A left click on the tray icon opens this window by default; General can switch it to the palette.
- **Window design.** Built in code like the other windows, on the same glass theme and shared styles. New shared styles: `Toolbox.Switch`, `Toolbox.NavItem`, `Toolbox.KeyCap`. Settings rows are grouped in cards, as in Windows Settings and PowerToys. The old Settings window is gone: its File Toolbox sections live on the tool's page, and Theme, Reduce motion, Start with Windows and Privacy live on General.
- **Light-theme fix (found while building this).** The global `TextBlock` style forced `Brush.Text` on text inside accent buttons, selected segments and selected tabs, so white-on-accent text turned black in light mode. Those three templates now reset the implicit style for their content.
- **Not done in phase 1:** a new brand icon (added on 8 Nov 2026, see [App icon](#app-icon-8-nov-2026)), the quick-launcher flyout and the shortcut cheat sheet (both on the roadmap), and Longshot itself (phase 2).

## Longshot (phase 2 — 8 Nov 2026)

- **Rewritten in C#** from the Python prototype in `tools/LongshotPrototype`. The prototype stays as a reference with its live-test tools and isn't shipped.
- **Where the code lives.**
  - The stitching engine (`ShiftFinder`, `LongImage`, `ScrollStitcher`) is pure image maths in `Toolbox.Processing/Capture`. It stays platform-neutral, and the existing network guard covers it without changes.
  - Screen reading, wheel input and the windows are in `Toolbox.Windows/Longshot`.
  - The session loop works against `IScreenSource` and `IScroller`, so it is tested end to end with a simulated page and scroller, without touching the real mouse or screen.
- **Matching** is the prototype's, with its three fixes from live tests in Chrome:
  - Rows next to a sticky bar count as part of it, because their soft edge changes as the page scrolls.
  - A match is refused when any 16-px stripe differs a lot, so look-alike rows (lists, tables) can't be joined in the wrong place.
  - The usual step size is used only as a tie-breaker.
- **Two more fixes found while porting:**
  - A pixel-perfect match beats a near miss: the tie tolerance shrinks with the best score.
  - "Nothing moved" now needs nearly identical frames. Before, a jump past the whole area could look like the end of the page and stop Auto early.
- **Limits.** A screenful of identical rows (shifts a row apart match equally well) has no right answer when scrolling by hand. Extend and Auto break the tie with the usual step size; Follow can't.
- **Capture:** GDI `BitBlt` with `CAPTUREBLT` on the area, in physical pixels (the app is PerMonitorV2). The selection screen is one window over the whole virtual screen, showing a frozen snapshot. Monitors with different scalings are untested, as for the wheel.
- **Scrolling:** real wheel input (`SendInput`). The pointer moves to the middle of the box, the wheel turns, and the pointer goes back, so hover effects aren't captured.
  - **Step size:** starts at one notch (half a notch for areas under 300 px), measures the pixels per notch, then aims for 55% of the area (35% "small", 75% "large").
  - **Overshoot:** if a step overshoots, it scrolls back and halves the notches, then the wheel delta.
  - **Can't scroll:** apps running as administrator, because Windows' UIPI blocks the input.
- **Never in the picture:** the panel, the frame and the countdown are marked `WDA_EXCLUDEFROMCAPTURE`. The frame is click-through, and the panel never takes focus, so Follow can be used with the keyboard in the captured app.
- **Output:** saved to `Pictures\Screenshots` (as Snipping Tool does) or a chosen folder, as "Longshot yyyy-MM-dd HHmmss.png"/".jpg". It is encoded on an STA worker, written to a temp file then renamed, never overwrites, and is copied to the clipboard by default.
  - **Result window:** shows a downscaled copy (at most 16,000 px tall), because WPF can render very tall bitmaps blank. **Open in File Toolbox** hands the file to the palette for any File Toolbox action, such as OCR, PDF or compression.
  - **Limits:** at most 40,000 px tall; Undo keeps the last 10 steps.
- **Shortcuts (defaults):** Ctrl+Alt+S starts a capture. Ctrl+Alt+Shift+S starts one after a delay (3, 5 or 10 s), with a countdown that is itself left out of captures. "Capture the same area again" has no default.

## App icon (8 Nov 2026)

- **"Tool tiles", chosen by the owner** from three options (tool tiles, an AL monogram, a lab flask). It shows four tiles on an accent square:
  - top-left holds a small wheel (File Toolbox);
  - bottom-right holds a down arrow (Longshot);
  - the two pale tiles stand for tools still to come, so the icon doesn't need to change when a tool is added.
- **Drawn in code** (`Shell/AppIcon.cs`) on a 64-unit grid, like the old wheel glyph. `tools/IconGen` writes `app.ico` from it at 16–256 px. Below 24 px the wheel and arrow are left out, because they would only blur the tiles at that size.
- **Colour:** the program icon uses the fixed Windows blue (`BrandAccent`); the tray icon and the window's sidebar follow the user's Windows accent colour, as before.
