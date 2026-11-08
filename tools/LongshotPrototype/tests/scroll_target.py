"""A throwaway window to test Longshot against: a long page in a canvas that
scrolls with the mouse wheel, with a sticky header and footer.

Writes <outdir>/target.json (where things are on screen) and
<outdir>/page.png (the true page) so the capture can be checked exactly.

    python tests/scroll_target.py OUTDIR [px_per_notch] [seconds]
"""
import ctypes

ctypes.windll.shcore.SetProcessDpiAwareness(2)

import json  # noqa: E402
import sys  # noqa: E402
import tkinter as tk  # noqa: E402
from pathlib import Path  # noqa: E402

from PIL import Image, ImageTk  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
from test_stitch import make_page  # noqa: E402

out = Path(sys.argv[1])
per_notch = float(sys.argv[2]) if len(sys.argv) > 2 else 111
seconds = float(sys.argv[3]) if len(sys.argv) > 3 else 60
out.mkdir(parents=True, exist_ok=True)

page = make_page(height=2600, seed=11)
Image.fromarray(page).save(out / "page.png")

root = tk.Tk()
root.overrideredirect(True)
root.geometry("+220+140")
root.configure(bg="#e9ecf2")
root.attributes("-topmost", True)
tk.Label(root, text="Sticky header · test window", bg="#2850a0", fg="white",
         font=("Segoe UI", 11, "bold"), pady=8).pack(fill="x")
cv = tk.Canvas(root, width=page.shape[1], height=430, highlightthickness=0, bd=0, bg="white",
               yscrollincrement=1, scrollregion=(0, 0, page.shape[1], page.shape[0]))
cv.pack(padx=24)
photo = ImageTk.PhotoImage(Image.fromarray(page))
cv.create_image(0, 0, anchor="nw", image=photo)
tk.Label(root, text="Sticky footer", bg="#333333", fg="white", font=("Segoe UI", 9), pady=4).pack(fill="x")


def wheel(e):
    cv.yview_scroll(int(round(-e.delta / 120 * per_notch)), "units")


root.bind_all("<MouseWheel>", wheel)


def report():
    root.update_idletasks()
    info = {
        "region": [root.winfo_rootx(), root.winfo_rooty(), root.winfo_width(), root.winfo_height()],
        "canvas": [cv.winfo_rootx() - root.winfo_rootx(), cv.winfo_rooty() - root.winfo_rooty(),
                   cv.winfo_width(), cv.winfo_height()],
        "page": [page.shape[1], page.shape[0]],
    }
    (out / "target.json").write_text(json.dumps(info))


root.after(300, report)
root.after(int(seconds * 1000), root.destroy)
root.mainloop()
