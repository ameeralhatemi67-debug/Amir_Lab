using System.Text.Json;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>
/// Pulse's chats on this PC: one JSON file per chat in Amir Lap's data folder. A quick chat (no
/// project folder) gets a folder of its own next to its file, where its agents run.
/// </summary>
public sealed class ChatStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ChatStore(string dir)
    {
        Dir = dir;
    }

    public string Dir { get; }

    public static string DefaultDir => Path.Combine(ClaudeCodeConnection.DefaultDataDir, "chats");

    public string FileOf(string id) => Path.Combine(Dir, id + ".json");

    /// <summary>Where a quick chat's agents run (created when first asked for).</summary>
    public string QuickFolder(string id)
    {
        var dir = Path.Combine(Dir, id + "-files");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The folder a chat's agents work in.</summary>
    public string WorkFolder(ChatInfo chat) => chat.Folder ?? QuickFolder(chat.Id);

    /// <summary>Every saved chat, newest first. Files that can't be read are skipped (and logged).</summary>
    public List<ChatFile> LoadAll()
    {
        var list = new List<ChatFile>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var file in Directory.EnumerateFiles(Dir, "*.json"))
        {
            if (Load(file) is { } chat) list.Add(chat);
        }
        return list.OrderByDescending(c => c.Info.Updated).ToList();
    }

    public static ChatFile? Load(string file)
    {
        try
        {
            var chat = JsonSerializer.Deserialize<ChatFile>(File.ReadAllText(file), Options);
            if (chat?.Info is null || string.IsNullOrEmpty(chat.Info.Id)) return null;
            chat.Messages ??= [];
            // A turn that was running when Amir Lap closed is over.
            foreach (var m in chat.Messages.Where(m => m.Working)) m.Working = false;
            return chat;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Pulse could not read the chat {Path.GetFileName(file)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes a chat (to a temporary file first, so a crash never leaves half a chat).</summary>
    public void Save(ChatFile chat)
    {
        Directory.CreateDirectory(Dir);
        var path = FileOf(chat.Info.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(chat, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Removes a chat's file and its quick-chat folder. The agents' own sessions stay where they are.</summary>
    public void Delete(string id)
    {
        try
        {
            File.Delete(FileOf(id));
            var quick = Path.Combine(Dir, id + "-files");
            if (Directory.Exists(quick)) Directory.Delete(quick, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Pulse could not delete the chat {id}: {ex.Message}");
        }
    }
}
