# Amir Lap

Your desktop tools in one place: a local Windows tray app, in the spirit of PowerToys, with one window where each tool can be switched on or off, given its own keyboard shortcuts and set up. Everything runs on this computer. There are no accounts, no uploads and no subscriptions.

| Tool | What it does | Shortcut |
|---|---|---|
| **File Toolbox** | Convert, edit, prepare and share files right from where you work: drag a file in Explorer or on the desktop, tap **Shift**, and drop it onto a wheel of actions, or onto its centre to preview it (a full PDF viewer and editor: search, outline, page colours, split, present; text, pen, highlighter, signatures, pages, dark copy). | Shift while dragging a file |
| **Longshot** | Scrolling screenshots of a whole page, a part that scrolls on its own (a chat, a list, a side panel) or any area you draw. It starts on what's under the pointer; press Auto (or Extend, or Follow while you scroll). Saved to Pictures\Screenshots and copied to the clipboard. | Alt+Shift+S (again to save), or Ctrl+Alt+Shift+S after a delay |
| **Pulse** | A glass dock on any edge of the screen: how much of Claude's and Codex's limits is used (read from their own files on this PC), what Claude Code is doing, and your shortcuts (ChatGPT, Longshot, File Toolbox, a focus timer, a note, a colour picker with a magnifier and a Pro window of codes, steps, contrast and history, a folder, an app or website, PC status). It can stay, slide away, fade, or wait as a thin line you click (Tap). | None by default ("Show or hide the dock" can be given one) |

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
- **Using it:** the app sits in the notification area. Click its icon for the Amir Lap window (Dashboard, a page per tool, General, About); right-click for each tool's menu. The window is white or black with glass panels, can be resized, maximized and snapped like any Windows window, and switches between English and Arabic (right-to-left) from its title bar or General.
- **File Toolbox:** drag a file and tap Shift: a frosted-glass wheel opens around the pointer (tap Shift again for the next ring). Drop the file on an option, or on **Share** (bottom-right) to send it with Quick Share, or Windows' share sheet if Quick Share isn't installed. File Toolbox has no keyboard shortcut; its window opens from the tray or the Dashboard.
  - **File Toolbox:** drag a file in Explorer or on the desktop and tap **Shift** for the wheel, or press **Ctrl+Alt+F** for its keyboard palette.
  - **Longshot:** point at what you want and press **Alt+Shift+S**: the rest of the screen dims, a frame appears on the part that scrolls under the pointer (or the whole window) and the panel beside it. **Auto** scrolls to the end, **Extend** adds one part, **Follow** adds as you scroll, and **Done** (or Alt+Shift+S again) saves and copies; the panel shows the whole capture growing.
    - **Window / Scroll area / Region** at the top of the panel choose what to capture: the whole window, a part that scrolls (point at it and click; it lights up as you hover), or any area you drag out. Esc goes back.
    - When only part of the window scrolls (a chat beside a sidebar, a list in a page that stays put), Longshot finds that part by itself and the frame glides onto it.
    - The round marker in the frame is where the wheel turns: drag it onto another part to scroll that instead. Drag the frame's corners to resize it, or its Move grip to move it. Esc stops Auto and Follow.
- **Pulse:** the dock sits on the right edge at first. Drag its dots to slide it along the edge or onto another edge, and its inner edge to make it bigger or smaller. Hover a tile for its card, click to open, right-click the dock to switch tile sets. Codex's limits show by themselves once you have used Codex; for Claude, press **Connect** on the Pulse page (it adds a status line and hooks to Claude Code's settings; **Remove** takes them out again).
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
