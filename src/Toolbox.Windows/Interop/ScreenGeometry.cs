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

    /// <summary>The wheel's square on the monitor under the pointer: sized by that monitor's scale, kept inside its work area.</summary>
    public static (int Left, int Top, int Size) PlaceWheel(Native.POINT pointer, MonitorArea monitor, double sizeDip)
    {
        int size = (int)Math.Round(sizeDip * monitor.Scale);
        var (left, top) = ClampCentered(pointer, size, monitor.WorkArea);
        return (left, top, size);
    }

    /// <summary>
    /// A window of <paramref name="width"/>×<paramref name="height"/> px centred horizontally on an
    /// anchor and <paramref name="offsetDip"/> below it, kept 8 DIP inside the anchor monitor's work area.
    /// </summary>
    public static (int Left, int Top) PlaceNear(Native.POINT anchor, int width, int height, double offsetDip, MonitorArea monitor)
    {
        var work = monitor.WorkArea;
        int margin = (int)(8 * monitor.Scale);
        int left = anchor.X - width / 2;
        int top = anchor.Y + (int)(offsetDip * monitor.Scale);
        left = Math.Clamp(left, work.Left + margin, Math.Max(work.Left + margin, work.Right - width - margin));
        top = Math.Clamp(top, work.Top + margin, Math.Max(work.Top + margin, work.Bottom - height - margin));
        return (left, top);
    }

    /// <summary>
    /// The monitor Windows would pick for a point with MONITOR_DEFAULTTONEAREST: the one containing
    /// it, else the closest. Used to test placement against simulated layouts.
    /// </summary>
    public static MonitorArea Nearest(Native.POINT p, IReadOnlyList<MonitorArea> monitors)
    {
        static long Distance(Native.POINT p, Native.RECT r)
        {
            long dx = p.X < r.Left ? r.Left - p.X : p.X >= r.Right ? p.X - r.Right + 1 : 0;
            long dy = p.Y < r.Top ? r.Top - p.Y : p.Y >= r.Bottom ? p.Y - r.Bottom + 1 : 0;
            return dx * dx + dy * dy;
        }
        return monitors.OrderBy(m => Distance(p, m.Bounds)).First();
    }
}
