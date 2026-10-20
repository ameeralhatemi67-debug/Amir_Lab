# Performance and resource use

_Measured 20 Oct 2026 on the development PC: Windows 11 build 26300, Ryzen 5 6600H (6 cores / 12 threads), 1920×1080 at 125%. Release build. Reproduce with `tools/Bench` (see TESTING.md)._

## Idle (the app waiting in the notification area)

| Measure | Value |
|---|---|
| CPU, steady state (2 min, after 1 min warm-up) | **0.08% of one core** |
| CPU, first minute after start-up | 0.34% of one core (.NET finishing JIT tiering) |
| Working set | 168–170 MB |
| Private memory | 112–114 MB, flat over time (no growth) |
| Threads | 15 |
| `--quit` to process exit | 0.6 s |

- **Loaded at start-up:**
  - libvips. It is initialised so the wheel knows immediately whether AVIF encoding is available, and its untrusted loaders are blocked before any file is opened.
  - The Windows SDK projection, which lists the OCR languages.
- **Loaded on first use:** PDFium and Skia.
- **Possible saving:** caching the feature checks would let libvips and the SDK projection load lazily too, which would likely reduce idle memory further. This is on the roadmap.
- **Nothing polls while idle:** the drag timers run only during a drag. The keyboard hook does no work in its callback; it posts the event and returns.

## Interaction

| Measure | Value |
|---|---|
| Activation key → wheel visible | ~30 ms warm; 120–150 ms for the first drag after start-up (target ≤ 200 ms) |
| Studio: open a 24 MP photo | 347 ms (decoded once into memory) |
| Studio: first preview | 111 ms |
| Studio: preview after each edit (≤1600 px proxy, adjustments plus background on/off) | median 88 ms, worst 162 ms |

## Heavy operations

Each operation ran in a fresh process. "Peak" is that process's peak working set; the desktop app runs at most 4 such items in parallel, depending on CPU count.

| Operation | Time | Peak memory | Result |
|---|---|---|---|
| 48 MP PNG (8000×6000) → WebP | 61.9 s | 770 MB | 13.1 MB |
| 24 MP JPEG (6000×4000) → PNG | 3.3 s | 166 MB | 42.2 MB |
| 60-page PDF → JPG at 300 DPI | 15.7 s (0.26 s/page) | 201 MB | 60 files, 63 MB |
| 60-page PDF → TXT | 0.2 s | 57 MB | 197 KB |
| Studio export, 24 MP + background → PNG | 4.0 s | 515 MB | 6240×4240, 42.4 MB |
| OCR: A4 page at 300 DPI → TXT (Arabic + English models) | 4.2 s | 223 MB | 3.3 KB |
| OCR: 5 scanned pages → searchable PDF | 20.5 s (4.1 s/page) | 349 MB | 4.1 MB |

### Findings

- **WebP for very large images is the slow case.** The test image is gradients plus strong noise, which is the worst case for any encoder. libwebp needs the whole picture in memory, so 48 MP peaks at 770 MB. Real photos encode faster, but very large WebP output is still the slowest conversion. Ideas for later are on the roadmap: a lower effort setting above 24 MP, or a warning before starting.
- **Memory stays bounded by streaming.** PDF pages are rendered and saved one at a time, so a 60-page export peaks at about 200 MB, the same as a single page. Huge pages are capped at 60 MP and the lower DPI is reported.
- **OCR costs about 2 s per recognition of a 300 DPI page.** The default `ara+eng` runs both models, about 4 s per page. Choosing a single language halves that.

## Package

| | Size |
|---|---|
| Self-contained folder (`tools/publish.ps1`) | 235 MB, 292 files |
| Zip | 93 MB |
| Largest parts | .NET + WPF/WinForms runtime; Windows SDK projection 24 MB (OCR only); OpenXML 22 MB; libvips 18 MB; Skia 12 MB; PDFium 7 MB |

- **ReadyToRun is skipped for the two largest rarely used assemblies,** the Windows SDK projection and OpenXML; compiling them ahead of time would roughly triple their size.
- **Third-party native debug symbols (110 MB) are not shipped.**
- **A framework-dependent build is smaller** but needs the .NET 9 Desktop Runtime installed (`publish.ps1 -FrameworkDependent`).
