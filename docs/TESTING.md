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
- `CaptureSessionTests` (Windows) runs Longshot's whole capture loop (Extend, Auto, Follow, Undo, end detection, the smaller-step retry) against a simulated page and scroller. The real mouse and screen are never touched.
- `ScreenCaptureTests` reads a 64×64 corner of the real screen (read-only) and checks where the panel goes.
- `HomeWindowTests` builds every page of the Amir Lap window, plus Longshot's panel, result window and area chooser, off-screen in light and dark, without showing anything or starting a tool. To look at them, set `AMIRLAP_SNAPSHOT_DIR` to a folder; each one is saved there as a PNG:

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

Install with `artifacts/publish/install.ps1 -Launch` (or run the Debug build). The app sits in the notification area. Then:

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
11. Uninstall from Settings > Apps > Installed apps → the shortcut, the folder and any Explorer entries are gone; your results are still where they were saved.
