namespace Toolbox.Windows.Interop;

/// <summary>Monitor information for a point, in physical pixels.</summary>
internal readonly record struct MonitorArea(Native.RECT Bounds, Native.RECT WorkArea, double Scale);

internal static class ScreenGeometry
{
    public static Native.POINT CursorPosition()
    {
        Native.GetCursorPos(out var p);
        return p;
    }

    public static MonitorArea MonitorAt(Native.POINT point)
    {
        var monitor = Native.MonitorFromPoint(point, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        double scale = 1.0;
        if (Native.GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var dpiX, out _) == 0 && dpiX > 0)
            scale = dpiX / 96.0;
        return new MonitorArea(info.rcMonitor, info.rcWork, scale);
    }

    /// <summary>
    /// Places a square of <paramref name="size"/> pixels centred on <paramref name="center"/>, shifted
    /// as little as needed to stay fully inside <paramref name="work"/>. Returns the top-left corner.
    /// </summary>
    public static (int Left, int Top) ClampCentered(Native.POINT center, int size, Native.RECT work)
    {
        int left = center.X - size / 2;
        int top = center.Y - size / 2;
        left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - size));
        top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - size));
        return (left, top);
    }
}
