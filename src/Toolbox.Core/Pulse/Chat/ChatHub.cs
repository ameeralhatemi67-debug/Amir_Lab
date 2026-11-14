using Toolbox.Core.Diagnostics;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>
/// Pulse's chats while Amir Lap runs: it takes the user's messages, decides who answers, runs one
/// agent turn at a time per chat, passes hand-offs between the agents (and stops at the chat's
/// limit), turns refused actions into cards the user answers, and saves every chat. Every method
/// is called on the owner's thread; agent events are brought there through <c>post</c>.
/// </summary>
public sealed class ChatHub : IDisposable
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif"];

    private readonly ChatStore _store;
    private readonly IAgentRunner _runner;
    private readonly Func<string, string?> _program;
    private readonly Action<Action> _post;
    private readonly List<ChatFile> _chats;
    private readonly Dictionary<string, Running> _running = [];
    private readonly Dictionary<string, Queue<Job>> _queues = [];

    public ChatHub(ChatStore store, IAgentRunner runner, Func<string, string?> program, Action<Action> post)
    {
        _store = store;
        _runner = runner;
        _program = program;
        _post = post;
        _chats = store.LoadAll();
    }

    /// <summary>A chat changed (its id), or the list did ("").</summary>
    public event Action<string>? Changed;

    /// <summary>Claude reported its limits during a turn (five-hour and weekly, 0–100).</summary>
    public event Action<double?, double?>? ClaudeLimits;

    /// <summary>Where Codex saves the pictures it makes (new ones during a Codex turn belong to it).</summary>
    public string? CodexPictures { get; set; } = Path.Combine(CodexUsageReader.DefaultHome, "generated_images");

    public IReadOnlyList<ChatFile> Chats => _chats;

    public ChatStore Store => _store;

    public ChatFile? Get(string id) => _chats.FirstOrDefault(c => c.Info.Id == id);

    /// <summary>The agent working in a chat right now, or null.</summary>
    public string? WorkingIn(string id) => _running.TryGetValue(id, out var r) ? r.Agent : null;

    public bool AnyWorking => _running.Count > 0;

    /// <summary>Chats with a card waiting for the user (something to allow, or the turn limit).</summary>
    public int NeedsYou => _chats.Count(NeedsAnswer);

    public static bool NeedsAnswer(ChatFile chat) => chat.Messages.Any(m => m.Kind is ChatKind.Denied or ChatKind.Limit && m.State == CardState.Open);

    public int UnreadCount => _chats.Count(c => c.Info.Unread);

    // ---- chats -------------------------------------------------------------------------------

    public ChatFile Create(ChatInfo info)
    {
        var now = DateTimeOffset.UtcNow;
        info.Created = now;
        info.Updated = now;
        if (info.Agents.Count == 0) info.Agents.Add(Agent.Claude);
        var chat = new ChatFile { Info = info };
        foreach (var (agent, session) in info.ForkFrom)
        {
            chat.Messages.Add(new ChatMessage
            {
                Author = ChatAuthor.Pulse,
                Kind = ChatKind.Note,
                About = agent,
                At = now,
                Text = $"Continues {Agent.Name(agent)}'s session {session} as a copy, so the original stays as it was.",
            });
        }
        _chats.Insert(0, chat);
        Save(chat);
        Changed?.Invoke("");
        return chat;
    }

    public void Rename(string id, string title)
    {
        if (Get(id) is not { } chat || string.IsNullOrWhiteSpace(title)) return;
        chat.Info.Title = title.Trim();
        Save(chat);
        Changed?.Invoke("");
    }

    public void Delete(string id)
    {
        Stop(id);
        if (Get(id) is { } chat) _chats.Remove(chat);
        _store.Delete(id);
        Changed?.Invoke("");
    }

    public void MarkRead(string id)
    {
        if (Get(id) is not { } chat || !chat.Info.Unread) return;
        chat.Info.Unread = false;
        Save(chat);
        Changed?.Invoke("");
    }

    // ---- messages ----------------------------------------------------------------------------

    /// <summary>The user's message: who answers follows its @ names, else <paramref name="to"/>, else the chat's lead.</summary>
    public void Send(string id, string text, string? to = null)
    {
        if (Get(id) is not { } chat || string.IsNullOrWhiteSpace(text)) return;
        var info = chat.Info;
        if (info.Title.Length == 0) info.Title = ChatRules.TitleFrom(text);
        // The user is back: the agents get a fresh set of hand-offs, and an open limit card is moot.
        info.Hops = 0;
        info.Held = null;
        foreach (var card in chat.Messages.Where(m => m.Kind == ChatKind.Limit && m.State == CardState.Open)) card.State = CardState.Paused;
        chat.Messages.Add(new ChatMessage { Author = ChatAuthor.User, Text = text.Trim(), At = DateTimeOffset.UtcNow });
        info.Updated = DateTimeOffset.UtcNow;
        foreach (var agent in ChatRules.Targets(info, text, to)) Enqueue(id, new Job(agent, null, null));
        Save(chat);
        Changed?.Invoke(id);
        Pump(id);
    }

    /// <summary>The user approves a picture (or takes the approval back). The agent that made it reads this on its next turn.</summary>
    public void Approve(string id, string path, string maker, bool on)
    {
        if (Get(id) is not { } chat) return;
        var existing = chat.Messages.LastOrDefault(m => m.Kind == ChatKind.Approval && m.Images.Contains(path) && m.State != CardState.Skipped);
        if (on && existing is null)
            chat.Messages.Add(new ChatMessage { Author = ChatAuthor.User, Kind = ChatKind.Approval, About = maker, Images = [path], Text = ChatRules.Relative(chat.Info, path), At = DateTimeOffset.UtcNow });
        else if (!on && existing is not null)
            existing.State = CardState.Skipped;
        else return;
        Save(chat);
        Changed?.Invoke(id);
    }

    public static bool IsApproved(ChatFile chat, string path) =>
        chat.Messages.Any(m => m.Kind == ChatKind.Approval && m.State != CardState.Skipped && m.Images.Contains(path));

    /// <summary>The user answers a card: allowed / always / skipped for a refused action, more / paused for the limit.</summary>
    public void Answer(string id, string messageId, string state)
    {
        if (Get(id) is not { } chat || chat.Messages.FirstOrDefault(m => m.Id == messageId) is not { State: CardState.Open } card) return;
        card.State = state;
        var info = chat.Info;
        if (card.Kind == ChatKind.Denied && card.About is { } agent)
        {
            if (state is CardState.Allowed or CardState.Always)
            {
                if (state == CardState.Always)
                    foreach (var rule in card.Denied.Select(d => d.AlwaysRule).Where(r => r.Length > 0 && !info.AllowedTools.Contains(r))) info.AllowedTools.Add(rule);
                var what = string.Join("; ", card.Denied.Select(d => d.Summary));
                Enqueue(id, new Job(agent, $"The user allowed this: {what}. Go ahead and carry on where you stopped.", state == CardState.Allowed ? card.Denied.Select(d => d.Rule).ToList() : null));
            }
        }
        else if (card.Kind == ChatKind.Limit)
        {
            var held = info.Held;
            info.Held = null;
            if (state == CardState.More && held is not null)
            {
                info.Hops = 0;
                Enqueue(id, new Job(held, null, null));
            }
        }
        Save(chat);
        Changed?.Invoke(id);
        Pump(id);
    }

    /// <summary>Stops the agent working in a chat and drops what was waiting.</summary>
    public void Stop(string id)
    {
        if (_queues.TryGetValue(id, out var q)) q.Clear();
        if (_running.TryGetValue(id, out var r))
        {
            r.Stopped = true;
            r.Turn?.Stop();
        }
    }

    public void Dispose()
    {
        foreach (var r in _running.Values.ToList())
        {
            r.Stopped = true;
            r.Turn?.Stop();
        }
    }

    // ---- turns -------------------------------------------------------------------------------

    private void Enqueue(string id, Job job)
    {
        if (!_queues.TryGetValue(id, out var q)) _queues[id] = q = new Queue<Job>();
        if (!q.Any(j => j.Agent == job.Agent && j.Extra == job.Extra)) q.Enqueue(job);
    }

    private void Pump(string id)
    {
        if (_running.ContainsKey(id) || !_queues.TryGetValue(id, out var q) || q.Count == 0) return;
        if (Get(id) is not { } chat)
        {
            q.Clear();
            return;
        }
        Run(chat, q.Dequeue());
    }

    private void Run(ChatFile chat, Job job)
    {
        var info = chat.Info;
        var id = info.Id;
        var agent = job.Agent;
        var exe = _program(agent);
        if (exe is null)
        {
            Note(chat, agent, $"Pulse can't find {Agent.Name(agent)} on this PC. Install it (or choose its program in Amir Lap › Pulse), then send the message again.");
            Pump(id);
            return;
        }
        string workDir;
        try
        {
            workDir = _store.WorkFolder(info);
            if (!Directory.Exists(workDir))
            {
                Note(chat, agent, $"The folder {workDir} is not there any more.");
                Pump(id);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Note(chat, agent, ex.Message);
            Pump(id);
            return;
        }

        var prompt = ChatRules.Digest(info, chat.Messages, agent, job.Extra);
        info.Seen[agent] = chat.Messages.Count;
        var reply = new ChatMessage { Author = agent, Working = true, At = DateTimeOffset.UtcNow };
        chat.Messages.Add(reply);
        var launch = AgentCommands.For(agent, exe, info, workDir, prompt, job.AllowOnce);
        var running = new Running(agent, reply, DateTime.UtcNow, Pictures(workDir));
        _running[id] = running;
        Changed?.Invoke(id);
        Log.Info($"Pulse chat {id}: {agent} turn in {workDir}");
        try
        {
            running.Turn = _runner.Start(launch, e => _post(() => OnEvent(chat, running, e)));
        }
        catch (Exception ex)
        {
            Log.Error("Pulse chat could not start a turn", ex);
            running.Done = new DoneEvent(null, [], ex.Message);
            Finish(chat, running, workDir);
            return;
        }
        running.Turn.Completion.ContinueWith(_ => _post(() => Finish(chat, running, workDir)), TaskContinuationOptions.ExecuteSynchronously);
    }

    private void OnEvent(ChatFile chat, Running running, AgentEvent e)
    {
        var reply = running.Reply;
        switch (e)
        {
            case SessionEvent s:
                chat.Info.Sessions[running.Agent] = s.SessionId;
                chat.Info.ForkFrom.Remove(running.Agent);
                return;
            case TextEvent t:
                // Earlier text in the same turn is the agent thinking aloud; it moves into the steps.
                if (reply.Text.Length > 0 && reply.Text != t.Text)
                    AddStep(reply, new ChatStep { Kind = "say", Text = ClaudeStream.Short(reply.Text, 200) });
                reply.Text = t.Text;
                break;
            case StepEvent s:
                AddStep(reply, s.Step);
                break;
            case LimitsEvent l:
                ClaudeLimits?.Invoke(l.FiveHour, l.Week);
                return;
            case DoneEvent d:
                running.Done = d;
                return;
            default:
                return;
        }
        Changed?.Invoke(chat.Info.Id);
    }

    private static void AddStep(ChatMessage reply, ChatStep step)
    {
        if (reply.Steps.Count < 300) reply.Steps.Add(step);
    }

    private void Finish(ChatFile chat, Running running, string workDir)
    {
        var info = chat.Info;
        var id = info.Id;
        if (!_running.TryGetValue(id, out var current) || current != running) return;
        _running.Remove(id);
        var reply = running.Reply;
        var done = running.Done ?? new DoneEvent(null, [], running.Stopped ? "Stopped." : $"{Agent.Name(running.Agent)} stopped.");
        reply.Working = false;
        if (done.Answer is { Length: > 0 } answer) reply.Text = answer;
        if (running.Stopped) reply.Error = "Stopped.";
        else if (done.Error is { Length: > 0 } error) reply.Error = error;
        reply.Images = NewPictures(workDir, running.Pictures, running.Agent == Agent.Codex ? CodexPictures : null, running.Started);
        info.Updated = DateTimeOffset.UtcNow;
        info.Unread = true;

        if (!running.Stopped && done.Denied.Count > 0)
        {
            chat.Messages.Add(new ChatMessage
            {
                Author = ChatAuthor.Pulse,
                Kind = ChatKind.Denied,
                About = running.Agent,
                Denied = done.Denied.ToList(),
                State = CardState.Open,
                At = DateTimeOffset.UtcNow,
            });
        }

        if (!running.Stopped && reply.Error is null && ChatRules.HandoffFrom(info, running.Agent, reply.Text) is { } next)
        {
            reply.To = next;
            if (ChatRules.MayHandOff(info))
            {
                info.Hops++;
                Enqueue(id, new Job(next, null, null));
            }
            else
            {
                info.Held = next;
                chat.Messages.Add(new ChatMessage { Author = ChatAuthor.Pulse, Kind = ChatKind.Limit, About = next, State = CardState.Open, At = DateTimeOffset.UtcNow, Text = info.Hops.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            }
        }

        Save(chat);
        Changed?.Invoke(id);
        Changed?.Invoke("");
        if (running.Stopped && _queues.TryGetValue(id, out var q)) q.Clear();
        Pump(id);
    }

    private void Note(ChatFile chat, string agent, string text)
    {
        chat.Messages.Add(new ChatMessage { Author = ChatAuthor.Pulse, Kind = ChatKind.Note, About = agent, Text = text, At = DateTimeOffset.UtcNow });
        chat.Info.Updated = DateTimeOffset.UtcNow;
        Save(chat);
        Changed?.Invoke(chat.Info.Id);
    }

    private void Save(ChatFile chat)
    {
        try { _store.Save(chat); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Pulse could not save the chat {chat.Info.Id}: {ex.Message}");
        }
    }

    // ---- pictures ----------------------------------------------------------------------------

    /// <summary>The pictures in a folder and when each was last written (skipping heavy folders).</summary>
    internal static Dictionary<string, DateTime> Pictures(string root)
    {
        var found = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var stack = new Stack<(string Dir, int Depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0 && found.Count < 5000)
            {
                var (dir, depth) = stack.Pop();
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) found[f] = File.GetLastWriteTimeUtc(f);
                if (depth >= 6) continue;
                foreach (var d in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(d);
                    if (name is "node_modules" or ".git" or "bin" or "obj" or "dist" or ".next" or ".venv" || name.StartsWith('.')) continue;
                    stack.Push((d, depth + 1));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>Pictures made or changed since a turn began, in the folder and (for Codex) where Codex keeps its own.</summary>
    internal static List<string> NewPictures(string root, Dictionary<string, DateTime> before, string? extra, DateTime started)
    {
        var list = Pictures(root).Where(p => !before.TryGetValue(p.Key, out var was) || p.Value > was).OrderBy(p => p.Value).Select(p => p.Key).ToList();
        if (extra is not null && Directory.Exists(extra))
        {
            try
            {
                list.AddRange(Directory.EnumerateFiles(extra)
                    .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(f) >= started.AddSeconds(-1))
                    .OrderBy(File.GetLastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).Take(48).ToList();
    }

    private sealed record Job(string Agent, string? Extra, IReadOnlyList<string>? AllowOnce);

    private sealed class Running(string agent, ChatMessage reply, DateTime started, Dictionary<string, DateTime> pictures)
    {
        public string Agent { get; } = agent;
        public ChatMessage Reply { get; } = reply;
        public DateTime Started { get; } = started;
        public Dictionary<string, DateTime> Pictures { get; } = pictures;
        public IAgentTurn? Turn { get; set; }
        public DoneEvent? Done { get; set; }
        public bool Stopped { get; set; }
    }
}
