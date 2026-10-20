using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Toolbox.Windows.Shell;

/// <summary>System-wide shortcut (default Ctrl+Alt+F) that opens the keyboard palette.</summary>
internal sealed partial class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const int Id = 0xF7B1;

    private readonly HwndSource _source;
    private readonly Action _pressed;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hwnd, int id);

    public GlobalHotkey(Action pressed)
    {
        _pressed = pressed;
        _source = new HwndSource(new HwndSourceParameters("FileToolboxHotkey") { WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _source.AddHook(Hook);
        Registered = RegisterHotKey(_source.Handle, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, 0x46 /* F */);
    }

    /// <summary>False when another program already owns the shortcut.</summary>
    public bool Registered { get; }

    public string Description => "Ctrl+Alt+F";

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == Id)
        {
            _pressed();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_source.Handle, Id);
        _source.Dispose();
    }
}
