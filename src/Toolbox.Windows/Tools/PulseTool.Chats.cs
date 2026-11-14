using System.Windows;
using System.Windows.Threading;
using Toolbox.Core.Pulse;
using Toolbox.Core.Pulse.Chat;
using Toolbox.Windows.Pulse;
using Toolbox.Windows.Pulse.Chat;
using Toolbox.Windows.Shell;
using Toolbox.Windows.Theme;

namespace Toolbox.Windows.Tools;

/// <summary>
/// Pulse's chats with Claude and Codex: the hub that runs them lives while Pulse is on; the Chats
/// tile opens the window and shows which chat needs the user, is working, or has a new reply.
/// </summary>
internal sealed partial class PulseTool
{
    private ChatHub? _chats;
    private ChatWindow? _chatWindow;

    internal ChatHub? Chats => _chats;

    internal ChatWindow? ChatView => _chatWindow;

    /// <summary>The program for an agent: the one chosen in Pulse's settings, else the one found on this PC.</summary>
    private string? ProgramFor(string agent) => AgentLocator.Find(agent, agent == Agent.Codex ? S.CodexProgram : S.ClaudeProgram);

    private void StartChats()
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        _chats = new ChatHub(new ChatStore(ChatStore.DefaultDir), new ProcessAgentRunner(), ProgramFor, a => dispatcher.BeginInvoke(a));
        _chats.Changed += _ => UpdateChatsTile();
        UpdateChatsTile();
    }

    private void StopChats()
    {
        _chatWindow?.Close();
        _chatWindow = null;
        _chats?.Dispose();
        _chats = null;
    }

    /// <summary>Opens the chat window (and a chat in it).</summary>
    internal void OpenChats(string? chatId = null)
    {
        if (_chats is null) return;
        if (_chatWindow is null)
        {
            _chatWindow = new ChatWindow(_chats, ProgramFor);
            _chatWindow.Closed += (_, _) => _chatWindow = null;
            _chatWindow.Show();
        }
        if (_chatWindow.WindowState == WindowState.Minimized) _chatWindow.WindowState = WindowState.Normal;
        if (chatId is not null) _chatWindow.Select(chatId);
        else if (_chats.Chats.FirstOrDefault(ChatHub.NeedsAnswer) is { } waiting) _chatWindow.Select(waiting.Info.Id);
        Foreground.Activate(_chatWindow);
    }

    private void UpdateChatsTile()
    {
        if (_dock?.Tile(PulseTiles.Chats) is not { } tile || _chats is null) return;
        int needs = _chats.NeedsYou;
        var state = needs > 0 ? ClaudeActivity.Waiting
            : _chats.AnyWorking ? ClaudeActivity.Working
            : _chats.UnreadCount > 0 && _chatWindow is not { IsActive: true } ? ClaudeActivity.Done
            : ClaudeActivity.Idle;
        tile.SetStatus(state, needs);
        tile.SetLabel(ShortName(PulseTiles.Chats));
    }

    private FrameworkElement ChatsCard()
    {
        var parts = new List<UIElement> { PulseCards.Head("lap-chats", Lang.T("Chats", "المحادثات")) };
        if (_chats is null || _chats.Chats.Count == 0)
        {
            parts.Add(PulseCards.Body(Lang.T("Chat with Claude, Codex, or both in one room: they hand work to each other with @, and you steer either one.", "تحدّث مع Claude أو Codex أو كليهما في غرفة واحدة: يتبادلان العمل بـ @، وتوجّه أيًّا منهما.")));
            parts.Add(PulseCards.Note(Lang.T("Click to start a chat. It uses Claude Code and Codex as they are signed in on this PC.", "انقر لبدء محادثة. تستخدم Claude Code وCodex كما سُجّل الدخول إليهما على هذا الحاسوب.")));
            return PulseCards.Card([.. parts]);
        }
        foreach (var chat in _chats.Chats.OrderByDescending(c => ChatHub.NeedsAnswer(c)).ThenByDescending(c => _chats.WorkingIn(c.Info.Id) is not null).ThenByDescending(c => c.Info.Updated).Take(4))
        {
            var working = _chats.WorkingIn(chat.Info.Id);
            var (dot, text) = ChatHub.NeedsAnswer(chat) ? (PulseTileView.Amber, Lang.T("needs you", "يحتاجك"))
                : working is not null ? (ThemeResources.Lap.Accent, Lang.T($"{Agent.Name(working)} is working", $"{Agent.Name(working)} يعمل"))
                : chat.Info.Unread ? (PulseTileView.Green, Lang.T("new reply", "رد جديد"))
                : (System.Windows.Media.Color.FromRgb(0x9A, 0x9F, 0xAA), Lang.Ago(chat.Info.Updated, DateTimeOffset.UtcNow));
            parts.Add(PulseCards.Status(dot, $"{chat.Info.Title} · {text}"));
        }
        parts.Add(PulseCards.Note(Lang.T("Click to open your chats.", "انقر لفتح محادثاتك.")));
        return PulseCards.Card([.. parts]);
    }
}
