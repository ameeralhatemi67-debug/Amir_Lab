using System.Text;
using System.Text.RegularExpressions;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>
/// The room's rules, kept apart from processes and windows so they can be tested: who answers a
/// message, when an agent hands work to the other, what each agent is told, and when Pulse waits
/// for the user.
/// </summary>
public static partial class ChatRules
{
    /// <summary>Longest piece of another message passed on in full (longer ones are cut).</summary>
    public const int MaxPassedOn = 6000;

    [GeneratedRegex(@"(?<![\w@./])@(claude|codex|both|all)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MentionPattern();

    [GeneratedRegex(@"```.*?(```|$)", RegexOptions.Singleline)]
    private static partial Regex CodeBlockPattern();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCodePattern();

    /// <summary>The agents a text names with @, in the order they first appear (@both and @all name both).</summary>
    public static IReadOnlyList<string> MentionsIn(string text, IReadOnlyList<string>? order = null)
    {
        var plain = InlineCodePattern().Replace(CodeBlockPattern().Replace(text ?? "", " "), " ");
        var found = new List<string>();
        foreach (Match m in MentionPattern().Matches(plain))
        {
            var word = m.Groups[1].Value.ToLowerInvariant();
            IEnumerable<string> agents = word is "both" or "all" ? order ?? Agent.All : [word];
            foreach (var a in agents)
                if (!found.Contains(a)) found.Add(a);
        }
        return found;
    }

    /// <summary>The order two agents answer in when both are asked: the lead first.</summary>
    public static IReadOnlyList<string> LeadOrder(ChatInfo chat)
    {
        var first = Agent.IsAgent(chat.Lead) && chat.Has(chat.Lead) ? chat.Lead : chat.Agents.FirstOrDefault() ?? Agent.Claude;
        return chat.Agents.OrderBy(a => a == first ? 0 : 1).ToList();
    }

    /// <summary>
    /// Who answers the user's message, in order. With one agent, that agent. Otherwise the agents it
    /// names with @; with none named, <paramref name="to"/> (an agent id or "both", from the To
    /// switch), or else the chat's lead.
    /// </summary>
    public static IReadOnlyList<string> Targets(ChatInfo chat, string text, string? to = null)
    {
        if (chat.Agents.Count == 0) return [];
        if (!chat.IsGroup) return [chat.Agents[0]];
        var named = MentionsIn(text, LeadOrder(chat)).Where(chat.Has).ToList();
        if (named.Count > 0) return named;
        var choice = to ?? chat.Lead;
        if (choice == "both") return LeadOrder(chat);
        return chat.Has(choice) ? [choice] : [LeadOrder(chat)[0]];
    }

    /// <summary>The agent an agent's reply hands work to (it names the other with @), or null.</summary>
    public static string? HandoffFrom(ChatInfo chat, string author, string reply)
    {
        if (!chat.IsGroup || !Agent.IsAgent(author)) return null;
        var other = Agent.Other(author);
        return chat.Has(other) && MentionsIn(reply).Contains(other) ? other : null;
    }

    /// <summary>
    /// What happens after an agent hands work over: it goes ahead (and counts as one hop), or the
    /// chat has used its hops and Pulse waits for the user.
    /// </summary>
    public static bool MayHandOff(ChatInfo chat) => chat.Hops < Math.Max(1, chat.HopLimit);

    /// <summary>
    /// The room's instructions for an agent: who else is here, how to hand work over, who owns what,
    /// the recipe's roles. Claude gets it as an addition to its system prompt on every turn; Codex at
    /// the top of its first message.
    /// </summary>
    public static string Preamble(ChatInfo chat, string agent)
    {
        var me = Agent.Name(agent);
        var sb = new StringBuilder();
        sb.Append("You are ").Append(me).Append(" in a Pulse chat. Pulse is part of Amir Lap, an app on the user's Windows PC.");
        if (chat.IsGroup)
        {
            var otherId = Agent.Other(agent);
            var other = Agent.Name(otherId);
            sb.Append(' ').Append("The chat also has ").Append(other).Append(", another AI coding agent (made by ").Append(Agent.Maker(otherId)).Append("). ")
              .Append("Pulse passes messages between you: at the start of each turn you are told what the user and ").Append(other).Append(" wrote since your last turn.\n");
            sb.Append("- To hand work or a question to ").Append(other).Append(", write @").Append(otherId).Append(" followed by what you need, at the end of your reply. ")
              .Append("Do it only when ").Append(other).Append(" has to act; otherwise just answer. Never write @").Append(otherId).Append(" only to mention them.\n");
            sb.Append("- One of you works at a time. After ").Append(Math.Max(1, chat.HopLimit)).Append(" hand-offs between you without the user, Pulse waits for the user.\n");
            var mine = OwnsOf(chat, agent);
            var theirs = OwnsOf(chat, otherId);
            if (mine.Count > 0 || theirs.Count > 0)
            {
                sb.Append("- Who owns what in this folder: ");
                if (mine.Count > 0) sb.Append("you change ").Append(string.Join(", ", mine)).Append("; ");
                if (theirs.Count > 0) sb.Append(other).Append(" changes ").Append(string.Join(", ", theirs)).Append(". ");
                sb.Append("Don't change what ").Append(other).Append(" owns; ask with @").Append(otherId).Append(" instead.\n");
            }
            switch (chat.Recipe)
            {
                case ChatRecipe.Builder:
                    sb.Append("- Roles: one of you builds (code, pages, data), the other makes the pictures. Pictures are saved as PNG files inside the folder their maker owns, with names that say what they show (for example front.png, back.png, side.png), and the builder uses them from there.\n");
                    break;
                case ChatRecipe.Reviewer:
                    sb.Append("- Roles: ").Append(chat.Lead == Agent.Codex ? "Codex" : "Claude").Append(" writes the changes; ")
                      .Append(chat.Lead == Agent.Codex ? "Claude" : "Codex").Append(" reviews them, reports problems plainly, and never edits files.\n");
                    break;
                case ChatRecipe.Free:
                    sb.Append("- Both of you may change files. Before changing a file the other one is working on, ask first.\n");
                    break;
            }
        }
        else sb.Append('\n');
        if (chat.IsQuick)
            sb.Append("- This is a quick chat without a project folder. Your working folder belongs to Pulse; don't change files on the PC outside it.\n");
        if (chat.Permission == ChatPermission.Read)
            sb.Append("- This chat is read-only: read, explain and plan, but don't change files or run commands that change anything.\n");
        sb.Append("- Write for the user: short and plain. Say what you did and what you need.");
        return sb.ToString();
    }

    public static IReadOnlyList<string> OwnsOf(ChatInfo chat, string agent) =>
        chat.Owns.TryGetValue(agent, out var list) ? list.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() : [];

    /// <summary>
    /// The message an agent gets for its turn: everything new in the chat since it last spoke,
    /// besides its own messages. When the only new thing is the user's message, that message
    /// itself. <paramref name="extra"/> (an allowed action, a reminder) goes at the end.
    /// </summary>
    public static string Digest(ChatInfo chat, IReadOnlyList<ChatMessage> messages, string agent, string? extra = null)
    {
        int from = Math.Clamp(chat.Seen.GetValueOrDefault(agent), 0, messages.Count);
        var fresh = messages.Skip(from).Where(m => m.Author != agent && m.Kind is ChatKind.Text or ChatKind.Approval && !m.Working).ToList();
        var sb = new StringBuilder();
        if (fresh.Count == 1 && fresh[0].Author == ChatAuthor.User && fresh[0].Kind == ChatKind.Text)
            sb.Append(fresh[0].Text.Trim());
        else if (fresh.Count > 0)
        {
            sb.Append("[New in this chat since your last turn]\n");
            foreach (var m in fresh)
            {
                if (m.Kind == ChatKind.Approval)
                {
                    sb.Append("User approved: ").Append(m.Text.Trim()).Append('\n');
                    continue;
                }
                var who = m.Author == ChatAuthor.User ? "User" : Agent.Name(m.Author) + (m.To == agent ? " (to you)" : "");
                var text = m.Text.Trim();
                if (text.Length > MaxPassedOn) text = text[..MaxPassedOn] + " […]";
                sb.Append(who).Append(": ").Append(text.Length == 0 && m.Error is { } e ? "(stopped: " + e + ")" : text).Append('\n');
                if (m.Images.Count > 0)
                    sb.Append("  Pictures ").Append(Agent.IsAgent(m.Author) ? Agent.Name(m.Author) + " made" : "attached").Append(": ")
                      .Append(string.Join(", ", m.Images.Select(p => Relative(chat, p)))).Append('\n');
            }
        }
        if (!string.IsNullOrWhiteSpace(extra))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(extra.Trim());
        }
        return sb.Length == 0 ? "Go on." : sb.ToString();
    }

    /// <summary>A path inside the chat's folder said relative to it (others stay as they are).</summary>
    public static string Relative(ChatInfo chat, string path)
    {
        if (chat.Folder is { } root)
        {
            try
            {
                var rel = Path.GetRelativePath(root, path);
                if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) return rel.Replace('\\', '/');
            }
            catch (ArgumentException) { }
        }
        return path;
    }

    /// <summary>A short title from the first message (for a chat the user did not name).</summary>
    public static string TitleFrom(string text)
    {
        var plain = MentionPattern().Replace(text ?? "", "").Trim();
        plain = Regex.Replace(plain, @"\s+", " ");
        if (plain.Length == 0) return "New chat";
        if (plain.Length <= 40) return plain;
        var cut = plain[..40];
        int space = cut.LastIndexOf(' ');
        return (space > 20 ? cut[..space] : cut) + "…";
    }

    /// <summary>The default folders each agent owns for a recipe (relative to the chat folder).</summary>
    public static Dictionary<string, List<string>> DefaultOwns(string recipe, string lead) => recipe switch
    {
        ChatRecipe.Builder => new()
        {
            [lead == Agent.Codex ? Agent.Codex : Agent.Claude] = ["src/", "index.html"],
            [lead == Agent.Codex ? Agent.Claude : Agent.Codex] = ["public/images/"],
        },
        // Coder + Reviewer and Free talk say their roles in the room instructions; nothing is fenced off.
        _ => [],
    };
}
