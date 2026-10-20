using System.Runtime.InteropServices;

namespace DragHarness;

/// <summary>Real (injected) mouse and keyboard input through SendInput, in physical screen pixels.</summary>
internal static partial class RealInput
{
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_LSHIFT = 0xA0;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_ESCAPE = 0x1B;
    public const ushort VK_LWIN = 0x5B;
    public const ushort VK_D = 0x44;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint n, [In] INPUT[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT p);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial IntPtr WindowFromPoint(POINT point);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>The top-level window that is actually visible at a screen point.</summary>
    public static IntPtr RootWindowAt(double x, double y) =>
        GetAncestor(WindowFromPoint(new POINT { X = (int)Math.Round(x), Y = (int)Math.Round(y) }), 2 /* GA_ROOT */);

    /// <summary>A zero-distance mouse move: makes this process the source of the last input event, which Windows requires before it lets SetForegroundWindow succeed.</summary>
    public static void Nudge() => SendMouse(MOUSEEVENTF_MOVE, 0, 0);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static List<IntPtr> TopLevelWindows(int processId)
    {
        var list = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static bool IsVisible(IntPtr hwnd) => IsWindowVisible(hwnd);

    public static string WindowTitle(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static void PostClose(IntPtr hwnd) => PostMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

    public static POINT Cursor
    {
        get
        {
            GetCursorPos(out var p);
            return p;
        }
    }

    public static void MoveTo(double x, double y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = GetSystemMetrics(SM_CXVIRTUALSCREEN), vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1));
        int ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny);
    }

    /// <summary>Moves in small steps like a hand would, so drag thresholds and hover tracking fire.</summary>
    public static void Glide(double toX, double toY, int durationMs = 220, int steps = 14)
    {
        var from = Cursor;
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double ease = 1 - Math.Pow(1 - t, 2);
            MoveTo(from.X + (toX - from.X) * ease, from.Y + (toY - from.Y) * ease);
            Thread.Sleep(Math.Max(1, durationMs / steps));
        }
    }

    public static void LeftDown() => SendMouse(MOUSEEVENTF_LEFTDOWN, 0, 0);

    public static void LeftUp() => SendMouse(MOUSEEVENTF_LEFTUP, 0, 0);

    public static void Click()
    {
        LeftDown();
        Thread.Sleep(40);
        LeftUp();
    }

    public static void KeyDown(ushort vk) => SendKey(vk, 0);

    public static void KeyUp(ushort vk) => SendKey(vk, KEYEVENTF_KEYUP);

    public static void Tap(ushort vk, int holdMs = 60)
    {
        KeyDown(vk);
        Thread.Sleep(holdMs);
        KeyUp(vk);
    }

    /// <summary>Releases everything the harness might hold, so a failed scenario never leaves keys stuck.</summary>
    public static void ReleaseAll()
    {
        LeftUp();
        foreach (var vk in new[] { VK_SHIFT, VK_LSHIFT, VK_CONTROL, VK_LWIN }) KeyUp(vk);
    }

    private static void SendMouse(uint flags, int dx, int dy)
    {
        var input = new INPUT[1];
        input[0].type = INPUT_MOUSE;
        input[0].u.mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }

    private static void SendKey(ushort vk, uint flags)
    {
        var input = new INPUT[1];
        input[0].type = INPUT_KEYBOARD;
        input[0].u.ki = new KEYBDINPUT { wVk = vk, dwFlags = flags };
        SendInput(1, input, Marshal.SizeOf<INPUT>());
    }
}
