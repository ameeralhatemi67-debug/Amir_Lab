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
9. Drag a scan or a photo of an Arabic letter, tap Shift twice, drop on **Text (OCR)** → a `.txt` beside it; open it and check names and numbers.
10. Drag a scanned PDF onto **Searchable** (Tools ring) → `… Searchable.pdf`; open it, search for an Arabic word and an English word, and copy a line.
