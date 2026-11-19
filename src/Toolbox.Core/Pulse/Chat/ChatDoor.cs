using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>
/// How an agent working in its own session (Claude Code or Codex) opens and directs a Pulse chat:
/// the requests that come through <see cref="ChatPipe"/> from <c>ftb chat</c> or <c>ftb mcp</c>.
/// Every request is one JSON object with an <c>op</c>; the answer is one JSON object with
/// <c>ok</c>, a <c>state</c>, a plain-text <c>text</c> for the agent to read, and the details.
///
///   open   start a chat with the other agent in the caller's folder (the owner allows it in Pulse)
///   say    the director writes to its guest
///   wait   wait for what's new: the guest's reply, the owner's words, a card for the owner
///   read   the chat so far (changes nothing)
///   list   the caller's chats
///   close  end a chat
///   models the models the guest can use
///   ping   is Pulse there
///
/// All work on chats happens on the hub's own thread (through <c>post</c>).
/// </summary>
public sealed class ChatDoor
{
    /// <summary>Longest wait one request may ask for (MCP can wait long; the command line uses less).</summary>
    public const int MaxWaitSeconds = 900;

    private readonly ChatHub _hub;
    private readonly Action<Action> _post;
    private readonly Func<string, string?> _program;
    private readonly Action<string>? _opened;
    private readonly string? _claudeHome;
    private readonly string? _codexHome;

    public ChatDoor(ChatHub hub, Action<Action> post, Func<string, string?> program, Action<string>? opened = null, string? claudeHome = null, string? codexHome = null)
    {
        _hub = hub;
        _post = post;
        _program = program;
        _opened = opened;
        _claudeHome = claudeHome;
        _codexHome = codexHome;
    }

    public async Task<string> HandleAsync(string line, CancellationToken token = default)
    {
        JsonObject req;
        try
        {
            req = JsonNode.Parse(line) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return Fail("Pulse expected one line of JSON.").ToJsonString();
        }
        JsonObject answer = Str(req, "op") switch
        {
            "ping" => new JsonObject { ["ok"] = true, ["state"] = "ready", ["text"] = "Pulse is ready." },
            "models" => Models(req),
            "open" => await Open(req).ConfigureAwait(false),
            "say" => await Say(req).ConfigureAwait(false),
            "wait" => await Wait(req, token).ConfigureAwait(false),
            "read" => await OnHub(() => Read(req)).ConfigureAwait(false),
            "list" => await OnHub(() => List(req)).ConfigureAwait(false),
            "close" => await OnHub(() => Close(req)).ConfigureAwait(false),
            var op => Fail($"Pulse doesn't know '{op}'. Use open, say, wait, read, list, close or models."),
        };
        return answer.ToJsonString();
    }

    // ---- open ------------------------------------------------------------------------------------

    private async Task<JsonObject> Open(JsonObject req)
    {
        if (Str(req, "inside") is { Length: > 0 })
            return Fail("You are running inside a Pulse chat, so you can't open another one. Answer in your reply instead.");
        var who = Str(req, "as");
        if (!Agent.IsAgent(who)) return Fail("Say who is opening the chat: as claude or as codex.");
        var guest = Str(req, "guest") is { Length: > 0 } g ? g.ToLowerInvariant() : Agent.Other(who!);
        if (!Agent.IsAgent(guest) || guest == who) return Fail($"The other agent in the chat must be {Agent.Name(Agent.Other(who!))}.");
        var folder = Str(req, "folder");
        if (folder is not { Length: > 0 } || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            return Fail("Give the folder you work in (a full path that exists). The other agent works there too.");
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var brief = Str(req, "brief")?.Trim();
        if (brief is not { Length: > 0 }) return Fail("Write a brief: the goal, what's decided, the rules, where outputs go, and your first request.");
        if (brief.Length > 40_000) return Fail("The brief is too long (over 40,000 characters). Point to files instead.");
        if (_program(guest) is null) return Fail($"{Agent.Name(guest)} isn't installed on this PC (or Pulse can't find it).");
        var permission = Str(req, "permission")?.ToLowerInvariant() switch
        {
            "edit" or "can-edit" or "write" => ChatPermission.Edit,
            "ask" => ChatPermission.Ask,
            _ => ChatPermission.Read,
        };
        string? outputs = null;
        if (Str(req, "outputs") is { Length: > 0 } o)
        {
            var full = Path.GetFullPath(Path.IsPathFullyQualified(o) ? o : Path.Combine(folder, o));
            var rel = Path.GetRelativePath(folder, full);
            if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return Fail("The outputs folder must be inside your folder.");
            outputs = rel == "." ? null : rel.Replace('\\', '/');
        }
        int budget = Math.Clamp(Int(req, "budget") ?? 8, 1, 40);
        var session = Str(req, "session");
        var info = new ChatInfo
        {
            Title = Str(req, "title")?.Trim() ?? "",
            Folder = folder,
            Agents = [guest],
            Director = who!,
            DirectorSession = session,
            DirectorPid = Int(req, "pid"),
            Permission = permission,
            Outputs = outputs,
            DirectorBudget = budget,
            Models = new() { [guest] = AgentModels.Pick(guest, Str(req, "model"), _codexHome) },
        };
        if (Bool(req, "notes") != false && session is { Length: > 0 } && DirectorNotes.FindSession(who!, session, folder, _claudeHome, _codexHome) is { } file)
        {
            var target = Path.Combine(_hub.Store.Dir, info.Id + "-files", "director-conversation.md");
            try
            {
                if (DirectorNotes.Write(who!, file, target)) info.DirectorNotes = target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var chat = await OnHub(() => _hub.CreateDirected(info, brief)).ConfigureAwait(false);
        _opened?.Invoke(info.Id);
        var model = AgentModels.NameOf(guest, info.ModelOf(guest), _codexHome);
        var text = new StringBuilder()
            .Append("Opened chat ").Append(info.Id).Append(" with ").Append(Agent.Name(guest)).Append(" (").Append(model).Append(") in ").Append(folder).Append(".\n")
            .Append("The owner must allow it in Pulse first: a card is waiting there. Then ").Append(Agent.Name(guest)).Append(" starts on your brief.\n")
            .Append(info.DirectorNotes is null ? "No copy of your conversation was made (your session wasn't found), so the brief is all it has.\n" : "It can also read a copy of your conversation if the brief isn't enough.\n")
            .Append("Next: wait for its reply (wait, chat ").Append(info.Id).Append(").");
        return Ok(chat, "waiting_for_owner", text.ToString());
    }

    // ---- say ---------------------------------------------------------------------------------------

    private Task<JsonObject> Say(JsonObject req) => OnHub(() =>
    {
        if (Mine(req, out var chat) is { } problem) return problem;
        var text = Str(req, "text") ?? "";
        var info = chat!.Info;
        var guest = Agent.Name(info.Guest ?? Agent.Other(info.Director));
        return _hub.Direct(info.Id, text) switch
        {
            DirectResult.Sent => Ok(chat, "sent", $"Sent to {guest}. Next: wait for its reply."),
            DirectResult.WaitingForOwner when info.Consent == ChatConsent.Open => Ok(chat, "waiting_for_owner", $"Saved. {guest} starts once the owner allows this chat in Pulse. Next: wait."),
            DirectResult.WaitingForOwner => Ok(chat, "needs_owner", $"Saved, but you have sent {info.DirectorTurns} messages without the owner, so Pulse waits for the owner to let you go on. Tell the owner, then wait."),
            DirectResult.Empty => Fail("Say something: the text was empty."),
            DirectResult.Closed => Ok(chat, info.Consent == ChatConsent.Declined ? "declined" : "closed", info.Consent == ChatConsent.Declined ? "The owner didn't allow this chat." : "This chat is closed."),
            _ => Ok(chat, Gone(info), GoneText(info)),
        };
    });

    // ---- wait --------------------------------------------------------------------------------------

    private async Task<JsonObject> Wait(JsonObject req, CancellationToken token)
    {
        int seconds = Math.Clamp(Int(req, "timeout") ?? 45, 1, MaxWaitSeconds);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? id = null;
        void OnChanged(string changed)
        {
            if (id is not null && (changed == id || changed.Length == 0) && Ready(id)) ready.TrySetResult();
        }
        var early = await OnHub<JsonObject?>(() =>
        {
            if (Mine(req, out var chat, allowGone: true) is { } problem) return problem;
            id = chat!.Info.Id;
            if (Ready(id)) return Answer(id, timedOut: false);
            _hub.Changed += OnChanged;
            _hub.MarkDirectorWaiting(id, true);
            return null;
        }).ConfigureAwait(false);
        if (early is not null) return early;
        try
        {
            await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(seconds), token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        return await OnHub(() =>
        {
            _hub.Changed -= OnChanged;
            _hub.MarkDirectorWaiting(id!, false);
            return Answer(id!, timedOut: !ready.Task.IsCompleted);
        }).ConfigureAwait(false);
    }

    /// <summary>Something for the director: the chat ended, or there is news (and the guest is done, or the owner or Pulse spoke).</summary>
    private bool Ready(string id)
    {
        if (_hub.Get(id) is not { } chat) return true;
        var info = chat.Info;
        if (info.Closed || !info.DirectorOutside) return true;
        var fresh = Fresh(chat);
        if (fresh.Count == 0) return false;
        return _hub.WorkingIn(id) is null || fresh.Any(m => m.Author is ChatAuthor.User or ChatAuthor.Pulse);
    }

    private static List<ChatMessage> Fresh(ChatFile chat) =>
        chat.Messages.Where(m => !m.ToldDirector && !m.Working && m.Author != chat.Info.Director).ToList();

    private JsonObject Answer(string id, bool timedOut)
    {
        if (_hub.Get(id) is not { } chat) return new JsonObject { ["ok"] = true, ["chat"] = id, ["state"] = "closed", ["text"] = "This chat was deleted in Pulse." };
        var info = chat.Info;
        var fresh = Fresh(chat);
        foreach (var m in fresh) m.ToldDirector = true;
        info.DirectorSeen = DateTimeOffset.UtcNow;
        if (fresh.Count > 0) _hub.Persist(chat);
        var state = StateOf(chat, fresh);
        var sb = new StringBuilder();
        foreach (var m in fresh) Describe(sb, chat, m);
        if (fresh.Count == 0) sb.Append(timedOut ? "Nothing new yet.\n" : "");
        sb.Append(Next(chat, state));
        var answer = Ok(chat, state, sb.ToString().TrimEnd());
        answer["items"] = Items(chat, fresh);
        return answer;
    }

    private string StateOf(ChatFile chat, List<ChatMessage> fresh)
    {
        var info = chat.Info;
        if (!info.DirectorOutside) return Gone(info);
        if (info.Closed) return info.Consent == ChatConsent.Declined ? "declined" : "closed";
        if (info.Consent == ChatConsent.Open || ChatHub.NeedsAnswer(chat)) return "needs_owner";
        if (_hub.WorkingIn(info.Id) is not null) return fresh.Any(m => m.Author == ChatAuthor.User) ? "owner" : "working";
        if (fresh.Any(m => m.Author == info.Guest && m.Kind == ChatKind.Text)) return "reply";
        if (fresh.Any(m => m.Author == ChatAuthor.User)) return "owner";
        return "idle";
    }

    private static string Gone(ChatInfo info) => info.DirectorInPulse ? "carried_on" : "taken_over";

    private static string GoneText(ChatInfo info) => info.DirectorInPulse
        ? $"Pulse carries {Agent.Name(info.Director)} on with a copy of your session, so this chat goes on inside Pulse. You don't direct it from here any more."
        : "The owner took over this chat. You can't write to it any more.";

    private string Next(ChatFile chat, string state)
    {
        var info = chat.Info;
        var guest = Agent.Name(info.Guest ?? Agent.Other(info.Director));
        return state switch
        {
            "reply" or "idle" => $"Next: your turn. Send the next request with say, or close the chat when the work is done.",
            "owner" => $"Next: the owner wrote in Pulse ({guest} sees it too). The owner's word comes first: answer or adjust with say, then wait.",
            "working" => $"Next: {guest} is still working. Wait again.",
            "needs_owner" => $"Next: waiting for the owner in Pulse ({OwnerReason(chat)}). Tell the owner, then wait again.",
            "declined" => "The owner didn't allow this chat.",
            "closed" => "This chat is closed.",
            _ => GoneText(info),
        };
    }

    private static string OwnerReason(ChatFile chat)
    {
        var info = chat.Info;
        if (info.Consent == ChatConsent.Open) return "to allow this chat";
        var card = chat.Messages.LastOrDefault(m => m.State == CardState.Open && m.Kind is ChatKind.Denied or ChatKind.Limit);
        return card?.Kind switch
        {
            ChatKind.Denied => $"to allow what {Agent.Name(card.About ?? info.Guest ?? "")} asked for",
            ChatKind.Limit => "to let you go on",
            _ => "a card is open",
        };
    }

    // ---- read, list, close, models -------------------------------------------------------------------

    private JsonObject Read(JsonObject req)
    {
        var id = Str(req, "chat");
        if (id is null || _hub.Get(id) is not { } chat) return Fail("No such chat. Use list to see your chats.");
        int last = Math.Clamp(Int(req, "last") ?? 40, 1, 300);
        var shown = chat.Messages.Where(m => !m.Working).TakeLast(last).ToList();
        var sb = new StringBuilder();
        var info = chat.Info;
        sb.Append(info.Title).Append(" · ").Append(Agent.Name(info.Guest ?? info.Agents.FirstOrDefault() ?? "")).Append(" in ").Append(info.Folder ?? "a quick chat").Append('\n');
        foreach (var m in shown) Describe(sb, chat, m, you: Str(req, "as"));
        var state = info.DirectorOutside ? StateOf(chat, []) : info.IsDirected ? Gone(info) : "owner_directs";
        var answer = Ok(chat, state, sb.ToString().TrimEnd());
        answer["items"] = Items(chat, shown);
        return answer;
    }

    private JsonObject List(JsonObject req)
    {
        var who = Str(req, "as");
        var session = Str(req, "session");
        var mine = _hub.Chats.Where(c => c.Info.Director == who || c.Info.TakenFrom == who).Take(20).ToList();
        var sb = new StringBuilder();
        var array = new JsonArray();
        if (mine.Count == 0) sb.Append("You have no chats in Pulse yet.");
        foreach (var c in mine)
        {
            var info = c.Info;
            var state = info.DirectorOutside ? StateOf(c, []) : Gone(info);
            bool yours = session is not { Length: > 0 } || info.DirectorSession is not { Length: > 0 } || info.DirectorSession == session;
            sb.Append(info.Id).Append("  ").Append(info.Title).Append(" · ").Append(Agent.Name(info.Guest ?? "")).Append(" · ").Append(state)
              .Append(" · ").Append(Ago(info.Updated)).Append(yours ? "" : " · another session").Append('\n');
            array.Add(new JsonObject { ["chat"] = info.Id, ["title"] = info.Title, ["guest"] = info.Guest, ["folder"] = info.Folder, ["state"] = state, ["yours"] = yours, ["updated"] = info.Updated.ToString("o", CultureInfo.InvariantCulture) });
        }
        return new JsonObject { ["ok"] = true, ["state"] = "list", ["text"] = sb.ToString().TrimEnd(), ["chats"] = array };
    }

    private JsonObject Close(JsonObject req)
    {
        if (Mine(req, out var chat) is { } problem) return problem;
        _hub.Close(chat!.Info.Id, chat.Info.Director);
        return Ok(chat, "closed", "Closed. The owner can still read it in Pulse, or take it over.");
    }

    private JsonObject Models(JsonObject req)
    {
        var agent = Str(req, "guest") is { Length: > 0 } g ? g.ToLowerInvariant() : Agent.Other(Str(req, "as") ?? Agent.Claude);
        if (!Agent.IsAgent(agent)) return Fail("Ask for claude or codex.");
        var list = AgentModels.For(agent, _codexHome);
        var array = new JsonArray();
        foreach (var m in list) array.Add(new JsonObject { ["id"] = m.Id, ["name"] = m.Name });
        var text = $"{Agent.Name(agent)} can use: " + string.Join(", ", list.Select(m => m.Id.Length == 0 ? m.Name : $"{m.Name} ({m.Id})"));
        return new JsonObject { ["ok"] = true, ["state"] = "models", ["text"] = text, ["models"] = array };
    }

    // ---- shared ------------------------------------------------------------------------------------

    /// <summary>The chat the request names, if the caller directs it (or directed it, for wait).</summary>
    private JsonObject? Mine(JsonObject req, out ChatFile? chat, bool allowGone = false)
    {
        chat = null;
        var id = Str(req, "chat");
        if (id is null || _hub.Get(id) is not { } found) return Fail("No such chat. Use list to see your chats.");
        var info = found.Info;
        var who = Str(req, "as");
        if (who is null || (info.Director != who && info.TakenFrom != who))
            return Fail(info.IsDirected ? $"This chat is directed by {Agent.Name(info.Director)}, not you." : "The owner directs this chat in Pulse.");
        var session = Str(req, "session");
        if (session is { Length: > 0 } && info.DirectorSession is { Length: > 0 } own && own != session)
            return Fail("This chat belongs to another session of yours. Only the session that opened it directs it.");
        if (!allowGone && !info.DirectorOutside)
        {
            chat = found;
            return Ok(found, Gone(info), GoneText(info));
        }
        chat = found;
        info.DirectorSeen = DateTimeOffset.UtcNow;
        return null;
    }

    private void Describe(StringBuilder sb, ChatFile chat, ChatMessage m, string? you = null)
    {
        var info = chat.Info;
        var at = "[" + m.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "] ";
        switch (m.Kind)
        {
            case ChatKind.Approval:
                sb.Append(at).Append(m.State == CardState.Skipped ? "Owner took back approval of: " : "Owner approved: ").Append(m.Text).Append('\n');
                return;
            case ChatKind.Note:
                sb.Append(at).Append("Pulse: ").Append(m.Text).Append('\n');
                return;
            case ChatKind.Consent:
                sb.Append(at).Append("Pulse: ").Append(m.State switch
                {
                    CardState.Open => $"waiting for the owner to allow this chat ({(m.Text == ChatPermission.Edit ? "you asked for editing" : "you asked for reading only")}).",
                    CardState.AllowEdit => $"the owner allowed this chat; {Agent.Name(m.About ?? "")} can edit files in the folder.",
                    CardState.AllowRead => $"the owner allowed this chat; {Agent.Name(m.About ?? "")} reads only.",
                    CardState.Paused => "the owner took over this chat.",
                    _ => "the owner didn't allow this chat.",
                }).Append('\n');
                return;
            case ChatKind.Limit:
                sb.Append(at).Append("Pulse: ").Append(m.State switch
                {
                    CardState.Open => info.DirectorOutside ? $"you sent {m.Text} messages without the owner; Pulse waits for the owner to let you go on." : "the agents handed work over as often as allowed; Pulse waits for the owner.",
                    CardState.More => "the owner let it go on.",
                    _ => "the owner paused here.",
                }).Append('\n');
                return;
            case ChatKind.Denied:
                var what = string.Join("; ", m.Denied.Select(d => d.Summary));
                sb.Append(at).Append("Pulse: ").Append(m.State switch
                {
                    CardState.Open => $"{Agent.Name(m.About ?? "")} wasn't allowed to: {what}. Waiting for the owner's OK.",
                    CardState.Allowed => $"the owner allowed once: {what}.",
                    CardState.Always => $"the owner allowed for this chat: {what}.",
                    _ => $"the owner didn't allow: {what}.",
                }).Append('\n');
                return;
        }
        var who = m.Author == ChatAuthor.User ? "Owner" : m.Author == you && m.Author == info.Director ? "You" : Agent.Name(m.Author);
        var text = m.Text.Trim();
        sb.Append(at).Append(who).Append(": ").Append(text.Length > 0 ? text : m.Error is { } e ? "(stopped: " + e + ")" : "(no answer)").Append('\n');
        if (text.Length > 0 && m.Error is { } error) sb.Append("  (stopped: ").Append(error).Append(")\n");
        if (m.Images.Count > 0) sb.Append("  Pictures: ").Append(ChatRules.PictureList(info, m.Images)).Append('\n');
        if (m.Overwrote is { Count: > 0 } over)
            sb.Append("  Saved over an earlier version (not a new one): ").Append(ChatRules.PictureList(info, over)).Append(". Check nothing approved was lost.\n");
        if (m.Author == info.Guest && m.Images.Count > 0)
            sb.Append("  Before you approve: open each picture with look (whole and every part) and hunt for faults; after an edit, measure it with diff.\n");
        if (m.Steps.Count(s => s.Kind == "edit") is > 0 and var edits) sb.Append("  Changed ").Append(edits).Append(edits == 1 ? " file" : " files").Append(": ")
            .Append(string.Join(", ", m.Steps.Where(s => s.Kind == "edit").Select(s => s.Text).Distinct().Take(12))).Append('\n');
    }

    private static JsonArray Items(ChatFile chat, IEnumerable<ChatMessage> messages)
    {
        var array = new JsonArray();
        foreach (var m in messages)
        {
            var item = new JsonObject
            {
                ["id"] = m.Id,
                ["author"] = m.Author,
                ["kind"] = m.Kind,
                ["text"] = m.Text,
                ["at"] = m.At.ToString("o", CultureInfo.InvariantCulture),
            };
            if (m.State is { } state) item["state"] = state;
            if (m.Error is { } error) item["error"] = error;
            if (m.Images.Count > 0) item["pictures"] = new JsonArray(m.Images.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
            array.Add(item);
        }
        return array;
    }

    private static JsonObject Ok(ChatFile chat, string state, string text) => new()
    {
        ["ok"] = true,
        ["chat"] = chat.Info.Id,
        ["state"] = state,
        ["guest"] = chat.Info.Guest,
        ["folder"] = chat.Info.Folder,
        ["text"] = text,
    };

    private static JsonObject Fail(string message) => new() { ["ok"] = false, ["state"] = "error", ["error"] = message, ["text"] = message };

    private static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.UtcNow - at;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : $"{(int)span.TotalDays} d ago";
    }

    private static string? Str(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var v) && v is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static int? Int(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<double>(out var d)) return (int)d;
        return value.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    private static bool? Bool(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is not JsonValue value) return null;
        if (value.TryGetValue<bool>(out var b)) return b;
        return value.TryGetValue<string>(out var s) ? s is "true" or "1" or "yes" : null;
    }

    private Task<T> OnHub<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try { done.TrySetResult(work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }
}
