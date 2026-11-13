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
- **Shortcuts (defaults):** Alt+Shift+S starts a capture (owner's choice; it was Ctrl+Alt+S, which another program held on the owner's PC). Ctrl+Alt+Shift+S starts one after a delay (3, 5 or 10 s), with a countdown that is itself left out of captures. "Capture the same area again" has no default.
- **Revised the same day (owner's notes):**
  - **No area-drawing step.** The shortcut shows the panel at once and the capture starts on the window in front (its visible frame, kept inside its screen's work area; the screen's work area when the desktop, the taskbar or Amir Lap is in front). The first part is taken straight away.
  - **The frame can be moved and resized:** corner knobs and edges resize it, a Move grip on its top edge moves it; everywhere else clicks and the wheel still go through to the app. Changing the area starts the capture again from the new area. The frame is locked while Auto or Follow runs.
  - **Glass panel**, like the Amir Lap window: the blurred screen behind it (the panel is left out of captures, so it can read what is under itself; refreshed while it moves and every 0.7 s, in memory only) under a frosted tint, grain and light edge. It can be resized from its corner, stays above every other window until closed (it re-asserts that every 0.7 s), and still never takes focus.
  - **After the owner's first live try (video, 8 Nov):**
    - The Move grip and the size label sit *inside* the frame, so they can be reached when the frame starts at the top of the screen.
    - Hovering an edge shows a handle under the pointer that follows along the edge; wiggling the pointer makes it grow (up to almost twice its size), so it is easy to grab. Corner knobs grow on hover. Edges can be grabbed a little outside the line too.
    - Resizing and moving stay inside the screen's work area, so the taskbar is never taken in.
    - **Pulling only the bottom edge down keeps the capture** and adds the rows now inside the frame (for the last bit of a page that scrolling can't bring up). If the page moved meanwhile, the top is lined up first; if that fails, it starts again. Any other change to the frame starts again.
    - **Refresh scrolls back to where the capture started** (by the wheel distance it scrolled, carried over area changes; to the top after Follow, where the user did the scrolling), then takes the first part again. It works between steps; the button is greyed out while a step runs. A restart used to begin wherever the page happened to be, which is why it needed several tries.
    - **Extend lines up more often:** how far one wheel notch moves the page is now kept when the step is halved (it used to be forgotten, so the smaller step was checked with no expected distance and failed on small changes such as hover effects) and carried over after Refresh or a new area. A brand-new capture starts with half a notch when the frame is shorter than 900 px, and tries a whole notch before saying "Nothing moved" (some apps only scroll by whole notches).
  - **Live preview of the whole capture:** the session keeps a shrunken copy of the whole long image, updated only for new rows (`LongImage.Overview`), so the preview shows everything captured so far, scrolls smoothly to each new part and makes it glow for a moment. An "Auto" or "Following" badge shows while Longshot is scrolling.

## Longshot: parts that scroll, targets and the picker (13 Nov 2026)

- **Owner's report:** Longshot often did nothing, most of all on "a section that can be scrolled while the page itself can't". The owner asked for it to be fully improved: page captures (the default), a specific area, a scrollable section, smoother controls, a clearer view of what is being captured and a better flow.
- **Why it failed (from the owner's own log that day, every try of 13 Nov):**
  - The wheel always turned in the middle of the frame. When the part that scrolls wasn't there, nothing moved ("Nothing moved").
  - Matching assumed the whole width of the frame moves. A fixed sidebar, the page around a scroll box, or the box's own scrollbar thumb stays put while the content moves, so every step was refused ("Part of it doesn't line up", worst stripe 8 to 35).
  - Follow used the same matching, so it failed the same way ("Lost track").
- **Finding the part that scrolls** (`ShiftFinder.FindPart`, `Toolbox.Processing/Capture/ScrollPart.cs`), tried only when the whole frame doesn't line up and only on the first step:
  1. A coarse map of what changed; the busiest run of columns is the core (a caret or a small spinner elsewhere is too quiet to count).
  2. The core's shift, then the rows where the core really lines up at that shift. A popup, a toast or a title bar repainting can change rows that have nothing to do with scrolling (Edge's translate offer did, in the live test).
  3. The columns that move by exactly that shift. The core's edges that don't (a scrollbar thumb) are trimmed. A band inside that moves on its own (a video) refuses the part rather than cutting it in two. The columns beside it that fit (blank margins) are taken in, up to a border: a flat line of another colour than the part's background (the median of its pixels).
  4. The rows the same way, outwards from the rows that line up. For a picked scroll area or a drawn region, the still rows right above and below are kept as its sticky header and footer. When only a thin strip was cut off the side (a page's own scrollbar), it is still the whole page and keeps its header and footer, as before.
  5. The part is accepted only if the ordinary matching then lines it up.
- **Narrowing:** when a part is found, the capture (one part so far) is cut down to it, the screen is read from the part alone from then on, and the frame glides onto it, so it's plain what is captured. Narrowing happens only before the second part goes in.
- **The scroll point:** the wheel turns at a point in the frame: where the pointer was when Longshot started, if it was inside. The frame shows it as a round marker, which can be dragged onto another part. Its middle is see-through, so the wheel still reaches the app. If one of Amir Lap's own windows is at that point (the panel inside a full-screen frame, say), a spot just beside it is used, since the wheel would otherwise go to that window.
  - If nothing moves at the scroll point on the very first step, four other spots are tried (the middle, left, right and lower middle of the frame); if none moves, the marker goes back and the panel says so.
- **Targets:** Window, Scroll area and Region at the top of the panel.
  - **Start:** with "Start on: What's under the pointer" (the default), the frame appears on the window at once, then glides onto the scroll area under the pointer if UI Automation names one that isn't the whole window. The lookup gives up after 0.9 s and asks a browser twice, because browsers build their accessibility tree when first asked.
  - **UI Automation:** the raw view is walked, because browsers expose a scrolling `<div>` as a plain container that the control view leaves out (Edge, measured).
  - **Scroll area (picking):** the screen dims and whatever scrolls under the pointer lights up (or the window, if nothing there scrolls); a click takes it, and dragging draws a region instead. Region draws only. Esc or a right-click goes back. Clicks are kept from the app underneath by low-level mouse and keyboard hooks that exist only while picking. Clicks on the Longshot panel still go through.
  - **Window** goes back to the window that was in front when the capture started.
- **Seeing what's captured:**
  - The rest of the screen dims, with a rounded hole on the capture. It is a plain Win32 window (a region and one alpha value), so moving the hole costs nothing on large or multiple monitors. It is click-through and left out of captures. It can be switched off ("Dim the rest of the screen").
  - The frame's label says what it is ("Scroll area · 547 × 848").
  - The glow breathes while Auto runs, a ring spreads from the marker each time the wheel turns, and the rows just added glow at the bottom of the frame.
- **Controls:**
  - Auto is the main button until the end is reached; then Done is.
  - Extend, Follow and Undo sit in one row under Auto.
  - Pressing the capture shortcut again saves (or leaves picking).
  - The session's messages are in English and Arabic like the rest of the panel. Icons keep their shape in Arabic, except Undo's arrow.
  - Cancel drops a finish already on its way, so a cancelled capture is never saved.
- **Live check (13 Nov, Edge at 125% scaling, the real wheel and screen):** each page row carries a barcode of its number, read back from the result.
  - A chat in a page that can't scroll, with the wheel over the chat: narrowed to the chat, all 120 rows, once each, in order.
  - A documentation page beside a fixed sidebar and top bar: narrowed to the content, 120/120.
  - A plain long page: 120/120 (its moving scrollbar strip trimmed).
  - Not verified live: the run where the wheel starts over the sidebar, and capturing the scroll area UI Automation found, because the test's Edge window didn't come to the front. Both are covered on simulated screens.
  - The test now refuses to run unless Edge is the window under every point it scrolls.

## Polish round: wheel glow, Pulse Tap, Pro colour picker (13 Nov 2026)

The owner proposed eight ideas. They were discussed and an order was agreed; this round is the first group.

- **Wheel glow (owner: "not looking good"):**
  - **Problems:** the lit tile was an opaque blue gradient (it stopped looking like glass). It sprang 6 px out past the rim, and its 22 px blur spilled onto the desktop.
  - **Lit tile:** now tinted glass. The accent is stronger at the outer edge and more see-through towards the hub, and the frost still shows. A soft light glows just inside its edges, a bright rim runs along the outer edge, and it nudges out 3 px.
  - **Glow:** clipped to the wheel (and the Share bubble), so nothing lights the desktop.
  - **Share bubble:** lights the same way.
- **Wheel body (owner: "the same transparent body as the Longshot window"):** the dark wheel uses the Longshot panel's glass (`Brush.Glass`). Its extra darkening over the blurred screen dropped from 35% to 10%, so the wallpaper's colour comes through. The light wheel was already at least as see-through and is unchanged.
- **Pulse "Tap"** (a 4th way to show, beside Always, Slide away and Fade):
  - It slides away like Slide away, but the pointer touching the line doesn't bring it back, so reaching for a scrollbar or a close button on that edge never opens it.
  - On hover, the line thickens and the dock peeks out 5 px.
  - A click on the line, or anywhere within 12 px of the edge along the dock, opens it.
  - A file held over the line for 0.4 s opens it (a click isn't possible while dragging).
  - It goes away again 1.5 s after the pointer leaves.
  - The right-click menu's "Slide away" counts Tap as on.
- **Colour picker, Basic and Pro** (a setting under Tile options; Basic stays the default, as before):
  - **Both modes:**
    - A magnifier beside the pointer: 11 × 11 pixels, the middle one framed, at 9, 12 or 15× (the wheel zooms).
    - Arrow keys move the pointer by one pixel (ten with Shift), Enter picks.
    - It keeps to whichever side of the pointer has room on that screen.
    - Every pick goes into a history.
  - **Basic:** a click copies the HEX code and a note says so, as before. Shift+click opens the Pro window once.
  - **Pro:** a click copies the starred code (HEX at first) and opens the colour window beside the pick. It opens below-right when there is room, above when picked near the bottom, to the left near the right edge, and the same in the corners: the owner's rule. It never covers the picked pixel.
  - **The colour window** is in the Longshot panel's glass, stays on top until closed, and is left out of screenshots. It shows:
    - the colour large, with its nearest CSS name (by OKLab distance);
    - nine lighter and darker steps in OKLCH (a click shows one);
    - eight codes: HEX, RGB, HSL, HSV, OKLCH, CMYK, WPF/C# and Android. A click copies one, 1–8 copy by number, and Ctrl+C copies the starred one;
    - WCAG contrast as text on white and on black;
    - saved colours (they don't count against the history) and recent picks. There are 10 by default, and 20 or 30 can be chosen in settings;
    - a switch for Pro mode.
  - **Codes:** always written with invariant digits, so they paste the same on an Arabic system. CMYK is the plain formula; print shops use an ICC profile.
  - **Picking again** from the window hides it first, so it is never in the way of what is picked.
- **Not done in this round:** a nearest Tailwind class (it needs Tailwind's palette as data, to be checked against the source) and live checks of the dock, picker and window with the real mouse.
- **Found on the way:** two of Pulse's Arabic tooltips (the dock's dots and its size handle) were stored as garbled text (UTF-8 read as Windows-1252). They are fixed.
- **Owner's first live try (0.5.5, video):** the magnifier lagged and left ghosts, and the Pro window opened as wide as the screen. Both were fixed in 0.5.6. Causes and rules are in [LESSONS.md](LESSONS.md):
  - **Picker:** it now photographs the screen once and moves the magnifier over that frozen photo every frame.
  - **Glass:** `ScreenBackdrop` takes no room, as `DockBackdrop` already did.
  - **Owner's request:** each code also has its own copy button (it turns into a tick for a moment).
  - **Window:** 36 DIPs wider, so the longest codes fit.
  - **Names:** greys are named only with grey names (#191919 had been "midnightblue").

## Preview, per-type rings, the PDF viewer and editor (13 Nov 2026)

These are ideas 6, 7 and 8 of the owner's list. The owner added a spec for the PDF viewer (controls, search, themes, remembering, peek, split, smooth scrolling).

- **Preview on the wheel's centre:**
  - The hub was a dead area (dropping there cancelled), and it feels like "just look", so dropping on it opens the Preview window. This works for any file, including kinds the wheel has no actions for (Word, video, text).
  - **Guard:** the wheel opens with the pointer on the hub, so a quick accidental drop must do nothing. The hub takes drops only after the pointer has left it once, or after 0.65 s. While armed and hovered it lights like a tile and reads "Preview · Edit in one click".
- **One window, look then edit** (owner: "editing should be a simple tap"):
  - Preview is the same kind of window as the editors.
  - **Edit:** for a PDF it turns the same window into the PDF editor. A picture opens in the image editor. A Word document opens in Word (owner's choice). Text opens in its editor.
  - **Other files:** video and audio play. Anything else shows Windows' own large thumbnail with an Open button.
- **Per-type Tools ring ("same seats, different guests"):**
  - **PDF:** Edit PDF, Metadata, Sign, Dark copy, Add BG, Pages, Searchable, Text (OCR).
  - **Images:** as before.
  - **Shared seats:** Metadata, Add BG and Text (OCR) keep the same seat in both, for muscle memory.
  - **No dead tiles:** the PDF ring no longer shows Compress and Redact greyed out (the owner's screenshot). This replaces the 8 Nov rule "the same seven tools for both".
  - **Settings:** `ToolsRingPdf` customises the PDF ring; `ToolsRing` stays the images' ring.
- **PDF viewer:**
  - **Pages:** continuous pages, a quick low-resolution render as each page comes into view, then a sharp one once scrolling settles.
  - **Overview (O):** all pages small in a grid for very fast scrolling; a click goes back to that page. This is the owner's "extremely zoomed out" idea, as a mode rather than an automatic switch while dragging the scrollbar, which would change the scroll range mid-drag.
  - **Zoom:** Ctrl+wheel around the pointer, buttons, fit width, fit page, 100%.
  - **Moving around:** first / previous / page box / next / last; the view turns either way.
  - **History:** back / forward (Alt+arrows) through jumps (outline, links, search, page box).
  - **Outline:** from the file's bookmarks.
  - **Search:** highlights every hit, has a match-case toggle and next/previous (Enter, F3), across all open files in edit mode.
  - **Page colours:** Light, Sepia, Grey, Dark (negative), Night, and "My shade" (a level slider). "Keep photos as they are" leaves raster pictures untouched; vector drawings can't be told apart from text and are coloured like the page.
  - **Remembered per file** (path, size and last change): page, position, zoom and colours.
  - **Peek:** a click on a link to another page shows that place in a card ("Go to page N" or close). Web links open in the browser (http, https and mailto only).
  - **Split:** the same PDF twice side by side, each with its own zoom and place.
  - **Present (F5)** and **Full screen (F11)**.
- **PDF editor:**
  - **Tools:** text (Arabic is right-to-left and shaped), pen, highlighter, white cover (to hide something and type over it), picture, and signature.
  - **Signatures:** drawn, typed in a handwriting font, or a picture whose white background is made see-through. They are kept as PNGs in the data folder and offered next time.
  - **Select** moves, resizes (corner) or deletes; a double-click edits text. Undo and redo cover everything.
  - **Pages panel:** move, turn, remove, and add pages from other PDFs (merge).
  - **Save** goes through the job pipeline (`pdf.save-edits`): a new PDF beside the original, never overwriting, with the usual toast and history.
- **How the PDF is written (chosen after checking the libraries):**
  - **The editor:** PDFium's own editing API copies pages exactly (text, links and fonts stay), reorders, turns and merges them. The additions are drawn with Skia into a PDF layer of their own (real text with embedded fonts, HarfBuzz for Arabic) and placed on each page as a form, through the page's exact display-to-user-space mapping. That is how marks land where they were drawn on rotated and cropped pages too.
  - **Why not PDFsharp for text:** PDFsharp 6.2's core build needs a font resolver and does no Arabic shaping.
  - **The dark copy:** PDFsharp's low-level API writes a white rectangle under each page and a white Difference-blend rectangle over it, which inverts every colour while the page stays vector and searchable. With "keep photos", the pictures' boxes (found with PDFium, including pictures inside form XObjects) are inverted back. It works in PDFium; other viewers still need checking.
- **Honest limits:**
  - Existing text can't be retyped in place (cover and type over it).
  - Changing the fonts of existing text isn't offered (the owner's "maybe even changing the font"): PDF text is positioned glyphs with subset fonts, and changing it isn't reliable, least of all for Arabic.
  - A drawn signature is a picture, not a certificate signature.
  - Password-protected PDFs don't open.

## App icon (8 Nov 2026)

- **"Tool tiles", chosen by the owner** from three options (tool tiles, an AL monogram, a lab flask). It shows four tiles on an accent square:
  - top-left holds a small wheel (File Toolbox);
  - bottom-right holds a down arrow (Longshot);
  - the two pale tiles stand for tools still to come, so the icon doesn't need to change when a tool is added.
- **Drawn in code** (`Shell/AppIcon.cs`) on a 64-unit grid, like the old wheel glyph. `tools/IconGen` writes `app.ico` from it at 16–256 px. Below 24 px the wheel and arrow are left out, because they would only blur the tiles at that size.
- **Colour:** the program icon uses the fixed Windows blue (`BrandAccent`); the tray icon and the window's sidebar follow the user's Windows accent colour, as before.
- **Redrawn as "Glass tiles" with the glass redesign** (below; option A of three on the design canvas): the same four tiles, now solid and frosted panes on a blue-to-indigo gradient with a light edge along the top. The program icon uses Amir Lap blue (#2563EB); the tray icon and the window follow the chosen accent.

## Glass redesign (8 Nov 2026)

- **Owner's brief:** white in light mode, black in dark mode, Apple-style "Liquid Glass", a light/dark switch, English or Arabic from the settings, a clean and professional look with some gradients, and reactions when idle, on hover and on click. Features stay as they are for now. Approved from a design canvas (the window in light and dark, English and Arabic; logo options; a design system board).
- **Glass only on the navigation layer** (sidebar, title-bar chips), solid pages and cards, as Apple prescribes; never glass on glass.
- **How the glass is built in WPF:** the window asks Windows for its acrylic backdrop (the blurred desktop) and lays a white or black tint over it. Glass panels add a gradient fill, a 1 px edge that is bright at two corners, and a light line along the top. WPF cannot bend light, so the lens effect is suggested by the edge and the light, not computed.
- **Reactions:** hover lifts by 1–2 px; a press shrinks to 96% and a soft light spreads from the pointer; switches spring across and stretch while held; the sidebar pill slides to the new page with a soft spring; pages fade in and rise 10 px; the sidebar edge nearest the pointer catches the light (the owner preferred this to a glow following the pointer). When idle, a light sweeps across the sidebar every 9 s (it starts and ends fully outside it) and soft colour drifts behind the glass. The curves are the design's cubic-beziers (`Theme/Motion.cs`). Idle motion runs only while the window is in front, and nothing moves with Reduce motion on.
- **A real window:** the native frame is kept (caption style, without the system menu so Windows draws no buttons of its own), so Windows provides minimize, maximize, resizing from every edge, snapping and its animations; the app draws the title bar. Our maximize button is reported to Windows as its maximize button, which opens the Windows 11 snap layouts on hover. `HomeWindowTests` checks this on a real window shown off-screen, by asking Windows what each point is (no input sent).
- **New settings (General):** Theme (Light, Dark, Windows), accent (Amir Lap blue by default; Windows accent, teal, violet), Reduce transparency (glass turns solid; follows Windows' transparency switch until changed), Reduce motion, Language (English or Arabic). The title bar has quick switches for theme and language.
- **Arabic:** the window mirrors (sidebar on the right, window buttons on the left as in Arabic Windows, switches slide the other way). Shortcuts, file names, folders and sizes stay left-to-right. Text uses Segoe UI's Arabic letters; IBM Plex Sans Arabic (used in the design, OFL licence) could be bundled later. The tray menu is translated too; File Toolbox's own windows (palette, Studio, wheel labels) are still English.
- **A shortcut held by another program:** the Dashboard and the tool's page say so and offer a free shortcut in one click (the command's listed alternative first, for example Ctrl+Alt+L for Longshot, then the same keys with another letter).
- **Closing the window** the first time shows a one-time notice that Amir Lap keeps running in the notification area.

## Pulse (tool #3 — 8 Nov 2026)

- **Owner's idea:** a dock on any of the four edges that shows how much of Claude's and Codex's usage is left and opens them on a click, plus more visuals and shortcuts the user picks in Amir Lap's settings (Claude, ChatGPT, Longshot, File Toolbox to start with). Designed on the canvas first (dock on a desktop, states board, settings page in English and Arabic), including the owner's request to move and resize it.
- **Local only, as drawn:** no sign-in and no internet. Exact, always-on Claude numbers would need Amir Lap to go online, which the design does not do; the network guard tests still pass.
  - **Codex:** every answer Codex gets carries its five-hour and weekly limits, which it writes into its session logs (`~/.codex/sessions/yyyy/MM/dd/*.jsonl`: `rate_limits.primary/secondary` with `used_percent`, `window_minutes`, `resets_at`, `plan_type`). Pulse reads the end of the newest log (512 KB at most) when it changes. Codex is never changed.
  - **Claude:** Claude Code passes `rate_limits.five_hour / seven_day` (`used_percentage`, `resets_at`) to its status line (claude.ai Pro and Max), and hooks report what each session does. Only when the user presses **Connect**, Amir Lap adds a status line and five hooks (UserPromptSubmit, PostToolUse, Notification, Stop, SessionEnd: long-standing events, so older versions do not reject the settings) to `~/.claude/settings.json` (or `CLAUDE_CONFIG_DIR`). Everything else in the file is kept, and a copy from before the first connection is kept in Amir Lap's data. A status line the user already had is remembered, run by Amir Lap with the same input (through Git Bash when present, else cmd) so its text still shows, and put back by **Remove**. If the file cannot be read, nothing is written. Limits update only while Claude Code runs; the dock shows the last numbers and when they were read.
  - **The bridge:** Claude Code runs `"…/AmirLap.exe" --pulse-status` and `--pulse-event` (forward slashes and quotes work in Git Bash, cmd and PowerShell). A custom `Program.Main` answers these before WPF or the single-instance check start: it reads the JSON on standard input, updates `pulse\claude-state.json` under a named lock (several hooks can run at once), prints the status line and exits 0, in about 0.2 s. Problems go to `pulse\bridge.log` and never fail Claude Code. The app is a GUI program, so no console window flashes when the Claude app runs hooks.
  - **"Needs you" and jumping to it:** a hook walks up its parent processes to the first one with a visible main window (Windows Terminal, VS Code, the Claude app) and stores it with the session; clicking Claude's tile while a session waits brings that window forward. With several sessions, the badge counts the ones waiting. Work with no news for 20 minutes counts as ended; sessions are forgotten after 12 hours. "Done" shows until Claude is opened from the dock.
- **ChatGPT** keeps no usage numbers on the PC, so its tile is a shortcut. Apps are found by their Start-menu name (Claude, ChatGPT; Codex by name or the `OpenAI.Codex` app id) and opened through `shell:AppsFolder`; without the app, its website opens in the browser.
- **The dock window:** a borderless, see-through, always-on-top window that never takes focus and is left out of screenshots (so its glass can read and blur the screen behind it, as Longshot's panel does, every few seconds and after it moves). The window is larger than the dock, to make room for the cards beside it; its fully transparent parts let clicks through. It is rounded only on the inner side, with corner pieces that join it to the edge, and placed in physical pixels on the chosen screen's work area, so it never covers the taskbar. Settings: edge, position along it, size (Small 0.8 / Medium / Large 1.25; dragging the inner edge resizes smoothly and snaps on release), Always / Slide away (a thin accent line stays) / Fade, the main screen or the one with the pointer, and hiding while a full-screen app is in front.
- **Tiles:** Claude and Codex as usage rings (green under 70%, amber from 70%, red from 85%), with Claude's light around the ring (circling blue while working, pulsing amber while it needs you, green when done) and a badge; shortcuts as glass squares. The gallery adds a focus timer (a countdown ring), a quick note (a small window that saves as you type), a colour picker (click anywhere and the hex code is copied), a folder, any app or website, and PC status (processor, memory, battery). Tile sets (Work, Study, and more) switch from the right-click menu or the tray.
- **Notes:** once per limit window at the chosen level (70/80/90%, or off), when a limit is used up, and when a Claude task that took over a minute finishes. They appear at the top of the screen without taking focus.
- **After the owner's first try (video, 8 Nov, version 0.5.0 → 0.5.1):**
  - The dock grew until a frosted pane covered half the screen, then all of it, and nothing could be clicked. Cause: the glass's picture of the screen behind it (one pixel per screen pixel) was counted when sizing the dock, so every refresh made the dock bigger, and the next picture bigger again. The picture now takes no room (it only fills the dock), and the dock's size comes from its tiles alone, capped at a large tile's thickness.
  - The right-click menu fell behind the dock (the dock puts itself back on top every few seconds, above the menu's own window). The menu is now a glass card drawn inside the dock's window, beside the dock, so it can never be under it; it closes when the pointer leaves it and the dock.
  - A check on the real dock window, shown far off-screen, now guards both: its size must stay the same across several glass refreshes, its empty side must let clicks through, and tile clicks and the menu must work.

## File Toolbox wheel in glass, Share, no shortcut (8 Nov 2026)

- **Owner's notes:** make the wheel match Amir Lap: more transparent, light at the edges, a texture so it looks like glass or ice rather than a ghost, the chosen option lit from behind with a splash of blue, and animated hover, click and transitions. Remove Ctrl+Alt+F: File Toolbox should only answer while a file is being dragged and Shift is tapped. Add sharing ("Quick Share").
- **Frosted glass without a system blur:** Windows cannot blur behind this kind of overlay window, so as the wheel opens it reads the screen behind it (its own window is still invisible then) and shows that picture blurred under the glass, magnified 6% like a lens. The picture lives in memory only and is dropped when the wheel closes. If reading the screen fails, the glass is simply see-through.
- **The material:** a tinted disc over that backdrop, a fine generated grain with a few sparkles, faint diagonal "ice" bands, light caught just inside the edge (strongest top-left and bottom-right), a 1 px rim, a highlight along the top-left, and a soft shadow ring around the outside (never under the glass, so it stays clear). Tiles are glass panes with their own rim; the hub is a lens.
- **Motion:** opening grows with a spring and a small turn while the tiles fly out from the hub one after another and the Share bubble pops in; the hovered tile lights up blue from behind (a bright core through the accent to indigo, with a soft glow around it) and pushes outward with a spring; switching rings spins the old tiles out and the new ones in; a drop sends a ripple out from the tile, then the wheel swells slightly as it fades, as if it took the file. All of it is instant with Reduce motion.
- **No keyboard shortcut** for File Toolbox (owner's decision). Its window is still reachable from the tray, the Dashboard's Open button and Explorer's menu / Send to.
- **Share bubble**, just outside the wheel at the bottom-right, on every ring, for any file (even ones File Toolbox can't convert). Dropping a file on it:
  - with Google's **Quick Share for Windows** installed, starts it with the files the same way its own Explorer menu entry does (`nearby_share.exe --files-from=context-menu <files>`, found in its menu handler). This is not a documented interface, so a Quick Share update could change it; if starting it fails, the next option is used;
  - otherwise opens **Windows' share sheet** (Nearby sharing, Phone Link, apps that accept files), using the documented `IDataTransferManagerInterop` desktop pattern.
  Amir Lap itself sends nothing: the user picks the device or app, which does the sending. The network guard tests still pass.
