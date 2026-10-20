using Microsoft.Win32;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Windows.Shell;

/// <summary>
/// Optional, per-user Windows integration (no administrator rights). Everything is under HKCU or
/// the user's own folders and is removed again when switched off (or by uninstall.ps1).
/// </summary>
internal static class ShellIntegration
{
    public const string VerbKey = "FileToolbox";

    /// <summary>File types that get the "File Toolbox…" entry in Explorer's classic context menu.</summary>
    public static readonly string[] Extensions =
        [".pdf", ".jpg", ".jpeg", ".png", ".webp", ".avif", ".heic", ".heif", ".tif", ".tiff", ".gif", ".bmp", ".svg"];

    public static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");

    /// <param name="classesRoot">Registry path under HKCU (overridable for tests).</param>
    public static void SetContextMenu(bool enabled, string classesRoot = @"Software\Classes")
    {
        foreach (var ext in Extensions)
        {
            var path = $@"{classesRoot}\SystemFileAssociations\{ext}\shell\{VerbKey}";
            if (enabled)
            {
                using var verb = Registry.CurrentUser.CreateSubKey(path);
                verb.SetValue("", "File Toolbox…");
                verb.SetValue("MUIVerb", "File Toolbox…");
                verb.SetValue("Icon", $"\"{ExePath}\",0");
                verb.SetValue("MultiSelectModel", "Player");
                using var command = verb.CreateSubKey("command");
                command.SetValue("", $"\"{ExePath}\" --open \"%1\"");
            }
            else
            {
                Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            }
        }
        Log.Info($"Explorer context menu {(enabled ? "registered" : "removed")}.");
    }

    public static bool IsContextMenuRegistered(string classesRoot = @"Software\Classes")
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{classesRoot}\SystemFileAssociations\.pdf\shell\{VerbKey}\command");
        return key?.GetValue("") is string command && command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    public static string SendToShortcutPath =>
        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "File Toolbox.lnk");

    public static void SetSendTo(bool enabled)
    {
        var link = SendToShortcutPath;
        if (!enabled)
        {
            if (File.Exists(link)) File.Delete(link);
            return;
        }
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows Script Host is not available.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = ExePath;
            shortcut.Arguments = "--open";
            shortcut.IconLocation = ExePath + ",0";
            shortcut.Description = "Open the files in File Toolbox";
            shortcut.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }

    public static bool IsSendToRegistered => File.Exists(SendToShortcutPath);

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(VerbKey, $"\"{ExePath}\" --background");
        else key.DeleteValue(VerbKey, throwOnMissingValue: false);
    }

    public static bool IsStartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(VerbKey) is string value && value.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }
}
