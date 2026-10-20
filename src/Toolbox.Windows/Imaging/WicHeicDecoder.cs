using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Toolbox.Core;
using Toolbox.Core.Actions;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Windows.Imaging;

/// <summary>
/// Decodes HEIC through the Windows Imaging Component using Microsoft's HEIF and HEVC extensions,
/// which the user installs from the Microsoft Store. The app ships no HEVC code of its own.
/// </summary>
internal static class WicHeicDecoder
{
    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>Detects the codec packages and registers the decoder with the processing layer.</summary>
    public static void Register()
    {
        bool heif = false, hevc = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PackagesKey);
            foreach (var name in key?.GetSubKeyNames() ?? [])
            {
                if (name.StartsWith("Microsoft.HEIFImageExtension_", StringComparison.OrdinalIgnoreCase)) heif = true;
                if (name.StartsWith("Microsoft.HEVCVideoExtension", StringComparison.OrdinalIgnoreCase)) hevc = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check codec packages: {ex.Message}");
        }

        bool available = heif && hevc;
        RuntimeFeatures.Set(RuntimeFeatures.HeicDecode, available, available
            ? "Uses the Windows HEIF + HEVC extensions installed on this computer."
            : "Needs Microsoft's free \"HEIF Image Extensions\" and \"HEVC Video Extensions\" from the Microsoft Store.");
        Processing.Imaging.ImageLoader.PlatformHeicDecoder = Decode;
        Log.Info($"HEIC decoding: {(available ? "available" : $"unavailable (HEIF {heif}, HEVC {hevc})")}.");
    }

    /// <summary>Returns tightly packed RGBA pixels of the primary image (HEIF rotation/mirroring is applied by the codec).</summary>
    public static (byte[] Rgba, int Width, int Height) Decode(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames[0];
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = width * 4;
            var pixels = new byte[stride * height];
            bgra.CopyPixels(pixels, stride, 0);
            for (int i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            return (pixels, width, height);
        }
        catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException or FileFormatException)
        {
            RuntimeFeatures.Set(RuntimeFeatures.HeicDecode, false,
                "Windows could not decode HEIC here; install \"HEIF Image Extensions\" and \"HEVC Video Extensions\" from the Microsoft Store.");
            throw new ProcessingException("Windows could not decode this HEIC image. Install Microsoft's \"HEIF Image Extensions\" and \"HEVC Video Extensions\", then try again.", ex);
        }
    }
}
