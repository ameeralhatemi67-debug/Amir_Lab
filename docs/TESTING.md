# Testing

## Automated

```bash
dotnet test FileToolbox.sln
```

Unit and integration tests cover the Core rules (probing, naming, collisions, outputs, jobs) and the Processing engines against generated fixtures.

Amir Lap's shell is covered by these tests:

- `KeyChordTests`, `ToolSettingsTests` and `LegacyDataMigrationTests` (Core): parsing shortcuts, per-tool settings, and the one-time copy of File Toolbox data.
- `ToolRegistryTests` (Windows): tools start and stop with their switch, shortcuts are released while a tool is off, clashes are reported, and changes, switch-offs and resets work. They register throwaway Ctrl+Alt+Shift+F22/F23 shortcuts for a moment.
- `ScrollStitcherTests` (Processing): Longshot's stitching on generated pages. The results are pixel-identical with sticky bars, hand scrolling, noise, soft footer edges and Undo, and blank stretches and look-alike rows are handled safely.
- `ScrollPartTests` (Processing): screens where only part scrolls (a panel beside a fixed sidebar, a scroll box in a still page, content beside its scrollbar thumb, a small list, dark mode). The exact part must be found and the result must be pixel-identical to its content. A caret, an animation in place and a video inside the content must not be taken for a part.
- `CaptureSessionTests` (Windows) runs Longshot's whole capture loop (Extend, Auto, Follow, Undo, end detection, the smaller-step retry) against a simulated page and scroller. Its second half uses a simulated window whose page stays put and whose box scrolls only when the wheel turns over it: the capture narrows to the box, other spots are tried when nothing moves, and the scroll point can be moved. The real mouse and screen are never touched. It shares a test collection with `HomeWindowTests`, which switches the app to Arabic for its snapshots, because the session's messages follow the app language.
- `CaptureSessionTests` also runs a section scrolled by itself (as through UI Automation): it must give the exact result without the wheel, keep the page around still (or put it back and stop), Undo and Refresh by percent, and fall back to the wheel when an app only says it scrolled.
- `HomeWindowTests.CheckLongshotNested` is the owner's page simulated. The page scrolls with a section in it that scrolls too, and its wheel behaves like a browser's. Started over the section, just the section is captured and the page never moves. With the marker dropped on the page, the page is captured whole. The wheel must never be used.
- `ScreenCaptureTests` reads a 64×64 corner of the real screen (read-only) and checks where the panel goes.
- `LiveLongshotTests` (Windows, **off unless `AMIRLAP_LIVE_LONGSHOT=1`**) runs Longshot on the real screen with the real wheel. It opens Microsoft Edge in a throwaway profile on test pages: a chat in a page that can't scroll, a docs page beside a fixed sidebar, and a plain long page. Every row carries a barcode of its number, and the result must show each row once, in order. It opens Edge InPrivate: otherwise Edge signs the throwaway profile in with the Windows account and covers the page with a "now syncing" prompt. The window is kept on top while it runs. The cases where the wheel is used move the pointer for a moment at each step, so keep hands off the mouse. The nested-page case (a page that scrolls, with two sections that scroll in it) scrolls each part by itself and must leave the page and the pointer untouched. It stops unless Edge is the window under every point it scrolls. Run it on its own, so the test process can be per-monitor DPI aware as Amir Lap is; set `AMIRLAP_SNAPSHOT_DIR` to keep the captures:

```bash
set AMIRLAP_LIVE_LONGSHOT=1 && dotnet test tests/Toolbox.Windows.Tests --filter LiveLongshotTests
```
- `PulseTests` (Core): reading Codex's logs, Claude's status line and hooks, the most urgent session, the shared state file under concurrent writes, the bridge's output, alerts, connecting and removing Claude Code on temporary settings files, and Pulse's settings.
- `ColourTests` (Core): the colour picker's codes (all eight, also on an Arabic system), reading HEX, WCAG contrast and grades, OKLCH both ways, the nine steps, the nearest CSS name, the history (newest first, each colour once, 10–30) and the settings it keeps.
- `PdfEditTests` (Processing): the viewer's and editor's PDF engine on generated files, rendered back with PDFium and checked pixel by pixel. It covers the dark copy with photos kept or inverted (also a picture inside a form XObject) and still searchable; the editor's Save (reorder, turn, merge, cover, ink, picture, text, on plain and rotated pages, Arabic kept as text, never overwriting); picture boxes; page maps under rotation; search with and without case; outline; page and web links.
- `SensitiveTextTests` (Processing): finding personal info in recognised lines. It covers e-mails (also with spaces round @), phone numbers in five shapes (Arabic digits included), dates, amounts, order numbers and versions that must not count, Luhn-checked cards, checksum-checked IBANs, keys, tokens, labelled passwords (English and Arabic), IP addresses, one hit per thing, boxes trimmed to the characters, and dotted-out text.
- `BlurToolTests` (Windows): a drag across text snaps to the words, one area per line; a brushed edge is not taken; a drag away from text stays as drawn.
- `StudioTests` also checks that Blur and Pixelate leave no letter shapes (contrast between columns falls below 1/12).
- `HomeWindowTests` also opens the image editor's Blur step on a generated chat screenshot. It finds the five pieces of personal info, hides them in one step and saves snapshots before and after.
- `VideoRecorderTests` (Windows): Longshot video's recorder on made-up frames (no screen). It writes an MP4 that Windows reads back with the right frame count, length, orientation and timing; a trimmed copy and a GIF come out right; names never overwrite; click rings are drawn.
- `HomeWindowTests.CheckVideoFlow` runs a whole video through the real controller, panel, frame, pill and recorder on made-up frames, every window off-screen. It covers choosing the area, the countdown and Discard, pause and resume, Stop, the shortcut alone, Discard deleting the file, and switching between Screenshot and Video on the same area.
- `VideoBlurTests` (Processing): video blur's pieces on made-up frames. It checks boxes moving between keys, followed keys thinned, and the blur leaving no letter shapes. The follower must keep a box on scrolling text, a dragged window, text leaving the top and text sliding under a fixed header, and must keep the box in place when the content changes.
- `VideoBlurPipelineTests` (Windows) runs on real MP4s made by the recorder. The frame reader must read frames upright, cropped and in order, and seek. A box must follow text through the compressed frames. The blurred copy must hide the box only while it is on, and a cancelled save must leave no file.
- `HomeWindowTests.CheckVideoBlur` opens the video blur editor far off-screen on a real recording. It draws a box and follows it, moves it, finds an e-mail with stand-in text recognition, and saves, with snapshots in light and dark.
- `ColourPlacementTests` (Windows): where the Pro colour window and the magnifier go for picks in the middle, near each edge and in the corners (never covering the picked point).
- `HomeWindowTests` also opens the Preview window far off-screen on a generated PDF. It renders, searches ("1 / 3"), turns dark, becomes the editor (additions drawn, undo and redo, the pages panel) and saves the plan. It also checks the wheel's Preview hub (no drops until armed) and the PDF Tools ring (its own tools, shared seats).
- `HomeWindowTests` builds every page of the Amir Lap window (Pulse's included, and its dock on all four edges with its cards; it also shows the real dock window far off-screen and checks that it keeps its size, lets clicks through its empty side, and answers clicks and its menu). It creates the app only for its styles (`AMIRLAP_STYLES_ONLY=1`), so start-up never runs in the test and never talks to an installed Amir Lap that is running; plus File Toolbox's wheel and Longshot's panel, frame and result window, off-screen in light and dark (and the window in Arabic), without showing anything or starting a tool. To look at them, set `AMIRLAP_SNAPSHOT_DIR` to a folder; each one is saved there as a PNG:

```bash
set AMIRLAP_SNAPSHOT_DIR=%TEMP%\amirlap-pages
dotnet test tests/Toolbox.Windows.Tests --filter HomeWindowTests
```

### Real-drag harness

The harness moves the **real mouse and keyboard** for a few seconds per scenario, so keep hands off while it runs.

```bash
dotnet build FileToolbox.sln
```

```bash
tools/DragHarness/bin/Debug/net9.0-windows/DragHarness.exe --expect-outputs
```

Options:

- `--only a,b` runs the named scenarios.
- `--desktop` adds the Desktop-source scenario, which shows the desktop with Win+D and uses a temporary file that it deletes afterwards.
- `--expect-outputs` also requires real output files (from M3 on).

It starts the app with an isolated data folder (`FILETOOLBOX_DATA`) and diagnostics on. It creates fixtures with Arabic and English names, opens a real File Explorer window, performs the drags with `SendInput`, and checks:

- the payload the app received equals the dragged files;
- the drop was answered with **Copy**;
- every source still exists with the same SHA-256, and no unexpected files appeared.

Results and screenshots go to `%TEMP%\ftb-harness\<timestamp>\`.

Scenarios: `pdf-to-jpg`, `image-tools-ring`, `image-to-webp-arabic-name`, `two-images-to-pdf`, `multipage-pdf-asks-for-pages`, `heic-to-jpg-via-windows-codec` (when the HEIF/HEVC extensions are installed), `studio-crop-background-save`, `palette-from-second-launch`, `recipe-ring-website-images`, `ocr-scan-to-text`, `escape-cancels`, `leave-wheel-closes`, `no-wheel-without-file-drag`, plus `desktop-source` (`--desktop`) and `qa-windows` (`--qa`, screenshots only).

### OCR evaluation

```bash
dotnet run --project tools/OcrEval -- --out artifacts/ocr-eval
```

It renders the generated Arabic/English/mixed corpus, runs the app's OCR pipeline with several strategies and writes `results.md`. Add `--samples <folder>` to include real scans; each image needs a same-named `.txt` holding its exact text. See [OCR_EVALUATION.md](OCR_EVALUATION.md).

### Packaging

```bash
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

This builds the self-contained folder and zip in `artifacts/publish`. It checks the result by running `ftb features`, then starting the published `FileToolbox.exe` and quitting it.

```bash
powershell -ExecutionPolicy Bypass -File tools/test-install.ps1
```

This runs install, update, uninstall and uninstall with `-RemoveUserData` against a sandbox: a temp folder and a throwaway `HKCU\Software\FileToolboxTests` branch. It checks:

- the Start Menu shortcut and the Installed apps entry;
- that Explorer menu, Send to and startup entries left by another copy are re-pointed;
- that unrelated entries are left alone;
- that the installed app starts and quits;
- what is removed and what is kept on uninstall.

The real Start Menu and registry are not touched.

### Measurements

```bash
tools/Bench/bin/Release/net9.0-windows10.0.19041.0/Bench.exe --out artifacts/bench
```

This generates large fixtures and runs each heavy operation in its own process (time and peak memory). It also measures the idle desktop app, so quit File Toolbox first. Results go to `artifacts/bench/results.md`; see [PERFORMANCE.md](PERFORMANCE.md).

## Manual checklist (owner)

Install by right-clicking `artifacts/publish/install.ps1` > Run with PowerShell (as people do from the zip): a window shows four steps, Amir Lap opens its welcome, and the window closes after a countdown. Scripts use `install.ps1 -Quiet -Launch`. Or run the Debug build. The app sits in the notification area. Then:

0. **Installing from the zip:** extract the release zip, right-click install.ps1 > Run with PowerShell. You should see the steps with "done", then the welcome (installed or updated, where it lives, what to try). Run it with a file in use to see an error stay on screen.
1. Drag a PDF from File Explorer, tap **Shift** mid-drag → the wheel appears at the pointer. Drop on **JPG**.
2. Same from the **Desktop**.
3. Drag an image, tap Shift twice → the **Tools** ring.
4. Drag several files → the centre shows the count and total size.
5. Press **Esc** during a drag with the wheel open → the wheel closes and nothing changes.
6. Open the wheel, then move away from it and drop somewhere else → normal Explorer behaviour resumes.
7. Hold Shift for a whole Explorer drag *without* the wheel → Explorer's usual "Move" behaviour, which is not ours.
8. Try at another display scaling and on a second monitor if you have one, and note any misplacement.
9. Drag a scan or a photo of an Arabic letter, tap Shift twice, drop on **Text (OCR)** → a `.txt` beside it; open it and check names and numbers.
10. Drag a scanned PDF onto **Searchable** (Tools ring) → `… Searchable.pdf`; open it, search for an Arabic word and an English word, and copy a line.
11. **Pulse:** the dock appears on the right edge. Hover each tile (cards), drag its dots along the edge and onto the top edge, drag its inner edge to resize it (it snaps to Small/Medium/Large), right-click it to switch sets, and set Show to Slide away. Start a full-screen video: the dock hides.
12. **Pulse and Codex:** use Codex once, then hover Codex's tile: the 5-hour and weekly numbers match Codex's own.
13. **Pulse and Claude Code:** on the Pulse page press **See what changes**, then **Connect**. In Claude Code, send a prompt (the ring's light circles), make it ask for permission (amber, "!"), click Claude's tile (its terminal comes forward), and let it finish a task that takes over a minute (a note at the top). Press **Remove**: `~/.claude/settings.json` is back as before.
14. **Longshot video:** point at a window and press **Alt+Shift+V**: the frame and the Video panel appear. Try **Region** and **Screen**, then **Record**. After 3, 2, 1, click around and type in the window: the pill and the red frame must not be in the video, and the pointer and click rings must be. Pause, resume, then **Stop**. In the result window, drag the trim handles, **Save trimmed copy** and **Save as GIF**, and paste the copied file into a chat. Try once at 60 fps on a large area and say if it stutters.
15. **Longshot on a page with sections that scroll:** open a page like the Salla order editor. Point at the product list and press Alt+Shift+S: the frame fits the list, and Auto captures the whole list while the page stays still. Drag the marker onto the page (not on a list): the page lights up as "Page", and dropping it captures the whole page. Click around on the panel while Auto runs: no text on the page gets highlighted. Drag and resize the frame: it follows smoothly.
16. **Video blur:** record a video that scrolls past an e-mail address (a mail or chat page). In the result window press **Hide personal info**: the address gets a box from the frame it appears, and the box follows it. Then pause elsewhere, drag a box over something, press **Follow it**, play it through, and **Save blurred copy**. Open the copy and check nothing private shows.
17. Uninstall from Settings > Apps > Installed apps → the shortcut, the folder and any Explorer entries are gone; your results are still where they were saved.
