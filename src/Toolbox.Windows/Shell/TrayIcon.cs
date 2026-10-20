using System.Windows.Forms;

namespace Toolbox.Windows.Shell;

/// <summary>Notification-area presence: the app stays available without a large main window.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();

    public TrayIcon(System.Drawing.Icon icon, string tooltip)
    {
        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = tooltip,
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) PrimaryAction?.Invoke();
        };
    }

    /// <summary>Invoked on a left click (opens the drop zone).</summary>
    public Action? PrimaryAction { get; set; }

    public void SetMenu(IEnumerable<(string Text, Action? Action)> items)
    {
        _menu.Items.Clear();
        foreach (var (text, action) in items)
        {
            if (text == "-")
            {
                _menu.Items.Add(new ToolStripSeparator());
                continue;
            }
            var item = new ToolStripMenuItem(text) { Enabled = action is not null };
            if (action is not null) item.Click += (_, _) => action();
            _menu.Items.Add(item);
        }
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
