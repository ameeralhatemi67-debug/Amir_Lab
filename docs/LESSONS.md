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
