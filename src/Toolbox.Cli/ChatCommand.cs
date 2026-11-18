using System.Text;
using System.Text.Json.Nodes;
using Toolbox.Core.Pulse.Chat;

namespace Toolbox.Cli;

/// <summary>
/// <c>ftb chat</c>: Claude Code or Codex opens a Pulse chat with the other agent from its own
/// session and directs it. Every command is one request to the running Amir Lap (see
/// <see cref="ChatPipe"/>); the answer is printed as plain text (or JSON with --json).
/// </summary>
internal static class ChatCommand
{
    public const int NotRunning = 3;

    public static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Help);
            return args.Length == 0 ? 1 : 0;
        }
        var verb = args[0].ToLowerInvariant();
        var (options, words) = Parse(args[1..]);
        bool json = options.ContainsKey("json");

        if (verb == "setup") return SkillSetup.Run(options);

        if (Environment.GetEnvironmentVariable(ChatEnvironment.InsideChat) is { Length: > 0 } && verb is "open" or "say" or "wait" or "close")
            return Fail("You are running inside a Pulse chat, so you can't open or direct one. Answer in your reply instead.");

        var caller = Caller.From(options);
        var req = new JsonObject { ["op"] = verb };
        caller.AddTo(req);
        int timeout = 30;
        switch (verb)
        {
            case "open":
                var brief = Text(options, "brief", words);
                if (brief is null) return Fail("Give the brief: --brief \"…\", --brief-file <file>, or --brief - to read it from standard input.");
                req["brief"] = brief;
                req["folder"] = Path.GetFullPath(options.GetValueOrDefault("folder") ?? Directory.GetCurrentDirectory());
                Copy(options, req, "guest", "title", "model", "permission", "outputs");
                if (options.GetValueOrDefault("budget") is { } budget) req["budget"] = budget;
                if (options.ContainsKey("no-notes")) req["notes"] = false;
                if (caller.As is null) return Fail("Say who you are: --as claude or --as codex.");
                break;
            case "say":
                if (words.Count == 0) return Fail("Usage: ftb chat say <chat> <text> (or --file <file>, or - to read standard input)");
                req["chat"] = words[0];
                var text = Text(options, "text", words.Skip(1).ToList(), fileKey: "file");
                if (text is null) return Fail("Say something: ftb chat say <chat> \"…\"");
                req["text"] = text;
                break;
            case "wait":
                if (words.Count == 0) return Fail("Usage: ftb chat wait <chat> [--timeout seconds]");
                req["chat"] = words[0];
                int seconds = int.TryParse(options.GetValueOrDefault("timeout"), out var t) ? Math.Clamp(t, 1, ChatDoor.MaxWaitSeconds) : 45;
                req["timeout"] = seconds;
                timeout = seconds + 20;
                break;
            case "read":
                if (words.Count == 0) return Fail("Usage: ftb chat read <chat> [--last n]");
                req["chat"] = words[0];
                if (options.GetValueOrDefault("last") is { } last) req["last"] = last;
                break;
            case "close":
                if (words.Count == 0) return Fail("Usage: ftb chat close <chat>");
                req["chat"] = words[0];
                break;
            case "models":
                req["guest"] = words.FirstOrDefault() ?? options.GetValueOrDefault("guest") ?? (caller.As is { } me ? Agent.Other(me) : Agent.Codex);
                break;
            case "list":
            case "ping":
                break;
            default:
                return Fail($"Unknown chat command '{verb}'. Run 'ftb chat help'.");
        }

        var answer = ChatPipe.Ask(req.ToJsonString(), TimeSpan.FromSeconds(timeout));
        if (answer is null)
        {
            Console.Error.WriteLine("ftb: Pulse isn't answering. Start Amir Lap and make sure Pulse is on (Amir Lap › Pulse), then try again.");
            return NotRunning;
        }
        if (json)
        {
            Console.WriteLine(answer);
        }
        else
        {
            var node = JsonNode.Parse(answer) as JsonObject;
            Console.WriteLine(node?["text"]?.GetValue<string>() ?? answer);
        }
        return JsonNode.Parse(answer)?["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    /// <summary>Who is calling: the agent, its session and its program, from --as/--session or the environment its agent sets.</summary>
    internal sealed record Caller(string? As, string? Session, int? Pid, string? Inside)
    {
        public static Caller From(IReadOnlyDictionary<string, string> options)
        {
            static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
            var claudeSession = Env("CLAUDE_CODE_SESSION_ID");
            var codexSession = Env("CODEX_SESSION_ID") ?? Env("CODEX_THREAD_ID");
            var asked = options.GetValueOrDefault("as")?.ToLowerInvariant();
            var who = Agent.IsAgent(asked) ? asked
                : claudeSession is not null || Env("CLAUDECODE") == "1" ? Agent.Claude
                : codexSession is not null ? Agent.Codex
                : null;
            var session = options.GetValueOrDefault("session") ?? (who == Agent.Claude ? claudeSession : who == Agent.Codex ? codexSession : null);
            int? pid = who == Agent.Claude && int.TryParse(Env("CLAUDE_PID"), out var p) ? p : null;
            return new Caller(who, session, pid, Env(ChatEnvironment.InsideChat));
        }

        public void AddTo(JsonObject req)
        {
            if (As is not null) req["as"] = As;
            if (Session is not null) req["session"] = Session;
            if (Pid is not null) req["pid"] = Pid;
            if (Inside is not null) req["inside"] = Inside;
        }
    }

    /// <summary>Options (--name value, or --flag) and the words in between.</summary>
    internal static (Dictionary<string, string> Options, List<string> Words) Parse(string[] args)
    {
        var flags = new HashSet<string> { "json", "no-notes", "mcp", "print", "claude", "codex" };
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Length > 2)
            {
                var name = a[2..];
                int eq = name.IndexOf('=');
                if (eq > 0) options[name[..eq]] = name[(eq + 1)..];
                else if (flags.Contains(name) || i + 1 >= args.Length) options[name] = "true";
                else options[name] = args[++i];
            }
            else words.Add(a);
        }
        return (options, words);
    }

    /// <summary>Text from --key, --key-file, "-" (standard input) or the remaining words.</summary>
    private static string? Text(IReadOnlyDictionary<string, string> options, string key, List<string> words, string? fileKey = null)
    {
        string? text = options.GetValueOrDefault(key);
        var file = options.GetValueOrDefault(fileKey ?? key + "-file");
        if (file is not null) text = file == "-" ? "-" : File.ReadAllText(Path.GetFullPath(file));
        if (text == "-" || (text is null && words.Count == 1 && words[0] == "-")) text = ReadInput();
        else if (text is null && words.Count > 0) text = string.Join(' ', words);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string ReadInput()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        return stdin.ReadToEnd();
    }

    private static void Copy(IReadOnlyDictionary<string, string> options, JsonObject req, params string[] keys)
    {
        foreach (var k in keys)
            if (options.GetValueOrDefault(k) is { Length: > 0 } v) req[k] = v;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("ftb: " + message);
        return 1;
    }

    public const string Help = """
        Pulse group chats, opened and directed from your own session (Claude Code or Codex).
        Amir Lap must be running with Pulse on. The owner allows each chat in Pulse.

          ftb chat open --as claude|codex --brief "…" | --brief-file f | --brief -
                        [--folder dir] [--guest codex|claude] [--model m] [--permission read|edit]
                        [--outputs dir] [--title t] [--budget n] [--no-notes]
                                            Start a chat with the other agent in your folder
          ftb chat say <chat> "…" | --file f | -      Write to the other agent
          ftb chat wait <chat> [--timeout 45]         Wait for its reply, the owner's words or a card
          ftb chat read <chat> [--last 40]            The chat so far (changes nothing)
          ftb chat list                               Your chats
          ftb chat close <chat>                       End a chat
          ftb chat models [claude|codex]              The models the other agent can use
          ftb chat setup [--print] [--mcp]            Install the skill for Claude Code and Codex
                                                      (--mcp also adds the MCP server to both)
          ftb mcp                                     The same commands as an MCP server (stdio)

        Add --json for the raw answer. --as and --session come from your agent's environment when
        it sets them (Claude Code does). Exit codes: 0 done, 1 refused or wrong, 3 Pulse not running.
        """;
}
