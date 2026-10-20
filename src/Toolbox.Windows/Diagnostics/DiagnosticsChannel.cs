using System.IO;
using System.Text.Json;
using Toolbox.Core;

namespace Toolbox.Windows.Diagnostics;

/// <summary>
/// Optional, local-only state files used by the automated drag harness (enabled with --diag or
/// FILETOOLBOX_DIAG=1). They describe the wheel geometry and each drop; no file contents.
/// </summary>
internal sealed class DiagnosticsChannel
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();

    public DiagnosticsChannel(bool enabled)
    {
        Enabled = enabled;
        if (enabled)
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDir);
            foreach (var f in new[] { WheelFile, DropsFile, ReadyFile, JobsFile })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    public bool Enabled { get; }
    public static string WheelFile => Path.Combine(AppPaths.DiagnosticsDir, "wheel.json");
    public static string DropsFile => Path.Combine(AppPaths.DiagnosticsDir, "drops.jsonl");
    public static string ReadyFile => Path.Combine(AppPaths.DiagnosticsDir, "ready.json");
    public static string JobsFile => Path.Combine(AppPaths.DiagnosticsDir, "jobs.jsonl");

    public void WriteReady(int processId) => Write(ReadyFile, new { processId, time = DateTimeOffset.Now });

    public void WriteWheel(object state) => Write(WheelFile, state);

    public void AppendDrop(object drop) => Append(DropsFile, drop);

    public void AppendJob(object job) => Append(JobsFile, job);

    private void Write(string path, object value)
    {
        if (!Enabled) return;
        lock (_gate)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, path, overwrite: true);
        }
    }

    private void Append(string path, object value)
    {
        if (!Enabled) return;
        lock (_gate)
            File.AppendAllText(path, JsonSerializer.Serialize(value) + "\n");
    }
}
