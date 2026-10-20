using System.Diagnostics;
using System.Text.Json;

namespace DragHarness;

internal sealed record WheelSegment(int Index, string ActionId, string Label, bool Enabled, double X, double Y);

internal sealed record WheelState(bool Visible, string? Ring, int RingIndex, double CenterX, double CenterY, double RevealLatencyMs, List<WheelSegment> Segments);

internal sealed record DropRecord(bool Accepted, string? ActionId, string? Ring, List<string> Paths, string AllowedEffects, string Effect, double RevealLatencyMs);

internal sealed record JobRecord(string ActionId, string State, List<string> Outputs, string? Error);

/// <summary>Starts the real app with diagnostics on and reads the state files it writes.</summary>
internal sealed class AppDriver : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly string _exe;
    private readonly string _dataDir;
    private Process? _process;

    public AppDriver(string exe, string dataDir)
    {
        _exe = exe;
        _dataDir = dataDir;
    }

    private string DiagDir => Path.Combine(_dataDir, "diagnostics");

    public int ProcessId => _process?.Id ?? 0;

    public void Start(TimeSpan timeout)
    {
        foreach (var existing in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(_exe)))
        {
            // A previously running instance would own the single-instance mutex.
            try { existing.Kill(); existing.WaitForExit(3000); } catch { }
        }

        var psi = new ProcessStartInfo(_exe, "--diag") { UseShellExecute = false };
        psi.Environment["FILETOOLBOX_DATA"] = _dataDir;
        _process = Process.Start(psi) ?? throw new InvalidOperationException("App did not start.");

        var ready = Path.Combine(DiagDir, "ready.json");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(ready)) { Thread.Sleep(300); return; }
            if (_process.HasExited) throw new InvalidOperationException($"App exited early (code {_process.ExitCode}).");
            Thread.Sleep(100);
        }
        throw new TimeoutException("App did not report ready.");
    }

    public WheelState? ReadWheel()
    {
        var path = Path.Combine(DiagDir, "wheel.json");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<WheelState>(File.ReadAllText(path), Json) : null;
            }
            catch (IOException) { Thread.Sleep(20); }
            catch (JsonException) { Thread.Sleep(20); }
        }
        return null;
    }

    public WheelState? WaitForWheel(Func<WheelState, bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var state = ReadWheel();
            if (state is not null && condition(state)) return state;
            Thread.Sleep(25);
        }
        return null;
    }

    public List<DropRecord> ReadDrops() => ReadLines<DropRecord>("drops.jsonl");

    public List<JobRecord> ReadJobs() => ReadLines<JobRecord>("jobs.jsonl");

    public string LogTail(int lines = 40)
    {
        var logs = Path.Combine(_dataDir, "logs");
        if (!Directory.Exists(logs)) return "";
        var latest = Directory.GetFiles(logs, "*.log").OrderBy(File.GetLastWriteTime).LastOrDefault();
        return latest is null ? "" : string.Join(Environment.NewLine, File.ReadAllLines(latest).TakeLast(lines));
    }

    private List<T> ReadLines<T>(string file)
    {
        var path = Path.Combine(DiagDir, file);
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<T>(l, Json)!).ToList();
    }

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch { }
    }
}
