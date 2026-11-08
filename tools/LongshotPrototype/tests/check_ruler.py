"""Check a capture of tests/page.html using its colour ruler: every 8-CSS-px
block must appear exactly once, in order, at its full height.

    python tests/check_ruler.py CAPTURE.png SCALE
(SCALE = Windows display scale, e.g. 1.25)
"""
import sys
from collections import Counter

import numpy as np
from PIL import Image

cap = np.asarray(Image.open(sys.argv[1]).convert("RGB")).astype(int)
scale = float(sys.argv[2])
header, footer = round(60 * scale), round(40 * scale)
col = cap[header : cap.shape[0] - footer, round(12 * scale)]  # middle of the ruler

idx = []
for r, g, b in col:
    if abs(b - 200) > 6:
        idx.append(None)  # not on a clean ruler colour (anti-aliased edge)
        continue
    idx.append(int(round((g - 30) / 60)) * 256 + r)

# Collapse into runs of the same block index.
runs = []
for v in idx:
    if v is None:
        continue
    if runs and runs[-1][0] == v:
        runs[-1][1] += 1
    else:
        runs.append([v, 1])

# Browsers draw a 1-2 px fringe where two blocks meet; keep only solid runs.
runs = [r for r in runs if r[1] >= 4]
blocks = [v for v, _ in runs]
gaps = [(a, b) for a, b in zip(blocks, blocks[1:]) if b != a + 1]
dupes = [v for v, c in Counter(blocks).items() if c > 1]
# (blocks 255/256, 511/512... change colour in a way that hides the fringe; skip them)
heights = Counter(n for v, n in runs[1:-1] if v % 256 not in (0, 255))
typical = heights.most_common(1)[0][0]
print(f"capture {cap.shape[1]}x{cap.shape[0]}; ruler blocks {blocks[0]}..{blocks[-1]} ({len(blocks)} seen)")
print(f"solid block heights (px): {dict(sorted(heights.items()))}  (typical {typical})")
print(f"gaps/jumps: {gaps[:10]}  repeats: {dupes[:10]}")
# A seam that is off by k px shows up as a block that is k px too tall/short.
off = {h: c for h, c in heights.items() if abs(h - typical) > 1}
print(f"blocks off by more than 1 px: {off}")
ok = not gaps and not dupes and not off
print("RESULT:", "PASS (no gaps, no repeats)" if ok else "FAIL")
sys.exit(0 if ok else 1)
