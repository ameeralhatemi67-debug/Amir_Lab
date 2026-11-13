# Lessons

Mistakes that cost a release, what caused them, and the rule that keeps them from coming back. Read this before building a new window or panel.

## Glass that grows the window

**Seen:** three times so far.
- **0.5.0, Pulse's dock:** it grew until a frosted pane covered the screen and nothing could be clicked.
- **Longshot's panel:** the owner reports the same stretching.
- **0.5.5, the colour picker's Pro window:** it opened as wide as the whole laptop screen (owner's video, 13 Nov 2026). The panel sat at one side, and blurred "glass" filled the rest.

**Cause:**
- The frosted glass is a picture of the screen behind the window, blurred, at one picture pixel per screen pixel. At 125% scaling that picture is 1.25× the window's size in DIPs.
- WPF's `Image` asks for its picture's size when measured. So when a window sizes itself to its content (`SizeToContent`) and the glass counts as content, every refresh makes the window bigger. The next picture is bigger again, until the window reaches the screen's edge.
- The dock's fix (0.5.1) went into its own class (`DockBackdrop`) and not into the shared `ScreenBackdrop`. So the next window that used `ScreenBackdrop` with `SizeToContent` grew the same way.

**Fix (0.5.6):** `ScreenBackdrop` takes no room. `MeasureOverride` returns 0 × 0, and `ArrangeOverride` stretches the picture over whatever the window already is. Both backdrop classes now carry it.

**Rules:**
- Anything that shows a picture of the screen behind a window must never be measured for its picture. It fills the space; it never asks for space.
- Fix a shared cause in the shared class. A fix in one copy leaves the others broken.
- A window that sizes to its content needs a test that lays it out with a large backdrop picture and checks that its size stays the content's size: `HomeWindowTests.RenderColour`.

## Borrowing the pointer turns clicks into drags

**Seen:** owner's video, 13 Nov 2026. The finished long screenshot had the page's text highlighted in blue.

**Cause:**
- To scroll, Longshot moved the real pointer to the scroll point, turned the wheel, waited about a tenth of a second and moved it back.
- The owner was clicking Extend on the panel. A press landing in that moment went to the page under the scroll point. The pointer jumping back while the button was still down made it a drag from there to the panel, which selected the text between them.

**Fix (0.6.1):**
- An area that scrolls on its own is scrolled through UI Automation, so the pointer is never touched.
- Where the wheel is still needed, Longshot waits until no mouse button is held, and a press during the borrow is swallowed with its release (`PointerGuard`).

**Rules:**
- Never move the user's pointer to make something happen if there is another way.
- If the pointer must be borrowed, check that no button is held and hold back clicks while it is away.

## A reported position can lag behind the screen

**Seen:** the first live run of the nested-page test, 14 Nov 2026. A section was captured only to row 19 of 120.

**Cause:** right after a scroll, Edge sometimes still reported the old scroll percent through UI Automation; the accessibility data catches up a moment later. Longshot read "+0%" and took it for the end, though the picture had moved 199 px.

**Fix:** Longshot remembers where it put the area itself, and judges "nothing moved" (the end) by the picture.

**Rule:** another app's reports about its own state are hints. Check them against what the screen shows.

## Windows' media pipeline: three traps

**Seen:** building Longshot video, 13 Nov 2026. All three were caught by `VideoRecorderTests` before the owner saw them, but each one cost a round of guessing.

**1. Raw frames are upside down.**
- **Cause:** raw RGB frames given to Windows' encoder are read bottom row first (Media Foundation's RGB32 always is). Screen captures are top row first.
- **Fix:** `ScreenRecorder.FlipRows` turns each frame over before it is queued.
- **Rule:** a test that reads the video back must check which way up it is (different colours top and bottom), not only that a file was written.

**2. `TrimStopTime` is a position, not an amount.**
- **Cause:** `MediaTranscoder.TrimStopTime` was read as "cut this much off the end", as the documentation suggests. The transcoder took it as the time to stop at, so the trimmed copies came out empty ("no samples processed").
- **Fix:** pass the end position itself, or zero to keep to the end.

**3. Recordings report a size of 0 × 0.**
- **Cause:** a file written from a `MediaStreamSource` reports a 0 × 0 picture to `MediaClip`, and an encoding profile built from it fails with a bare E_POINTER.
- **Fix:** take the size from the first frame when the reported one is zero (`VideoFiles.TrimAsync`). `MediaComposition.RenderToFileAsync` also failed with a bare COM error in this desktop app; the transcoder works.

**Rules:**
- Treat every number Windows' media classes report about our own recordings as unverified until read back.
- Test media code end to end on made-up frames with known colours and timing, and check the result frame by frame.

## A see-through window the size of the screen is slow

**Seen:** 0.5.5, the colour picker's magnifier. It lagged behind the pointer and left ghosts (owner's video, 13 Nov 2026).

**Cause:**
- The picker was a layered (`AllowsTransparency`) window over the whole screen, with the magnifier drawn inside it.
- Windows redraws a layered window completely whenever anything in it changes, so every mouse move pushed a full-screen picture.
- On top of that, every move read the screen again with GDI.

**Fix (0.5.6):**
- The screen is photographed once when picking starts and shown frozen in an ordinary window, which the GPU draws cheaply.
- The magnifier moves once per frame (`CompositionTarget.Rendering`) and only when the pointer moved. Its pixels come from the photo in memory.
- Colours are exact, and hover effects underneath can't change what is picked.

**Rules:**
- Keep layered (see-through) windows small. Anything that follows the pointer across the whole screen belongs in an ordinary window over a frozen photo, or in a small window of its own that is moved.
- Drive pointer-following visuals from the frame clock, not from every mouse message.

**Seen again:** Longshot's frame and pick highlight, which were layered windows the size of the capture area (owner's video, 13 Nov 2026). Dragging and resizing the frame lagged, and its breathing glow kept copying the whole picture.

**Fix (0.6.1):** `SeeThrough`. They are ordinary windows whose frame is extended over the whole window, with no backdrop, so the desktop compositor draws their see-through pixels with no copy back. A window region (`WindowShape`) makes the inside of the frame a hole, so clicks go to the app below.

**Rule:** a see-through window bigger than a panel uses `SeeThrough` with a shape, not AllowsTransparency.
