# Testing

## Automated

```bash
dotnet test FileToolbox.sln
```

Unit and integration tests cover the Core rules (probing, naming, collisions, outputs, jobs) and the Processing engines against generated fixtures.

### Real-drag harness

The harness moves the **real mouse and keyboard** for a few seconds per scenario, so keep hands off while it runs.

```bash
dotnet build FileToolbox.sln
```

```bash
tools/DragHarness/bin/Debug/net9.0-windows/DragHarness.exe
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

## Manual checklist (owner)

Run `FileToolbox.exe` (it sits in the notification area), then:

1. Drag a PDF from File Explorer, tap **Shift** mid-drag → the wheel appears at the pointer. Drop on **JPG**.
2. Same from the **Desktop**.
3. Drag an image, tap Shift twice → the **Tools** ring.
4. Drag several files → the centre shows the count and total size.
5. Press **Esc** during a drag with the wheel open → the wheel closes and nothing changes.
6. Open the wheel, then move away from it and drop somewhere else → normal Explorer behaviour resumes.
7. Hold Shift for a whole Explorer drag *without* the wheel → Explorer's usual "Move" behaviour, which is not ours.
8. Try at another display scaling and on a second monitor if you have one, and note any misplacement.
