# Third-party components

Everything below runs locally. Versions are pinned in the project files. Re-check this list whenever a dependency changes.

| Component | Version | Licence | How it is used |
|---|---|---|---|
| .NET 9 runtime, WPF, WinForms | 9.0 | MIT | Application runtime and UI |
| NetVips | 3.2.0 | MIT | .NET binding for libvips |
| NetVips.Native.win-x64 (libvips and bundled libraries) | 8.18.7 | LGPL-3.0-or-later (bundle) | Image decoding, encoding and processing. **Dynamically linked** (`libvips-42.dll`), so it can be replaced by the user, which satisfies LGPL. |
| SkiaSharp (+ HarfBuzzSharp) | 4.153.1 | MIT | Composition: backgrounds, annotation, redaction, text shaping |
| PDFium (bblanchon.PDFium.Win32) | 158.0.8111 | Apache-2.0 (PDFium: BSD-3-Clause / Apache-2.0) | PDF rendering and text extraction |
| PDFsharp | **6.2.4 (pinned)** | MIT | PDF creation. **6.5.0 and later are AGPL-3.0 with a licence check; do not upgrade without a licensing decision.** |
| DocumentFormat.OpenXml | 3.5.1 | MIT | DOCX creation |
| MetadataExtractor | 2.9.3 | Apache-2.0 | Metadata inspection |

## Libraries inside the libvips bundle

From `THIRD-PARTY-NOTICES.md` in the NetVips.Native package:

| Library | Licence |
|---|---|
| libvips, glib, libheif, librsvg, pango, fribidi, libexif, proxy-libintl | LGPL-3.0 |
| aom (AV1/AVIF) | BSD-2-Clause + Alliance for Open Media Patent License 1.0 |
| cairo | MPL-1.1 |
| mozjpeg | zlib / IJG / BSD-3-Clause |
| libpng, libtiff, fontconfig, freetype | permissive (BSD-like) |
| libwebp, highway | BSD-3-Clause |
| cgif, expat, harfbuzz, lcms, libffi, libnsgif, libultrahdr, libxml2, pixman | MIT |
| libimagequant, libarchive | BSD-2-Clause |
| zlib-ng | zlib |

No HEVC/H.265 codec is shipped. HEIC input relies on codecs the user installs from Microsoft.

## Planned (not yet included)

| Component | Licence | Milestone |
|---|---|---|
| Tesseract OCR 5 (sidecar) + `eng`/`ara` tessdata | Apache-2.0 | M7 |
