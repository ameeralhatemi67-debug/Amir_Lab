"""Tiny mouse/screenshot driver for UI tests.

    python tests/drive.py shot OUT.png [x y w h]
    python tests/drive.py drag X0 Y0 X1 Y1
    python tests/drive.py click X Y
"""
import ctypes
import sys
import time

ctypes.windll.shcore.SetProcessDpiAwareness(2)
import mss  # noqa: E402
from PIL import Image  # noqa: E402

u = ctypes.windll.user32
LEFTDOWN, LEFTUP = 0x0002, 0x0004


def move(x, y):
    u.SetCursorPos(int(x), int(y))


def down():
    u.mouse_event(LEFTDOWN, 0, 0, 0, 0)


def up():
    u.mouse_event(LEFTUP, 0, 0, 0, 0)


cmd, args = sys.argv[1], sys.argv[2:]
if cmd == "shot":
    with (getattr(mss, "MSS", None) or mss.mss)() as s:
        mon = s.monitors[0]
        if len(args) == 5:
            x, y, w, h = map(int, args[1:])
            mon = {"left": x, "top": y, "width": w, "height": h}
        g = s.grab(mon)
        Image.frombytes("RGB", g.size, g.bgra, "raw", "BGRX").save(args[0])
elif cmd == "drag":
    x0, y0, x1, y1 = map(int, args)
    move(x0, y0); time.sleep(0.1); down(); time.sleep(0.1)
    for i in range(1, 21):
        move(x0 + (x1 - x0) * i / 20, y0 + (y1 - y0) * i / 20); time.sleep(0.015)
    time.sleep(0.1); up()
elif cmd == "wheel":  # wheel X Y NOTCHES (positive = up)
    x, y, n = map(int, args)
    move(x, y); time.sleep(0.08)
    for _ in range(abs(n)):
        u.mouse_event(0x0800, 0, 0, 120 if n > 0 else -120, 0); time.sleep(0.01)
elif cmd == "click":
    x, y = map(int, args)
    move(x, y); time.sleep(0.08); down(); time.sleep(0.05); up()
