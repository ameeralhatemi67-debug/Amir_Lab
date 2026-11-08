# Longshot

A scrolling screenshot tool for Windows that works like Samsung's scroll capture:
draw a box, then extend it downward.

## Use it

1. Double-click **Longshot.bat**. The first run takes about a minute to set itself up.
2. The screen dims. **Drag a box** over what you want to capture.
   *Click without dragging* to use the whole screen. *Esc* or right-click cancels.
3. A panel appears next to the box:

| Button | What it does |
|---|---|
| **↓ Extend** | Scrolls once and adds what appeared below. Click it again for more. |
| **▶ Auto** | Keeps extending until the page stops moving. **Esc** or **Stop** ends it. |
| **◎ Follow** | You scroll the page yourself, and Longshot adds as you go. Go at a normal pace. |
| **↶ Undo** | Removes the last part that was added and scrolls back. |
| **✓ Done** | Saves to `Pictures\Screenshots`, copies it to the clipboard, and shows a preview. |

The box's frame turns **amber** during Auto and **green** during Follow. The panel and frame
never appear in your screenshots, and the panel doesn't take focus away from the app you're capturing.

## Keyboard shortcut (optional)

```
powershell -ExecutionPolicy Bypass -File make_shortcut.ps1
```

This adds **Longshot** to the Start menu and sets **Ctrl+Alt+S** to open it from anywhere.
Add `-Hotkey "CTRL+ALT+L"` to choose a different shortcut.

## Tips

- **Sticky headers and footers** (site menus, cookie bars) are detected automatically. They appear
  once, at the top and bottom of the image.
- **Leave the scrollbar out of the box.** Otherwise the moving scrollbar thumb shows up in every strip.
- **Videos, animations or carousels inside the box** can stop it, because they change between shots. Draw the box around them.
- **Long blank stretches** (pure white space) can't be measured. Extend and Auto use the usual
  step size there; Follow waits until there's something to line up.
- If it says *"Lost track"*, scroll back up a little until the last part is visible again.
- It can't scroll apps running as administrator (for example Task Manager). Windows blocks that.
- `longshot.log` next to the script records what happened, which helps if something goes wrong.

## Command line

```
.venv\Scripts\pythonw.exe longshot.py --delay 3                 # 3 s to open a menu first
.venv\Scripts\pythonw.exe longshot.py --region 100,200,800,600  # skip drawing the box
.venv\Scripts\python.exe  longshot.py --region 100,200,800,600 --auto --out page.png
```

## How it works

Each step grabs the box, scrolls, and grabs it again. `stitch.py` finds rows that didn't change at
the top and bottom (sticky bars). It then slides the new shot against the old one to find exactly
how far the content moved, and appends only the new rows. A match is refused if any stripe of it
doesn't line up. That stops it from gluing look-alike rows (lists, tables) in the wrong place.

Tests: `.venv\Scripts\python.exe tests\test_stitch.py`
