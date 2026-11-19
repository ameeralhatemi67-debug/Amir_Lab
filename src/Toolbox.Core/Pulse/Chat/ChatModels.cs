namespace Toolbox.Core.Pulse.Chat;

/// <summary>The agents a Pulse chat can hold, by id.</summary>
public static class Agent
{
    public const string Claude = "claude";
    public const string Codex = "codex";

    public static readonly IReadOnlyList<string> All = [Claude, Codex];

    public static string Name(string id) => id switch { Claude => "Claude", Codex => "Codex", _ => id };

    /// <summary>Who makes it (said in the room instructions).</summary>
    public static string Maker(string id) => id == Codex ? "OpenAI" : "Anthropic";

    public static string Other(string id) => id == Claude ? Codex : Claude;

    public static bool IsAgent(string? id) => id is Claude or Codex;
}

/// <summary>Who wrote a message besides the agents.</summary>
public static class ChatAuthor
{
    public const string User = "user";

    /// <summary>Pulse itself: cards (allow, keep going) and notes (stopped, errors).</summary>
    public const string Pulse = "pulse";
}

/// <summary>What a message is.</summary>
public static class ChatKind
{
    /// <summary>Something the user or an agent wrote.</summary>
    public const string Text = "text";

    /// <summary>An agent was not allowed to do something: the user can allow it once, always, or skip it.</summary>
    public const string Denied = "denied";

    /// <summary>The agents talked to each other as often as the chat allows: keep going or pause.</summary>
    public const string Limit = "limit";

    /// <summary>The user approved a picture (a small line; the agent that made it reads it on its next turn).</summary>
    public const string Approval = "approval";

    /// <summary>Pulse says something: stopped, picked up from elsewhere, an error.</summary>
    public const string Note = "note";

    /// <summary>An agent opened this chat from its own session: the owner allows it (read only or editing) or not.</summary>
    public const string Consent = "consent";
}

/// <summary>
/// Who directs a chat, fixed when it is made: the owner (from Pulse, where Pulse runs every agent),
/// or Claude or Codex working from its own session (which opened the chat and talks to it through
/// Amir Lap's command line or its MCP server; Pulse runs only the other agent, the guest).
/// </summary>
public static class ChatDirector
{
    public const string User = "user";
}

/// <summary>Where a chat an agent opened stands with the owner.</summary>
public static class ChatConsent
{
    /// <summary>Waiting for the owner to allow it in Pulse; the guest doesn't start until then.</summary>
    public const string Open = "open";

    public const string Allowed = "allowed";

    public const string Declined = "declined";
}

/// <summary>What the agents may change on the PC.</summary>
public static class ChatPermission
{
    /// <summary>They read freely; every edit and command asks first.</summary>
    public const string Ask = "ask";

    /// <summary>They edit (their own folders in a group); commands ask first.</summary>
    public const string Edit = "edit";

    /// <summary>They only read.</summary>
    public const string Read = "read";

    public static readonly IReadOnlyList<string> All = [Ask, Edit, Read];
}

/// <summary>How two agents work together.</summary>
public static class ChatRecipe
{
    /// <summary>One builds, the other makes the pictures.</summary>
    public const string Builder = "builder";

    /// <summary>One writes the change, the other reviews it and never edits.</summary>
    public const string Reviewer = "reviewer";

    /// <summary>Both answer and edit; they ask before touching the same file.</summary>
    public const string Free = "free";

    public static readonly IReadOnlyList<string> All = [Builder, Reviewer, Free];
}

/// <summary>States of a card (a denied action or the turn limit).</summary>
public static class CardState
{
    public const string Open = "open";
    public const string Allowed = "allowed";
    public const string Always = "always";
    public const string Skipped = "skipped";
    public const string More = "more";
    public const string Paused = "paused";

    /// <summary>A chat an agent opened is allowed, with the guest only reading.</summary>
    public const string AllowRead = "allow-read";

    /// <summary>A chat an agent opened is allowed, with the guest changing files in the folder.</summary>
    public const string AllowEdit = "allow-edit";
}

/// <summary>A chat: who is in it, where it works, its rules, and the agents' own sessions.</summary>
public sealed class ChatInfo
{
    public string Id { get; set; } = NewId();

    public string Title { get; set; } = "";

    /// <summary>The project folder, or null for a quick chat (which runs in Amir Lap's own folder).</summary>
    public string? Folder { get; set; }

    /// <summary>The agents in it (one, or both).</summary>
    public List<string> Agents { get; set; } = [];

    /// <summary>A group's recipe (<see cref="ChatRecipe"/>); empty for one agent.</summary>
    public string Recipe { get; set; } = "";

    /// <summary>Who answers a message without @: an agent id, or "both".</summary>
    public string Lead { get; set; } = Agent.Claude;

    /// <summary>How many times the agents may hand work to each other before Pulse waits for the user.</summary>
    public int HopLimit { get; set; } = 4;

    /// <summary><see cref="ChatPermission"/>.</summary>
    public string Permission { get; set; } = ChatPermission.Ask;

    /// <summary>Folders (relative to <see cref="Folder"/>) each agent owns in a group, by agent id.</summary>
    public Dictionary<string, List<string>> Owns { get; set; } = [];

    /// <summary>Claude permission rules the user allowed for good in this chat ("Bash(npm install:*)").</summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>Each agent's own session (Claude Code session id, Codex thread id), once it has one.</summary>
    public Dictionary<string, string> Sessions { get; set; } = [];

    /// <summary>An earlier session to continue as a copy on the agent's first turn, by agent id.</summary>
    public Dictionary<string, string> ForkFrom { get; set; } = [];

    /// <summary>How many of the chat's messages each agent has been told about.</summary>
    public Dictionary<string, int> Seen { get; set; } = [];

    /// <summary>Hand-offs between the agents since the user last wrote.</summary>
    public int Hops { get; set; }

    /// <summary>An agent waiting for its turn because the limit was reached (null when none).</summary>
    public string? Held { get; set; }

    public DateTimeOffset Created { get; set; }

    public DateTimeOffset Updated { get; set; }

    /// <summary>An agent replied since the user last looked at this chat.</summary>
    public bool Unread { get; set; }

    /// <summary>Each agent's model (an alias such as "opus", or a model id); missing or empty means the agent's own default.</summary>
    public Dictionary<string, string> Models { get; set; } = [];

    // ---- a chat an agent directs ----------------------------------------------------------------

    /// <summary><see cref="ChatDirector.User"/>, or the agent that directs it from its own session. Set when the chat is made.</summary>
    public string Director { get; set; } = ChatDirector.User;

    /// <summary>The director's own session (a Claude Code session id or a Codex thread id), when known.</summary>
    public string? DirectorSession { get; set; }

    /// <summary>The process of the director's program, when known (to tell whether its session is still open).</summary>
    public int? DirectorPid { get; set; }

    /// <summary>A readable copy of the director's conversation when it opened the chat, for the guest to read if the brief isn't enough.</summary>
    public string? DirectorNotes { get; set; }

    /// <summary>When the director last talked to Pulse (said something, waited or read).</summary>
    public DateTimeOffset? DirectorSeen { get; set; }

    /// <summary>True after the owner had Pulse carry the director on (a copy of its session, run by Pulse).</summary>
    public bool DirectorInPulse { get; set; }

    /// <summary>The agent that directed the chat before the owner took over (shown in the chat), else null.</summary>
    public string? TakenFrom { get; set; }

    /// <summary>Where the guest saves what it makes, relative to <see cref="Folder"/>, when the director said.</summary>
    public string? Outputs { get; set; }

    /// <summary>How many messages the director may send without the owner before Pulse waits for the owner.</summary>
    public int DirectorBudget { get; set; } = 8;

    /// <summary>Messages the director sent since the owner last wrote or said keep going.</summary>
    public int DirectorTurns { get; set; }

    /// <summary><see cref="ChatConsent"/> for a chat an agent opened; empty for a chat the owner made.</summary>
    public string Consent { get; set; } = "";

    /// <summary>The chat is over (its director or the owner closed it): nothing more runs in it.</summary>
    public bool Closed { get; set; }

    public bool IsGroup => Agents.Count > 1;

    /// <summary>An agent directs this chat (from its own session, or carried on by Pulse).</summary>
    public bool IsDirected => Agent.IsAgent(Director);

    /// <summary>The director works from its own session, outside Pulse (Pulse runs only the guest).</summary>
    public bool DirectorOutside => IsDirected && !Agents.Contains(Director);

    /// <summary>The agent the director works with, in a chat an agent directs.</summary>
    public string? Guest => IsDirected ? Agents.FirstOrDefault(a => a != Director) : null;

    /// <summary>Everyone in the chat besides the owner: the agents Pulse runs, and a director working from outside.</summary>
    public IReadOnlyList<string> Members => DirectorOutside ? [Director, .. Agents] : Agents;

    public string ModelOf(string agent) => Models.GetValueOrDefault(agent) ?? "";

    public bool IsQuick => Folder is null;

    public bool Has(string agent) => Agents.Contains(agent);

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
}

/// <summary>One message in a chat, or one card.</summary>
public sealed class ChatMessage
{
    public string Id { get; set; } = ChatInfo.NewId();

    /// <summary>user, pulse, or an agent id.</summary>
    public string Author { get; set; } = ChatAuthor.User;

    /// <summary><see cref="ChatKind"/>.</summary>
    public string Kind { get; set; } = ChatKind.Text;

    public string Text { get; set; } = "";

    /// <summary>The agent an agent handed this to (shown as "to Codex").</summary>
    public string? To { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>True while the agent is still working on it.</summary>
    public bool Working { get; set; }

    /// <summary>What the agent did on its way (read, edited, ran).</summary>
    public List<ChatStep> Steps { get; set; } = [];

    /// <summary>Pictures made or changed during this turn (full paths).</summary>
    public List<string> Images { get; set; } = [];

    /// <summary>Of <see cref="Images"/>, the ones that already existed and were saved over, instead of saved as a new version.</summary>
    public List<string>? Overwrote { get; set; }

    /// <summary>For a <see cref="ChatKind.Denied"/> card: what was not allowed.</summary>
    public List<DeniedAction> Denied { get; set; } = [];

    /// <summary>For a card: the agent it is about.</summary>
    public string? About { get; set; }

    /// <summary>For a card: <see cref="CardState"/>.</summary>
    public string? State { get; set; }

    /// <summary>What went wrong, when the turn failed.</summary>
    public string? Error { get; set; }

    /// <summary>In a chat an agent directs: the director has been given this message (so it isn't given twice).</summary>
    public bool ToldDirector { get; set; }

    public bool IsAgent => Agent.IsAgent(Author);
}

/// <summary>One thing an agent did during a turn.</summary>
public sealed class ChatStep
{
    /// <summary>read, edit, run, web, image, say, other.</summary>
    public string Kind { get; set; } = "other";

    public string Text { get; set; } = "";
}

/// <summary>Something an agent wanted to do and was not allowed to.</summary>
public sealed class DeniedAction
{
    /// <summary>The tool it asked for (Bash, Edit, Write...).</summary>
    public string Tool { get; set; } = "";

    /// <summary>What it is, said plainly ("Run npm install", "Edit src/App.tsx").</summary>
    public string Summary { get; set; } = "";

    /// <summary>The permission rule that allows exactly this once ("Bash(npm install)").</summary>
    public string Rule { get; set; } = "";

    /// <summary>The rule for "always in this chat" ("Bash(npm install:*)").</summary>
    public string AlwaysRule { get; set; } = "";
}

/// <summary>A chat with its messages, as saved.</summary>
public sealed class ChatFile
{
    public ChatInfo Info { get; set; } = new();

    public List<ChatMessage> Messages { get; set; } = [];
}
