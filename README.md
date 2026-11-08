# Amir Lap

Your desktop tools in one place: a local Windows tray app, in the spirit of PowerToys, with one window where each tool can be switched on or off, given its own keyboard shortcuts and set up. Everything runs on this computer. There are no accounts, no uploads and no subscriptions.

| Tool | What it does | Shortcut |
|---|---|---|
| **File Toolbox** | Convert, edit and prepare files right from where you work: drag a file in Explorer or on the desktop, tap **Shift**, and drop it onto a wheel of actions. | Ctrl+Alt+F opens its window |
| **Longshot** | Scrolling screenshots: draw a box, then press Extend (or Auto, or Follow while you scroll) to grow it downward. Saved to Pictures\Screenshots and copied to the clipboard. | Ctrl+Alt+S, or Ctrl+Alt+Shift+S after a delay |

- Product direction: [`sead_idea.md`](sead_idea.md) (the seed: owner decisions and feature reference). The move to Amir Lap is recorded in [`docs/DECISIONS.md`](docs/DECISIONS.md).
- What works today: [`docs/IMPLEMENTATION_STATUS.md`](docs/IMPLEMENTATION_STATUS.md)
- Engineering decisions and the drag-and-drop mechanism: [`docs/DECISIONS.md`](docs/DECISIONS.md)
- Milestones, the Amir Lap phases and preserved future ideas: [`docs/ROADMAP.md`](docs/ROADMAP.md)
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

- **Where it goes:** Amir Lap installs to `%LOCALAPPDATA%\Programs\AmirLap`, with a Start Menu shortcut and an entry in Settings > Apps > Installed apps.
- **Coming from File Toolbox:** the installer replaces it: the old program folder, shortcut and Installed apps entry are removed. Your settings, history, recipes and brand styles are copied into Amir Lap the first time it starts, and the old data folder is left untouched. Explorer menu, Send to and start-with-Windows carry over if you had them on.
- **Uninstalling:** remove it from Installed apps, or run `uninstall.ps1` in the install folder.
  - Uninstalling also removes the Explorer menu, Send to and start-with-Windows entries if you switched them on.
  - It keeps your settings and history unless you pass `-RemoveUserData`.
  - It never touches the files the tools produced.
- **Using it:** the app sits in the notification area. Click its icon for the Amir Lap window (Dashboard, a page per tool, General); right-click for each tool's menu.
  - **File Toolbox:** drag a file in Explorer or on the desktop and tap **Shift** for the wheel, or press **Ctrl+Alt+F** for its keyboard palette.
  - **Longshot:** press **Ctrl+Alt+S** and drag a box, then use the panel beside it: **Extend** adds the next part, **Auto** keeps going to the bottom, **Follow** adds as you scroll, and **Done** saves and copies. Esc stops Auto and Follow.
- **Text recognition:** this uses Windows' own OCR. Arabic needs the Arabic language with "Optical character recognition" installed (Settings > Time & language > Language & region).
- **Requirements:** Windows 10 1809 or later, x64. Tested on Windows 11 only.

## Layout

| Path | Role |
|---|---|
| `src/Toolbox.Core` | Platform-neutral model: capability map, probing, outputs, jobs, settings (including per-tool switches and shortcuts), history |
| `src/Toolbox.Processing` | Local engines: libvips, PDFium, Skia, PDFsharp, OpenXML, and Longshot's stitching (`Capture/`) |
| `src/Toolbox.Cli` | `ftb`, File Toolbox's command-line runner (tests, scripting, future out-of-process worker) |
| `src/Toolbox.Windows` | Windows app (`AmirLap.exe`): the Amir Lap window and tool registry (`Home/`, `Tools/`), shortcuts, tray, File Toolbox's keyboard hook, OLE drop target, wheel and editors, and Longshot's capture windows and session (`Longshot/`) |
| `src/Toolbox.Windows.Ocr` | Windows' built-in OCR (`Windows.Media.Ocr`) behind the portable `IOcrEngine` |
| `tests/` | xUnit tests |
| `tools/DragHarness` | Automated real-Explorer drag test |
| `tools/OcrEval` | Arabic/English OCR evaluation (generated corpus + your samples, CER/WER) |
| `tools/Bench` | Idle cost, preview latency, time and peak memory of heavy operations |
| `tools/IconGen` | Regenerates `app.ico` from the glyph the app draws |
| `tools/LongshotPrototype` | The Python prototype Longshot was ported from, with its live-test tools (reference only, not shipped) |
| `tools/publish.ps1`, `install.ps1`, `uninstall.ps1`, `test-install.ps1` | Packaging, per-user install and its sandboxed end-to-end test |

## Build

Requires the .NET 9 SDK on Windows 10/11 x64.

```bash
dotnet build FileToolbox.sln
```

```bash
src/Toolbox.Windows/bin/Debug/net9.0-windows10.0.19041.0/AmirLap.exe
```

`AmirLap.exe --quit` closes a running copy (as the tray's Quit does), `--background` starts it quietly in the tray, and `--palette` opens File Toolbox's window directly. `ftb` (in `src/Toolbox.Cli/bin/...` or the installed folder) runs the same conversions from the command line; `ftb help` lists the commands.
