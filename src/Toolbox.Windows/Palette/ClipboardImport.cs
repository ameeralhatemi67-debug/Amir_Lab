using System.Windows;
using System.Windows.Media.Imaging;
using Toolbox.Core;

namespace Toolbox.Windows.CommandPalette;

/// <summary>
/// Reads the clipboard only when the user explicitly pastes: a file list, or an image which is
/// saved as a PNG in Pictures\File Toolbox so it has a real, visible location for its results.
/// </summary>
internal static class ClipboardImport
{
    public static IReadOnlyList<string> Read(out string? note)
    {
        note = null;
        try
        {
            if (Clipboard.ContainsFileDropList())
                return Clipboard.GetFileDropList().Cast<string>().Where(p => File.Exists(p)).ToList();

            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource image)
            {
                var folder = AppPaths.DefaultFallbackOutputDir;
                Directory.CreateDirectory(folder);
                var path = IOPath.Combine(folder, $"Clipboard {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path)) encoder.Save(stream);
                note = $"Pasted image saved as {IOPath.GetFileName(path)} in {folder}.";
                return [path];
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            note = "The clipboard is busy; try again.";
        }
        return [];
    }
}
