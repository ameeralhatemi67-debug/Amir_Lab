"""Longshot: an "extend downward" scrolling screenshot tool for Windows.

Draw a rectangle on the screen, then press Extend: Longshot scrolls whatever
is under the rectangle and adds the part that appears below, like the scroll
capture button on Samsung phones. Auto keeps going to the bottom; Follow lets
you scroll yourself while it adds as you go.

Run:  .venv\\Scripts\\pythonw.exe longshot.py      (or double-click Longshot.bat)
"""
from __future__ import annotations

import ctypes
import ctypes.wintypes as wt


def _dpi_aware() -> None:
    """Use real pixels everywhere. Must run before any window exists."""
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)  # per-monitor
    except Exception:
        try:
            ctypes.windll.user32.SetProcessDPIAware()
        except Exception:
            pass


_dpi_aware()

import argparse  # noqa: E402
import datetime  # noqa: E402
import io  # noqa: E402
import logging  # noqa: E402
import os  # noqa: E402
import queue  # noqa: E402
import subprocess  # noqa: E402
import sys  # noqa: E402
import threading  # noqa: E402
import time  # noqa: E402
import tkinter as tk  # noqa: E402
import traceback  # noqa: E402
import uuid  # noqa: E402
from dataclasses import dataclass  # noqa: E402
from pathlib import Path  # noqa: E402
from tkinter import filedialog  # noqa: E402

import mss  # noqa: E402
import numpy as np  # noqa: E402
from PIL import Image, ImageDraw, ImageEnhance, ImageTk  # noqa: E402

from stitch import Stitcher, find_shift, to_gray  # noqa: E402

HERE = Path(__file__).resolve().parent
log = logging.getLogger("longshot")

MAX_HEIGHT = 40000  # stop growing past this many pixels

# ---------------------------------------------------------------- look & feel
FONT = "Segoe UI"
BG = "#17191f"
PANEL = "#22252d"
EDGE = "#3a3f4c"
FG = "#eceef3"
MUTED = "#a2a8b5"
DIM = "#6c7280"
PREVIEW_BG = "#0f1115"
BTN = "#323644"
BTN_HOVER = "#3e4354"
ACCENT = "#4f8cff"
ACCENT_HOVER = "#6b9fff"
GOOD = "#2b9d63"
GOOD_HOVER = "#33b572"
DANGER = "#e5484d"
FRAME_IDLE = "#4f8cff"
FRAME_AUTO = "#ffb224"
FRAME_FOLLOW = "#30c27a"

# ---------------------------------------------------------------- Win32 glue
_user32 = ctypes.WinDLL("user32", use_last_error=True)
_kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

GWL_EXSTYLE = -20
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_TRANSPARENT = 0x00000020
WS_EX_LAYERED = 0x00080000
WS_EX_NOACTIVATE = 0x08000000
WDA_EXCLUDEFROMCAPTURE = 0x11
SWP_FLAGS = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020  # NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
GA_ROOT = 2
INPUT_MOUSE = 0
MOUSEEVENTF_WHEEL = 0x0800
VK_ESCAPE = 0x1B
CF_DIB = 8
GMEM_MOVEABLE = 0x0002

_user32.GetWindowLongPtrW.restype = ctypes.c_ssize_t
_user32.GetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int]
_user32.SetWindowLongPtrW.restype = ctypes.c_ssize_t
_user32.SetWindowLongPtrW.argtypes = [wt.HWND, ctypes.c_int, ctypes.c_ssize_t]
_user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
_user32.SetWindowDisplayAffinity.argtypes = [wt.HWND, wt.DWORD]
_user32.GetAncestor.restype = wt.HWND
_user32.GetAncestor.argtypes = [wt.HWND, wt.UINT]
_user32.GetAsyncKeyState.restype = ctypes.c_short
_user32.MonitorFromPoint.restype = ctypes.c_void_p
_user32.MonitorFromPoint.argtypes = [wt.POINT, wt.DWORD]
_user32.OpenClipboard.argtypes = [wt.HWND]
_user32.SetClipboardData.restype = ctypes.c_void_p
_user32.SetClipboardData.argtypes = [wt.UINT, ctypes.c_void_p]
_kernel32.GlobalAlloc.restype = ctypes.c_void_p
_kernel32.GlobalAlloc.argtypes = [wt.UINT, ctypes.c_size_t]
_kernel32.GlobalLock.restype = ctypes.c_void_p
_kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
_kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
_kernel32.GlobalFree.argtypes = [ctypes.c_void_p]


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wt.DWORD), ("wParamL", wt.WORD), ("wParamH", wt.WORD)]


class _INPUTUNION(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("type", wt.DWORD), ("u", _INPUTUNION)]


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT), ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]


_user32.SendInput.argtypes = [wt.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
_user32.GetMonitorInfoW.argtypes = [ctypes.c_void_p, ctypes.POINTER(MONITORINFO)]


@dataclass
class Rect:
    x: int
    y: int
    w: int
    h: int

    @property
    def right(self) -> int:
        return self.x + self.w

    @property
    def bottom(self) -> int:
        return self.y + self.h

    @property
    def cx(self) -> int:
        return self.x + self.w // 2

    @property
    def cy(self) -> int:
        return self.y + self.h // 2

    def overlaps(self, o: "Rect") -> bool:
        return self.x < o.right and o.x < self.right and self.y < o.bottom and o.y < self.bottom


def get_cursor() -> tuple[int, int]:
    pt = wt.POINT()
    _user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def set_cursor(x: int, y: int) -> None:
    _user32.SetCursorPos(int(x), int(y))


def send_wheel(delta: int) -> None:
    """Mouse-wheel event; negative = scroll down. 120 = one notch."""
    inp = INPUT(type=INPUT_MOUSE)
    inp.mi = MOUSEINPUT(0, 0, delta & 0xFFFFFFFF, MOUSEEVENTF_WHEEL, 0, 0)
    _user32.SendInput(1, ctypes.byref(inp), ctypes.sizeof(INPUT))


def esc_pressed() -> bool:
    return bool(_user32.GetAsyncKeyState(VK_ESCAPE) & 0x8000)


def monitor_at(x: int, y: int, work: bool = False) -> Rect:
    hmon = _user32.MonitorFromPoint(wt.POINT(int(x), int(y)), 2)  # nearest
    mi = MONITORINFO()
    mi.cbSize = ctypes.sizeof(MONITORINFO)
    _user32.GetMonitorInfoW(hmon, ctypes.byref(mi))
    r = mi.rcWork if work else mi.rcMonitor
    return Rect(r.left, r.top, r.right - r.left, r.bottom - r.top)


def style_overlay_window(win: tk.Toplevel, *, click_through: bool = False) -> None:
    """Keep a helper window out of screenshots, out of Alt+Tab, and stop it
    from stealing focus from the app being captured."""
    win.update_idletasks()
    hwnd = _user32.GetAncestor(win.winfo_id(), GA_ROOT)
    if not hwnd:
        return
    ex = _user32.GetWindowLongPtrW(hwnd, GWL_EXSTYLE)
    ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
    if click_through:
        ex |= WS_EX_LAYERED | WS_EX_TRANSPARENT
    _user32.SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex)
    _user32.SetWindowPos(hwnd, None, 0, 0, 0, 0, SWP_FLAGS)
    if os.environ.get("LONGSHOT_SHOW_IN_CAPTURES"):  # debugging: let screenshots see the panel
        return
    if not _user32.SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE):
        log.info("SetWindowDisplayAffinity failed (error %s)", ctypes.get_last_error())


def dark_title_bar(win: tk.Toplevel) -> None:
    try:
        hwnd = _user32.GetAncestor(win.winfo_id(), GA_ROOT)
        on = ctypes.c_int(1)
        ctypes.windll.dwmapi.DwmSetWindowAttribute(wt.HWND(hwnd), 20, ctypes.byref(on), ctypes.sizeof(on))
    except Exception:
        pass


def copy_image_to_clipboard(img: Image.Image) -> None:
    buf = io.BytesIO()
    img.convert("RGB").save(buf, "BMP")
    data = buf.getvalue()[14:]  # CF_DIB = the BMP without its file header
    for _ in range(20):
        if _user32.OpenClipboard(None):
            break
        time.sleep(0.05)
    else:
        raise RuntimeError("The clipboard is busy")
    try:
        _user32.EmptyClipboard()
        h = _kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
        if not h:
            raise MemoryError("Not enough memory for the clipboard")
        p = _kernel32.GlobalLock(h)
        ctypes.memmove(p, data, len(data))
        _kernel32.GlobalUnlock(h)
        if not _user32.SetClipboardData(CF_DIB, h):
            _kernel32.GlobalFree(h)
            raise RuntimeError("Could not put the image on the clipboard")
    finally:
        _user32.CloseClipboard()


class _GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD), ("Data4", ctypes.c_ubyte * 8)]


def screenshots_dir() -> Path:
    """Pictures\\Screenshots, the same folder Snipping Tool saves to."""
    pictures = Path.home() / "Pictures"
    try:
        u = uuid.UUID("{33E28130-4E1E-4676-835A-98395C3BC3BB}")  # FOLDERID_Pictures
        g = _GUID(u.fields[0], u.fields[1], u.fields[2], (ctypes.c_ubyte * 8)(*u.bytes[8:]))
        p = ctypes.c_wchar_p()
        if ctypes.windll.shell32.SHGetKnownFolderPath(ctypes.byref(g), 0, None, ctypes.byref(p)) == 0:
            pictures = Path(p.value)
            ctypes.windll.ole32.CoTaskMemFree(p)
    except Exception:
        pass
    return pictures / "Screenshots"


def new_mss():
    return (getattr(mss, "MSS", None) or mss.mss)()


def make_icon(size: int = 64) -> Image.Image:
    s = size
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([s * .04, s * .04, s * .96, s * .96], radius=s * .22, fill=(79, 140, 255, 255))
    d.rounded_rectangle([s * .27, s * .14, s * .73, s * .60], radius=s * .06, fill=(255, 255, 255, 255))
    for i in range(3):
        y = s * (.25 + i * .1)
        d.rounded_rectangle([s * .34, y, s * (.66 if i != 2 else .54), y + s * .045], radius=s * .02,
                            fill=(79, 140, 255, 255))
    d.polygon([(s * .5, s * .88), (s * .31, s * .67), (s * .69, s * .67)], fill=(255, 255, 255, 255))
    return img


# ---------------------------------------------------------------- widgets
class FlatButton(tk.Label):
    """A flat, hoverable button (tk.Button can't be styled much on Windows)."""

    def __init__(self, master, text, command, *, bg=BTN, hover=BTN_HOVER, fg=FG, bold=False,
                 padx=10, pady=6, size=10, hint=None, on_hint=None):
        super().__init__(master, text=text, bg=bg, fg=fg, padx=padx, pady=pady, cursor="hand2",
                         font=(FONT, size, "bold" if bold else "normal"))
        self._bg, self._hover, self._fg = bg, hover, fg
        self.command, self.enabled = command, True
        self.hint, self.on_hint = hint, on_hint
        self.bind("<Enter>", self._enter)
        self.bind("<Leave>", self._leave)
        self.bind("<ButtonRelease-1>", self._click)

    def _enter(self, _e):
        if self.enabled:
            self.config(bg=self._hover)
        if self.on_hint and self.hint:
            self.on_hint(self.hint)

    def _leave(self, _e):
        self.config(bg=self._bg)
        if self.on_hint:
            self.on_hint(None)

    def _click(self, e):
        if self.enabled and 0 <= e.x < self.winfo_width() and 0 <= e.y < self.winfo_height():
            self.command()

    def set(self, *, text=None, enabled=None, bg=None, hover=None):
        if text is not None:
            self.config(text=text)
        if bg is not None:
            self._bg = bg
            self.config(bg=bg)
        if hover is not None:
            self._hover = hover
        if enabled is not None:
            self.enabled = enabled
            self.config(fg=self._fg if enabled else DIM, cursor="hand2" if enabled else "arrow")


# ---------------------------------------------------------------- 1. choose the area
class Selector:
    """Full-screen frozen snapshot, dimmed; drag to pick a rectangle."""

    def __init__(self, app: "App", on_done, on_cancel):
        self.app, self.on_done, self.on_cancel = app, on_done, on_cancel
        with new_mss() as s:
            v = s.monitors[0]
            shot = s.grab(v)
        self.ox, self.oy = v["left"], v["top"]
        W, H = v["width"], v["height"]
        self.img = Image.frombytes("RGB", shot.size, shot.bgra, "raw", "BGRX")
        dark = ImageEnhance.Brightness(self.img).enhance(0.45)

        win = self.win = tk.Toplevel(app.root)
        win.overrideredirect(True)
        win.geometry(f"{W}x{H}+{self.ox}+{self.oy}")
        win.attributes("-topmost", True)
        c = self.c = tk.Canvas(win, width=W, height=H, highlightthickness=0, bd=0, bg="black", cursor="crosshair")
        c.pack()
        self.bg_photo = ImageTk.PhotoImage(dark)
        c.create_image(0, 0, anchor="nw", image=self.bg_photo)
        self.sel_photo = None
        self.sel = c.create_image(0, 0, anchor="nw", state="hidden")
        self.frame = c.create_rectangle(0, 0, 0, 0, outline=FRAME_IDLE, width=2, state="hidden")
        self.size_bg = c.create_rectangle(0, 0, 0, 0, fill="#000000", outline="", state="hidden")
        self.size_txt = c.create_text(0, 0, anchor="nw", fill="white", font=(FONT, 9), state="hidden")

        mx, my = get_cursor()
        mon = monitor_at(mx, my)
        hx, hy = mon.cx - self.ox, mon.y - self.oy + app.px(36)
        self.hint = c.create_text(hx, hy, fill=FG, font=(FONT, 11),
                                  text="Drag over the area to capture   ·   Click for the whole screen   ·   Esc to cancel")
        x0, y0, x1, y1 = c.bbox(self.hint)
        pad = app.px(14)
        self.hint_bg = c.create_rectangle(x0 - pad, y0 - pad // 2, x1 + pad, y1 + pad // 2, fill=PANEL, outline=EDGE)
        c.tag_lower(self.hint_bg, self.hint)

        self.start = None
        self.cur = None
        self._pending = False
        c.bind("<ButtonPress-1>", self._press)
        c.bind("<B1-Motion>", self._motion)
        c.bind("<ButtonRelease-1>", self._release)
        c.bind("<ButtonPress-3>", lambda e: self.cancel())
        win.bind("<Escape>", lambda e: self.cancel())
        win.lift()
        win.focus_force()
        c.focus_set()

    def _box(self):
        (ax, ay), (bx, by) = self.start, self.cur
        return min(ax, bx), min(ay, by), max(ax, bx), max(ay, by)

    def _press(self, e):
        self.start = self.cur = (e.x, e.y)
        for item in (self.hint, self.hint_bg):
            self.c.itemconfigure(item, state="hidden")

    def _motion(self, e):
        if self.start is None:
            return
        self.cur = (e.x, e.y)
        if not self._pending:
            self._pending = True
            self.win.after(10, self._redraw)

    def _redraw(self):
        self._pending = False
        x0, y0, x1, y1 = self._box()
        if x1 - x0 < 2 or y1 - y0 < 2:
            return
        c = self.c
        self.sel_photo = ImageTk.PhotoImage(self.img.crop((x0, y0, x1, y1)))
        c.itemconfigure(self.sel, image=self.sel_photo, state="normal")
        c.coords(self.sel, x0, y0)
        c.coords(self.frame, x0 - 1, y0 - 1, x1, y1)
        c.itemconfigure(self.frame, state="normal")
        c.itemconfigure(self.size_txt, text=f"{x1 - x0} × {y1 - y0}", state="normal")
        ty = y0 - self.app.px(24) if y0 > self.app.px(30) else y0 + self.app.px(6)
        c.coords(self.size_txt, x0 + self.app.px(6), ty)
        bx0, by0, bx1, by1 = c.bbox(self.size_txt)
        c.coords(self.size_bg, bx0 - 5, by0 - 2, bx1 + 5, by1 + 2)
        c.itemconfigure(self.size_bg, state="normal")

    def _release(self, e):
        if self.start is None:
            return
        self.cur = (e.x, e.y)
        x0, y0, x1, y1 = self._box()
        w, h = x1 - x0, y1 - y0
        if w < 8 and h < 8:  # a click: the whole screen under the pointer
            rect = monitor_at(e.x + self.ox, e.y + self.oy)
        elif w < 60 or h < 60:
            self.start = None
            for item in (self.sel, self.frame, self.size_txt, self.size_bg):
                self.c.itemconfigure(item, state="hidden")
            self.c.itemconfigure(self.hint, text="That's a bit small. Drag a bigger area (Esc to cancel)",
                                 state="normal")
            self.c.itemconfigure(self.hint_bg, state="normal")
            return
        else:
            rect = Rect(x0 + self.ox, y0 + self.oy, w, h)
        self.win.destroy()
        self.app.root.after(150, lambda: self.on_done(rect))

    def cancel(self):
        self.win.destroy()
        self.on_cancel()


# ---------------------------------------------------------------- 2. capture engine (worker thread)
def frames_same(a: np.ndarray, b: np.ndarray) -> bool:
    """Close enough to identical (ignores a blinking text cursor)."""
    x = a[::2, ::2].astype(np.int16)
    y = b[::2, ::2].astype(np.int16)
    return float(np.abs(x - y).mean()) < 0.25


class Engine(threading.Thread):
    """Grabs, scrolls and stitches off the UI thread. Talks to the UI via queues."""

    def __init__(self, rect: Rect, events: queue.Queue, preview_size: tuple[int, int], out: Path | None):
        super().__init__(daemon=True)
        self.rect, self.events, self.out = rect, events, out
        self.pv_w, self.pv_h = preview_size
        self.cmds: queue.Queue = queue.Queue()
        self.stop_evt = threading.Event()
        self.unit = 120 if rect.h >= 300 else 60  # wheel delta per "notch" we send
        self.px_per_unit: list[float] = []
        self.region = rect.h
        self.finished = False

    # -- plumbing
    def post(self, kind, data=None):
        self.events.put((kind, data))

    def run(self):
        try:
            self.sct = new_mss()
            first = self.grab_settled(min_wait=0.05)
            self.st = Stitcher(first)
            self.post("ready")
            self.update("Press Extend to add what's below.")
            while not self.finished:
                cmd = self.cmds.get()
                if cmd == "quit":
                    break
                self.post("busy", cmd)
                try:
                    getattr(self, "do_" + cmd)()
                except Exception as e:
                    log.exception("command %s failed", cmd)
                    self.update(f"Something went wrong: {e}")
                if not self.finished:
                    self.post("busy", None)
        except Exception as e:
            log.exception("engine crashed")
            self.post("fatal", f"{type(e).__name__}: {e}")

    def update(self, msg: str | None = None):
        L = self.st.long
        rows = min(L.height, int(self.pv_h * L.width / self.pv_w) + 1)
        tail = Image.fromarray(L.tail(rows))
        h = max(1, round(tail.height * self.pv_w / tail.width))
        prev = tail.resize((self.pv_w, h), Image.BILINEAR, reducing_gap=2.0)
        self.post("update", dict(width=L.width, height=L.height, steps=self.st.steps, preview=prev,
                                 msg=msg, can_undo=bool(self.st.history)))

    # -- screen helpers
    def grab(self) -> np.ndarray:
        r = self.rect
        shot = self.sct.grab({"left": r.x, "top": r.y, "width": r.w, "height": r.h})
        a = np.frombuffer(shot.bgra, np.uint8).reshape(shot.height, shot.width, 4)
        return a[:, :, 2::-1].copy()  # BGRA -> RGB

    def grab_settled(self, min_wait=0.08, timeout=1.6) -> np.ndarray:
        """Wait for smooth-scrolling / animations to finish, then grab."""
        t0 = time.monotonic()
        time.sleep(min_wait)
        prev, stable = self.grab(), 0
        while True:
            time.sleep(0.045)
            cur = self.grab()
            stable = stable + 1 if frames_same(cur, prev) else 0
            if stable >= 2 or time.monotonic() - t0 > timeout:
                return cur
            prev = cur

    def wheel(self, notches: int, down: bool = True, unit: int | None = None):
        """Scroll the window under the rectangle, then give the pointer back."""
        unit = unit or self.unit
        old = get_cursor()
        set_cursor(self.rect.cx, self.rect.cy)
        time.sleep(0.03)
        for _ in range(notches):
            send_wheel(-unit if down else unit)
            time.sleep(0.012)
        time.sleep(0.06)
        set_cursor(*old)

    # -- one extend step
    def _notches(self) -> int:
        if not self.px_per_unit:
            return 1
        per = float(np.median(self.px_per_unit))
        return max(1, min(15, int(0.55 * self.region // max(per, 1))))

    def extend_once(self) -> tuple[str, str]:
        if self.st.long.height >= MAX_HEIGHT:
            return "max", f"Stopped at {MAX_HEIGHT:,} px, the size limit."
        n = self._notches()
        per = float(np.median(self.px_per_unit)) if self.px_per_unit else None
        m = None
        for _attempt in range(4):
            expected = per * n if per else None
            self.wheel(n)
            frame = self.grab_settled()
            m, g = self.st.match(frame, expected)
            log.info("extend n=%d unit=%d -> moved=%s ok=%s d=%d score=%.2f worst=%.1f top=%d bottom=%d est=%s %s",
                     n, self.unit, m.moved, m.ok, m.d, m.score, m.worst, m.top, m.bottom, m.estimated, m.reason)
            if not m.moved:
                return "end", "Reached the bottom. Nothing moved. Press Done to save."
            if m.ok:
                self.st.apply(frame, g, m, notches=n, unit=self.unit)
                self.region = m.region
                if not m.estimated and (expected is None or m.d >= 0.8 * expected):
                    self.px_per_unit = (self.px_per_unit + [m.d / n])[-8:]
                note = " (blank area, estimated)" if m.estimated else ""
                return "ok", f"Added {m.d} px{note}."
            # It moved, but too far to line up: go back and take a smaller step.
            self.wheel(n, down=False)
            self.grab_settled()
            if n > 1:
                n = max(1, n // 2)
            elif self.unit > 30:
                self.unit //= 2
                self.px_per_unit.clear()
                per = None
            else:
                break
        return "fail", (m.reason if m else "Couldn't line it up") + ". Try a taller area."

    # -- commands from the toolbar
    def do_extend(self):
        _kind, msg = self.extend_once()
        self.update(msg)

    def do_auto(self):
        msg = "Stopped."
        while not self.stop_evt.is_set() and not esc_pressed():
            kind, msg = self.extend_once()
            self.update(msg if kind != "ok" else f"Auto: {msg}  (Esc to stop)")
            if kind != "ok":
                break
        else:
            msg = "Stopped."
        self.update(msg)

    def do_follow(self):
        self.update("Scroll the page yourself. I'll add as you go. Stop or Esc when done.")
        prev = self.grab()
        reported = None  # the still frame we last complained about
        while not self.stop_evt.is_set() and not esc_pressed():
            time.sleep(0.05)
            cur = self.grab()
            settled = frames_same(cur, prev)
            prev = cur
            if frames_same(cur, self.st.last):
                continue
            if self.st.long.height >= MAX_HEIGHT:
                self.update(f"Reached the {MAX_HEIGHT:,} px size limit.")
                break
            m, g = self.st.match(cur)
            # While the page is still moving, only take near-perfect matches;
            # this keeps up with fast scrolling without risking a bad seam.
            if m.moved and m.ok and (settled or m.score <= 0.5):
                log.info("follow d=%d score=%.2f worst=%.1f top=%d bottom=%d settled=%s", m.d, m.score,
                         m.worst, m.top, m.bottom, settled)
                self.st.apply(cur, g, m)
                self.region = m.region
                self.update(f"Added {m.d} px. Keep scrolling.")
                reported = None
            elif settled and m.moved and not (reported is not None and frames_same(cur, reported)):
                reported = cur
                up = find_shift(to_gray(cur), self.st.last_g)
                log.info("follow no match d=%d score=%.2f worst=%.1f (%s); scrolled up=%s", m.d, m.score,
                         m.worst, m.reason, up.ok)
                if up.moved and up.ok:
                    self.update("You scrolled up. Scroll down again to continue.")
                else:
                    self.update("Lost track. Scroll back up a little, then go slower.")
        self.update("Stopped following.")

    def do_undo(self):
        step = self.st.undo()
        if not step:
            self.update("Nothing to undo.")
            return
        n, unit = step.info.get("notches", 0), step.info.get("unit", 0)
        if n:
            self.wheel(n, down=False, unit=unit)
            self.grab_settled()
            self.update("Removed the last part.")
        else:
            self.update("Removed the last part. Scroll back up a little to continue.")

    def do_finish(self):
        img = Image.fromarray(self.st.long.to_array())
        if self.out:
            path = self.out
        else:
            stamp = datetime.datetime.now().strftime("%Y-%m-%d %H%M%S")
            path = screenshots_dir() / f"Longshot {stamp}.png"
        path.parent.mkdir(parents=True, exist_ok=True)
        img.save(path, compress_level=4)
        log.info("saved %s (%dx%d, %d steps)", path, img.width, img.height, self.st.steps)
        self.finished = True
        self.post("final", (img, path))


# ---------------------------------------------------------------- 3. frame + toolbar while capturing
HINTS = {
    "extend": "Scroll down once and add the new part.",
    "auto": "Keep extending until the bottom. Esc stops.",
    "follow": "You scroll, Longshot adds as you go.",
    "undo": "Remove the last part that was added.",
    "done": "Save to Pictures\\Screenshots and copy.",
    "close": "Cancel without saving.",
}
DEFAULT_HINT = "Tip: drag the title to move this panel."


class CaptureUI:
    def __init__(self, app: "App", rect: Rect):
        self.app, self.rect = app, rect
        self.busy: str | None = "starting"
        self.can_undo = False
        self.closed = False
        self.auto_started = False
        self.events: queue.Queue = queue.Queue()
        self.pv_w, self.pv_h = app.px(200), app.px(170)
        self._make_frame()
        self._make_toolbar()
        self._set_busy("starting")
        self.engine = Engine(rect, self.events, (self.pv_w, self.pv_h), app.args.out)
        self.engine.start()
        self._poll()

    # -- the coloured frame around the area (outside it, click-through, not captured)
    def _make_frame(self):
        r, t = self.rect, max(2, self.app.px(3))
        boxes = [(r.x - t, r.y - t, r.w + 2 * t, t), (r.x - t, r.bottom, r.w + 2 * t, t),
                 (r.x - t, r.y, t, r.h), (r.right, r.y, t, r.h)]
        self.edges = []
        for x, y, w, h in boxes:
            e = tk.Toplevel(self.app.root, bg=FRAME_IDLE)
            e.overrideredirect(True)
            e.geometry(f"{w}x{h}+{x}+{y}")
            e.attributes("-alpha", 0.9)
            e.attributes("-topmost", True)
            style_overlay_window(e, click_through=True)
            self.edges.append(e)

    def _frame_color(self, color):
        for e in self.edges:
            e.configure(bg=color)

    # -- the floating panel
    def _make_toolbar(self):
        px = self.app.px
        w = self.bar = tk.Toplevel(self.app.root, bg=EDGE)
        w.withdraw()  # position it before it first appears, or Windows ignores the position
        w.overrideredirect(True)
        body = tk.Frame(w, bg=PANEL, padx=px(12), pady=px(10))
        body.pack(padx=1, pady=1)

        head = tk.Frame(body, bg=PANEL)
        head.pack(fill="x")
        title = tk.Label(head, text="Longshot", bg=PANEL, fg=FG, font=(FONT, 10, "bold"), cursor="fleur")
        title.pack(side="left")
        FlatButton(head, "✕", self.cancel, bg=PANEL, hover=DANGER, padx=px(7), pady=0,
                   hint=HINTS["close"], on_hint=self._hint).pack(side="right")
        for wdg in (head, title):
            wdg.bind("<ButtonPress-1>", self._drag_start)
            wdg.bind("<B1-Motion>", self._drag_move)

        self.pv = tk.Canvas(body, width=self.pv_w, height=self.pv_h, bg=PREVIEW_BG, highlightthickness=0, bd=0)
        self.pv.pack(pady=(px(8), px(6)))
        self.pv_item = self.pv.create_image(0, 0, anchor="nw")
        self.pv_photo = None

        self.info = tk.Label(body, text=" ", bg=PANEL, fg=FG, font=(FONT, 9, "bold"), anchor="w")
        self.info.pack(fill="x")
        self.status = tk.Label(body, text="Getting ready…", bg=PANEL, fg=MUTED, font=(FONT, 9), anchor="nw",
                               justify="left", wraplength=self.pv_w, height=2)
        self.status.pack(fill="x")

        g = tk.Frame(body, bg=PANEL)
        g.pack(fill="x", pady=(px(6), 0))
        g.columnconfigure(0, weight=1, uniform="b")
        g.columnconfigure(1, weight=1, uniform="b")
        mk = lambda text, cmd, key, **kw: FlatButton(g, text, cmd, hint=HINTS[key], on_hint=self._hint, **kw)  # noqa: E731
        self.b_extend = mk("↓  Extend", self.on_extend, "extend", bg=ACCENT, hover=ACCENT_HOVER, fg="white",
                           bold=True, pady=px(9), size=11)
        self.b_extend.grid(row=0, column=0, columnspan=2, sticky="ew")
        self.b_auto = mk("▶  Auto", self.on_auto, "auto")
        self.b_auto.grid(row=1, column=0, sticky="ew", padx=(0, px(3)), pady=(px(6), 0))
        self.b_follow = mk("◎  Follow", self.on_follow, "follow")
        self.b_follow.grid(row=1, column=1, sticky="ew", padx=(px(3), 0), pady=(px(6), 0))
        self.b_undo = mk("↶  Undo", self.on_undo, "undo")
        self.b_undo.grid(row=2, column=0, sticky="ew", padx=(0, px(3)), pady=(px(6), 0))
        self.b_done = mk("✓  Done", self.on_done, "done", bg=GOOD, hover=GOOD_HOVER, fg="white", bold=True)
        self.b_done.grid(row=2, column=1, sticky="ew", padx=(px(3), 0), pady=(px(6), 0))

        self.hint = tk.Label(body, text=DEFAULT_HINT, bg=PANEL, fg=DIM, font=(FONT, 8), anchor="w",
                             justify="left", wraplength=self.pv_w)
        self.hint.pack(fill="x", pady=(px(8), 0))

        w.update_idletasks()
        tw, th = w.winfo_reqwidth(), w.winfo_reqheight()
        x, y = self._toolbar_spot(tw, th)
        w.geometry(f"{tw}x{th}+{x}+{y}")
        w.deiconify()
        w.attributes("-topmost", True)
        style_overlay_window(w)
        log.info("toolbar %dx%d at %d,%d (actual %d,%d), area %s", tw, th, x, y, w.winfo_rootx(),
                 w.winfo_rooty(), self.rect)

    def _toolbar_spot(self, tw, th):
        r, gap = self.rect, self.app.px(14)
        mon = monitor_at(r.cx, r.cy, work=True)

        def clamp(v, lo, hi):
            return max(lo, min(v, hi))

        keepout = Rect(r.x - gap // 2, r.y - gap // 2, r.w + gap, r.h + gap)
        for x, y in [(r.right + gap, r.y), (r.x - gap - tw, r.y), (r.right - tw, r.bottom + gap),
                     (r.right - tw, r.y - gap - th)]:
            x = clamp(x, mon.x + 4, mon.right - tw - 4)
            y = clamp(y, mon.y + 4, mon.bottom - th - 4)
            if not Rect(x, y, tw, th).overlaps(keepout):
                return x, y
        # No room outside (e.g. the whole screen): sit inside the bottom-right
        # corner. The panel is invisible to screenshots, so it won't show up.
        return (clamp(r.right - tw - gap, mon.x + 4, mon.right - tw - 4),
                clamp(r.bottom - th - gap, mon.y + 4, mon.bottom - th - 4))

    def _drag_start(self, e):
        self._drag = (e.x_root - self.bar.winfo_x(), e.y_root - self.bar.winfo_y())

    def _drag_move(self, e):
        dx, dy = self._drag
        self.bar.geometry(f"+{e.x_root - dx}+{e.y_root - dy}")

    def _hint(self, text):
        self.hint.config(text=text or DEFAULT_HINT, fg=MUTED if text else DIM)

    # -- state
    def _set_busy(self, state):
        self.busy = state
        idle = state is None
        self.b_extend.set(enabled=state in (None, "extend"))
        self.b_auto.set(text="■  Stop" if state == "auto" else "▶  Auto", enabled=state in (None, "auto"))
        self.b_follow.set(text="■  Stop" if state == "follow" else "◎  Follow", enabled=state in (None, "follow"))
        self.b_undo.set(enabled=idle and self.can_undo)
        self.b_done.set(enabled=state not in ("finish", "starting"))
        self._frame_color(FRAME_AUTO if state == "auto" else FRAME_FOLLOW if state == "follow" else FRAME_IDLE)

    def _poll(self):
        if self.closed:
            return
        try:
            while True:
                kind, data = self.events.get_nowait()
                self._handle(kind, data)
                if self.closed:
                    return
        except queue.Empty:
            pass
        self.app.root.after(40, self._poll)

    def _handle(self, kind, data):
        if kind == "ready":
            self._set_busy(None)
            if self.app.args.auto and not self.auto_started:
                self.auto_started = True
                self.on_auto()
        elif kind == "busy":
            if self.busy == "finish":
                return
            self._set_busy(data)
            if data is None and self.auto_started and self.app.args.out:
                self.on_done()
        elif kind == "update":
            self.pv_photo = ImageTk.PhotoImage(data["preview"])
            self.pv.itemconfigure(self.pv_item, image=self.pv_photo)
            parts = data["steps"] + 1
            self.info.config(text=f"{data['width']:,} × {data['height']:,} px  ·  {parts} part{'s' * (parts != 1)}")
            if data["msg"]:
                self.status.config(text=data["msg"])
            self.can_undo = data["can_undo"]
            self.b_undo.set(enabled=self.busy is None and self.can_undo)
        elif kind == "final":
            self._finish(*data)
        elif kind == "fatal":
            self.close()
            self.app.error("Longshot stopped", data)
            self.app.quit()

    # -- buttons
    def on_extend(self):
        if self.busy in (None, "extend"):
            self.engine.cmds.put("extend")

    def _toggle(self, mode):
        if self.busy == mode:
            self.engine.stop_evt.set()
        elif self.busy is None:
            self.engine.stop_evt.clear()
            self._set_busy(mode)
            self.engine.cmds.put(mode)

    def on_auto(self):
        self._toggle("auto")

    def on_follow(self):
        self._toggle("follow")

    def on_undo(self):
        if self.busy is None and self.can_undo:
            self._set_busy("undo")
            self.engine.cmds.put("undo")

    def on_done(self):
        if self.busy == "finish":
            return
        self.engine.stop_evt.set()
        self.engine.cmds.put("finish")
        self._set_busy("finish")
        self.status.config(text="Saving…")

    def cancel(self):
        self.engine.stop_evt.set()
        self.engine.cmds.put("quit")
        self.close()
        self.app.quit()

    def _finish(self, img, path):
        copied = False
        if not self.app.args.out:
            try:
                copy_image_to_clipboard(img)
                copied = True
            except Exception:
                log.exception("clipboard")
        self.close()
        if self.app.args.out:
            self.app.quit()
        else:
            ResultWindow(self.app, img, path, copied)

    def close(self):
        if self.closed:
            return
        self.closed = True
        for wdg in self.edges + [self.bar]:
            try:
                wdg.destroy()
            except tk.TclError:
                pass


# ---------------------------------------------------------------- 4. result
class ResultWindow:
    def __init__(self, app: "App", img: Image.Image, path: Path, copied: bool):
        self.app, self.img, self.path = app, img, path
        px = app.px
        w = self.win = tk.Toplevel(app.root, bg=BG)
        w.title(f"Longshot · {img.width} × {img.height}")
        w.iconphoto(False, app.icon)

        top = tk.Frame(w, bg=BG, padx=px(18), pady=px(14))
        top.pack(fill="x")
        tk.Label(top, text="Long screenshot ready", bg=BG, fg=FG, font=(FONT, 13, "bold")).pack(anchor="w")
        sub = f"{img.width:,} × {img.height:,} px   ·   saved to Pictures\\Screenshots\\{path.name}"
        if path.parent != screenshots_dir():
            sub = f"{img.width:,} × {img.height:,} px   ·   saved to {path}"
        if copied:
            sub += "   ·   copied, paste it anywhere with Ctrl+V"
        self.sub = tk.Label(top, text=sub, bg=BG, fg=MUTED, font=(FONT, 9), justify="left")
        self.sub.pack(anchor="w")

        screen_h = monitor_at(*get_cursor(), work=True).h
        scale = min(1.0, px(600) / img.width, 30000 / img.height)
        pw, ph = max(1, round(img.width * scale)), max(1, round(img.height * scale))
        shown = img if scale == 1 else img.resize((pw, ph), Image.LANCZOS, reducing_gap=3.0)
        self.photo = ImageTk.PhotoImage(shown)
        view_h = min(ph, int(screen_h * 0.6))

        mid = tk.Frame(w, bg=PREVIEW_BG, padx=px(10), pady=px(10))
        mid.pack(fill="both", expand=True, padx=px(18))
        cv = self.cv = tk.Canvas(mid, width=pw, height=view_h, bg=PREVIEW_BG, highlightthickness=0,
                                 scrollregion=(0, 0, pw, ph), yscrollincrement=px(40))
        cv.pack(side="left", fill="both", expand=True)
        if ph > view_h:
            sb = tk.Scrollbar(mid, orient="vertical", command=cv.yview)
            sb.pack(side="right", fill="y")
            cv.configure(yscrollcommand=sb.set)
        item = cv.create_image(0, 0, anchor="n", image=self.photo)
        cv.bind("<Configure>", lambda e: (cv.coords(item, e.width // 2, 0),
                                          cv.configure(scrollregion=(0, 0, e.width, ph))))
        w.bind("<MouseWheel>", lambda e: cv.yview_scroll(int(-e.delta / 120) * 3, "units"))

        bar = tk.Frame(w, bg=BG, padx=px(18), pady=px(14))
        bar.pack(fill="x")
        FlatButton(bar, "New capture", self.new, bg=ACCENT, hover=ACCENT_HOVER, fg="white", bold=True).pack(side="left")
        for text, cmd in (("Copy", self.copy), ("Save as…", self.save_as), ("Open", self.open),
                          ("Show in folder", self.reveal)):
            FlatButton(bar, text, cmd).pack(side="left", padx=(px(6), 0))
        FlatButton(bar, "Close", self.close).pack(side="right")

        w.bind("<Escape>", lambda e: self.close())
        w.bind("<Control-c>", lambda e: self.copy())
        w.bind("<Control-s>", lambda e: self.save_as())
        w.protocol("WM_DELETE_WINDOW", self.close)
        w.update_idletasks()
        dark_title_bar(w)
        mon = monitor_at(*get_cursor(), work=True)
        ww, wh = w.winfo_reqwidth(), w.winfo_reqheight()
        w.geometry(f"+{mon.x + max(0, (mon.w - ww) // 2)}+{mon.y + max(0, (mon.h - wh) // 2)}")
        w.lift()
        w.attributes("-topmost", True)
        w.after(400, lambda: w.attributes("-topmost", False))
        w.focus_force()

    def _say(self, text):
        self.sub.config(text=text, fg=FG)

    def copy(self):
        try:
            copy_image_to_clipboard(self.img)
            self._say("Copied. Paste it anywhere with Ctrl+V.")
        except Exception as e:
            self._say(f"Couldn't copy: {e}")

    def save_as(self):
        name = filedialog.asksaveasfilename(parent=self.win, title="Save long screenshot",
                                            initialdir=str(self.path.parent), initialfile=self.path.name,
                                            defaultextension=".png",
                                            filetypes=[("PNG image", "*.png"), ("JPEG image", "*.jpg *.jpeg")])
        if not name:
            return
        p = Path(name)
        if p.suffix.lower() in (".jpg", ".jpeg"):
            self.img.convert("RGB").save(p, quality=92)
        else:
            self.img.save(p)
        self._say(f"Saved to {p}")

    def open(self):
        os.startfile(self.path)

    def reveal(self):
        subprocess.Popen(["explorer", "/select,", str(self.path)])

    def new(self):
        self.win.destroy()
        self.app.root.after(250, self.app.select)

    def close(self):
        self.win.destroy()
        self.app.quit()


# ---------------------------------------------------------------- app
class App:
    def __init__(self, args):
        self.args = args
        self.root = tk.Tk()
        self.root.withdraw()
        self.root.title("Longshot")
        self.root.report_callback_exception = self._tk_error
        self.scale = self.root.winfo_fpixels("1i") / 96.0
        self.icon = ImageTk.PhotoImage(make_icon(64))
        self.root.iconphoto(True, self.icon)

    def px(self, v: float) -> int:
        return int(round(v * self.scale))

    def run(self):
        delay = int(self.args.delay * 1000)
        if self.args.region:
            self.root.after(delay, lambda: CaptureUI(self, self.args.region))
        else:
            self.root.after(delay, self.select)
        self.root.mainloop()

    def select(self):
        Selector(self, on_done=lambda rect: CaptureUI(self, rect), on_cancel=self.quit)

    def quit(self):
        try:
            self.root.destroy()
        except tk.TclError:
            pass

    def error(self, title, text):
        ctypes.windll.user32.MessageBoxW(None, str(text), title, 0x10)

    def _tk_error(self, *exc):
        log.error("UI error:\n%s", "".join(traceback.format_exception(*exc)))
        self.error("Longshot", f"Something went wrong:\n{exc[1]}\n\nDetails are in longshot.log")


def _region(text: str) -> Rect:
    try:
        x, y, w, h = (int(v) for v in text.split(","))
    except ValueError:
        raise argparse.ArgumentTypeError("use x,y,width,height, e.g. 100,200,800,600")
    return Rect(x, y, w, h)


def main(argv=None):
    p = argparse.ArgumentParser(prog="longshot", description="Scrolling ('extend downward') screenshots.")
    p.add_argument("--delay", type=float, default=0, help="seconds to wait first (to open a menu, etc.)")
    p.add_argument("--region", type=_region, help="x,y,width,height in screen pixels; skips drawing the box")
    p.add_argument("--auto", action="store_true", help="start Auto right away")
    p.add_argument("--out", type=Path, help="with --auto: save to this file and exit when done")
    p.add_argument("--make-icon", type=Path, metavar="FILE.ico", help="write the app icon and exit")
    args = p.parse_args(argv)

    if args.make_icon:
        make_icon(256).save(args.make_icon, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (256, 256)])
        return
    logging.basicConfig(filename=HERE / "longshot.log", filemode="w", level=logging.INFO,
                        format="%(asctime)s %(levelname)s %(message)s")
    App(args).run()


if __name__ == "__main__":
    try:
        main()
    except SystemExit:
        raise
    except Exception:
        tb = traceback.format_exc()
        try:
            (HERE / "longshot.log").write_text(tb, encoding="utf-8")
        except OSError:
            pass
        ctypes.windll.user32.MessageBoxW(None, tb, "Longshot could not start", 0x10)
        sys.exit(1)
