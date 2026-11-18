using System.Text.Json;
using System.Text.Json.Nodes;
using Toolbox.Core.Pulse.Chat;

namespace Toolbox.Core.Tests;

/// <summary>
/// Chats an agent opens and directs from its own session (Claude Code or Codex), the models a chat
/// runs with, the door the command line and MCP server use, the pipe, and the copy of the
/// director's conversation. The agents are stand-ins.
/// </summary>
public sealed class PulseDirectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amirlap-director-" + Guid.NewGuid().ToString("N"));

    public PulseDirectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- stand-ins ------------------------------------------------------------------------------

    private sealed class FakeTurn(AgentLaunch launch, Action<AgentEvent> onEvent) : IAgentTurn
    {
        private readonly TaskCompletionSource _done = new();
        public AgentLaunch Launch { get; } = launch;
        public Task Completion => _done.Task;
        public void Stop() => _done.TrySetResult();

        public void Reply(string text, string session = "guest-1")
        {
            onEvent(new SessionEvent(session));
            onEvent(new TextEvent(text));
            onEvent(new DoneEvent(text, [], null));
            _done.TrySetResult();
        }
    }

    private sealed class FakeRunner : IAgentRunner
    {
        public List<FakeTurn> Turns { get; } = [];
        public IAgentTurn Start(AgentLaunch launch, Action<AgentEvent> onEvent)
        {
            var t = new FakeTurn(launch, onEvent);
            lock (Turns) Turns.Add(t);
            return t;
        }
    }

    private (ChatHub Hub, FakeRunner Runner, string Folder) Hub()
    {
        var runner = new FakeRunner();
        var hub = new ChatHub(new ChatStore(Path.Combine(_dir, "chats")), runner, a => a + ".exe", a => a()) { CodexPictures = null };
        var folder = Path.Combine(_dir, "project");
        Directory.CreateDirectory(folder);
        return (hub, runner, folder);
    }

    private static ChatInfo Directed(string folder, string director = Agent.Claude, int budget = 8, string permission = ChatPermission.Read) => new()
    {
        Folder = folder,
        Agents = [Agent.Other(director)],
        Director = director,
        DirectorSession = "director-session",
        Permission = permission,
        Outputs = "assets/frames",
        DirectorBudget = budget,
        Models = new() { [Agent.Other(director)] = "gpt-6.2-sol" },
    };

    // ---- models -----------------------------------------------------------------------------------

    [Fact]
    public void CodexModelsComeFromItsCacheWithItsDefaultFirst()
    {
        var home = Path.Combine(_dir, "codex");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "config.toml"), "model = \"gpt-6.1-sol\"\nmodel_reasoning_effort = \"max\"\n");
        File.WriteAllText(Path.Combine(home, "models_cache.json"), JsonSerializer.Serialize(new
        {
            models = new object[]
            {
                new { slug = "gpt-6.1-sol", display_name = "GPT-6.1-Sol", visibility = "list", priority = 5 },
                new { slug = "gpt-reserve", display_name = "GPT-Reserve", visibility = "hide", priority = 4 },
                new { slug = "gpt-6.2-sol", display_name = "GPT-6.2-Sol", visibility = "list", priority = 1 },
            },
        }));
        var list = AgentModels.Codex(home);
        Assert.Equal(["", "gpt-6.2-sol", "gpt-6.1-sol"], list.Select(m => m.Id));
        Assert.Equal("Default (GPT-6.1-Sol)", list[0].Name);
        Assert.Equal("gpt-6.2-sol", AgentModels.Pick(Agent.Codex, "GPT-6.2-SOL", home));
        Assert.Equal("gpt-7-new", AgentModels.Pick(Agent.Codex, "gpt-7-new", home));
        Assert.Equal("", AgentModels.Pick(Agent.Codex, "default", home));
        Assert.Equal("", AgentModels.Pick(Agent.Codex, "rm -rf /", home));
        Assert.Equal("opus", AgentModels.Pick(Agent.Claude, "Opus"));
        Assert.Equal("Default", AgentModels.Codex(Path.Combine(_dir, "nothing"))[0].Name);
    }

    [Fact]
    public void EachAgentRunsWithItsModelAndKnowsTheChatItIsIn()
    {
        var chat = new ChatInfo { Agents = [Agent.Claude, Agent.Codex], Permission = ChatPermission.Edit, Models = new() { [Agent.Claude] = "opus", [Agent.Codex] = "gpt-6.2-sol" } };
        var claude = AgentCommands.Claude("claude.exe", chat, "x", "hi");
        Assert.Equal("opus", After(claude.Args, "--model"));
        Assert.Equal(chat.Id, claude.Env[ChatEnvironment.InsideChat]);
        var codex = AgentCommands.Codex("codex.exe", chat, "x", "hi");
        Assert.Contains("model=\"gpt-6.2-sol\"", codex.Args);
        Assert.Equal("-", codex.Args[^1]);
        Assert.Equal(chat.Id, codex.Env[ChatEnvironment.InsideChat]);
        var plain = AgentCommands.Claude("claude.exe", new ChatInfo { Agents = [Agent.Claude] }, "x", "hi");
        Assert.DoesNotContain("--model", plain.Args);

        // An engine without the code-mode host beside it runs commands the plain way.
        Assert.Contains("features.code_mode_host=false", codex.Args);
        var engine = Path.Combine(_dir, "app", "resources");
        Directory.CreateDirectory(engine);
        File.WriteAllText(Path.Combine(engine, "codex.exe"), "");
        File.WriteAllText(Path.Combine(engine, "codex-code-mode-host.exe"), "");
        Assert.DoesNotContain("features.code_mode_host=false", AgentCommands.Codex(Path.Combine(engine, "codex.exe"), chat, "x", "hi").Args);
    }

    [Fact]
    public void AClaudeGuestMayReadTheDirectorsNotesOutsideItsFolder()
    {
        var info = Directed(@"D:\Work\film", director: Agent.Codex);
        info.DirectorNotes = Path.Combine(_dir, "chats", info.Id + "-files", "director-conversation.md");
        var launch = AgentCommands.Claude("claude.exe", info, @"D:\Work\film", "hi");
        Assert.Equal(Path.GetDirectoryName(info.DirectorNotes), After(launch.Args, "--add-dir"));
        info.DirectorNotes = @"D:\Work\film\notes\copy.md";
        Assert.DoesNotContain("--add-dir", AgentCommands.Claude("claude.exe", info, @"D:\Work\film", "hi").Args);
    }

    // ---- what the guest is told -----------------------------------------------------------------

    [Fact]
    public void TheGuestKnowsWhoDirectsWhereToSaveAndThatTheOwnerComesFirst()
    {
        var info = Directed(@"D:\Work\film");
        info.DirectorNotes = @"C:\notes\director-conversation.md";
        var text = ChatRules.Preamble(info, Agent.Codex);
        Assert.Contains("Claude, another AI coding agent (made by Anthropic), directs this chat", text);
        Assert.Contains("Don't write @claude", text);
        Assert.Contains("owner's word comes first", text);
        Assert.Contains(@"same folder: D:\Work\film", text);
        Assert.Contains("assets/frames/", text);
        Assert.Contains(@"C:\notes\director-conversation.md", text);
        Assert.Contains("read-only", text);

        var messages = new List<ChatMessage>
        {
            new() { Author = Agent.Claude, Text = "Make frame 1.", To = Agent.Codex },
            new() { Author = ChatAuthor.User, Text = "Keep the imp's colours." },
        };
        var digest = ChatRules.Digest(info, messages, Agent.Codex);
        Assert.Contains("Claude (directs this chat) (to you): Make frame 1.", digest);
        Assert.Contains("Owner: Keep the imp's colours.", digest);
        // One message from the owner alone still says it is the owner's.
        Assert.StartsWith("[New in this chat", ChatRules.Digest(info, [messages[1]], Agent.Codex));
    }

    // ---- the room -----------------------------------------------------------------------------------

    [Fact]
    public void ADirectedChatWaitsForTheOwnerThenRunsOnlyTheGuest()
    {
        var (hub, runner, folder) = Hub();
        var chat = hub.CreateDirected(Directed(folder), "Brief: make 3 key frames of the imp waving.");
        var id = chat.Info.Id;
        Assert.Empty(runner.Turns);
        var consent = Assert.Single(chat.Messages, m => m.Kind == ChatKind.Consent);
        Assert.Equal(CardState.Open, consent.State);
        Assert.Equal(1, hub.NeedsYou);
        Assert.Equal(Agent.Codex, chat.Info.Lead);
        Assert.Equal(1, chat.Info.DirectorTurns);

        hub.Answer(id, consent.Id, CardState.AllowEdit);
        Assert.Equal(ChatPermission.Edit, chat.Info.Permission);
        var turn = Assert.Single(runner.Turns);
        Assert.Equal(Agent.Codex, turn.Launch.Agent);
        Assert.Equal(folder, turn.Launch.WorkingDir);
        Assert.Contains("sandbox_mode=\"workspace-write\"", turn.Launch.Args);
        Assert.Contains("model=\"gpt-6.2-sol\"", turn.Launch.Args);
        Assert.Contains("Claude (directs this chat) (to you): Brief: make 3 key frames", turn.Launch.Input);

        turn.Reply("Frame 1 is in assets/frames/01.png.");
        var reply = chat.Messages.Last(m => m.Author == Agent.Codex);
        Assert.Equal(Agent.Claude, reply.To);
        Assert.Single(runner.Turns); // the director is never run by Pulse

        Assert.Equal(DirectResult.Sent, hub.Direct(id, "Good. Now frame 2, arm higher."));
        Assert.Equal(2, runner.Turns.Count);
        Assert.Contains("resume", runner.Turns[1].Launch.Args);
        Assert.Contains("arm higher", runner.Turns[1].Launch.Input);
        Assert.DoesNotContain("frame 1 is in", runner.Turns[1].Launch.Input, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDirectorsBudgetEndsInACardTheOwnerAnswers()
    {
        var (hub, runner, folder) = Hub();
        var chat = hub.CreateDirected(Directed(folder, budget: 2), "Brief");
        var id = chat.Info.Id;
        hub.Answer(id, chat.Messages.First(m => m.Kind == ChatKind.Consent).Id, CardState.AllowRead);
        Assert.Equal(ChatPermission.Read, chat.Info.Permission);
        runner.Turns[0].Reply("one");
        Assert.Equal(DirectResult.Sent, hub.Direct(id, "two"));
        runner.Turns[1].Reply("two done");
        Assert.Equal(DirectResult.WaitingForOwner, hub.Direct(id, "three"));
        Assert.Equal(2, runner.Turns.Count);
        var card = chat.Messages.Last();
        Assert.Equal(ChatKind.Limit, card.Kind);
        Assert.Equal(CardState.Open, card.State);

        hub.Answer(id, card.Id, CardState.More);
        Assert.Equal(3, runner.Turns.Count);
        Assert.Contains("three", runner.Turns[2].Launch.Input);
        Assert.Equal(0, chat.Info.DirectorTurns);
    }

    [Fact]
    public void TheOwnersWordsGoToTheGuestAndResetTheBudget()
    {
        var (hub, runner, folder) = Hub();
        var chat = hub.CreateDirected(Directed(folder), "Brief");
        var id = chat.Info.Id;
        hub.Answer(id, chat.Messages.First(m => m.Kind == ChatKind.Consent).Id, CardState.AllowRead);
        runner.Turns[0].Reply("done");
        hub.Send(id, "Make it brighter, both of you.");
        Assert.Equal(2, runner.Turns.Count);
        Assert.Contains("Owner: Make it brighter", runner.Turns[1].Launch.Input);
        Assert.Equal(0, chat.Info.DirectorTurns);
    }

    [Fact]
    public void DecliningClosesTheChatAndTheDirectorCantWriteAnyMore()
    {
        var (hub, runner, folder) = Hub();
        var chat = hub.CreateDirected(Directed(folder), "Brief");
        hub.Answer(chat.Info.Id, chat.Messages.First(m => m.Kind == ChatKind.Consent).Id, CardState.Skipped);
        Assert.True(chat.Info.Closed);
        Assert.Equal(ChatConsent.Declined, chat.Info.Consent);
        Assert.Equal(DirectResult.Closed, hub.Direct(chat.Info.Id, "hello?"));
        Assert.Empty(runner.Turns);
    }

    [Fact]
    public void TakingOverMakesTheOwnerTheDirector()
    {
        var (hub, runner, folder) = Hub();
        var chat = hub.CreateDirected(Directed(folder), "Brief");
        var id = chat.Info.Id;
        hub.Answer(id, chat.Messages.First(m => m.Kind == ChatKind.Consent).Id, CardState.AllowRead);
        runner.Turns[0].Reply("done");
        hub.Close(id, Agent.Claude);
        Assert.True(chat.Info.Closed);
        hub.Send(id, "ignored while closed");
        Assert.Single(runner.Turns);

        Assert.True(hub.TakeOver(id));
        Assert.Equal(ChatDirector.User, chat.Info.Director);
        Assert.Equal(Agent.Claude, chat.Info.TakenFrom);
        Assert.False(chat.Info.Closed);
        Assert.Equal(DirectResult.NotDirector, hub.Direct(id, "I'm back"));
        hub.Send(id, "Carry on with frame 3.");
        Assert.Equal(2, runner.Turns.Count);
    }

    [Fact]
    public void ResumingTheDirectorRunsACopyOfItsSessionAndRepliesGoBackToIt()
    {
        var (hub, runner, folder) = Hub();
        var chat = hub.CreateDirected(Directed(folder), "Brief");
        var id = chat.Info.Id;
        hub.Answer(id, chat.Messages.First(m => m.Kind == ChatKind.Consent).Id, CardState.AllowEdit);
        runner.Turns[0].Reply("Frame 1 done.");

        Assert.True(hub.ResumeDirector(id));
        Assert.True(chat.Info.DirectorInPulse);
        Assert.Equal([Agent.Claude, Agent.Codex], chat.Info.Agents);
        var director = runner.Turns[1];
        Assert.Equal(Agent.Claude, director.Launch.Agent);
        Assert.Equal("director-session", After(director.Launch.Args, "--resume"));
        Assert.Contains("--fork-session", director.Launch.Args);
        Assert.Contains("Frame 1 done.", director.Launch.Input);
        Assert.Contains("carry on directing", director.Launch.Input);
        Assert.Equal(DirectResult.NotDirector, hub.Direct(id, "from the old window"));

        director.Reply("Looks good. @codex now frame 2.", session: "claude-copy");
        Assert.Equal(Agent.Codex, runner.Turns[2].Launch.Agent);
        runner.Turns[2].Reply("Frame 2 done."); // no @claude needed: it goes back to the director
        Assert.Equal(Agent.Claude, runner.Turns[3].Launch.Agent);
        Assert.Equal("claude-copy", After(runner.Turns[3].Launch.Args, "--resume"));
        Assert.DoesNotContain("--fork-session", runner.Turns[3].Launch.Args);
    }

    // ---- the door ------------------------------------------------------------------------------------

    private static async Task<JsonObject> Ask(ChatDoor door, object request) =>
        (JsonObject)JsonNode.Parse(await door.HandleAsync(JsonSerializer.Serialize(request)))!;

    [Fact]
    public async Task TheDoorOpensWaitsSaysReadsListsAndCloses()
    {
        var (hub, runner, folder) = Hub();
        var opened = new List<string>();
        var door = new ChatDoor(hub, a => a(), a => a + ".exe", opened.Add, claudeHome: Path.Combine(_dir, "no-claude"), codexHome: Path.Combine(_dir, "no-codex"));
        var me = new { @as = "claude", session = "s-1" };

        var open = await Ask(door, new { op = "open", me.@as, me.session, folder, brief = "Make the frames.", outputs = Path.Combine(folder, "assets", "frames"), model = "gpt-6.2-sol" });
        Assert.True(open["ok"]!.GetValue<bool>());
        Assert.Equal("waiting_for_owner", open["state"]!.GetValue<string>());
        var id = open["chat"]!.GetValue<string>();
        Assert.Equal([id], opened);
        var chat = hub.Get(id)!;
        Assert.Equal("assets/frames", chat.Info.Outputs);
        Assert.Equal("s-1", chat.Info.DirectorSession);

        // The first wait says Pulse waits for the owner (the consent card is news).
        var first = await Ask(door, new { op = "wait", chat = id, me.@as, me.session, timeout = 1 });
        Assert.Equal("needs_owner", first["state"]!.GetValue<string>());
        Assert.Contains("waiting for the owner to allow this chat", first["text"]!.GetValue<string>());

        hub.Answer(id, chat.Messages.First(m => m.Kind == ChatKind.Consent).Id, CardState.AllowEdit);
        var frame = Path.Combine(folder, "assets", "frames", "01.png");
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
            File.WriteAllBytes(frame, [1, 2, 3]);
            runner.Turns[0].Reply("Frame 1 is ready.");
        });
        var reply = await Ask(door, new { op = "wait", chat = id, me.@as, me.session, timeout = 10 });
        Assert.Equal("reply", reply["state"]!.GetValue<string>());
        var text = reply["text"]!.GetValue<string>();
        Assert.Contains("Codex: Frame 1 is ready.", text);
        Assert.Contains("Pictures: assets/frames/01.png", text);
        Assert.DoesNotContain("waiting for the owner", text); // told once already
        Assert.Contains("Next: your turn", text);

        // Nothing new: a short wait times out quietly.
        var quiet = await Ask(door, new { op = "wait", chat = id, me.@as, me.session, timeout = 1 });
        Assert.Equal("idle", quiet["state"]!.GetValue<string>());
        Assert.Contains("Nothing new yet.", quiet["text"]!.GetValue<string>());

        var say = await Ask(door, new { op = "say", chat = id, me.@as, me.session, text = "Frame 2, please." });
        Assert.Equal("sent", say["state"]!.GetValue<string>());
        Assert.Equal(2, runner.Turns.Count);

        // The owner's words reach the director even while the guest still works.
        hub.Send(id, "Darker background.");
        var owner = await Ask(door, new { op = "wait", chat = id, me.@as, me.session, timeout = 1 });
        Assert.Equal("owner", owner["state"]!.GetValue<string>());
        Assert.Contains("Owner: Darker background.", owner["text"]!.GetValue<string>());

        var read = await Ask(door, new { op = "read", chat = id, me.@as });
        Assert.Contains("You: Frame 2, please.", read["text"]!.GetValue<string>());
        var list = await Ask(door, new { op = "list", me.@as, me.session });
        Assert.Contains(id, list["text"]!.GetValue<string>());

        var stranger = await Ask(door, new { op = "say", chat = id, @as = "claude", session = "s-2", text = "hi" });
        Assert.False(stranger["ok"]!.GetValue<bool>());
        var codex = await Ask(door, new { op = "say", chat = id, @as = "codex", text = "hi" });
        Assert.False(codex["ok"]!.GetValue<bool>());

        var close = await Ask(door, new { op = "close", chat = id, me.@as, me.session });
        Assert.Equal("closed", close["state"]!.GetValue<string>());
        Assert.True(chat.Info.Closed);
    }

    [Fact]
    public async Task TheDoorRefusesWhatItShouldnt()
    {
        var (hub, _, folder) = Hub();
        var door = new ChatDoor(hub, a => a(), a => a == Agent.Codex ? "codex.exe" : null);
        async Task<string> Error(object request)
        {
            var answer = await Ask(door, request);
            Assert.False(answer["ok"]!.GetValue<bool>());
            return answer["error"]!.GetValue<string>();
        }
        Assert.Contains("inside a Pulse chat", await Error(new { op = "open", @as = "claude", folder, brief = "x", inside = "abc" }));
        Assert.Contains("Say who", await Error(new { op = "open", folder, brief = "x" }));
        Assert.Contains("must be Codex", await Error(new { op = "open", @as = "claude", guest = "claude", folder, brief = "x" }));
        Assert.Contains("folder", await Error(new { op = "open", @as = "claude", folder = "relative", brief = "x" }));
        Assert.Contains("brief", await Error(new { op = "open", @as = "claude", folder }));
        Assert.Contains("inside your folder", await Error(new { op = "open", @as = "claude", folder, brief = "x", outputs = "../elsewhere" }));
        Assert.Contains("isn't installed", await Error(new { op = "open", @as = "codex", folder, brief = "x" }));
        Assert.Contains("doesn't know", await Error(new { op = "dance" }));
        Assert.Contains("one line of JSON", (await door.HandleAsync("not json")));
        var models = await Ask(door, new { op = "models", guest = "claude" });
        Assert.Contains("Opus (opus)", models["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ATakenOverChatTellsItsOldDirector()
    {
        var (hub, _, folder) = Hub();
        var door = new ChatDoor(hub, a => a(), a => a + ".exe");
        var open = await Ask(door, new { op = "open", @as = "codex", session = "t-1", folder, brief = "Review my change." });
        var id = open["chat"]!.GetValue<string>();
        Assert.Equal(Agent.Claude, hub.Get(id)!.Info.Guest);
        hub.TakeOver(id);
        var say = await Ask(door, new { op = "say", chat = id, @as = "codex", session = "t-1", text = "and?" });
        Assert.Equal("taken_over", say["state"]!.GetValue<string>());
        var wait = await Ask(door, new { op = "wait", chat = id, @as = "codex", session = "t-1", timeout = 1 });
        Assert.Equal("taken_over", wait["state"]!.GetValue<string>());
        Assert.Contains("You took over from Codex", wait["text"]!.GetValue<string>());
    }

    // ---- the pipe ------------------------------------------------------------------------------------

    [Fact]
    public async Task ThePipeCarriesOneRequestAndItsAnswer()
    {
        var name = "AmirLapTest." + Guid.NewGuid().ToString("N");
        Assert.Null(ChatPipe.Ask("""{"op":"ping"}""", TimeSpan.FromSeconds(2), name));
        using var server = new ChatPipeServer((line, _) => Task.FromResult("""{"ok":true,"echo":""" + JsonSerializer.Serialize(line) + "}"), name);
        server.Start();
        string? answer = null;
        for (int i = 0; i < 20 && answer is null; i++) answer = ChatPipe.Ask("""{"op":"ping","text":"héllo مرحبا"}""", TimeSpan.FromSeconds(5), name);
        Assert.NotNull(answer);
        var echo = JsonNode.Parse(answer!)!["echo"]!.GetValue<string>();
        Assert.Contains("héllo مرحبا", echo);
        // Several at once, each answered on its own.
        var many = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() => ChatPipe.Ask($$"""{"n":{{i}}}""", TimeSpan.FromSeconds(10), name))));
        Assert.All(many, Assert.NotNull);
    }

    // ---- the director's conversation ---------------------------------------------------------------

    [Fact]
    public void TheDirectorsConversationIsCopiedReadably()
    {
        var claudeHome = Path.Combine(_dir, "claude");
        var folder = @"D:\Work\film";
        var sessionDir = Path.Combine(claudeHome, "projects", AgentSessions.ClaudeSlug(folder));
        Directory.CreateDirectory(sessionDir);
        File.WriteAllLines(Path.Combine(sessionDir, "abc.jsonl"),
        [
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"<command-name>/clear</command-name>"}}""",
            """{"type":"user","message":{"role":"user","content":"Let's plan the imp's wave."}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Six key frames, arm up on 3."},{"type":"tool_use","id":"t","name":"Bash","input":{"command":"ls assets"}}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"secret output"}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Start a group chat with Codex for the frames."}]}}""",
        ]);
        var file = DirectorNotes.FindSession(Agent.Claude, "abc", folder, claudeHome: claudeHome);
        Assert.NotNull(file);
        Assert.Equal(file, DirectorNotes.FindSession(Agent.Claude, "abc", @"D:\Other", claudeHome: claudeHome));
        Assert.Null(DirectorNotes.FindSession(Agent.Claude, "..\\abc", folder, claudeHome: claudeHome));
        var target = Path.Combine(_dir, "out", "copy.md");
        Assert.True(DirectorNotes.Write(Agent.Claude, file!, target));
        var text = File.ReadAllText(target);
        Assert.Contains("## Owner\n\nLet's plan the imp's wave.", text);
        Assert.Contains("## Claude\n\nSix key frames, arm up on 3.\n- Ran ls assets", text);
        Assert.Contains("Start a group chat with Codex", text);
        Assert.DoesNotContain("secret output", text);
        Assert.DoesNotContain("/clear", text);

        var codexHome = Path.Combine(_dir, "codex");
        var day = Path.Combine(codexHome, "sessions", "2026", "11", "18");
        Directory.CreateDirectory(day);
        File.WriteAllLines(Path.Combine(day, "rollout-2026-11-18T10-00-00-th-9.jsonl"),
        [
            """{"type":"session_meta","payload":{"id":"th-9","cwd":"D:\\Work\\film"}}""",
            """{"type":"event_msg","payload":{"type":"user_message","message":"Review the stitcher."}}""",
            """{"type":"response_item","payload":{"type":"function_call","name":"shell","arguments":"{\"command\":[\"git\",\"diff\"]}"}}""",
            """{"type":"event_msg","payload":{"type":"agent_message","message":"I'll ask Claude to review it."}}""",
        ]);
        var rollout = DirectorNotes.FindSession(Agent.Codex, "th-9", null, codexHome: codexHome);
        Assert.NotNull(rollout);
        var codex = DirectorNotes.Read(Agent.Codex, rollout!);
        Assert.Contains("## Owner\n\nReview the stitcher.", codex);
        Assert.Contains("- shell", codex);
        Assert.Contains("I'll ask Claude to review it.", codex);
    }

    private static string? After(IReadOnlyList<string> args, string flag)
    {
        int i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }
}
