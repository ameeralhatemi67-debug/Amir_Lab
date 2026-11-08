using System.Windows.Forms;

namespace Toolbox.Windows.Shell;

/// <summary>One tray-menu entry: a command, a separator ("-"), or a sub-menu (<see cref="Children"/>).</summary>
internal sealed record TrayItem(string Text, Action? Action = null, IReadOnlyList<TrayItem>? Children = null, string? Shortcut = null, bool Bold = false)
{
    public static readonly TrayItem Separator = new("-");
}

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

    /// <summary>Invoked on a left click.</summary>
    public Action? PrimaryAction { get; set; }

    public void SetMenu(IEnumerable<TrayItem> items)
    {
        _menu.Items.Clear();
        Fill(_menu.Items, items);
    }

    private static void Fill(ToolStripItemCollection target, IEnumerable<TrayItem> items)
    {
        foreach (var entry in items)
        {
            if (entry.Text == "-")
            {
                target.Add(new ToolStripSeparator());
                continue;
            }
            var item = new ToolStripMenuItem(entry.Text) { ShortcutKeyDisplayString = entry.Shortcut ?? "" };
            if (entry.Bold) item.Font = new System.Drawing.Font(item.Font, System.Drawing.FontStyle.Bold);
            if (entry.Children is { Count: > 0 } children)
                Fill(item.DropDownItems, children);
            else
            {
                item.Enabled = entry.Action is not null;
                if (entry.Action is { } action) item.Click += (_, _) => action();
            }
            target.Add(item);
        }
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    /// <summary>New icon (after the accent colour changed).</summary>
    public void SetIcon(System.Drawing.Icon icon)
    {
        var old = _icon.Icon;
        _icon.Icon = icon;
        old?.Dispose();
    }

    /// <summary>Tooltip text and menu direction for the app's language (Arabic is right-to-left).</summary>
    public void SetLanguage(string tooltip, bool rightToLeft)
    {
        _icon.Text = tooltip;
        _menu.RightToLeft = rightToLeft ? RightToLeft.Yes : RightToLeft.No;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
