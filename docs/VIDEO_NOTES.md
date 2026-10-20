# Reference video notes

`VID_20261018_104256_286.mp4` (25.6 s, 1080×1920 portrait screen recording of a macOS desktop; the interface matches Tangerine for Mac). I inspected it frame by frame on 20 Oct 2026, with extracted frames at 2–5 fps and full-resolution crops of the screen area. The video is not stored in the repository.

| Time | Observation | What we took from it |
|---|---|---|
| 0:01 | `Page 001.pdf` is picked up from the desktop. While it is dragged, a small wheel scales up from the pointer and the file's drag ghost stays under the cursor (green "+" copy badge). | The wheel appears at the pointer during a real drag; the copy semantics are visible. |
| 0:01–0:02 | PDF ring: **DOCX** (top), **JPG** (right), **PNG** (bottom), **TXT** (left). The centre pill shows **"1.1 MB"**; while a segment is hovered, the pill shows its label. The hovered tile fills solid red-orange. | Idle centre text = size; hover text = action. Strong, flat highlight. |
| 0:02–0:03 | Drop on JPG → a glass toast **"✕ Converting to JPG — 1 of 1 saved"** with a thin progress bar. `Page 001.jpg` appears beside the PDF. | Immediate conversions report progress plus a count; the output sits next to the source. |
| ~0:04 | Dragging the JPG: an 8-segment ring **PNG, WEBP, HEIC, TIFF, AVIF, BMP, PDF, DOCX** (clockwise from top). | Context-specific ring per input type. |
| ~0:04.6 | Without the pointer moving, the ring turns into a 7-segment tools ring: **COMPRESS, METADATA, EDIT, ANNOTATE, ADD BG, CROP, REDACT**, each with an icon above an uppercase label. The pill reads "1.2 MB". | A second key tap switches rings (we inferred this from the stationary pointer). |
| 0:06–0:10 | **Crop Image** window: the image with an orange frame, 8 handles, and a darkened area outside the crop. Aspect choices **Free · 1:1 · 16:9 · 9:16 · 4:3 · 3:4**; **W 2550 H 1877 px** fields (2550 = letter width at 300 DPI); **Reset** and **Apply**. Choosing 1:1 snaps the frame and updates H to 2550. | A focused editor with direct manipulation and numeric fields; PDF pages render at 300 DPI by default. |
| ~0:11 | `Page 001 Cropped.jpg` appears. | The suffix naming pattern. |
| 0:14–0:18 | **Add BG** window: live preview; *Background* row of gradient presets plus an image-picker button; a row of solid colours plus a colour well; **Padding 160→609 px**, **Corners 184 px**, **Shadow 24 px** sliders; **Ratio Auto · 1:1 · 16:9 · 4:5**; **Reset** and **Save with Background**. | Our Background tool has the same controls, plus 9:16. |
| 0:19–0:21 | The desktop shows `Page 001.pdf`, `Page 001.jpg`, `Page 001 Cropped.jpg`, `Page 001 Cropped With Background.png`. | The friction we improve on: each step needs a new drag and leaves an intermediate file. Our Studio chains the steps without intermediates. |
| 0:21–0:25 | Outro card. | — |

Editor windows use warm translucent "vibrancy" from macOS. On Windows we use our own neutral glass with the system accent colour.
