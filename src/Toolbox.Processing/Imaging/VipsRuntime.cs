using NetVips;
using Toolbox.Core.Actions;
using Log = Toolbox.Core.Diagnostics.Log;

namespace Toolbox.Processing.Imaging;

/// <summary>One-time libvips configuration for a desktop app that must not hold files open.</summary>
internal static class VipsRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;

            // The operation cache would keep source and temp files open (blocking renames on Windows)
            // and hold memory between jobs; this app re-reads files deliberately.
            Cache.Max = 0;
            Cache.MaxFiles = 0;
            Cache.MaxMem = 0;

            // Refuse loaders libvips marks as unsafe for untrusted input.
            NetVips.NetVips.BlockUntrusted = true;

            RuntimeFeatures.Set(RuntimeFeatures.AvifEncode, ProbeAvifEncode(out var detail), detail);
            _initialized = true;
            Log.Info($"libvips {NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)} ready; AVIF encode: {detail}");
        }
    }

    private static bool ProbeAvifEncode(out string detail)
    {
        try
        {
            using var probe = Image.Black(16, 16, bands: 3);
            var bytes = probe.HeifsaveBuffer(q: 50, compression: Enums.ForeignHeifCompression.Av1, effort: 0);
            detail = bytes.Length > 0 ? "available (libheif + aom)" : "encoder returned no data";
            return bytes.Length > 0;
        }
        catch (Exception ex)
        {
            detail = $"not available ({ex.Message.Split('\n')[0]})";
            return false;
        }
    }
}
