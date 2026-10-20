# File Toolbox (working name)

A local Windows desktop utility for converting, editing and preparing files right from where you work: drag a file in Explorer or on the desktop, tap **Shift**, and drop it onto a wheel of actions. Everything runs on this computer. There are no accounts, no uploads and no subscriptions.

- Product direction: [`sead_idea.md`](sead_idea.md) (the seed: owner decisions and feature reference)
- What works today: [`docs/IMPLEMENTATION_STATUS.md`](docs/IMPLEMENTATION_STATUS.md)
- Engineering decisions and the drag-and-drop mechanism: [`docs/DECISIONS.md`](docs/DECISIONS.md)
- Milestones and preserved future ideas: [`docs/ROADMAP.md`](docs/ROADMAP.md)
- How to test: [`docs/TESTING.md`](docs/TESTING.md)

## Layout

| Path | Role |
|---|---|
| `src/Toolbox.Core` | Platform-neutral model: capability map, probing, outputs, jobs, settings, history |
| `src/Toolbox.Processing` | Local engines: libvips, PDFium, Skia, PDFsharp, OpenXML |
| `src/Toolbox.Cli` | `ftb` command-line runner (tests, scripting, future out-of-process worker) |
| `src/Toolbox.Windows` | Windows adapter: keyboard hook, OLE drop target, wheel, editors, tray |
| `tests/` | xUnit tests |
| `tools/DragHarness` | Automated real-Explorer drag test |

## Build

Requires the .NET 9 SDK on Windows 10/11 x64.

```bash
dotnet build FileToolbox.sln
```

```bash
src/Toolbox.Windows/bin/Debug/net9.0-windows/FileToolbox.exe
```
