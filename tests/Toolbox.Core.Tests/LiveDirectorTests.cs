using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Toolbox.Core.Pulse.Chat;

namespace Toolbox.Core.Tests;

/// <summary>
/// A chat opened and directed from outside, for real: a stand-in Pulse (the hub, the door and
/// the pipe under a test name) and the real ftb.exe as the director's command line and MCP
/// server, with the Codex installed on this PC as the guest (one short turn). Off unless
/// AMIRLAP_LIVE_CHAT=1, because it uses the user's own limits; it needs ftb built (Debug).
/// </summary>
public sealed class LiveDirectorTests
{
    [Fact]
    public async Task ADirectorOpensAChatAndCodexAnswersFromItsFolder()
    {
        if (Environment.GetEnvironmentVariable("AMIRLAP_LIVE_CHAT") != "1") return;
        var ftb = FindFtb();
        Assert.True(ftb is not null, "Build Toolbox.Cli (Debug) first: ftb.exe wasn't found.");
        var dir = Path.Combine(Path.GetTempPath(), "amirlap-livedirector-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(dir, "imp-film");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "imp-notes.txt"), "The secret word is BANANA-42.\n");
        var pipe = "AmirLapLive." + Guid.NewGuid().ToString("N");
        var gate = new object();
        using var hub = new ChatHub(new ChatStore(Path.Combine(dir, "chats")), new ProcessAgentRunner(), a => AgentLocator.Find(a), a => { lock (gate) a(); });
        var door = new ChatDoor(hub, a => { lock (gate) a(); }, a => AgentLocator.Find(a));
        using var server = new ChatPipeServer(door.HandleAsync, pipe);
        server.Start();
        try
        {
            var open = Json(await Ftb(ftb!, pipe, null, "chat", "open", "--as", "claude", "--session", "live-test-session", "--folder", folder, "--json",
                "--brief", "This is a quick test of Pulse chats. Read imp-notes.txt in this folder and reply with only the secret word in it."));
            Assert.True(open["ok"]!.GetValue<bool>(), open.ToJsonString());
            var id = open["chat"]!.GetValue<string>();
            lock (gate)
            {
                var chat = hub.Get(id)!;
                Assert.Equal(folder, chat.Info.Folder);
                hub.Answer(id, chat.Messages.First(m => m.Kind == ChatKind.Consent).Id, CardState.AllowRead);
            }

            JsonObject? reply = null;
            var waits = new List<string>();
            var until = DateTime.UtcNow.AddMinutes(6);
            while (DateTime.UtcNow < until)
            {
                var wait = Json(await Ftb(ftb!, pipe, null, "chat", "wait", id, "--as", "claude", "--session", "live-test-session", "--timeout", "60", "--json"));
                waits.Add(wait.ToJsonString());
                Assert.True(wait["ok"]!.GetValue<bool>(), wait.ToJsonString());
                if (wait["state"]!.GetValue<string>() is "reply" or "idle") { reply = wait; break; }
            }
            string log;
            lock (gate)
            {
                var chat = hub.Get(id)!;
                log = string.Join("\n", chat.Messages.Select(m => $"{m.Author}/{m.Kind}/{m.State}: {m.Text} {(m.Error is null ? "" : "[" + m.Error + "]")} steps={string.Join(" | ", m.Steps.Select(s => s.Text))}"))
                      + "\n\nwaits:\n" + string.Join("\n", waits);
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "amirlap-livedirector-last.txt"), log);
            Assert.True(reply is not null, log);
            Assert.True(reply!["text"]!.GetValue<string>().Contains("BANANA-42", StringComparison.Ordinal), log);

            // The same chat through the MCP server, as Claude Code or Codex would see it.
            var mcp = await Ftb(ftb!, pipe, string.Join('\n',
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"claude-code","version":"test"}}}""",
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "pulse_read", ["arguments"] = new JsonObject { ["chat"] = id } } }.ToJsonString()) + "\n", "mcp");
            var lines = mcp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (JsonObject)JsonNode.Parse(l)!).ToList();
            Assert.Equal("amir-lap-pulse", lines.First(l => l["id"]!.GetValue<int>() == 1)["result"]!["serverInfo"]!["name"]!.GetValue<string>());
            Assert.Contains("pulse_open", lines.First(l => l["id"]!.GetValue<int>() == 2).ToJsonString());
            Assert.Contains("BANANA-42", lines.First(l => l["id"]!.GetValue<int>() == 3).ToJsonString());

            var close = Json(await Ftb(ftb!, pipe, null, "chat", "close", id, "--as", "claude", "--session", "live-test-session", "--json"));
            Assert.Equal("closed", close["state"]!.GetValue<string>());
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "amirlap-livedirector-last.txt"), "\n\nmcp:\n" + mcp);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonObject Json(string output) => (JsonObject)JsonNode.Parse(output.Trim().Split('\n').Last())!;

    private static async Task<string> Ftb(string exe, string pipe, string? input, params string[] args)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment[ChatPipe.NameVariable] = pipe;
        info.Environment.Remove(ChatEnvironment.InsideChat);
        using var p = Process.Start(info)!;
        if (input is not null) await p.StandardInput.WriteAsync(input);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        var text = await output;
        Assert.True(text.Length > 0, "ftb wrote nothing. " + await errors);
        return text;
    }

    private static string? FindFtb()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "FileToolbox.sln"))) continue;
            var bin = Path.Combine(dir.FullName, "src", "Toolbox.Cli", "bin", "Debug");
            return Directory.Exists(bin) ? Directory.EnumerateFiles(bin, "ftb.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        }
        return null;
    }
}
