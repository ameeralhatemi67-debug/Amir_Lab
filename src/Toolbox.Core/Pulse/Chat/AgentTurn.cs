using System.Diagnostics;
using System.Text;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>One agent turn that is running.</summary>
public interface IAgentTurn
{
    /// <summary>Finishes when the agent's program has exited (always with a <see cref="DoneEvent"/> reported first).</summary>
    Task Completion { get; }

    /// <summary>Stops the agent's program and everything it started.</summary>
    void Stop();
}

/// <summary>Starts agent turns (the real programs, or a stand-in in tests).</summary>
public interface IAgentRunner
{
    IAgentTurn Start(AgentLaunch launch, Action<AgentEvent> onEvent);
}

/// <summary>
/// Runs an agent's program for one turn: the message goes in on standard input, each line it writes
/// is read as it comes (stream-json for Claude Code, JSONL for Codex), and the turn ends when the
/// program exits. A program that exits without saying it is done reports what it wrote last.
/// </summary>
public sealed class ProcessAgentRunner : IAgentRunner
{
    public IAgentTurn Start(AgentLaunch launch, Action<AgentEvent> onEvent) => new ProcessTurn(launch, onEvent);

    private sealed class ProcessTurn : IAgentTurn
    {
        private readonly Process _process;
        private readonly StringBuilder _errors = new();
        private volatile bool _stopped;

        public ProcessTurn(AgentLaunch launch, Action<AgentEvent> onEvent)
        {
            var info = new ProcessStartInfo(launch.Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = launch.WorkingDir,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                StandardInputEncoding = new UTF8Encoding(false),
            };
            foreach (var a in launch.Args) info.ArgumentList.Add(a);
            info.Environment["NO_COLOR"] = "1";
            foreach (var (key, value) in launch.Env) info.Environment[key] = value;
            _process = new Process { StartInfo = info, EnableRaisingEvents = true };
            Completion = Run(launch, onEvent);
        }

        public Task Completion { get; }

        private async Task Run(AgentLaunch launch, Action<AgentEvent> onEvent)
        {
            bool done = false;
            string? lastText = null;
            void Report(AgentEvent e)
            {
                if (e is DoneEvent) done = true;
                if (e is TextEvent t) lastText = t.Text;
                try { onEvent(e); }
                catch (Exception ex) { Log.Error("Pulse chat could not take an agent's event", ex); }
            }

            try
            {
                if (!_process.Start())
                {
                    Report(new DoneEvent(null, [], $"{Agent.Name(launch.Agent)} did not start."));
                    return;
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Report(new DoneEvent(null, [], $"{Agent.Name(launch.Agent)} could not start: {ex.Message}"));
                return;
            }

            var errors = Task.Run(async () =>
            {
                try
                {
                    var buffer = new char[2048];
                    int n;
                    while ((n = await _process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        lock (_errors)
                        {
                            _errors.Append(buffer, 0, n);
                            if (_errors.Length > 8000) _errors.Remove(0, _errors.Length - 8000);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            });

            try
            {
                await _process.StandardInput.WriteAsync(launch.Input).ConfigureAwait(false);
                _process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Log.Warn($"Pulse chat could not write to {launch.Agent}: {ex.Message}");
            }

            var codex = launch.Agent == Agent.Codex ? new CodexStream() : null;
            try
            {
                while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    var events = codex is not null ? codex.Parse(line) : ClaudeStream.Parse(line);
                    foreach (var e in events)
                    {
                        if (e is NoticeEvent n)
                        {
                            if (n.Text.Length > 0) Log.Info($"Pulse chat, {launch.Agent}: {ClaudeStream.Short(n.Text, 300)}");
                            continue;
                        }
                        if (done && e is DoneEvent) continue;
                        Report(e);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }

            try { await _process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
            await errors.ConfigureAwait(false);
            if (!done)
            {
                string tail;
                lock (_errors) tail = _errors.ToString().Trim();
                int code = SafeExitCode();
                var why = _stopped ? "Stopped." : tail.Length > 0 ? ClaudeStream.Short(tail.Split('\n').Last(l => l.Trim().Length > 0), 300) : $"{Agent.Name(launch.Agent)} stopped (exit code {code}).";
                Report(new DoneEvent(lastText, [], why));
            }
            _process.Dispose();
        }

        private int SafeExitCode()
        {
            try { return _process.ExitCode; }
            catch (InvalidOperationException) { return -1; }
        }

        public void Stop()
        {
            _stopped = true;
            try
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
    }
}
