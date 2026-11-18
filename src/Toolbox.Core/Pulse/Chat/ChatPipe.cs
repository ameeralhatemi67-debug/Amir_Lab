using System.IO.Pipes;
using System.Text;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>
/// The two-way line into Pulse's chats while Amir Lap runs: a per-user named pipe. A request is one
/// line of JSON, the answer is one line of JSON, then the connection closes. Only programs running
/// as the same Windows user can connect. Amir Lap's command line (<c>ftb chat</c>) and its MCP
/// server (<c>ftb mcp</c>) are the clients.
/// </summary>
public static class ChatPipe
{
    /// <summary>Overrides the pipe's name (tests, and a stand-in Pulse for the live test).</summary>
    public const string NameVariable = "AMIRLAP_CHAT_PIPE";

    private static readonly UTF8Encoding Utf8 = new(false);

    public static string Name => Environment.GetEnvironmentVariable(NameVariable) is { Length: > 0 } name ? name :
        "AmirLap." + string.Concat($"{Environment.UserDomainName}.{Environment.UserName}".Select(c => char.IsLetterOrDigit(c) ? c : '_')) + ".chat";

    /// <summary>
    /// Sends one request and returns the answer, or null when no Pulse answered (Amir Lap isn't
    /// running, Pulse is off, or it is an older version without chats from outside).
    /// </summary>
    public static string? Ask(string request, TimeSpan answerWithin, string? name = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name ?? Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { client.Connect(2000); }
            catch (Exception ex) when (ex is TimeoutException or IOException) { return null; }
            var bytes = Utf8.GetBytes(request.ReplaceLineEndings(" ") + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            using var reader = new StreamReader(client, Utf8, false, 8192, leaveOpen: true);
            using var cts = new CancellationTokenSource(answerWithin);
            return reader.ReadLineAsync(cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return null;
        }
    }
}

/// <summary>Pulse's side of <see cref="ChatPipe"/>: answers each connection on its own, so a long wait never holds up another request.</summary>
public sealed class ChatPipeServer : IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Func<string, CancellationToken, Task<string>> _handle;
    private readonly string _name;
    private readonly CancellationTokenSource _cts = new();

    public ChatPipeServer(Func<string, CancellationToken, Task<string>> handle, string? name = null)
    {
        _handle = handle;
        _name = name ?? ChatPipe.Name;
    }

    public string Name => _name;

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    var connected = server;
                    server = null;
                    _ = Task.Run(() => Serve(connected));
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    return;
                }
                catch (Exception ex)
                {
                    server?.Dispose();
                    Log.Warn($"Pulse chat pipe: {ex.Message}");
                    try { await Task.Delay(500, _cts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        });
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        try
        {
            using (pipe)
            {
                using var reader = new StreamReader(pipe, Utf8, false, 8192, leaveOpen: true);
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null) return;
                string answer;
                try { answer = await _handle(line, _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error("Pulse chat pipe could not answer a request", ex);
                    answer = """{"ok":false,"error":"Pulse could not answer that request. See Amir Lap's log."}""";
                }
                var bytes = Utf8.GetBytes(answer.ReplaceLineEndings(" ") + "\n");
                await pipe.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                await pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
                try { pipe.WaitForPipeDrain(); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The caller went away (its command was stopped, or it gave up waiting).
        }
    }

    public void Dispose() => _cts.Cancel();
}
