using System.Diagnostics;
using System.Reflection;
using System.Text;
using Toolbox.Core.Pulse;
using Toolbox.Core.Pulse.Chat;

namespace Toolbox.Cli;

/// <summary>
/// <c>ftb chat setup</c>: installs the pulse-group-chat skill for Claude Code
/// (<c>~/.claude/skills</c>) and Codex (<c>~/.codex/skills</c>), with this ftb.exe's full path
/// written in, and with --mcp also adds <c>ftb mcp</c> as an MCP server named "pulse" to both
/// (through their own <c>mcp add</c> commands). --print shows what it would do.
/// </summary>
internal static class SkillSetup
{
    public const string SkillName = "pulse-group-chat";

    public static int Run(IReadOnlyDictionary<string, string> options)
    {
        bool print = options.ContainsKey("print");
        bool onlyClaude = options.ContainsKey("claude") && !options.ContainsKey("codex");
        bool onlyCodex = options.ContainsKey("codex") && !options.ContainsKey("claude");
        var ftb = Environment.ProcessPath ?? "ftb.exe";
        var skill = Skill(ftb);
        var targets = new List<(string Agent, string Dir)>();
        if (!onlyCodex) targets.Add((Agent.Claude, Path.Combine(AgentSessions.ClaudeHome, "skills", SkillName)));
        if (!onlyClaude) targets.Add((Agent.Codex, Path.Combine(CodexUsageReader.DefaultHome, "skills", SkillName)));
        foreach (var (agent, dir) in targets)
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (print)
            {
                Console.WriteLine($"Would write {file}");
                continue;
            }
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, skill, new UTF8Encoding(false));
            Console.WriteLine($"{Agent.Name(agent)}: skill written to {file}");
        }
        var mcp = new List<(string Agent, string? Exe, string[] Args)>();
        if (!onlyCodex) mcp.Add((Agent.Claude, AgentLocator.FindClaude(), ["mcp", "add", "--scope", "user", "pulse", "--", ftb, "mcp"]));
        if (!onlyClaude) mcp.Add((Agent.Codex, AgentLocator.FindCodex(), ["mcp", "add", "pulse", "--", ftb, "mcp"]));
        if (!options.ContainsKey("mcp"))
        {
            Console.WriteLine();
            Console.WriteLine("To also give them the MCP tools (pulse_open, pulse_say, pulse_wait…), run 'ftb chat setup --mcp', or:");
            foreach (var (agent, exe, args) in mcp)
                Console.WriteLine($"  {(agent == Agent.Codex ? "codex" : "claude")} {string.Join(' ', args.Select(Quote))}");
            return 0;
        }
        int failed = 0;
        foreach (var (agent, exe, args) in mcp)
        {
            if (exe is null)
            {
                Console.WriteLine($"{Agent.Name(agent)} isn't on this PC: skipped its MCP server.");
                continue;
            }
            if (print)
            {
                Console.WriteLine($"Would run: \"{exe}\" {string.Join(' ', args.Select(Quote))}");
                continue;
            }
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
            p.WaitForExit(30_000);
            bool ok = p.HasExited && p.ExitCode == 0;
            if (!ok) failed++;
            Console.WriteLine($"{Agent.Name(agent)}: MCP server 'pulse' {(ok ? "added" : "not added")}{(output.Length > 0 ? " · " + output.Split('\n')[0].Trim() : "")}");
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>The skill's text, with this ftb.exe's path in its commands.</summary>
    public static string Skill(string ftbPath)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SKILL.md")
            ?? throw new InvalidOperationException("The skill is missing from ftb.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("{{FTB}}", ftbPath, StringComparison.Ordinal);
    }

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}
