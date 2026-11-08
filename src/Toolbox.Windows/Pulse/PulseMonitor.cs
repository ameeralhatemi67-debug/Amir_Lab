using System.IO;
using System.Windows.Threading;
using Toolbox.Core.Pulse;

namespace Toolbox.Windows.Pulse;

/// <summary>Everything the dock shows at one moment.</summary>
internal sealed record PulseSnapshot(ClaudeState Claude, UsageSnapshot? Codex, ClaudeStatus Status, DateTimeOffset Now);

/// <summary>
/// Keeps the dock's numbers fresh: watches Pulse's Claude state file and Codex's session logs and
/// re-reads them (at most a few times a second), and checks again every 30 seconds so times and
/// limits that reset update on their own. Reading happens off the UI thread.
/// </summary>
internal sealed class PulseMonitor : IDisposable
{
    private readonly string _claudePath;
    private readonly string _codexHome;
    private readonly Func<bool> _readCodex;
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _tick;
    private readonly List<FileSystemWatcher> _watchers = [];
    private int _reading;
    private bool _again;

    public PulseMonitor(string claudePath, string codexHome, Func<bool> readCodex)
    {
        _claudePath = claudePath;
        _codexHome = codexHome;
        _readCodex = readCodex;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Refresh(); };
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _tick.Tick += (_, _) => Refresh();
    }

    public PulseSnapshot Current { get; private set; } = new(new ClaudeState(), null, ClaudeStatus.Idle, DateTimeOffset.UtcNow);

    /// <summary>When the user last opened Claude from the dock ("done" shows until then).</summary>
    public DateTimeOffset SeenAt { get; set; } = DateTimeOffset.UtcNow;

    public event Action<PulseSnapshot>? Changed;

    public void Start()
    {
        Watch(Path.GetDirectoryName(_claudePath)!, Path.GetFileName(_claudePath), subdirectories: false);
        Watch(CodexUsageReader.SessionsDir(_codexHome), "*.jsonl", subdirectories: true);
        _tick.Start();
        Refresh();
    }

    private void Watch(string dir, string filter, bool subdirectories)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var w = new FileSystemWatcher(dir, filter)
            {
                IncludeSubdirectories = subdirectories,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            FileSystemEventHandler poke = (_, _) => Poke();
            w.Changed += poke;
            w.Created += poke;
            w.Renamed += (_, _) => Poke();
            _watchers.Add(w);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Without a watcher the 30-second check still keeps it current.
        }
    }

    /// <summary>A file changed: re-read soon (many writes in a row become one read).</summary>
    public void Poke() => ToolboxHost.OnUi(() =>
    {
        _debounce.Stop();
        _debounce.Start();
    });

    /// <summary>Reads both sources now (in the background) and raises <see cref="Changed"/>.</summary>
    public void Refresh()
    {
        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            _again = true;
            return;
        }
        bool codex = _readCodex();
        var seen = SeenAt;
        Task.Run(() =>
        {
            var claude = ClaudeStateStore.Load(_claudePath);
            var usage = codex ? CodexUsageReader.ReadLatest(_codexHome) : null;
            var now = DateTimeOffset.UtcNow;
            return new PulseSnapshot(claude, usage, ClaudeStatus.Of(claude, now, seen), now);
        }).ContinueWith(t => ToolboxHost.OnUi(() =>
        {
            Interlocked.Exchange(ref _reading, 0);
            if (t.IsCompletedSuccessfully)
            {
                Current = t.Result;
                Changed?.Invoke(Current);
            }
            if (_again)
            {
                _again = false;
                Refresh();
            }
        }), TaskScheduler.Default);
    }

    public void Dispose()
    {
        _debounce.Stop();
        _tick.Stop();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
