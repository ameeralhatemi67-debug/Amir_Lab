"""Image logic for Longshot.

Works out how far the content scrolled between two captures of the same
rectangle, and glues only the new rows onto a growing long image.
No Windows or UI code lives here, so it can be tested on its own.

How a step works
----------------
1. Rows that are identical at the very top / bottom of both frames are treated
   as fixed (a sticky header, toolbar, footer, ...). They are not used for
   matching, and a fixed footer is kept only once, at the very bottom.
2. In the part that scrolls, we try every possible shift `d` and pick the one
   where the old frame's lower part lines up with the new frame's upper part.
3. The last `d` rows of the scrolling part (plus the footer) are appended.
"""
from __future__ import annotations

from dataclasses import dataclass, field

import numpy as np

STATIC_TOL = 1.5          # mean |diff| (0-255) under which a row counts as unchanged
MATCH_TOL = 6.0           # refuse to stitch if the best overlap is worse than this
WORST_TOL = 4.0           # ...or if any 16-px stripe of it is worse than this (look-alike rows)
TEXTURE_MIN = 2.0         # overlap std-dev under which the area is "blank"
MAX_FIXED_FRACTION = 0.4  # header/footer may each take at most this much of the height
CLUSTER_PX = 3            # candidate shifts this close together are the same minimum
EDGE_PAD = 4              # extra rows next to a sticky header/footer treated as part of it:
                          # browsers soften the bar's edge, and that edge changes as the
                          # page scrolls, which would otherwise leave a line at every seam


def to_gray(rgb: np.ndarray) -> np.ndarray:
    """H x W x 3 uint8 -> H x W float32 brightness."""
    return rgb.mean(axis=2, dtype=np.float32)


def _reduce_cols(g: np.ndarray, target: int = 160) -> np.ndarray:
    """Average blocks of columns so the coarse search is fast. Rows are kept."""
    h, w = g.shape
    block = max(1, w // target)
    if block == 1:
        return g
    wb = w // block
    return g[:, : wb * block].reshape(h, wb, block).mean(axis=2)


def _worst_stripe(x: np.ndarray, y: np.ndarray, width: int = 16) -> float:
    """Mean |diff| in the worst `width`-px column stripe. A true match differs
    evenly (tiny anti-aliasing noise); a false match on look-alike content
    (same layout, different numbers/words) differs a lot in a few stripes."""
    diff = np.abs(x - y)
    w = diff.shape[1] // width * width
    if w == 0:
        return float(diff.mean())
    return float(diff[:, :w].reshape(diff.shape[0], -1, width).mean(axis=(0, 2)).max())


@dataclass
class Match:
    moved: bool               # did anything scroll at all?
    ok: bool = False          # can we stitch this frame?
    d: int = 0                # how many rows the content moved up
    top: int = 0              # fixed rows at the top (sticky header)
    bottom: int = 0           # fixed rows at the bottom (sticky footer)
    score: float = 0.0        # mean |diff| of the overlap (0 = perfect)
    worst: float = 0.0        # mean |diff| of the worst vertical stripe of the overlap
    estimated: bool = False   # True when d was guessed because the area is blank
    region: int = 0           # height of the part that scrolls
    reason: str = ""


def find_shift(prev: np.ndarray, cur: np.ndarray, expected: float | None = None) -> Match:
    """Compare two grayscale frames of the same rectangle.

    `expected` is the scroll distance we think happened (from earlier steps);
    it is only used to break ties between equally good shifts, e.g. on
    repeating content like identical table rows.
    """
    if prev.shape != cur.shape:
        raise ValueError("frames must be the same size")
    h = prev.shape[0]

    rowdiff = np.abs(prev - cur).mean(axis=1)
    static = rowdiff < STATIC_TOL
    if static.all():
        return Match(moved=False, reason="Nothing moved")

    cap = int(h * MAX_FIXED_FRACTION)
    top = int(np.argmin(static))                     # first changed row
    bottom = int(np.argmin(static[::-1]))            # changed rows counted from the end
    top = min(top + EDGE_PAD if top else 0, cap)
    bottom = min(bottom + EDGE_PAD if bottom else 0, cap)
    a = prev[top : h - bottom]
    b = cur[top : h - bottom]
    r = a.shape[0]
    min_overlap = max(8, r // 10)
    max_d = r - min_overlap
    if max_d < 1:
        return Match(moved=True, top=top, bottom=bottom, region=r,
                     reason="The selected area is too small to line up")

    # Coarse search over every shift on column-averaged frames.
    ac, bc = _reduce_cols(a), _reduce_cols(b)
    coarse = np.empty(max_d, np.float32)
    for d in range(1, max_d + 1):
        coarse[d - 1] = np.abs(ac[d:] - bc[: r - d]).mean()

    # Keep the best local minima, then re-score them (and neighbours) at full detail.
    is_min = np.ones(max_d, bool)
    if max_d > 1:
        is_min[1:] &= coarse[1:] <= coarse[:-1]
        is_min[:-1] &= coarse[:-1] <= coarse[1:]
    idx = np.flatnonzero(is_min)
    idx = idx[np.argsort(coarse[idx], kind="stable")[:12]]
    cands: set[int] = set()
    for i in idx:
        for d in (i, i + 1, i + 2):  # shift i+1 and its neighbours
            if 1 <= d <= max_d:
                cands.add(int(d))
    if expected:
        e = int(round(expected))
        cands.update(d for d in range(e - 2, e + 3) if 1 <= d <= max_d)

    scored = sorted((float(np.abs(a[d:] - b[: r - d]).mean()), d) for d in cands)
    best = scored[0][0]

    # Did it really move, or is something small just animating in place?
    still = float(np.abs(a - b).mean())
    if still <= best:
        return Match(moved=False, reason="Nothing moved")

    # One representative per minimum, so d-1 / d+1 never compete with d.
    reps: list[tuple[float, int]] = []
    for s, d in scored:
        if all(abs(d - rd) > CLUSTER_PX for _, rd in reps):
            reps.append((s, d))
    tol = max(0.05, best * 0.1)  # only true ties (e.g. identical repeating rows)
    near = [(s, d) for s, d in reps if s <= best + tol]
    if expected:
        score, d = min(near, key=lambda sd: (abs(sd[1] - expected), sd[1]))
    else:
        score, d = min(near, key=lambda sd: sd[1])  # smallest shift = biggest overlap

    m = Match(moved=True, d=d, top=top, bottom=bottom, score=score, region=r, worst=_worst_stripe(a[d:], b[: r - d]))
    if float(b[: r - d].std()) < TEXTURE_MIN:
        if expected and 1 <= round(expected) <= max_d:
            m.d, m.ok, m.estimated = int(round(expected)), True, True
            m.reason = "Blank area, used the usual scroll distance"
        else:
            m.reason = "The area is too blank to line up"
        return m
    if score > MATCH_TOL:
        m.reason = "Couldn't line up the new part (it may have scrolled too far)"
        return m
    # Look-alike content (lists, tables, repeated layouts) can line up almost
    # perfectly at the wrong place when the page jumped further than we can
    # see. Real matches differ evenly; wrong ones differ a lot in a few
    # stripes. Trust it anyway only if it equals the usual scroll step.
    if m.worst > WORST_TOL and not (expected and abs(d - expected) <= 2):
        m.reason = "Part of it doesn't line up (it scrolled too far, or something in the area is moving)"
        return m
    m.ok = True
    return m


class LongImage:
    """A tall image kept as a list of row-chunks so appending stays cheap."""

    def __init__(self, first: np.ndarray):
        self._chunks: list[np.ndarray] = [first]
        self.width = first.shape[1]
        self.height = first.shape[0]

    def append(self, rows: np.ndarray) -> None:
        if rows.shape[0]:
            self._chunks.append(rows)
            self.height += rows.shape[0]

    def trim(self, n: int) -> np.ndarray:
        """Remove `n` rows from the bottom and return them."""
        removed = []
        while n > 0 and self._chunks:
            last = self._chunks[-1]
            if last.shape[0] <= n:
                removed.append(self._chunks.pop())
                n -= last.shape[0]
                self.height -= last.shape[0]
            else:
                removed.append(last[-n:])
                self._chunks[-1] = last[:-n]
                self.height -= n
                n = 0
        removed.reverse()
        if not removed:
            return np.empty((0, self.width, 3), np.uint8)
        return np.concatenate(removed)

    def tail(self, n: int) -> np.ndarray:
        """The bottom `n` rows (or fewer, if the image is shorter)."""
        parts, got = [], 0
        for chunk in reversed(self._chunks):
            if got >= n:
                break
            take = chunk[-(n - got):] if chunk.shape[0] > n - got else chunk
            parts.append(take)
            got += take.shape[0]
        parts.reverse()
        return np.concatenate(parts)

    def to_array(self) -> np.ndarray:
        return np.concatenate(self._chunks)


@dataclass
class Step:
    removed: np.ndarray       # rows trimmed off the bottom (the old footer)
    added: int                # rows appended
    prev_frame: np.ndarray    # the frame we matched against
    info: dict = field(default_factory=dict)


class Stitcher:
    """Holds the long image and the last frame that went into it."""

    def __init__(self, first: np.ndarray, max_undo: int = 30):
        self.long = LongImage(first.copy())
        self.last = first
        self.last_g = to_gray(first)
        self.history: list[Step] = []
        self.max_undo = max_undo
        self.steps = 0

    def match(self, frame: np.ndarray, expected: float | None = None) -> tuple[Match, np.ndarray]:
        g = to_gray(frame)
        return find_shift(self.last_g, g, expected), g

    def apply(self, frame: np.ndarray, gray: np.ndarray, m: Match, **info) -> None:
        h = frame.shape[0]
        removed = self.long.trim(m.bottom)
        new_rows = frame[h - m.bottom - m.d :].copy()
        self.long.append(new_rows)
        self.history.append(Step(removed, new_rows.shape[0], self.last, info))
        if len(self.history) > self.max_undo:
            self.history.pop(0)
        self.last, self.last_g = frame, gray
        self.steps += 1

    def add(self, frame: np.ndarray, expected: float | None = None, **info) -> Match:
        """Match and, if it lines up, append. Returns the match either way."""
        m, g = self.match(frame, expected)
        if m.moved and m.ok:
            self.apply(frame, g, m, **info)
        return m

    def undo(self) -> Step | None:
        if not self.history:
            return None
        step = self.history.pop()
        self.long.trim(step.added)
        self.long.append(step.removed)
        self.last = step.prev_frame
        self.last_g = to_gray(step.prev_frame)
        self.steps -= 1
        return step
