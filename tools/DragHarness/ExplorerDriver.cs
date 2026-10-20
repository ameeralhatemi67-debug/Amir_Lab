using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;

namespace DragHarness;

/// <summary>Opens a real File Explorer window on a folder and locates items with UI Automation.</summary>
internal sealed class ExplorerDriver : IDisposable
{
    private readonly string _folder;
    private AutomationElement? _window;

    public ExplorerDriver(string folder)
    {
        _folder = folder;
    }

    public AutomationElement Window => _window ?? throw new InvalidOperationException("Explorer window not open.");

    public void Open(TimeSpan timeout)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
        var name = Path.GetFileName(_folder);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass"));
            foreach (AutomationElement w in windows)
            {
                if (w.Current.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                {
                    _window = w;
                    Thread.Sleep(900); // let the view finish populating
                    return;
                }
            }
            Thread.Sleep(150);
        }
        throw new TimeoutException($"Explorer window for '{name}' did not appear.");
    }

    public void BringToFront()
    {
        var hwnd = new IntPtr(Window.Current.NativeWindowHandle);
        RealInput.SetForegroundWindow(hwnd);
        if (Window.TryGetCurrentPattern(WindowPattern.Pattern, out var p) && p is WindowPattern wp
            && wp.Current.WindowVisualState == WindowVisualState.Minimized)
            wp.SetWindowVisualState(WindowVisualState.Normal);
        Thread.Sleep(200);
    }

    /// <summary>Screen rectangle (physical pixels) of the item for a file, matching with or without extension.</summary>
    public Rect FindItem(string fileName, TimeSpan timeout)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            foreach (var candidate in new[] { fileName, stem })
            {
                var item = Window.FindFirst(TreeScope.Descendants, new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.NameProperty, candidate)));
                if (item is not null)
                {
                    var rect = item.Current.BoundingRectangle;
                    if (!rect.IsEmpty && rect.Width > 0) return rect;
                }
            }
            Thread.Sleep(200);
        }
        throw new TimeoutException($"Item '{fileName}' not found in Explorer.");
    }

    /// <summary>Grab point inside an item: the name area in details view, the centre otherwise.</summary>
    public static Point GrabPoint(Rect item) =>
        item.Width > item.Height * 4
            ? new Point(item.Left + Math.Min(60, item.Width / 4), item.Top + item.Height / 2)
            : new Point(item.Left + item.Width / 2, item.Top + item.Height / 2);

    public Rect Bounds => Window.Current.BoundingRectangle;

    public void Dispose()
    {
        try
        {
            if (_window is not null && _window.TryGetCurrentPattern(WindowPattern.Pattern, out var p) && p is WindowPattern wp)
                wp.Close();
        }
        catch (ElementNotAvailableException) { }
    }
}
