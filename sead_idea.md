# Desktop File Toolbox — Project Seed

**File:** sead_idea.md  
**Document version:** 1.0  
**Created:** 20 October 2026  
**Status:** Canonical product concept and feature reference; implementation status is not implied.  
**Working description:** A local desktop toolbox for converting, editing, and preparing files directly from the user's existing workflow.  
**Companion reference:** VID_20261018_104256_286.mp4

> Build for Windows first, then macOS. The current product is for local use and processes files on the user's computer. Selling the application is a future possibility, not part of the current implementation.

## 1. Purpose and authority

This document preserves the original project idea, the reference video's interaction, the features discussed, and the owner's current decisions. It is intended to keep future development aligned with what we are building.

Use it as the stable product reference when creating implementation plans, design specifications, code, tests, and future feature proposals. Detailed task lists and engineering decisions should live in separate documents so that implementation progress does not obscure the original intent.

The owner will give the building agent the same reference video alongside this file. The agent should inspect that video before implementing the desktop interaction and editors.

This seed contains several kinds of information:

| Label | Meaning |
|---|---|
| Owner decision | An explicit instruction from the owner that governs the project. |
| Core requirement | A defining behavior or quality requirement for our application. |
| Planned improvement | A proposed part of the product direction, to be implemented in a sensible order. |
| Future candidate | An idea preserved for later evaluation; it is not automatically a first-version requirement. |
| Reference only | Information about the video or reference product, not a claim about our implementation. |

A listed feature is not evidence that it has been built. Maintain a separate implementation-status document that distinguishes working, partial, blocked, and future capabilities.

Later explicit owner instructions take precedence over this seed. Implementation convenience should not silently change its defining decisions.

## 2. Confirmed owner decisions

### 2.1 Windows first

The first working application must target Windows.

Validate the experience with real files dragged from Windows Explorer and the Windows desktop. Windows should receive the initial implementation, interaction refinement, local processing, and real-file testing.

Choose supported Windows versions and processor architectures based on the actual development and testing environment. Do not invent a broad compatibility promise before testing.

### 2.2 macOS second

A macOS application is part of the intended longer-term direction.

Keep file-processing logic, action definitions, recipes, and naming rules reusable where practical. Implement the macOS desktop integration after the Windows experience is dependable.

The later Mac version should feel appropriate to macOS. Keyboard conventions, native sharing, menus, permissions, and file handling may differ from Windows. Equivalent outcomes matter more than forcing identical platform-specific behavior.

Do not delay useful Windows work to build both native integration layers simultaneously.

### 2.3 Local use and local processing now

The current product is an installed desktop utility whose ordinary file operations run on the user's computer.

The core application should:

- Perform real conversions and edits locally.
- Work offline for its supported core operations.
- Save outputs to local destinations selected or authorized by the user.
- Store settings, recipes, and ordinary job history locally.
- Work without a mandatory account, hosted backend, subscription, or online activation.
- Avoid app-controlled uploads of source files, extracted text, thumbnails, or outputs.
- Avoid mandatory cloud AI, a large local language model, or a separate processing server.
- Keep diagnostic reporting local by default.

A user may choose a folder that another application synchronizes. That external behavior does not authorize this app to upload files itself.

Development tools may need dependency downloads. This is separate from the requirement that the finished core workflow can run locally without a live processing service.

“Local” describes the operating model. The result should still be a polished, functional application with dependable outputs.

### 2.4 Selling is deferred

Commercialization may be considered later.

Do not build payment collection, checkout, subscriptions, paid tiers, usage quotas, conversion credits, trial limits, customer accounts, license activation, or a sales backend as part of the current scope.

No price or business model is selected. Earlier discussion of selling the utility is retained only as a future possibility.

Routine engineering decisions should support a maintainable application without introducing commercial infrastructure prematurely.

### 2.5 Use the video together with this document

The companion video is an interaction and visual-quality reference. This document supplies the broader product direction and the owner's additional requirements.

Use both. The video's Mac environment does not override the Windows-first decision.

## 3. Product identity and purpose

### 3.1 Core idea

Create a desktop utility that helps someone turn an existing file into a usable result with very few interruptions.

The user starts with a file they are already working with, invokes a contextual menu near the pointer, chooses a relevant operation, adjusts settings when needed, and receives a new output.

The intended product combines:

1. File-format conversion.
2. Lightweight image and document preparation.
3. Focused editing tools.
4. Reusable workflows.
5. Clear output-quality and file-handling controls.

The circular menu is the signature entry point. Dependable results, useful defaults, and repeatable actions are the reason the application remains useful.

### 3.2 Product promise

**Prepare files for the next task quickly, locally, and predictably.**

Examples of that next task include:

- Uploading an attachment within a size limit.
- Preparing images for a website.
- Turning a PDF page into a report graphic.
- Cropping and framing a screenshot.
- Producing branded graphics in several dimensions.
- Extracting Arabic or English text from a scan.
- Applying the same preparation steps to a group of files.

### 3.3 Intended users

The first audience can include the owner, colleagues, office staff, creators, educators, and people who repeatedly prepare documents or graphics.

The initial product should be useful for an individual working locally. Organization-oriented presets and shared recipe files can grow from that foundation.

These are audience hypotheses, not a requirement to build separate role-based dashboards.

### 3.4 Working name

“Desktop File Toolbox” is a descriptive working label. A final product name, logo, and brand have not been selected.

Develop original branding and interface details while preserving the reference's useful interaction principles.

## 4. Companion video and evidence

### 4.1 Reference asset

**Filename:** VID_20261018_104256_286.mp4  
**Approximate duration:** 25.6 seconds  
**Visible environment:** macOS desktop  
**Completed workflow:** PDF page to JPG, then crop, then add a styled background.

The prior analysis confidently matched the interface to Tangerine for Mac using the developer's public materials. This is a reference-product identification, not a claim that the application was installed or independently benchmarked. [R1, R2]

The short clip does not establish performance on large files, reliability across applications, or the quality of every visible conversion.

### 4.2 What the building agent should inspect

Observe:

- Where the wheel appears relative to the pointer.
- How the dragged file remains visually associated with the action.
- Segment highlighting and selection feedback.
- How available actions change with file type.
- The transition from a wheel action to a focused editor.
- Crop handles, aspect-ratio choices, and dimension fields.
- Background presets and framing controls.
- Preview responsiveness.
- The creation of separate outputs.
- The repeated dragging required to complete a multi-step task.

If the video cannot be read in the agent's environment, retain the written requirements and report that visual inspection is incomplete. Do not claim to have reviewed inaccessible footage.

### 4.3 Demonstrated behavior

Timestamps are approximate.

| Time | What is demonstrated | Interpretation for our project |
|---|---|---|
| 00:01–00:03 | A PDF is dragged into a circular format menu and exported as a JPG. | Support real desktop invocation and a genuine conversion result. |
| Around 00:04 | The image has a different set of conversion options. | Actions must be relevant to the input. |
| Around 00:05 | An editing-tools wheel appears. | Keep conversion and editing accessible without opening a large workspace. |
| 00:06–00:10 | A crop editor is adjusted and applied. | Provide direct manipulation, presets, and understandable dimensions. |
| Around 00:11 | The cropped result appears as another file. | Preserve originals and make outputs easy to locate. |
| 00:14–00:18 | A background editor changes the framing and appearance of the image. | Provide a focused styling tool with a useful live preview. |
| 00:19–00:21 | Original and derived files remain visible. | Improve multi-step work without creating unnecessary intermediate clutter. |

Additional visible feedback includes colored active segments, a center action label, and a file-size value.

### 4.4 Visible conversion choices

These choices appear in the video. Their presence does not mean every conversion is demonstrated or should be implemented immediately.

| Input shown | Visible choices |
|---|---|
| PDF | DOCX, TXT, JPG, PNG |
| JPG/image | PNG, WebP, HEIC, TIFF, AVIF, BMP, PDF, DOCX |

Only PDF-to-JPG is completed in the clip.

For our product, every conversion must have a defined meaning. An image placed in a DOCX file and text recognized into an editable DOCX are different actions.

### 4.5 Visible tools

| Tool label | Evidence in the clip |
|---|---|
| Compress | Menu option visible. |
| Metadata | Menu option visible. |
| Edit | Menu option visible; adjustment types are not opened. |
| Annotate | Menu option visible. |
| Add BG | Demonstrated. |
| Crop | Demonstrated. |
| Redact | Menu option visible; content-removal behavior is not demonstrated. |

### 4.6 Crop-editor details

The demonstrated crop editor includes:

- Draggable boundaries and handles.
- A shaded area outside the selected region.
- Freeform cropping.
- Aspect ratios of 1:1, 16:9, 9:16, 4:3, and 3:4.
- Width and height fields in pixels.
- A preview and an apply action.

Preserve these capabilities in the product direction.

### 4.7 Background-editor details

The demonstrated background editor includes:

- Gradient presets.
- Solid-color choices.
- Adjustable padding.
- Adjustable corner rounding.
- Adjustable shadow.
- Output ratios including Auto, 1:1, 16:9, and 4:5.
- A preview and an export action.

“Add background” in this reference means placing and styling the image within a surrounding canvas. Subject cutout and background removal are separate future capabilities.

## 5. Broader reference-product catalog

This section preserves research context. It is not our first-version checklist.

At the research snapshot on 20 October 2026, Tangerine's website advertised 275 conversion paths, 26 tools, and six file actions. Conversion paths are transformations, not distinct formats. [R1]

### 5.1 Advertised format coverage

| Category | Publicly listed coverage |
|---|---|
| Images | JPG, PNG, WebP, HEIC, TIFF, AVIF, BMP; PDF export; SVG input. |
| Audio | MP3, WAV, FLAC, M4A, OGG, Opus, AIFF, WMA. |
| Video | MP4, MOV, MKV, WebM, AVI, WMV, GIF; MP3 export. |
| PDF outputs | DOCX, Pages, PPTX, XLSX, JPG, PNG, EPUB, TXT, Markdown. |
| Documents | DOCX, Pages, Markdown, TXT, EPUB; RTF/RTFD input; HTML export. |
| Presentations | PPT/PPTX to PDF or images; PDF to PPTX. |
| Spreadsheets | XLSX, ODS, CSV, TSV; PDF export; XLS input. |
| Subtitles | SRT, VTT; TXT export. |
| Archives | ZIP, TAR, GZIP; RAR input. |

These listings include input-only and output-only support. They do not establish every possible source-to-target pairing. [R1]

### 5.2 Advertised tools and workflow

The reference listing also describes:

- Image, video, audio, and PDF compression, including size targets.
- Photo adjustment, annotation, and redaction.
- Video trimming, cropping, speed changes, splitting, joining, and snapshots.
- Audio trimming, normalization, bleeping, and channel changes.
- Metadata inspection, editing, and removal.
- PDF merging, splitting, and page reorganization.
- Image-to-PDF creation, collages, and QR reading.
- Batch processing, progress feedback, and revealing completed files. [R3]

The developer additionally lists AirDrop, Float, Copy, Move, Copy Path, and Terminal actions, and describes processing locally and saving derived copies. [R2]

For our Windows application, platform-specific features such as AirDrop are not direct requirements. A later equivalent sharing action should follow the target operating system's capabilities.

### 5.3 What this means for our direction

Offline processing, batches, metadata removal, target-size compression, and basic PDF organization are established reference capabilities.

Our proposed differentiation centers on reusable multi-step actions, useful output presets, branded exports, measured Arabic/English handling, and predictable results.

Do not use the reference's marketing counts, pricing, trial behavior, or complete catalog as mandatory scope for this local project.

## 6. Core experience requirements

### 6.1 Desktop invocation

The normal flow should begin from a real file in Explorer or on the desktop.

The application stays available through an appropriate Windows tray presence. A large main window should not be necessary for common actions.

A configurable gesture activates a contextual wheel near the pointer. The reference uses Shift, but Windows shortcut behavior must be evaluated before selecting the final default.

### 6.2 Contextual radial menu

The wheel should:

- Offer actions appropriate to the actual dragged input.
- Keep common actions close to the pointer.
- Clearly highlight the intended selection.
- Show an understandable action name.
- Avoid appearing partially off-screen.
- Behave correctly with display scaling and multiple monitors.
- Support cancellation without changing the source.
- Keep favorite positions stable.

Aim for roughly four to six prominent actions in the primary wheel. More actions can appear in a secondary or searchable interface.

Context sensitivity should not constantly rearrange pinned favorites and break familiar movement patterns.

### 6.3 Alternative access

Provide accessible ways to use the same actions:

- Keyboard-accessible action list or command menu.
- A conventional drop zone.
- A tray entry for opening the tool.
- A suitable Explorer context-menu entry where practical.
- Escape to cancel.
- Clear labels, visible focus, sufficient contrast, adjustable target size, and reduced motion.

These are complementary entry points into the same local processing system.

### 6.4 Focused editors

Crop, resize, background styling, and similar tasks should open compact editors with meaningful controls and previews.

Keep simple operations quick. Advanced settings may be available without overwhelming the default experience.

Preserve the polished motion and immediate feedback that make the reference appealing. Visual refinement should support clarity and responsiveness.

### 6.5 Results and originals

Default behavior must preserve the source and create a new output.

Use clear, collision-safe names and make the destination visible. Offer actions such as opening the result, revealing it in Explorer, and repeating the operation.

Do not overwrite or move an original as an incidental effect of conversion.

Explicit file-management actions such as Move, if ever introduced, must be separate from conversion and have their own clear behavior.

### 6.6 Multi-step editing session

Allow the user to keep preparing a result before exporting:

**Convert or render → crop → adjust or annotate → style → export.**

Retain editable parameters during the session where practical. Save intermediate files only when the user requests them.

Use the original-quality input for final rendering so that repeated previews do not force repeated lossy exports.

## 7. Initial Windows capability focus

This focus establishes a dependable first implementation without deleting the broader vision.

| Area | Initial direction |
|---|---|
| Desktop access | Real Explorer/Desktop drops, contextual wheel, keyboard alternative, drop zone. |
| Image conversion | JPG, PNG, WebP; add HEIC when the shipped codec setup is validated. |
| PDF preparation | Page previews, selected-page rendering to images, images to PDF. |
| Basic editing | Crop, resize, rotate, compression, background styling. |
| Styling | Gradients, solid colors, padding, corners, shadow, aspect-ratio controls. |
| Work management | Progress, cancellation, small batches, per-file outcomes, output-folder settings. |
| Reuse | A small set of saved actions and branded presets. |
| File integrity | Source preservation, safe output naming, validation, recoverable failures. |
| Preferences | Activation gesture, pinned actions, destination, quality defaults, appearance. |

Metadata inspection/removal and raster annotation are natural early additions once their behavior is validated. Advanced redaction and document reconstruction require separate quality gates.

Unsupported actions should not appear as working features. Hide them or mark them clearly as unavailable, without using simulated success to imply real processing.

## 8. Planned improvements and differentiating features

### 8.1 Saved multi-step actions

This is a central improvement to the original interaction.

Let a user define, inspect, name, save, edit, duplicate, and reuse a sequence of operations. These saved sequences may be called actions, workflows, or recipes in the final interface.

The user should understand the steps and output before execution.

Example recipes:

| Recipe | Intended sequence |
|---|---|
| Prepare a report graphic | Select PDF page → crop → apply background style → add logo/caption → export PNG. |
| Website images | Resize to maximum width → convert to WebP → apply quality → remove selected metadata → rename → save. |
| Upload-ready attachment | Check permitted format → attempt size target within quality limits → verify output → report result. |
| Share document pages | Select pages → render images → apply naming pattern → save to a chosen folder. |
| Presentation screenshot | Crop → annotate → add branded framing → export landscape output. |

Recipe execution should use ordinary deterministic processing. A language model is not required.

### 8.2 Result-oriented presets

Help users specify the intended outcome:

- Prepare for email.
- Prepare for a website.
- Create a presentation graphic.
- Make text searchable.
- Export selected pages.
- Fit a chosen upload limit.

A preset can combine format, size, dimensions, page count, naming, and quality constraints.

Show the actual settings and verify the result. A target is a requested outcome, not an unconditional guarantee.

For example, a “portal attachment” preset might require a PDF below 2 MB with a particular filename pattern. Report which conditions were met and which were not.

### 8.3 Arabic and English document handling

Preserve Arabic/English support as an important product direction.

Potential capabilities include:

- Extracting text from images.
- Recognizing scanned documents.
- Handling mixed Arabic/English content.
- Creating searchable PDFs.
- Correct right-to-left interface and paragraph behavior where applicable.
- Arabic-safe filenames and sorting.
- Useful text export and copying.

Tesseract provides Arabic language data, which is a possible starting point for local recognition. Its availability does not establish accuracy on our document collection. [R9]

Evaluate connected letters, numerals, diacritics, reading order, tables, headings, multicolumn layouts, scan quality, and mixed scripts.

Promise only the workflows that have been tested. OCR accuracy and faithful editable-document reconstruction are different capabilities.

### 8.4 Brand presets and export packs

Allow users to save:

- Logos.
- Colors and gradients.
- Fonts where text is added.
- Padding.
- Corner and shadow treatments.
- Watermarks.
- Caption placement.
- Output dimensions and naming rules.

A single preparation session could export square, portrait, vertical, and landscape variants.

Provide a preview for each variant. Reframing should preserve important content and avoid silently cutting off text.

A department, project, or organization can have several local presets without requiring accounts or a team backend.

### 8.5 Clear quality and output information

Before final export, expose relevant information such as:

- Source and output dimensions.
- Actual size or clearly labeled size estimate.
- Selected pages or frames.
- Transparency changes.
- Text editability or searchability.
- Metadata changes.
- Compression and resizing decisions.
- Output destination.

Provide useful zoom and before/after inspection, especially for text-heavy images.

After processing, report actual results. Do not present a size estimate as a verified output measurement.

### 8.6 Useful batch processing

Batch handling should include:

- Clear applicability of an action to every selected file.
- Queued, running, completed, failed, and cancelled states.
- Per-file output size and destination.
- Understandable partial success.
- Retry of failed items.
- Cancellation that leaves completed outputs intact and identifies unfinished work.

Mixed inputs should not be silently skipped. Explain unsupported files or offer a compatible action.

Use bounded processing so large batches do not overwhelm the computer.

### 8.7 Local history and repeat actions

Record enough information to:

- Find recent outputs.
- Repeat a previous operation.
- Reuse settings.
- Identify failures.
- Understand which recipe created a result.

Ordinary history does not need to retain document contents. Include clear-history and sensible retention controls.

If Undo is offered, define its scope precisely. For example, removing a new output is different from reversing every possible later change to that file.

### 8.8 Clipboard and naming workflows

Early convenience additions may include:

- Paste an image from the clipboard into an editor.
- Repeat the last action.
- Rename outputs using page numbers, dimensions, dates, project names, or suffixes.
- Choose a default destination per recipe.
- Drag a completed output onward to another application.

Clipboard input should follow a deliberate user action rather than requiring continuous collection of unrelated clipboard history.

### 8.9 Metadata and sharing preparation

Allow inspection and selective removal of supported metadata.

Explain what was changed. Metadata removal alone does not remove identifying information visible in document content.

Combine chosen metadata rules with saved export actions where useful.

## 9. Conversion semantics and quality boundaries

### 9.1 Define the operation, not just the extension

Maintain an explicit capability map for supported input, action, output, settings, and limitations.

These operations must remain distinguishable:

| Operation | Expected result |
|---|---|
| PDF page rendering | A visual image of one or more selected pages. |
| PDF text extraction | Existing text recovered from a digitally generated PDF. |
| OCR | Recognized text from raster images or scanned pages. |
| Image in DOCX | A Word document containing the image. |
| Editable document reconstruction | Text and structure rebuilt as editable document elements. |
| Image-to-PDF | A PDF containing chosen images with defined ordering and page settings. |

Never imply that changing an extension creates meaningful conversion.

### 9.2 PDF page handling

For multipage PDFs:

- Let users choose pages or an explicit all-pages option.
- Preserve correct order.
- Use predictable page numbering in filenames.
- Expose render resolution or a useful quality preset.
- Explain whether the result is separate images, a PDF, or another chosen output.
- Do not silently export only the first page.

### 9.3 Transparency, animation, orientation, and color

Handle relevant input properties deliberately.

Examples:

- JPG export needs a defined background when the source contains transparency.
- Static export of an animated file needs a selected-frame policy.
- Photo orientation should match the displayed result.
- Color-profile handling should be tested for supported workflows.
- Resizing and cropping should show the resulting dimensions.

A conversion should not claim to be lossless when it removes content, changes dimensions, discards animation, or recompresses lossily.

### 9.4 Compression

Distinguish:

- Lossless optimization.
- Lossy quality reduction.
- Dimension reduction.
- Format changes.
- PDF-specific optimization.

Support reasonable limits on quality or dimensions when aiming for a file-size target.

If the requested size cannot be achieved within those limits, report that outcome and offer understandable choices.

### 9.5 Editable Office conversions

PDF-to-Word and other document-reconstruction features belong behind document-specific validation.

Even Microsoft describes PDF-to-Word conversion as potentially different from the original layout, especially for complex content. [R6]

Do not promise perfect conversion of every PDF, table, font, or Arabic document.

Broader Office support can use suitable local engines later, with supported cases and dependencies clearly documented.

### 9.6 Redaction

Keep annotation, obscuring, and secure redaction distinct.

A drawn rectangle over a PDF may leave underlying content available. True PDF redaction requires removal of the relevant content, with hidden-data handling considered separately. Adobe documents redaction and sanitization as distinct operations. [R7]

For our product:

- Raster redaction should permanently alter the exported pixels and flatten the result appropriately.
- Do not describe blur or pixelation as guaranteed secure removal.
- PDF redaction requires an appropriate engine and verification of extraction/removal behavior.
- Hidden metadata and embedded content need their own treatment.
- Keep advanced PDF redaction out of the initial working scope until these properties are validated.

Original-file preservation still applies: export the redacted result as a new file.

## 10. Windows integration and architecture direction

### 10.1 Prove native drag-and-drop first

A global input listener can reveal the overlay, but it does not by itself supply the files being dragged.

The Windows wheel must receive the actual native drag payload through the operating system's drop-target mechanism. Microsoft's IDropTarget documentation describes delivery of the data object, modifier state, pointer position, and allowed operations. [R4]

Test with real Explorer and desktop drags.

Do not substitute the current Explorer selection or a simulated browser drag for the actual payload.

### 10.2 Preserve copy semantics

Windows modifiers influence drag-and-drop effects. Shift can participate in move behavior. [R4]

Conversion must preserve the source:

- Accept an appropriate copy operation.
- Never report a successful move as a side effect of converting.
- Reject an incompatible operation safely.
- Make activation configurable.
- Ensure cancellation and leaving the wheel have clear behavior.

### 10.3 Keep platform integration separate from processing

Recommended conceptual components:

| Component | Responsibility |
|---|---|
| Desktop adapter | Activation, native drops, window behavior, tray integration, display coordinates. |
| Action catalog | Supported inputs, settings, outputs, and operation meaning. |
| Preview layer | Responsive visual feedback using suitable preview sizes. |
| Job queue | Progress, cancellation, concurrency limits, per-file status. |
| Processing workers | Image, PDF, OCR, and later media operations. |
| Output manager | Validation, safe naming, temporary writes, destination handling. |
| Local settings/history | Preferences, recipes, recent outcomes, and retention controls. |

This separation supports a later macOS adapter without requiring the same desktop code on both platforms.

### 10.4 Technology selection remains an engineering decision

No stack is mandated by this seed.

Plausible Windows approaches discussed include C# with WPF/WinUI and native integration, or Tauri with a web-based interface, Rust processing coordination, and the necessary native adapter.

Tauri separates interface code from an operating-system-capable core and supports bundled external processing programs. [R10, R11]

Select the approach after proving the critical interaction. A framework's general cross-platform claim is not evidence that the full gesture works automatically.

Image-processing libraries such as libvips can provide established operations, with actual format support depending on the codecs distributed. [R8]

Use suitable existing engines rather than attempting to invent file formats or codecs.

### 10.5 macOS implementation later

The future Mac adapter will need native drag destinations, window/focus behavior, appropriate permissions, and output access.

Apple documents native drag-destination handling and obtaining data from the actual dragging session. [R5]

Preserve reusable action definitions and processing behavior. Evaluate Mac-specific integration and distribution when that phase is reached.

## 11. Reliability and local data handling

### 11.1 File integrity

For normal conversion and editing:

- Read the input without mutating it.
- Validate the file and requested operation.
- Process outside the interface thread.
- Write to an appropriate temporary destination.
- Validate the produced output.
- Finalize a collision-safe new file.
- Explain failure without losing the source.

Avoid leaving corrupt partial outputs that appear to be successful results.

### 11.2 Resource behavior

The application should remain comfortable to leave running.

Measure:

- Idle CPU and memory use.
- Wheel responsiveness.
- Preview latency.
- Large-image and multipage-PDF memory behavior.
- Batch concurrency.
- Cancellation responsiveness.
- Temporary-file cleanup.

A tentative warm-app wheel target of about 200 milliseconds may be useful during testing. It is an engineering target, not an existing benchmark or an unconditional promise.

### 11.3 Local privacy

Keep file contents, extracted text, thumbnails, and recipes on the device for ordinary processing.

Use local diagnostics by default and avoid logging document contents unnecessarily.

If a future feature introduces external processing, it must be an explicit product decision with a clearly disclosed per-feature behavior. It must not silently change the current local model.

### 11.4 Permission failures

Handle unreadable inputs, read-only folders, unavailable destinations, and insufficient disk space with clear messages.

Offer a user-selected destination when the original folder cannot be used.

Do not request elevated privileges merely to avoid ordinary file-handling design.

## 12. Future candidates preserved for later

These ideas remain available without becoming current prerequisites.

| Candidate | Intended value | Boundary |
|---|---|---|
| PDF merge, split, and reorder | Everyday document preparation. | Validate page ordering and output integrity. |
| More image formats | Broader compatibility. | Add supported codec paths deliberately. |
| Shared recipe files | Reuse department/project settings. | Local import/export can come before any synchronization service. |
| Watched folders | Repeat an established workflow automatically. | Opt-in; avoid processing the app's own outputs repeatedly. |
| Video/audio processing | Conversion, trimming, extraction, and preparation. | Separate performance, codec, and packaging work. |
| Collages and QR reading | Convenient supporting tools. | Add when real use justifies them. |
| Archives | Packing and extracting files. | Requires its own file-handling scope and validation. |
| Broader Office/EPUB/subtitle support | Document and publishing workflows. | Treat every transformation as a defined capability. |
| Subject background removal | Prepare isolated subjects or product graphics. | Separate from decorative background styling. |
| Sensitive-content suggestions | Help locate content that may need removal. | Require review; do not promise perfect detection. |
| Natural-language action selection | Translate an instruction into visible processing steps. | Optional; ordinary execution remains deterministic and local. |
| Filename suggestions | Propose useful output names. | Preserve user control and avoid silent content changes. |
| Extension system | Specialized actions and integrations. | Consider after the action model is stable. |
| macOS application | A polished equivalent experience on Mac. | Follows the dependable Windows implementation. |
| Commercial release | Potential future distribution or revenue. | No current payment, account, quota, or licensing-activation work. |

If FFmpeg or other external engines are added, document the actual distributed components and their licenses. FFmpeg's licensing varies with build options. [R12] Deferring sales does not make dependency selection irrelevant.

## 13. Scope boundaries and unresolved choices

### 13.1 Explicitly deferred work

The current implementation does not require:

- A cloud conversion service.
- Account registration.
- A hosted team administration system.
- Payment or subscription infrastructure.
- Online license activation.
- Conversion credits or trial metering.
- A mandatory AI assistant or model server.
- Simultaneous Windows and macOS delivery.
- A mobile application.
- A public extension marketplace.
- Universal editable-document conversion.
- Every feature advertised by the reference product.

### 13.2 Choices to resolve during implementation

Resolve and record:

- Final application name and visual identity.
- Supported Windows versions and hardware.
- Framework and native-integration approach.
- Default activation gesture.
- Initial shipped codecs.
- Default export folder and naming pattern.
- Preview and export defaults.
- Supported Arabic OCR cases.
- Ordering of the preserved future capabilities.
- The point at which the Windows experience is ready for a Mac implementation.

Choose reasonable defaults for routine details. Do not treat these open implementation choices as authorization to change Windows-first, local processing, or deferred commercialization.

## 14. Directional implementation sequence

This seed preserves product direction rather than a fixed schedule.

A sensible dependency order is:

1. Inspect this document and the companion video.
2. Prove the real Windows drag-and-drop interaction.
3. Establish reliable local processing and output handling.
4. Reproduce the demonstrated PDF-to-image, crop, and background workflow.
5. Add useful image/PDF actions, accessibility alternatives, and batch results.
6. Introduce saved multi-step actions and branded presets.
7. Validate with representative files and actual local use.
8. Expand the justified capabilities.
9. Implement macOS using reusable processing and a native platform adapter.
10. Revisit selling only when the owner chooses to do so.

Earlier effort estimates were planning assumptions. They are not deadlines or product requirements in this seed.

Keep detailed milestones, tasks, estimates, and implementation decisions in separate planning documents.

## 15. Acceptance criteria

### 15.1 Windows desktop interaction

- The application accepts actual Explorer/Desktop file drags.
- The wheel appears near the pointer and stays within usable screen bounds.
- Focus and hit testing behave correctly.
- Display scaling and multiple monitors are tested.
- Unsupported payloads produce clear outcomes.
- Escape and cancellation preserve the source.
- Conversion does not accidentally move the original.
- Alternative keyboard/drop-zone access works.

### 15.2 Real file processing

- Every claimed action produces a genuine usable output.
- Outputs have the promised format and content.
- Original files remain unchanged during ordinary conversions.
- Existing outputs are not silently overwritten.
- Selected PDF pages and output counts match.
- Transparency, orientation, and dimensions are handled as documented.
- Failure leaves understandable state and no misleading success result.

### 15.3 Local operation

- Supported core operations complete without a live network connection.
- No account or payment is required.
- No source files or extracted contents are uploaded by the app.
- Settings, recipes, and history are local.
- Heavy work does not freeze the interface.

### 15.4 Representative test collection

Include:

- Transparent images.
- Rotated phone photos.
- Large images.
- Multipage PDFs.
- Scanned PDFs.
- Digitally generated PDFs.
- Arabic/English filenames.
- Mixed-direction text.
- Duplicate names.
- Corrupt or unsupported inputs.
- Read-only destinations.
- Cancelled batches.
- Files near the chosen practical size limits.

Expand tests to resolve concrete quality risks as capabilities are added.

### 15.5 Product usefulness

Evaluate ordinary tasks such as:

- Convert and shrink an image.
- Export a chosen PDF page.
- Prepare a screenshot for a report.
- Apply a saved action to a batch.
- Create several branded output variants.

Observe task completion, mistakes, output quality, and repeated use. Preserve the short, focused interaction as the feature set grows.

## 16. Instructions for the building agent

1. Read this document fully and inspect VID_20261018_104256_286.mp4.
2. Treat Windows-first, macOS-second, local operation, and deferred selling as fixed current decisions.
3. Build a real desktop utility with real file transformations.
4. Preserve the circular desktop interaction and focused editors as defining parts of the experience.
5. Keep the interface polished, responsive, and accessible.
6. Preserve the original files and explain output behavior.
7. Keep conversion semantics precise.
8. Use the broader reference catalog as context, not an automatic first-release checklist.
9. Preserve planned improvements and future candidates in project documentation even when they are not yet implemented.
10. Prove native dragging before committing to a large interface implementation.
11. Keep business infrastructure and mandatory cloud dependencies outside the current scope.
12. Record stack choices, tradeoffs, implementation status, and detailed plans separately.
13. Describe incomplete or unsupported features honestly.
14. Validate with actual files and the target operating system.
15. Keep later changes aligned with the core promise: fast, local, predictable preparation of files.

## 17. References and provenance

The source review was performed on 20 October 2026. External product catalogs and framework details can change; re-check them before relying on a specific version, codec, or distribution rule.

The video is the primary visual reference. External sources support feasibility and reference-product context; they do not override the owner's decisions.

- **[V1] Supplied reference video:** VID_20261018_104256_286.mp4. Provided separately by the owner to the building agent.
- **[R1]** [Tangerine official website and format catalog](https://tangerineformac.com/)
- **[R2]** [Tangerine developer press kit](https://tangerineformac.com/press)
- **[R3]** [Tangerine App Store listing](https://apps.apple.com/us/app/tangerine-file-converter/id6813034065)
- **[R4]** [Microsoft — IDropTarget::DragEnter](https://learn.microsoft.com/en-us/windows/win32/api/oleidl/nf-oleidl-idroptarget-dragenter)
- **[R5]** [Apple — Dragging Destinations](https://developer.apple.com/library/archive/documentation/Cocoa/Conceptual/DragandDrop/Concepts/dragdestination.html)
- **[R6]** [Microsoft — Opening PDFs in Word](https://support.microsoft.com/en-us/word/opening-pdfs-in-word)
- **[R7]** [Adobe — Redaction and sanitization](https://helpx.adobe.com/acrobat/desktop/protect-documents/redact-pdfs/redacting-sanitizing.html)
- **[R8]** [libvips official project](https://github.com/libvips/libvips)
- **[R9]** [Tesseract official trained-data documentation](https://tesseract-ocr.github.io/tessdoc/Data-Files.html)
- **[R10]** [Tauri process model](https://v2.tauri.app/concept/process-model/)
- **[R11]** [Tauri external-binary support](https://v2.tauri.app/develop/sidecar/)
- **[R12]** [FFmpeg license information](https://www.ffmpeg.org/legal.html)

## 18. Decision log

| Date | Decision |
|---|---|
| 20 October 2026 | Preserve the original concept and discussed features in sead_idea.md. |
| 20 October 2026 | Build Windows first, then macOS. |
| 20 October 2026 | Keep the current application local, with on-device core processing. |
| 20 October 2026 | Defer selling and associated business infrastructure. |
| 20 October 2026 | Supply the same reference video to the building agent. |

