using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Toolbox.Core.Pulse.Chat;

/// <summary>How to start one agent turn: the program, its arguments, where, and what it reads.</summary>
public sealed record AgentLaunch(string Agent, string Exe, IReadOnlyList<string> Args, string WorkingDir, string Input)
{
    /// <summary>Extra environment for the program (the chat it runs in, so it can't open chats of its own).</summary>
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
}

/// <summary>The environment variable an agent Pulse runs carries: the chat it is in.</summary>
public static class ChatEnvironment
{
    /// <summary>Set for every agent turn Pulse starts. Amir Lap's command line refuses to open or direct chats when it sees it.</summary>
    public const string InsideChat = "AMIRLAP_CHAT";
}

/// <summary>
/// Finds Claude Code and Codex on this PC. Pulse only starts the programs the user installed and
/// signed in to; it never reads or passes on their sign-in.
/// </summary>
public static partial class AgentLocator
{
    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string RoamingData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    [GeneratedRegex(@"""%dp0%\\([^""]+\.exe)""", RegexOptions.IgnoreCase)]
    private static partial Regex NpmShimTarget();

    /// <summary>Claude Code's program: the chosen one, then claude.exe on the PATH, then npm's and the installer's copies.</summary>
    public static string? FindClaude(string? chosen = null) => First(
        chosen,
        () => OnPath("claude.exe"),
        () => ShimTarget(OnPath("claude.cmd")),
        () => Path.Combine(RoamingData, "npm", "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe"),
        () => Path.Combine(UserProfile, ".local", "bin", "claude.exe"));

    /// <summary>
    /// Codex's program: the chosen one, then codex.exe on the PATH, then npm's copy, then the
    /// Codex app's own engine (current, with its helpers beside it), then the older copy the app
    /// leaves in <c>~/.codex/.sandbox-bin</c>.
    /// </summary>
    public static string? FindCodex(string? chosen = null) => First(
        chosen,
        () => OnPath("codex.exe"),
        () => ShimTarget(OnPath("codex.cmd")),
        () => Path.Combine(RoamingData, "npm", "node_modules", "@openai", "codex", "vendor", "x86_64-pc-windows-msvc", "codex", "codex.exe"),
        CodexAppEngine,
        () => Path.Combine(CodexUsageReader.DefaultHome, ".sandbox-bin", "codex.exe"));

    /// <summary>
    /// The engine inside the Codex desktop app (<c>…\WindowsApps\OpenAI.Codex_…\app\resources\codex.exe</c>).
    /// The WindowsApps folder can't be listed, so its place comes from Windows' own record of
    /// installed app packages for this user (the newest Codex package wins).
    /// </summary>
    internal static string? CodexAppEngine()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var packages = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (packages is null) return null;
            var found = new List<(System.Version Version, string Exe)>();
            foreach (var name in packages.GetSubKeyNames().Where(n => n.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)))
            {
                using var key = packages.OpenSubKey(name);
                if (key?.GetValue("PackageRootFolder") is not string root) continue;
                var exe = Path.Combine(root, "app", "resources", "codex.exe");
                if (!File.Exists(exe)) continue;
                var parts = name.Split('_');
                found.Add((parts.Length > 1 && System.Version.TryParse(parts[1], out var v) ? v : new System.Version(0, 0), exe));
            }
            return found.OrderByDescending(f => f.Version).Select(f => f.Exe).FirstOrDefault();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static string? Find(string agent, string? chosen = null) => agent == Agent.Codex ? FindCodex(chosen) : FindClaude(chosen);

    /// <summary>The program's own version line ("2.1.321 (Claude Code)", "codex-cli 0.160.1"), or null.</summary>
    public static string? Version(string exe, int timeoutMs = 8000)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return null;
            var read = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            var line = read.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return string.IsNullOrEmpty(line) ? null : line;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>Just the number from a version line ("2.1.321", "0.160.1").</summary>
    public static string? VersionNumber(string? line) =>
        line is null ? null : Regex.Match(line, @"\d+\.\d+(\.\d+)?") is { Success: true } m ? m.Value : null;

    private static string? First(string? chosen, params Func<string?>[] candidates)
    {
        if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen)) return chosen;
        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate() is { Length: > 0 } path && File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        return null;
    }

    private static string? OnPath(string file)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var path = Path.Combine(dir.Trim('"'), file);
                if (File.Exists(path)) return path;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>The program an npm shim (claude.cmd) starts: <c>"%dp0%\node_modules\...\claude.exe"</c>.</summary>
    internal static string? ShimTarget(string? shim)
    {
        if (shim is null || !File.Exists(shim)) return null;
        var m = NpmShimTarget().Match(File.ReadAllText(shim));
        return m.Success ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(shim)!, m.Groups[1].Value)) : null;
    }
}

/// <summary>
/// The command lines for one turn. Claude Code: a print-mode run that writes stream-json, resumes
/// the chat's session (or continues an earlier one as a copy), and gets the room's rules as an
/// addition to its system prompt; anything that would need approval is refused and reported, and
/// Pulse asks the user. Codex: <c>codex exec --json</c>, resumed the same way, in a sandbox that
/// only reads or that may write inside the working folder.
/// </summary>
public static class AgentCommands
{
    public static AgentLaunch Claude(string exe, ChatInfo chat, string workDir, string prompt, IEnumerable<string>? allowOnce = null)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--permission-prompts", "none", "--permission-mode", chat.Permission == ChatPermission.Edit ? "acceptEdits" : "manual" };
        if (chat.Sessions.TryGetValue(Agent.Claude, out var session) && session.Length > 0)
        {
            args.Add("--resume");
            args.Add(session);
        }
        else if (chat.ForkFrom.TryGetValue(Agent.Claude, out var from) && from.Length > 0)
        {
            args.Add("--resume");
            args.Add(from);
            args.Add("--fork-session");
        }
        if (chat.ModelOf(Agent.Claude) is { Length: > 0 } model)
        {
            args.Add("--model");
            args.Add(model);
        }
        // The director's notes live in Pulse's own folder: let Claude read them without asking.
        if (NotesFolder(chat, workDir) is { } notes)
        {
            args.Add("--add-dir");
            args.Add(notes);
        }
        args.Add("--append-system-prompt");
        args.Add(ChatRules.Preamble(chat, Agent.Claude));
        var denied = DeniedRules(chat, Agent.Claude);
        if (denied.Count > 0)
        {
            args.Add("--disallowedTools");
            args.AddRange(denied);
        }
        var allowed = chat.AllowedTools.Concat(allowOnce ?? []).Where(r => r.Length > 0).Distinct().ToList();
        if (allowed.Count > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(allowed);
        }
        return new AgentLaunch(Agent.Claude, exe, args, workDir, prompt) { Env = EnvFor(chat) };
    }

    private static Dictionary<string, string> EnvFor(ChatInfo chat) => new() { [ChatEnvironment.InsideChat] = chat.Id };

    internal static bool HasCodeModeHost(string exe)
    {
        try { return Path.GetDirectoryName(Path.GetFullPath(exe)) is { } dir && File.Exists(Path.Combine(dir, "codex-code-mode-host.exe")); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return false; }
    }

    /// <summary>The folder holding the director's notes, when they are outside the working folder.</summary>
    internal static string? NotesFolder(ChatInfo chat, string workDir)
    {
        if (chat.DirectorNotes is not { Length: > 0 } notes || Path.GetDirectoryName(notes) is not { Length: > 0 } dir) return null;
        try
        {
            var rel = Path.GetRelativePath(workDir, dir);
            return rel == "." || (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel)) ? null : dir;
        }
        catch (ArgumentException) { return dir; }
    }

    public static AgentLaunch Codex(string exe, ChatInfo chat, string workDir, string prompt)
    {
        var args = new List<string> { "exec" };
        bool first = true;
        if (chat.Sessions.TryGetValue(Agent.Codex, out var session) && session.Length > 0)
        {
            args.Add("resume");
            args.Add(session);
            first = false;
        }
        else if (chat.ForkFrom.TryGetValue(Agent.Codex, out var from) && from.Length > 0)
        {
            args.Add("fork");
            args.Add(from);
        }
        args.AddRange(["--json", "--skip-git-repo-check", "-c", $"sandbox_mode=\"{CodexSandbox(chat)}\"", "-c", "approval_policy=\"never\""]);
        // The Codex app turns on "code mode", whose host program sits beside the app's own engine.
        // An engine without it beside it (the older copy in .sandbox-bin) can't run a single
        // command that way, so it runs them the plain way instead.
        if (!HasCodeModeHost(exe)) args.AddRange(["-c", "features.code_mode_host=false"]);
        if (chat.ModelOf(Agent.Codex) is { Length: > 0 } model) args.AddRange(["-c", $"model=\"{model.Replace("\"", "", StringComparison.Ordinal)}\""]);
        args.Add("-");
        var input = first ? ChatRules.Preamble(chat, Agent.Codex) + "\n\n---\n\n" + prompt : prompt;
        return new AgentLaunch(Agent.Codex, exe, args, workDir, input) { Env = EnvFor(chat) };
    }

    /// <summary>
    /// Codex can't stop mid-turn to ask, so a chat that asks first lets it read only; a chat that
    /// edits lets it write inside its working folder.
    /// </summary>
    public static string CodexSandbox(ChatInfo chat) => chat.Permission == ChatPermission.Edit ? "workspace-write" : "read-only";

    /// <summary>What Claude may never do in this chat: edits in a read-only chat, and the other agent's folders.</summary>
    public static List<string> DeniedRules(ChatInfo chat, string agent)
    {
        var rules = new List<string>();
        if (chat.Permission == ChatPermission.Read) rules.AddRange(["Edit", "Write", "MultiEdit", "NotebookEdit"]);
        else if (chat.IsGroup)
        {
            foreach (var owned in ChatRules.OwnsOf(chat, Agent.Other(agent)))
            {
                var p = owned.Trim().Replace('\\', '/');
                if (p.Length == 0 || p.Contains(' ')) continue;
                var pattern = p.EndsWith('/') ? p + "**" : p;
                rules.Add($"Edit({pattern})");
                rules.Add($"Write({pattern})");
            }
        }
        return rules;
    }

    public static AgentLaunch For(string agent, string exe, ChatInfo chat, string workDir, string prompt, IEnumerable<string>? allowOnce = null) =>
        agent == Agent.Codex ? Codex(exe, chat, workDir, prompt) : Claude(exe, chat, workDir, prompt, allowOnce);
}
