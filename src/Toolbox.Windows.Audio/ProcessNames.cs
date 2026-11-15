using System.Collections.Concurrent;
using System.Diagnostics;
using Toolbox.Core.Sound;

namespace Toolbox.Windows.Audio;

/// <summary>
/// The program behind an audio session and a name for it ("Spotify", "Microsoft Edge"). The path
/// comes from the process when Windows lets us ask (limited query rights only), else from the
/// session's own identifier ("\Device\HarddiskVolume3\..."), turned into a drive path.
/// </summary>
public static class ProcessNames
{
    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string>? _volumes;

    internal static (string Name, string? Path) Resolve(int processId, string? devicePath)
    {
        var path = ImagePath(processId) ?? DosPath(devicePath);
        if (path is null) return (SoundApps.NameFrom(null, null, devicePath), devicePath);
        return (Names.GetOrAdd(path, NameOf), path);
    }

    /// <summary>The program file of a running process, or null when Windows won't say.</summary>
    public static unsafe string? ImagePath(int processId)
    {
        if (processId <= 0) return null;
        var handle = AudioNative.OpenProcess(AudioNative.ProcessQueryLimitedInformation, 0, (uint)processId);
        if (handle == 0) return null;
        try
        {
            var buffer = stackalloc char[1024];
            uint size = 1024;
            return AudioNative.QueryFullProcessImageName(handle, 0, buffer, ref size) != 0 ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            AudioNative.CloseHandle(handle);
        }
    }

    /// <summary>"\Device\HarddiskVolume3\Program Files\x.exe" → "C:\Program Files\x.exe".</summary>
    internal static string? DosPath(string? devicePath)
    {
        if (string.IsNullOrEmpty(devicePath)) return null;
        if (devicePath.Length > 2 && devicePath[1] == ':') return devicePath;
        foreach (var (device, drive) in Volumes())
        {
            if (devicePath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase))
                return drive + devicePath[device.Length..];
        }
        return null;
    }

    private static unsafe Dictionary<string, string> Volumes()
    {
        if (_volumes is { } known) return known;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buffer = stackalloc char[512];
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var letter = drive.TrimEnd('\\');
            uint n = AudioNative.QueryDosDevice(letter, buffer, 512);
            if (n > 0)
            {
                var target = new string(buffer).TrimEnd('\0');
                if (target.Length > 0) map.TryAdd(target, letter);
            }
        }
        return _volumes = map;
    }

    private static string NameOf(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return SoundApps.NameFrom(info.FileDescription, info.ProductName, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return SoundApps.NameFrom(null, null, path);
        }
    }
}
