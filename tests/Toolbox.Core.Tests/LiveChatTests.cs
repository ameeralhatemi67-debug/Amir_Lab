using Toolbox.Core.Pulse.Chat;

namespace Toolbox.Core.Tests;

/// <summary>
/// Runs a real group chat with the Claude Code and Codex installed on this PC (two tiny turns).
/// Off unless AMIRLAP_LIVE_CHAT=1, because it uses the user's own limits.
/// </summary>
public sealed class LiveChatTests
{
    [Fact]
    public async Task ClaudeHandsATaskToCodexForReal()
    {
        if (Environment.GetEnvironmentVariable("AMIRLAP_LIVE_CHAT") != "1") return;
        var dir = Path.Combine(Path.GetTempPath(), "amirlap-livechat-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(dir, "project");
        Directory.CreateDirectory(folder);
        var gate = new object();
        using var hub = new ChatHub(new ChatStore(Path.Combine(dir, "chats")), new ProcessAgentRunner(), a => AgentLocator.Find(a), a => { lock (gate) a(); });
        ChatFile chat;
        lock (gate)
        {
            chat = hub.Create(new ChatInfo
            {
                Title = "Live check",
                Folder = folder,
                Agents = [Agent.Claude, Agent.Codex],
                Recipe = ChatRecipe.Free,
                Permission = ChatPermission.Read,
                HopLimit = 1,
            });
            hub.Send(chat.Info.Id, "@claude This is a quick test of the chat. Reply with one short sentence, then hand over to Codex and ask it to reply with just the word pong.");
        }

        var until = DateTime.UtcNow.AddMinutes(4);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1000);
            lock (gate)
            {
                if (hub.WorkingIn(chat.Info.Id) is null && chat.Messages.Count(m => m.IsAgent) >= 2) break;
            }
        }

        lock (gate)
        {
            var log = string.Join("\n", chat.Messages.Select(m => $"{m.Author}/{m.Kind}: {m.Text} {(m.Error is null ? "" : "[" + m.Error + "]")} steps={m.Steps.Count}"));
            var claude = chat.Messages.FirstOrDefault(m => m.Author == Agent.Claude);
            var codex = chat.Messages.FirstOrDefault(m => m.Author == Agent.Codex);
            Assert.True(claude is not null && claude.Error is null, log);
            Assert.True(chat.Info.Sessions.ContainsKey(Agent.Claude), log);
            Assert.Equal(Agent.Codex, claude!.To);
            Assert.True(codex is not null && codex.Error is null, log);
            Assert.Contains("pong", codex!.Text, StringComparison.OrdinalIgnoreCase);
            Assert.True(chat.Info.Sessions.ContainsKey(Agent.Codex), log);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "amirlap-livechat-last.txt"), log);
        }
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
