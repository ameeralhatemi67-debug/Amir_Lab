# File Toolbox (working name)

A local Windows desktop utility for converting, editing and preparing files right from where you work: drag a file in Explorer or on the desktop, tap **Shift**, and drop it onto a wheel of actions. Everything runs on this computer. There are no accounts, no uploads and no subscriptions.

- Product direction: [`sead_idea.md`](sead_idea.md) (the seed: owner decisions and feature reference)
- What works today: [`docs/IMPLEMENTATION_STATUS.md`](docs/IMPLEMENTATION_STATUS.md)
- Engineering decisions and the drag-and-drop mechanism: [`docs/DECISIONS.md`](docs/DECISIONS.md)
- Milestones and preserved future ideas: [`docs/ROADMAP.md`](docs/ROADMAP.md)
- How to test: [`docs/TESTING.md`](docs/TESTING.md)
- Arabic/English text recognition, measured: [`docs/OCR_EVALUATION.md`](docs/OCR_EVALUATION.md)
- Speed, memory and package size: [`docs/PERFORMANCE.md`](docs/PERFORMANCE.md)

## Install (current user, no administrator rights)

Build the package, then install it:

```bash
powershell -ExecutionPolicy Bypass -File tools/publish.ps1
```

```bash
powershell -ExecutionPolicy Bypass -File artifacts/publish/install.ps1 -Launch
```

- **Where it goes:** File Toolbox installs to `%LOCALAPPDATA%\Programs\FileToolbox`, with a Start Menu shortcut and an entry in Settings > Apps > Installed apps.
- **Uninstalling:** remove it from Installed apps, or run `uninstall.ps1` in the install folder.
  - Uninstalling also removes the Explorer menu, Send to and start-with-Windows entries if you switched them on.
  - It keeps your settings and history unless you pass `-RemoveUserData`.
  - It never touches the files File Toolbox produced.
- **Using it:** the app sits in the notification area. Drag a file in Explorer or on the desktop and tap **Shift**, or press **Ctrl+Alt+F** for the keyboard palette.
- **Text recognition:** this uses Windows' own OCR. Arabic needs the Arabic language with "Optical character recognition" installed (Settings > Time & language > Language & region).
- **Requirements:** Windows 10 1809 or later, x64. Tested on Windows 11 only.

## Layout

| Path | Role |
|---|---|
| `src/Toolbox.Core` | Platform-neutral model: capability map, probing, outputs, jobs, settings, history |
| `src/Toolbox.Processing` | Local engines: libvips, PDFium, Skia, PDFsharp, OpenXML |
| `src/Toolbox.Cli` | `ftb` command-line runner (tests, scripting, future out-of-process worker) |
| `src/Toolbox.Windows` | Windows adapter: keyboard hook, OLE drop target, wheel, editors, tray |
| `src/Toolbox.Windows.Ocr` | Windows' built-in OCR (`Windows.Media.Ocr`) behind the portable `IOcrEngine` |
| `tests/` | xUnit tests |
| `tools/DragHarness` | Automated real-Explorer drag test |
| `tools/OcrEval` | Arabic/English OCR evaluation (generated corpus + your samples, CER/WER) |
| `tools/Bench` | Idle cost, preview latency, time and peak memory of heavy operations |
| `tools/IconGen` | Regenerates `app.ico` from the glyph the app draws |
| `tools/publish.ps1`, `install.ps1`, `uninstall.ps1`, `test-install.ps1` | Packaging, per-user install and its sandboxed end-to-end test |

## Build

Requires the .NET 9 SDK on Windows 10/11 x64.

```bash
dotnet build FileToolbox.sln
```

```bash
src/Toolbox.Windows/bin/Debug/net9.0-windows10.0.19041.0/FileToolbox.exe
```

`FileToolbox.exe --quit` closes a running copy (as the tray's Quit does). `ftb` (in `src/Toolbox.Cli/bin/...` or the installed folder) runs the same conversions from the command line; `ftb help` lists the commands.
