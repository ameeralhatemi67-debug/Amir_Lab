using System.Text.Json;
using Toolbox.Core.Pulse.Chat;

namespace Toolbox.Core.Tests;

public sealed class PulseChatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "amirlap-chat-" + Guid.NewGuid().ToString("N"));

    public PulseChatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ChatInfo Group(string lead = Agent.Claude, string recipe = ChatRecipe.Builder, int hops = 4) => new()
    {
        Title = "Gallery",
        Folder = @"D:\Work\brand-gallery",
        Agents = [Agent.Claude, Agent.Codex],
        Recipe = recipe,
        Lead = lead,
        HopLimit = hops,
        Permission = ChatPermission.Edit,
        Owns = ChatRules.DefaultOwns(recipe, lead),
    };

    // ---- who answers --------------------------------------------------------------------------

    [Fact]
    public void MentionsAreFoundInOrderAndBothMeansBoth()
    {
        Assert.Equal([Agent.Codex, Agent.Claude], ChatRules.MentionsIn("@codex draw it, then @claude builds"));
        Assert.Equal([Agent.Claude, Agent.Codex], ChatRules.MentionsIn("@both please"));
        Assert.Equal([Agent.Codex, Agent.Claude], ChatRules.MentionsIn("@ALL go", [Agent.Codex, Agent.Claude]));
        Assert.Empty(ChatRules.MentionsIn("mail me at me@codex.dev"));
        Assert.Empty(ChatRules.MentionsIn("run `git log @codex` and\n```\n@claude\n```"));
    }

    [Fact]
    public void TargetsFollowMentionsThenTheSwitchThenTheLead()
    {
        var chat = Group();
        Assert.Equal([Agent.Codex], ChatRules.Targets(chat, "@codex make the fronts"));
        Assert.Equal([Agent.Claude, Agent.Codex], ChatRules.Targets(chat, "@claude build it. @codex draw it."));
        Assert.Equal([Agent.Claude], ChatRules.Targets(chat, "hello"));
        Assert.Equal([Agent.Codex], ChatRules.Targets(chat, "hello", Agent.Codex));
        Assert.Equal([Agent.Claude, Agent.Codex], ChatRules.Targets(chat, "hello", "both"));
        Assert.Equal([Agent.Codex, Agent.Claude], ChatRules.Targets(Group(lead: Agent.Codex), "hi", "both"));
        var single = new ChatInfo { Agents = [Agent.Codex] };
        Assert.Equal([Agent.Codex], ChatRules.Targets(single, "@claude are you there?"));
    }

    [Fact]
    public void AHandOffIsTheOtherAgentNamedWithAt()
    {
        var chat = Group();
        Assert.Equal(Agent.Codex, ChatRules.HandoffFrom(chat, Agent.Claude, "garments.json is ready.\n@codex start with the fronts."));
        Assert.Null(ChatRules.HandoffFrom(chat, Agent.Claude, "I'll do it myself. @claude"));
        Assert.Null(ChatRules.HandoffFrom(chat, Agent.Claude, "Done, see `@codex` in the log."));
        Assert.Null(ChatRules.HandoffFrom(new ChatInfo { Agents = [Agent.Claude] }, Agent.Claude, "@codex?"));
    }

    // ---- what each agent is told ---------------------------------------------------------------

    [Fact]
    public void ThePreambleSaysWhoIsHereWhoOwnsWhatAndTheLimit()
    {
        var text = ChatRules.Preamble(Group(hops: 3), Agent.Claude);
        Assert.Contains("You are Claude", text);
        Assert.Contains("Codex, another AI coding agent (made by OpenAI)", text);
        Assert.Contains("write @codex", text);
        Assert.Contains("After 3 hand-offs", text);
        Assert.Contains("you change src/, index.html", text);
        Assert.Contains("Codex changes public/images/", text);
        Assert.Contains("makes the pictures", text);
        var codex = ChatRules.Preamble(Group(), Agent.Codex);
        Assert.Contains("You are Codex", codex);
        Assert.Contains("made by Anthropic", codex);
        var quick = ChatRules.Preamble(new ChatInfo { Agents = [Agent.Codex], Permission = ChatPermission.Read }, Agent.Codex);
        Assert.Contains("quick chat", quick);
        Assert.Contains("read-only", quick);
        Assert.DoesNotContain("@claude", quick);
    }

    [Fact]
    public void TheDigestHoldsWhatIsNewAndNotTheAgentsOwnWords()
    {
        var chat = Group();
        var messages = new List<ChatMessage>
        {
            new() { Author = ChatAuthor.User, Text = "@claude build it. @codex draw it." },
        };
        Assert.Equal("@claude build it. @codex draw it.", ChatRules.Digest(chat, messages, Agent.Claude));
        chat.Seen[Agent.Claude] = 1;
        messages.Add(new ChatMessage { Author = Agent.Claude, Text = "Built. @codex garments.json is ready.", To = Agent.Codex });
        var picture = Path.Combine(chat.Folder!, "public", "images", "shirt", "front.png");
        messages.Add(new ChatMessage { Author = Agent.Codex, Text = "Front of the shirt is done.", Images = [picture] });
        messages.Add(new ChatMessage { Author = ChatAuthor.User, Kind = ChatKind.Approval, Text = "public/images/shirt/front.png", Images = [picture] });
        messages.Add(new ChatMessage { Author = ChatAuthor.Pulse, Kind = ChatKind.Note, Text = "not for agents" });

        var forClaude = ChatRules.Digest(chat, messages, Agent.Claude);
        Assert.DoesNotContain("Built.", forClaude);
        Assert.Contains("Codex: Front of the shirt is done.", forClaude);
        Assert.Contains("Pictures Codex made: public/images/shirt/front.png", forClaude);
        Assert.Contains("User approved: public/images/shirt/front.png", forClaude);
        Assert.DoesNotContain("not for agents", forClaude);

        var forCodex = ChatRules.Digest(chat, messages, Agent.Codex, "The user allowed this: Run npm test.");
        Assert.Contains("User: @claude build it.", forCodex);
        Assert.Contains("Claude (to you): Built.", forCodex);
        Assert.EndsWith("The user allowed this: Run npm test.", forCodex);
    }

    [Fact]
    public void TitlesComeFromTheFirstMessageWithoutMentions()
    {
        Assert.Equal("build the brand gallery", ChatRules.TitleFrom("@claude build the brand gallery"));
        Assert.EndsWith("…", ChatRules.TitleFrom("a very long first message that goes on and on about many things at once"));
        Assert.Equal("New chat", ChatRules.TitleFrom("@codex"));
    }

    // ---- command lines ------------------------------------------------------------------------

    [Fact]
    public void ClaudeResumesItsSessionAndAsksThroughCards()
    {
        var chat = Group();
        var first = AgentCommands.Claude("claude.exe", chat, @"D:\Work\brand-gallery", "hi");
        Assert.Contains("stream-json", first.Args);
        Assert.Equal("none", After(first.Args, "--permission-prompts"));
        Assert.Equal("acceptEdits", After(first.Args, "--permission-mode"));
        Assert.DoesNotContain("--resume", first.Args);
        Assert.Contains("Edit(public/images/**)", first.Args);
        Assert.Contains("Write(public/images/**)", first.Args);
        Assert.Equal("hi", first.Input);

        chat.ForkFrom[Agent.Claude] = "old-session";
        var fork = AgentCommands.Claude("claude.exe", chat, "x", "hi");
        Assert.Equal("old-session", After(fork.Args, "--resume"));
        Assert.Contains("--fork-session", fork.Args);

        chat.Sessions[Agent.Claude] = "mine";
        chat.AllowedTools.Add("Bash(npm install:*)");
        var next = AgentCommands.Claude("claude.exe", chat, "x", "hi", ["Bash(npm test)"]);
        Assert.Equal("mine", After(next.Args, "--resume"));
        Assert.DoesNotContain("--fork-session", next.Args);
        Assert.Contains("Bash(npm install:*)", next.Args);
        Assert.Contains("Bash(npm test)", next.Args);

        var ask = AgentCommands.Claude("claude.exe", new ChatInfo { Agents = [Agent.Claude], Permission = ChatPermission.Read }, "x", "hi");
        Assert.Equal("manual", After(ask.Args, "--permission-mode"));
        Assert.Contains("Edit", ask.Args);
    }

    [Fact]
    public void CodexStartsResumesOrForksInTheRightSandbox()
    {
        var chat = Group();
        var first = AgentCommands.Codex("codex.exe", chat, "x", "draw");
        Assert.Equal(["exec", "--json"], first.Args.Take(2));
        Assert.Contains("sandbox_mode=\"workspace-write\"", first.Args);
        Assert.Equal("-", first.Args[^1]);
        Assert.StartsWith("You are Codex", first.Input);
        Assert.EndsWith("draw", first.Input);

        chat.ForkFrom[Agent.Codex] = "thread-1";
        var fork = AgentCommands.Codex("codex.exe", chat, "x", "draw");
        Assert.Equal(["exec", "fork", "thread-1", "--json"], fork.Args.Take(4));

        chat.Sessions[Agent.Codex] = "thread-2";
        var next = AgentCommands.Codex("codex.exe", chat, "x", "again");
        Assert.Equal(["exec", "resume", "thread-2", "--json"], next.Args.Take(4));
        Assert.Equal("again", next.Input);

        Assert.Equal("read-only", AgentCommands.CodexSandbox(new ChatInfo { Permission = ChatPermission.Ask }));
    }

    private static string? After(IReadOnlyList<string> args, string flag)
    {
        int i = args.ToList().IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    // ---- reading the agents -------------------------------------------------------------------

    [Fact]
    public void ClaudesStreamGivesTheSessionStepsAnswerAndRefusals()
    {
        var lines = new[]
        {
            """{"type":"system","subtype":"init","cwd":"D:\\x","session_id":"40fe2edc","tools":["Bash"]}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Let me look."},{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"D:\\x\\src\\App.tsx"}}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t2","name":"Bash","input":{"command":"npm create vite@latest . -- --template react-ts"}}]}}""",
            """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","unifiedWindows":{"five_hour":{"utilization":0.11},"seven_day":{"utilization":0.01}}}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"I need to run npm. @codex make the fronts."}]}}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"I need to run npm. @codex make the fronts.","session_id":"40fe2edc","permission_denials":[{"tool_name":"Bash","tool_use_id":"t2","tool_input":{"command":"npm create vite@latest . -- --template react-ts"}},{"tool_name":"Edit","tool_use_id":"t3","tool_input":{"file_path":"src/App.tsx"}}]}""",
        };
        var events = lines.SelectMany(ClaudeStream.Parse).ToList();
        Assert.Equal("40fe2edc", Assert.IsType<SessionEvent>(events[0]).SessionId);
        Assert.Equal("Let me look.", Assert.IsType<TextEvent>(events[1]).Text);
        Assert.Equal("Read D:\\x\\src\\App.tsx", Assert.IsType<StepEvent>(events[2]).Step.Text);
        Assert.Equal("run", Assert.IsType<StepEvent>(events[3]).Step.Kind);
        var limits = Assert.IsType<LimitsEvent>(events[4]);
        Assert.Equal(11, limits.FiveHour);
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.Null(done.Error);
        Assert.Equal("I need to run npm. @codex make the fronts.", done.Answer);
        Assert.Equal(2, done.Denied.Count);
        Assert.Equal("Run npm create vite@latest . -- --template react-ts", done.Denied[0].Summary);
        Assert.Equal("Bash(npm create vite@latest . -- --template react-ts)", done.Denied[0].Rule);
        Assert.Equal("Bash(npm create:*)", done.Denied[0].AlwaysRule);
        Assert.Equal("Edit src/App.tsx", done.Denied[1].Summary);
        Assert.Equal("Edit", done.Denied[1].Rule);

        var failed = ClaudeStream.Parse("""{"type":"result","subtype":"error_max_turns","is_error":true,"result":""}""").Single();
        Assert.Equal("error_max_turns", Assert.IsType<DoneEvent>(failed).Error);
        Assert.Empty(ClaudeStream.Parse("not json"));
    }

    [Fact]
    public void CodexsStreamTreatsSetupErrorsAsNoticesAndEndsWithTheLastMessage()
    {
        var stream = new CodexStream();
        var lines = new[]
        {
            """{"type":"thread.started","thread_id":"01a1dc08"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"error","message":"Codex is ignoring 1 unrecognized configuration setting."}}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"reasoning","text":"thinking"}}""",
            """{"type":"item.completed","item":{"id":"item_2","type":"command_execution","command":"\"C:\\\\WINDOWS\\\\powershell.exe\" -Command \"Get-ChildItem\"","exit_code":0,"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"item_3","type":"file_change","changes":[{"path":"public/images/shirt/front.png","kind":"add"},{"path":"notes.md","kind":"update"}]}}""",
            """{"type":"item.completed","item":{"id":"item_4","type":"agent_message","text":"Fronts are ready. @claude use them."}}""",
            """{"type":"turn.completed","usage":{"input_tokens":25650}}""",
        };
        var events = lines.SelectMany(stream.Parse).ToList();
        Assert.Equal("01a1dc08", Assert.IsType<SessionEvent>(events[0]).SessionId);
        Assert.IsType<NoticeEvent>(events[1]);
        var steps = events.OfType<StepEvent>().Select(s => s.Step.Text).ToList();
        Assert.Equal(["Ran Get-ChildItem", "Created public/images/shirt/front.png", "Edited notes.md"], steps);
        var done = Assert.IsType<DoneEvent>(events[^1]);
        Assert.Equal("Fronts are ready. @claude use them.", done.Answer);
        Assert.Null(done.Error);

        var failing = new CodexStream();
        failing.Parse("""{"type":"turn.started"}""").ToList();
        var failed = failing.Parse("""{"type":"turn.failed","error":{"message":"usage limit reached"}}""").Single();
        Assert.Equal("usage limit reached", Assert.IsType<DoneEvent>(failed).Error);
    }

    // ---- earlier sessions -----------------------------------------------------------------------

    [Fact]
    public void ClaudeSessionsAreFoundByTheFolderName()
    {
        Assert.Equal("D--Agents-main-projects-ideas-Amir-web", AgentSessions.ClaudeSlug(@"D:\Agents\main\projects\ideas\Amir_web"));
        var home = Path.Combine(_dir, "claude");
        var folder = Path.Combine(home, "projects", "d--Work-brand-gallery");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "aaa.jsonl"),
        [
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"<command-name>/clear</command-name>"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Set up the brand notes please"}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"ok"}]}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"..."}]}}""",
            """{"type":"user","message":{"role":"user","content":"And the colours"}}""",
        ]);
        File.WriteAllLines(Path.Combine(folder, "bbb.jsonl"),
        [
            """{"type":"user","message":{"role":"user","content":"first words"}}""",
            """{"type":"custom-title","customTitle":"Colour \"palette\"","sessionId":"bbb"}""",
        ]);
        File.SetLastWriteTimeUtc(Path.Combine(folder, "aaa.jsonl"), DateTime.UtcNow.AddHours(-2));
        var list = AgentSessions.Claude(@"D:\Work\brand-gallery\", home);
        Assert.Equal(2, list.Count);
        Assert.Equal("bbb", list[0].Id);
        Assert.Equal("Colour \"palette\"", list[0].Title);
        Assert.Equal("Set up the brand notes please", list[1].Title);
        Assert.Equal(2, list[1].Messages);
        Assert.Empty(AgentSessions.Claude(@"D:\Elsewhere", home));
    }

    [Fact]
    public void CodexSessionsAreFoundByTheirFolderAndNamedFromItsIndex()
    {
        var home = Path.Combine(_dir, "codex");
        var day = Path.Combine(home, "sessions", "2026", "11", "14");
        Directory.CreateDirectory(day);
        string Meta(string id, string cwd) => JsonSerializer.Serialize(new { timestamp = "2026-11-14T10:00:00Z", type = "session_meta", payload = new { id, cwd } });
        string Ask(string text) => JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "user_message", message = text } });
        File.WriteAllLines(Path.Combine(day, "rollout-a-t1.jsonl"), [Meta("t1", @"D:\Work\brand-gallery"), Ask("draw the shirts")]);
        File.WriteAllLines(Path.Combine(day, "rollout-b-t2.jsonl"), [Meta("t2", @"d:\work\BRAND-GALLERY\"), Ask("logo tests")]);
        File.WriteAllLines(Path.Combine(day, "rollout-c-t3.jsonl"), [Meta("t3", @"D:\Other"), Ask("no")]);
        File.WriteAllLines(Path.Combine(home, "session_index.jsonl"),
        [
            """{"id":"t2","thread_name":"Logo","updated_at":"2026-11-14T10:00:00Z"}""",
            """{"id":"t2","thread_name":"Logo tests","updated_at":"2026-11-14T11:00:00Z"}""",
        ]);
        var list = AgentSessions.Codex(@"D:\Work\brand-gallery", home);
        Assert.Equal(2, list.Count);
        Assert.Contains(list, s => s.Id == "t1" && s.Title == "draw the shirts");
        Assert.Contains(list, s => s.Id == "t2" && s.Title == "Logo tests");
    }

    [Fact]
    public void AnNpmShimPointsAtTheRealProgram()
    {
        var npm = Path.Combine(_dir, "npm");
        var exe = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "@ECHO off\r\n\"%dp0%\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.exe\"   %*\r\n");
        Assert.Equal(exe, AgentLocator.ShimTarget(Path.Combine(npm, "claude.cmd")));
        Assert.Equal("2.1.321", AgentLocator.VersionNumber("2.1.321 (Claude Code)"));
        Assert.Equal("0.160.1", AgentLocator.VersionNumber("codex-cli 0.160.1"));
    }

    // ---- the room ------------------------------------------------------------------------------

    private sealed class FakeTurn : IAgentTurn
    {
        private readonly TaskCompletionSource _done = new();
        public FakeTurn(AgentLaunch launch, Action<AgentEvent> onEvent) { Launch = launch; OnEvent = onEvent; }
        public AgentLaunch Launch { get; }
        public Action<AgentEvent> OnEvent { get; }
        public bool Stopped { get; private set; }
        public Task Completion => _done.Task;
        public void Stop() { Stopped = true; _done.TrySetResult(); }

        public void Reply(string text, IReadOnlyList<DeniedAction>? denied = null, string session = "s")
        {
            OnEvent(new SessionEvent(session));
            OnEvent(new StepEvent(new ChatStep { Kind = "read", Text = "Read a file" }));
            OnEvent(new TextEvent(text));
            OnEvent(new DoneEvent(text, denied ?? [], null));
            _done.TrySetResult();
        }
    }

    private sealed class FakeRunner : IAgentRunner
    {
        public List<FakeTurn> Turns { get; } = [];
        public IAgentTurn Start(AgentLaunch launch, Action<AgentEvent> onEvent)
        {
            var t = new FakeTurn(launch, onEvent);
            Turns.Add(t);
            return t;
        }
    }

    private (ChatHub Hub, FakeRunner Runner, ChatFile Chat) Room(int hops = 2)
    {
        var runner = new FakeRunner();
        var hub = new ChatHub(new ChatStore(Path.Combine(_dir, "chats")), runner, a => a + ".exe", a => a()) { CodexPictures = null };
        var info = Group(hops: hops);
        info.Folder = Path.Combine(_dir, "project");
        Directory.CreateDirectory(info.Folder);
        return (hub, runner, hub.Create(info));
    }

    [Fact]
    public void AgentsHandWorkToEachOtherOneAtATimeUntilTheLimit()
    {
        var (hub, runner, chat) = Room(hops: 2);
        var id = chat.Info.Id;
        hub.Send(id, "@claude build the gallery and ask codex for pictures");
        Assert.Single(runner.Turns);
        Assert.Equal(Agent.Claude, runner.Turns[0].Launch.Agent);
        Assert.Equal(Agent.Claude, hub.WorkingIn(id));

        runner.Turns[0].Reply("garments.json is ready. @codex make the fronts.", session: "claude-1");
        Assert.Equal("claude-1", chat.Info.Sessions[Agent.Claude]);
        Assert.Equal(Agent.Codex, chat.Messages.Last(m => m.Author == Agent.Claude).To);
        Assert.Equal(2, runner.Turns.Count);
        Assert.Equal(Agent.Codex, runner.Turns[1].Launch.Agent);
        Assert.Contains("Claude (to you): garments.json is ready.", runner.Turns[1].Launch.Input);
        Assert.Equal(1, chat.Info.Hops);

        runner.Turns[1].Reply("Fronts done. @claude put them in.", session: "codex-1");
        Assert.Equal(3, runner.Turns.Count);
        Assert.Equal(2, chat.Info.Hops);
        Assert.Contains("Codex (to you): Fronts done.", runner.Turns[2].Launch.Input);
        Assert.DoesNotContain("garments.json is ready", runner.Turns[2].Launch.Input);

        // The third hand-off waits for the user.
        runner.Turns[2].Reply("In. @codex now the backs?", session: "claude-1");
        Assert.Equal(3, runner.Turns.Count);
        var card = chat.Messages.Last();
        Assert.Equal(ChatKind.Limit, card.Kind);
        Assert.Equal(CardState.Open, card.State);
        Assert.Equal(1, hub.NeedsYou);

        hub.Answer(id, card.Id, CardState.More);
        Assert.Equal(4, runner.Turns.Count);
        Assert.Equal(Agent.Codex, runner.Turns[3].Launch.Agent);
        Assert.Equal(0, hub.NeedsYou);
        Assert.Contains("resume", runner.Turns[3].Launch.Args);
        Assert.Contains("codex-1", runner.Turns[3].Launch.Args);

        // Saved and read back (the reply being written is saved when its turn ends).
        var again = ChatStore.Load(hub.Store.FileOf(id))!;
        Assert.Equal(chat.Messages.Count(m => !m.Working), again.Messages.Count);
        Assert.Equal("claude-1", again.Info.Sessions[Agent.Claude]);
    }

    [Fact]
    public void ARefusedActionBecomesACardAndAllowingItRunsTheAgentAgain()
    {
        var (hub, runner, chat) = Room();
        var id = chat.Info.Id;
        hub.Send(id, "set up the project");
        var denied = new DeniedAction { Tool = "Bash", Summary = "Run npm install", Rule = "Bash(npm install)", AlwaysRule = "Bash(npm install:*)" };
        runner.Turns[0].Reply("I need to install packages.", [denied]);
        var card = chat.Messages.Last();
        Assert.Equal(ChatKind.Denied, card.Kind);
        Assert.Equal(Agent.Claude, card.About);

        hub.Answer(id, card.Id, CardState.Allowed);
        Assert.Equal(2, runner.Turns.Count);
        Assert.Contains("Bash(npm install)", runner.Turns[1].Launch.Args);
        Assert.Contains("The user allowed this: Run npm install.", runner.Turns[1].Launch.Input);
        Assert.Empty(chat.Info.AllowedTools);

        runner.Turns[1].Reply("Installed.", [denied]);
        hub.Answer(id, chat.Messages.Last().Id, CardState.Always);
        Assert.Equal(["Bash(npm install:*)"], chat.Info.AllowedTools);
        Assert.Contains("Bash(npm install:*)", runner.Turns[2].Launch.Args);
    }

    [Fact]
    public void StopEndsTheTurnAndDropsWhatWasWaiting()
    {
        var (hub, runner, chat) = Room();
        var id = chat.Info.Id;
        hub.Send(id, "@both say hi");
        Assert.Single(runner.Turns);
        hub.Stop(id);
        Assert.True(runner.Turns[0].Stopped);
        Assert.Null(hub.WorkingIn(id));
        Assert.Single(runner.Turns);
        var reply = chat.Messages.Last(m => m.Author == Agent.Claude);
        Assert.False(reply.Working);
        Assert.Equal("Stopped.", reply.Error);
    }

    [Fact]
    public void PicturesMadeDuringATurnAreAttachedAndApprovalsReachTheirMaker()
    {
        var (hub, runner, chat) = Room();
        var id = chat.Info.Id;
        var old = Path.Combine(chat.Info.Folder!, "old.png");
        File.WriteAllBytes(old, [1]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-5));
        hub.Send(id, "@codex draw the shirt");
        var front = Path.Combine(chat.Info.Folder!, "public", "images", "shirt", "front.png");
        Directory.CreateDirectory(Path.GetDirectoryName(front)!);
        File.WriteAllBytes(front, [1, 2, 3]);
        runner.Turns[0].Reply("Here is the front.");
        var reply = chat.Messages.Last(m => m.Author == Agent.Codex);
        Assert.Equal([front], reply.Images);

        hub.Approve(id, front, Agent.Codex, true);
        Assert.True(ChatHub.IsApproved(chat, front));
        hub.Send(id, "@codex now the back");
        Assert.Contains("User approved: public/images/shirt/front.png", runner.Turns[1].Launch.Input);
        hub.Approve(id, front, Agent.Codex, false);
        Assert.False(ChatHub.IsApproved(chat, front));
    }

    [Fact]
    public void AMissingProgramLeavesANoteInsteadOfATurn()
    {
        var runner = new FakeRunner();
        var hub = new ChatHub(new ChatStore(Path.Combine(_dir, "chats2")), runner, _ => null, a => a());
        var chat = hub.Create(new ChatInfo { Agents = [Agent.Codex] });
        hub.Send(chat.Info.Id, "hello");
        Assert.Empty(runner.Turns);
        var note = chat.Messages.Last();
        Assert.Equal(ChatKind.Note, note.Kind);
        Assert.Contains("can't find Codex", note.Text);
    }
}
