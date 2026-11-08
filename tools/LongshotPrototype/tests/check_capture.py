"""Compare a Longshot capture of tests/scroll_target.py with the true page.

    python tests/check_capture.py OUTDIR CAPTURE.png
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

out, cap_path = Path(sys.argv[1]), Path(sys.argv[2])
info = json.loads((out / "target.json").read_text())
page = np.asarray(Image.open(out / "page.png").convert("RGB"))
cap = np.asarray(Image.open(cap_path).convert("RGB"))
rx, ry, rw, rh = info["region"]
cx, cy, cw, ch = info["canvas"]
footer_h = rh - (cy + ch)
want_h = cy + page.shape[0] + footer_h

print(f"capture {cap.shape[1]}x{cap.shape[0]}, expected {rw}x{want_h}")
ok = cap.shape[0] == want_h and cap.shape[1] == rw
if ok:
    content = cap[cy : cy + page.shape[0], cx : cx + cw]
    diff = np.abs(content.astype(int) - page[:, :cw].astype(int))
    bad_rows = np.flatnonzero(diff.max(axis=(1, 2)) > 0)
    print(f"content rows that differ from the page: {len(bad_rows)}")
    ok = len(bad_rows) == 0
    if bad_rows.size:
        print("first bad rows:", bad_rows[:10])
print("RESULT:", "PASS (pixel-identical)" if ok else "FAIL")
sys.exit(0 if ok else 1)
