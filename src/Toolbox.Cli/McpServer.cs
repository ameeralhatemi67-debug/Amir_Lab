using System.Text;
using System.Text.Json.Nodes;
using Toolbox.Core.Pulse.Chat;

namespace Toolbox.Cli;

/// <summary>
/// <c>ftb mcp</c>: Pulse's group chats as an MCP server over standard input and output (JSON-RPC,
/// one message per line), for Claude Code and Codex. Its tools are the same requests as
/// <c>ftb chat</c>; each one goes to the running Amir Lap through <see cref="ChatPipe"/>. The
/// caller is told apart by the client's name (claude-code, codex…) or the tool's <c>as</c>.
/// </summary>
internal static class McpServer
{
    private static readonly object WriteLock = new();
    private static string? _client;

    public static int Run()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var pending = new List<Task>();
        while (input.ReadLine() is { } line)
        {
            if (line.Trim().Length == 0) continue;
            JsonObject? msg;
            try { msg = JsonNode.Parse(line) as JsonObject; }
            catch (System.Text.Json.JsonException)
            {
                Write(output, Error(null, -32700, "Parse error"));
                continue;
            }
            if (msg is null) continue;
            // Tool calls can wait a long time: each runs on its own so pings and other calls aren't
            // held up. Everything else (initialize first of all) is answered in order, right away.
            if (msg["method"]?.GetValue<string>() != "tools/call")
            {
                if (Handle(msg) is { } now) Write(output, now);
                continue;
            }
            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(Task.Run(() =>
            {
                var reply = Handle(msg);
                if (reply is not null) Write(output, reply);
            }));
        }
        Task.WaitAll([.. pending], TimeSpan.FromSeconds(5));
        return 0;
    }

    private static void Write(StreamWriter output, JsonObject reply)
    {
        lock (WriteLock) output.WriteLine(reply.ToJsonString());
    }

    internal static JsonObject? Handle(JsonObject msg)
    {
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();
        var p = msg["params"] as JsonObject;
        if (id is null) return null; // a notification (initialized, cancelled): nothing to answer
        try
        {
            return method switch
            {
                "initialize" => Result(id, Initialize(p)),
                "ping" => Result(id, new JsonObject()),
                "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
                "tools/call" => Result(id, Call(p)),
                _ => Error(id, -32601, $"Method not found: {method}"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            return Error(id, -32602, ex.Message);
        }
    }

    private static JsonObject Initialize(JsonObject? p)
    {
        _client = p?["clientInfo"]?["name"]?.GetValue<string>();
        var version = p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "amir-lap-pulse", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0" },
            ["instructions"] = "Pulse (in Amir Lap on this PC) runs a group chat between you and the other AI coding agent here (Claude Code or Codex). " +
                "You open it from this session and direct it: pulse_open with a full brief, then pulse_wait, then pulse_say for each next request, and pulse_close when done. " +
                "The other agent works in your folder. The owner allows each chat in Pulse and can write to either of you; the owner's word comes first.",
        };
    }

    private static JsonArray Tools()
    {
        static JsonObject Prop(string type, string description) => new() { ["type"] = type, ["description"] = description };
        static JsonObject Tool(string name, string description, JsonObject props, params string[] required) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            },
        };
        JsonObject Who() => new() { ["type"] = "string", ["enum"] = new JsonArray("claude", "codex"), ["description"] = "Who you are. Optional when Pulse can tell from your app." };
        JsonObject Session() => Prop("string", "Your own session id (Claude Code: $CLAUDE_CODE_SESSION_ID). Lets Pulse give the other agent a copy of this conversation and keeps the chat yours.");
        JsonObject Chat() => Prop("string", "The chat id pulse_open gave you.");
        return
        [
            Tool("pulse_open", "Start a Pulse group chat with the other AI agent on this PC (Codex if you are Claude, Claude if you are Codex), working in your folder. You direct it. The owner must allow it in Pulse before the other agent starts.", new JsonObject
            {
                ["brief"] = Prop("string", "Everything the other agent needs: the goal, what's decided, rules and style, exact files, where outputs go, the first request and what done looks like."),
                ["folder"] = Prop("string", "The folder you work in (full path). Defaults to this server's folder."),
                ["outputs"] = Prop("string", "Where the other agent saves what it makes, inside the folder (e.g. assets/frames)."),
                ["permission"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("read", "edit"), ["description"] = "read (default) or edit when it must save files. The owner can change it when allowing the chat." },
                ["model"] = Prop("string", "The other agent's model (see pulse_models). Empty for its default."),
                ["title"] = Prop("string", "A short name for the chat."),
                ["budget"] = Prop("integer", "Messages you may send without the owner before Pulse asks the owner (default 8)."),
                ["as"] = Who(),
                ["session"] = Session(),
            }, "brief"),
            Tool("pulse_say", "Write to the other agent in a chat you direct. One clear request at a time.", new JsonObject { ["chat"] = Chat(), ["text"] = Prop("string", "Your message."), ["as"] = Who(), ["session"] = Session() }, "chat", "text"),
            Tool("pulse_wait", "Wait for what's new in your chat: the other agent's reply (with any pictures and changed files), the owner's words, or a card waiting for the owner. Call again while the state is working or needs_owner.", new JsonObject { ["chat"] = Chat(), ["timeout_seconds"] = Prop("integer", "How long to wait at most (default 50, up to 900)."), ["as"] = Who(), ["session"] = Session() }, "chat"),
            Tool("pulse_read", "Read a chat so far (changes nothing).", new JsonObject { ["chat"] = Chat(), ["last"] = Prop("integer", "How many messages (default 40).") }, "chat"),
            Tool("pulse_list", "List your Pulse chats.", new JsonObject { ["as"] = Who(), ["session"] = Session() }),
            Tool("pulse_close", "End a chat you direct, when the work is done.", new JsonObject { ["chat"] = Chat(), ["as"] = Who(), ["session"] = Session() }, "chat"),
            Tool("pulse_models", "The models the other agent can use in a chat.", new JsonObject { ["agent"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("claude", "codex") } }),
        ];
    }

    private static JsonObject Call(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>() ?? "";
        var args = p?["arguments"] as JsonObject ?? new JsonObject();
        string? Arg(string key) => args[key] is JsonValue v ? v.ToString() : null;
        var who = Agent.IsAgent(Arg("as")) ? Arg("as") : WhoFromClient();
        var caller = new ChatCommand.Caller(who, Arg("session") ?? SessionFor(who), null, Environment.GetEnvironmentVariable(ChatEnvironment.InsideChat) is { Length: > 0 } inside ? inside : null);
        var req = new JsonObject();
        caller.AddTo(req);
        int wait = 30;
        switch (name)
        {
            case "pulse_open":
                req["op"] = "open";
                req["brief"] = Arg("brief");
                req["folder"] = Arg("folder") is { Length: > 0 } f ? f : Directory.GetCurrentDirectory();
                foreach (var key in new[] { "outputs", "permission", "model", "title" })
                    if (Arg(key) is { Length: > 0 } v) req[key] = v;
                if (Arg("budget") is { Length: > 0 } b) req["budget"] = b;
                if (who is null) return Text("Say who you are: as claude or as codex.", error: true);
                break;
            case "pulse_say":
                req["op"] = "say";
                req["chat"] = Arg("chat");
                req["text"] = Arg("text");
                break;
            case "pulse_wait":
                req["op"] = "wait";
                req["chat"] = Arg("chat");
                int seconds = int.TryParse(Arg("timeout_seconds"), out var s) ? Math.Clamp(s, 1, ChatDoor.MaxWaitSeconds) : 50;
                req["timeout"] = seconds;
                wait = seconds + 20;
                break;
            case "pulse_read":
                req["op"] = "read";
                req["chat"] = Arg("chat");
                if (Arg("last") is { Length: > 0 } last) req["last"] = last;
                break;
            case "pulse_list":
                req["op"] = "list";
                break;
            case "pulse_close":
                req["op"] = "close";
                req["chat"] = Arg("chat");
                break;
            case "pulse_models":
                req["op"] = "models";
                req["guest"] = Arg("agent") ?? (who is null ? Agent.Codex : Agent.Other(who));
                break;
            default:
                return Text($"Unknown tool {name}.", error: true);
        }
        var answer = ChatPipe.Ask(req.ToJsonString(), TimeSpan.FromSeconds(wait));
        if (answer is null) return Text("Pulse isn't answering. Amir Lap must be running with Pulse on. Tell the user.", error: true);
        var node = JsonNode.Parse(answer) as JsonObject;
        var text = node?["text"]?.GetValue<string>() ?? answer;
        var state = node?["state"]?.GetValue<string>();
        var chat = node?["chat"]?.GetValue<string>();
        var head = chat is null || state is null ? "" : $"chat {chat} · state {state}\n";
        var result = Text(head + text, error: node?["ok"]?.GetValue<bool>() != true);
        if (node is not null) result["structuredContent"] = node.DeepClone();
        return result;
    }

    private static string? WhoFromClient()
    {
        var c = (_client ?? "").ToLowerInvariant();
        if (c.Contains("claude", StringComparison.Ordinal)) return Agent.Claude;
        if (c.Contains("codex", StringComparison.Ordinal)) return Agent.Codex;
        if (Environment.GetEnvironmentVariable("CLAUDECODE") == "1" || Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 }) return Agent.Claude;
        return null;
    }

    private static string? SessionFor(string? who) => who switch
    {
        Agent.Claude => Environment.GetEnvironmentVariable("CLAUDE_CODE_SESSION_ID") is { Length: > 0 } s ? s : null,
        Agent.Codex => Environment.GetEnvironmentVariable("CODEX_SESSION_ID") is { Length: > 0 } c ? c : Environment.GetEnvironmentVariable("CODEX_THREAD_ID") is { Length: > 0 } t ? t : null,
        _ => null,
    };

    private static JsonObject Text(string text, bool error = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = error,
    };

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };
}
