# Third-party components

Everything below runs locally. Versions are pinned in the project files. Re-check this list whenever a dependency changes.

The published package (`tools/publish.ps1`) carries this file, the libvips bundle's `THIRD-PARTY-NOTICES.md` and SkiaSharp's licence in `FileToolbox\licences\`. The libvips DLLs sit next to the program and can be replaced, as LGPL requires. **Before giving the package to anyone else,** add the full LGPL-3.0, GPL-3.0 and MPL-1.1 licence texts and the remaining packages' licence files to that folder. They are not in the NuGet packages, and this build is for the owner's own use.

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
| Windows SDK projection (`Microsoft.Windows.SDK.NET.Ref`, from the .NET SDK) and WinRT.Runtime (C#/WinRT) | 10.0.19041 | MIT | Calls Windows' built-in OCR (`Windows.Media.Ocr`). The recogniser and its language data are part of Windows and are not shipped by us. |

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

## Not included

Tesseract OCR (Apache-2.0) was planned for M7 but is not used; Windows' built-in recogniser replaced it ([DECISIONS.md](DECISIONS.md)). Development-only tools (`tools/DragHarness`, `tools/OcrEval`) use the same components as above.
