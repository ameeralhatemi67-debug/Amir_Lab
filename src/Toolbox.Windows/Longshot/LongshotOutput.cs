using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Toolbox.Windows.Longshot;

/// <summary>Saving and copying finished captures.</summary>
internal static class LongshotOutput
{
    /// <summary>Pictures\Screenshots: where Windows' own Snipping Tool saves too.</summary>
    public static string DefaultFolder =>
        IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public static BitmapSource ToBitmap(int width, int height, byte[] pixels)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A free name like "Longshot 2026-11-08 143012.png" (a number is added if it exists).</summary>
    public static string NewPath(string folder, string format, DateTime when)
    {
        string ext = format == "jpg" ? ".jpg" : ".png";
        string stem = $"Longshot {when:yyyy-MM-dd HHmmss}";
        string path = IOPath.Combine(folder, stem + ext);
        for (int i = 2; File.Exists(path); i++) path = IOPath.Combine(folder, $"{stem} ({i}){ext}");
        return path;
    }

    /// <summary>Encodes to a temporary file next to the target, then renames it, so a half-written file never appears.</summary>
    public static void Save(BitmapSource bitmap, string path)
    {
        Directory.CreateDirectory(IOPath.GetDirectoryName(path)!);
        BitmapEncoder encoder = IOPath.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg"
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var temp = path + ".ftb-tmp";
        try
        {
            using (var file = File.Create(temp)) encoder.Save(file);
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>Runs <paramref name="work"/> on a fresh STA thread (WPF image encoders want one), off the UI thread.</summary>
    public static Task<T> OnStaThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(work()); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "Longshot save" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>Puts the image on the clipboard (call on the UI thread). Retries briefly if another app holds it.</summary>
    public static bool CopyToClipboard(BitmapSource bitmap)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetImage(bitmap);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(60);
            }
        }
        return false;
    }
}
