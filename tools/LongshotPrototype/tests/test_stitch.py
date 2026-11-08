"""Synthetic tests for stitch.py: fake a scrolling page and check that the
stitched result is pixel-identical to the original page.

Run:  .venv\\Scripts\\python.exe tests\\test_stitch.py
"""
import random
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from stitch import Stitcher, find_shift, to_gray  # noqa: E402

W = 640


def make_page(height=3200, seed=1, gap=True):
    """A white page with numbered text lines, boxes, a blank gap and a block of
    identical rows (the hard case for matching)."""
    rnd = random.Random(seed)
    img = Image.new("RGB", (W, height), "white")
    dr = ImageDraw.Draw(img)
    font = ImageFont.load_default(size=16)
    y, n = 10, 1
    while y < height - 30:
        kind = rnd.random()
        if gap and 1200 < y < 1500:              # blank gap
            y = 1500
            continue
        if 2000 < y < 2300:                      # identical repeating rows
            dr.rectangle([20, y, W - 20, y + 26], outline=(200, 200, 200))
            dr.text((30, y + 4), "same row", fill="black", font=font)
            y += 30
            continue
        if kind < 0.12:
            col = tuple(rnd.randrange(60, 230) for _ in range(3))
            hh = rnd.randrange(40, 120)
            dr.rectangle([rnd.randrange(10, 200), y, rnd.randrange(300, W - 10), y + hh], fill=col)
            y += hh + 12
        else:
            dr.text((rnd.randrange(10, 60), y), f"Line {n:03d}  " + "lorem ipsum " * rnd.randrange(1, 4),
                    fill="black", font=font)
            n += 1
            y += 24
    return np.asarray(img).copy()


def bar(h, color, label):
    img = Image.new("RGB", (W, h), color)
    ImageDraw.Draw(img).text((10, 4), label, fill="white", font=ImageFont.load_default(size=14))
    return np.asarray(img).copy()


def frame_at(page, off, view_h, header, footer):
    return np.concatenate([header, page[off : off + view_h], footer])


def run_scroll(page, offsets, view_h=330, header_h=50, footer_h=30, noise=0, use_expected=True):
    header = bar(header_h, (40, 70, 160), "Sticky header") if header_h else np.zeros((0, W, 3), np.uint8)
    footer = bar(footer_h, (30, 30, 30), "Sticky footer") if footer_h else np.zeros((0, W, 3), np.uint8)
    rng = np.random.default_rng(0)

    def fr(o):
        f = frame_at(page, o, view_h, header, footer)
        if noise:
            f = np.clip(f.astype(np.int16) + rng.integers(-noise, noise + 1, f.shape), 0, 255).astype(np.uint8)
        return f

    st = Stitcher(fr(offsets[0]))
    deltas = []
    for prev_o, o in zip(offsets, offsets[1:]):
        exp = (np.median(deltas) if deltas else None) if use_expected else None
        m = st.add(fr(o), expected=exp)
        assert m.moved and m.ok, f"offset {prev_o}->{o}: {m}"
        assert m.d == o - prev_o, f"offset {prev_o}->{o}: found d={m.d}, real {o - prev_o} ({m})"
        deltas.append(m.d)
    want = np.concatenate([header, page[offsets[0] : offsets[-1] + view_h], footer])
    return st, want


def test_regular_scroll():
    page = make_page()
    offs = list(range(0, page.shape[0] - 330, 111)) + [page.shape[0] - 330]
    st, want = run_scroll(page, offs)
    got = st.long.to_array()
    assert got.shape == want.shape, (got.shape, want.shape)
    assert np.array_equal(got, want), "stitched image differs from the page"


def test_irregular_scroll_no_header():
    """Like a person scrolling by hand: random distances, no sticky bars."""
    page = make_page(seed=3, gap=False)
    rnd = random.Random(7)
    offs, o = [0], 0
    while o < page.shape[0] - 330 - 10:
        o = min(o + rnd.randrange(20, 260), page.shape[0] - 330)
        offs.append(o)
    st, want = run_scroll(page, offs, header_h=0, footer_h=0, use_expected=False)
    assert np.array_equal(st.long.to_array(), want)


def test_noise_tolerance():
    page = make_page(seed=5)
    offs = list(range(0, 1100, 150))
    st, want = run_scroll(page, offs, noise=2)
    assert st.long.height == want.shape[0]


def test_no_movement_and_undo():
    page = make_page()
    header, footer = bar(50, (40, 70, 160), "H"), bar(30, (30, 30, 30), "F")
    f0 = frame_at(page, 0, 330, header, footer)
    st = Stitcher(f0)
    assert not st.add(f0.copy()).moved
    h0 = st.long.height
    st.add(frame_at(page, 120, 330, header, footer))
    assert st.long.height == h0 + 120
    st.undo()
    assert st.long.height == h0 and np.array_equal(st.long.to_array(), f0)


def test_soft_footer_edge():
    """The rows where a fixed footer meets the page are a blend of both, so
    they change on every scroll. They must not end up in the middle of the
    stitched image."""
    page = make_page(seed=9, gap=False)
    header, footer = bar(50, (40, 70, 160), "H"), bar(40, (30, 30, 30), "F")
    view = 330

    def fr(o):
        f = frame_at(page, o, view, header, footer).astype(np.float32)
        e = header.shape[0] + view                           # first footer row
        f[e - 2] = 0.5 * f[e - 2] + 0.5 * np.float32(30)     # blended edge rows
        f[e - 1] = 0.2 * f[e - 1] + 0.8 * np.float32(30)
        return f.round().astype(np.uint8)

    offs = list(range(0, 1500, 123))
    st = Stitcher(fr(offs[0]))
    for o in offs[1:]:
        m = st.add(fr(o), expected=123)
        assert m.ok and m.d == 123, m
    got = st.long.to_array()
    want = np.concatenate([header, page[: offs[-1] + view], footer])
    assert got.shape == want.shape
    content_end = got.shape[0] - footer.shape[0] - 2         # last frame's own edge is fine
    bad = np.flatnonzero(np.abs(got[:content_end].astype(int) - want[:content_end]).max(axis=(1, 2)) > 0)
    assert bad.size == 0, f"seam rows differ from the page: {bad[:10]}"


def test_blank_gap():
    """Inside a fully blank stretch the distance can't be measured: refuse to
    guess when hand-scrolling, use the usual step size when we scrolled."""
    page = make_page()
    none = np.zeros((0, W, 3), np.uint8)
    f0, f1 = (frame_at(page, o, 330, none, none) for o in (1081, 1250))
    m = find_shift(to_gray(f0), to_gray(f1))
    assert m.moved and not m.ok, m
    m = find_shift(to_gray(f0), to_gray(f1), expected=169)
    assert m.ok and m.estimated and m.d == 169, m


def make_lookalike_page(height=3000):
    """Every row looks the same except for its number: the classic trap."""
    img = Image.new("RGB", (W, height), "white")
    dr = ImageDraw.Draw(img)
    font = ImageFont.load_default(size=16)
    for i, y in enumerate(range(8, height - 30, 40)):
        dr.rectangle([12, y, W - 12, y + 32], outline=(210, 210, 210))
        dr.text((22, y + 7), f"Item {i + 1:03d}   The quick brown fox jumps over the lazy dog", fill="black",
                font=font)
    return np.asarray(img).copy()


def test_lookalike_rows():
    page = make_lookalike_page()
    none = np.zeros((0, W, 3), np.uint8)
    fr = lambda o: frame_at(page, o, 330, none, none)  # noqa: E731
    # In range: must find the true shift.
    for o, D in [(0, 90), (100, 200), (400, 263), (700, 290)]:
        m = find_shift(to_gray(fr(o)), to_gray(fr(o + D)))
        assert m.ok and m.d == D, (o, D, m)
    # Out of range (jumped further than the area): must refuse, not guess.
    for o, D in [(0, 340), (100, 400), (400, 520), (700, 610)]:
        m = find_shift(to_gray(fr(o)), to_gray(fr(o + D)))
        assert not m.ok, f"jump of {D} px accepted as d={m.d} (score {m.score:.2f}, worst {m.worst:.1f})"


def test_too_far_is_rejected():
    page = make_page()
    f0 = frame_at(page, 100, 330, np.zeros((0, W, 3), np.uint8), np.zeros((0, W, 3), np.uint8))
    f1 = frame_at(page, 100 + 500, 330, np.zeros((0, W, 3), np.uint8), np.zeros((0, W, 3), np.uint8))
    m = find_shift(to_gray(f0), to_gray(f1))
    assert m.moved and not m.ok, m


if __name__ == "__main__":
    failed = 0
    for name, fn in list(globals().items()):
        if name.startswith("test_") and callable(fn):
            t = time.perf_counter()
            try:
                fn()
                print(f"PASS  {name}  ({(time.perf_counter() - t) * 1000:.0f} ms)")
            except AssertionError as e:
                failed += 1
                print(f"FAIL  {name}: {e}")
    sys.exit(1 if failed else 0)
