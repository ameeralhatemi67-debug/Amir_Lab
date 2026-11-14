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

    public bool IsGroup => Agents.Count > 1;

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

    /// <summary>For a <see cref="ChatKind.Denied"/> card: what was not allowed.</summary>
    public List<DeniedAction> Denied { get; set; } = [];

    /// <summary>For a card: the agent it is about.</summary>
    public string? About { get; set; }

    /// <summary>For a card: <see cref="CardState"/>.</summary>
    public string? State { get; set; }

    /// <summary>What went wrong, when the turn failed.</summary>
    public string? Error { get; set; }

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
