using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Toolbox.Windows.Interop;

/// <summary>
/// Low-level keyboard hook that only observes: it never swallows keys and does no work in the
/// callback (Windows removes slow hooks), it just records the event and raises it asynchronously.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly Native.LowLevelKeyboardProc _proc;
    private readonly HashSet<int> _down = [];
    private IntPtr _hook;
    private System.Windows.Threading.Dispatcher? _dispatcher;

    public KeyboardHook()
    {
        _proc = HookCallback; // keep the delegate alive for the lifetime of the hook
    }

    /// <summary>Raised on the installing thread's dispatcher for a key that was not already down (no auto-repeat).</summary>
    public event Action<KeyEvent>? KeyPressed;

    public event Action<KeyEvent>? KeyReleased;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        using var module = Process.GetCurrentProcess().MainModule;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(module?.ModuleName), 0);
        if (_hook == IntPtr.Zero)
            throw new InvalidOperationException($"Could not install the keyboard hook (error {Marshal.GetLastWin32Error()}).");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)data.vkCode;
            int msg = (int)wParam;
            var evt = new KeyEvent(vk, (data.flags & Native.LLKHF_INJECTED) != 0, Stopwatch.GetTimestamp());

            if (msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN)
            {
                if (_down.Add(vk))
                    _dispatcher?.BeginInvoke(() => KeyPressed?.Invoke(evt));
            }
            else if (msg is Native.WM_KEYUP or Native.WM_SYSKEYUP)
            {
                if (_down.Remove(vk))
                    _dispatcher?.BeginInvoke(() => KeyReleased?.Invoke(evt));
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

internal readonly record struct KeyEvent(int VirtualKey, bool Injected, long Timestamp)
{
    public bool IsShift => VirtualKey is Native.VK_SHIFT or Native.VK_LSHIFT or Native.VK_RSHIFT;
    public bool IsControl => VirtualKey is Native.VK_CONTROL or Native.VK_LCONTROL or Native.VK_RCONTROL;
    public bool IsAlt => VirtualKey is Native.VK_MENU or Native.VK_LMENU or Native.VK_RMENU;
    public bool IsCapsLock => VirtualKey == Native.VK_CAPITAL;
    public bool IsEscape => VirtualKey == Native.VK_ESCAPE;

    public bool Matches(string activationKey) => activationKey.ToLowerInvariant() switch
    {
        "ctrl" or "control" => IsControl,
        "alt" => IsAlt,
        "capslock" => IsCapsLock,
        _ => IsShift,
    };
}
