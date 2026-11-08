"""Print the client area (x,y,w,h in real pixels) of the first visible window
whose title contains the given text.   python tests/find_window.py "title" """
import ctypes
import ctypes.wintypes as wt
import sys

ctypes.windll.shcore.SetProcessDpiAwareness(2)
u = ctypes.windll.user32
want = sys.argv[1].lower()
found = []


@ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
def cb(hwnd, _):
    if u.IsWindowVisible(hwnd):
        n = u.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1)
        u.GetWindowTextW(hwnd, buf, n + 1)
        if want in buf.value.lower():
            found.append(hwnd)
    return True


u.EnumWindows(cb, 0)
if not found:
    sys.exit("not found")
hwnd = found[0]
rc = wt.RECT()
u.GetClientRect(hwnd, ctypes.byref(rc))
pt = wt.POINT(0, 0)
u.ClientToScreen(hwnd, ctypes.byref(pt))
print(f"{pt.x},{pt.y},{rc.right},{rc.bottom}")
