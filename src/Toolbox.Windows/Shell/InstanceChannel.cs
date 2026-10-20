using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Windows.Shell;

/// <summary>
/// Hands command-line requests (Explorer menu, Send to, a second launch) to the running instance
/// over a per-user named pipe, so there is only ever one tray app.
/// </summary>
internal sealed class InstanceChannel : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public static string PipeName =>
        "FileToolbox." + string.Concat($"{Environment.UserDomainName}.{Environment.UserName}".Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    /// <summary>Sends the arguments to the running instance. Returns false when none answered.</summary>
    public static bool TrySend(string[] args)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(600);
                var line = JsonSerializer.Serialize(args) + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                Thread.Sleep(300); // the first instance may still be starting
            }
        }
        return false;
    }

    /// <summary>Listens for requests; <paramref name="received"/> is called on a worker thread.</summary>
    public void Start(Action<string[]> received)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new System.IO.StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is not null && JsonSerializer.Deserialize<string[]>(line) is { } args) received(args);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Instance channel error: {ex.Message}");
                    await Task.Delay(500);
                }
            }
        });
    }

    public void Dispose() => _cts.Cancel();
}
