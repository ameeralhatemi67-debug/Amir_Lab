using System.Drawing;
using System.Drawing.Imaging;

namespace DragHarness;

/// <summary>Captures the primary screen to the run folder, for diagnosing failed scenarios and visual QA.</summary>
internal static class Snapshots
{
    public static string? Folder { get; set; }

    public static string? Capture(string name)
    {
        if (Folder is null) return null;
        try
        {
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var g = Graphics.FromImage(bitmap))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            Directory.CreateDirectory(Folder);
            // ":" in a name would create an NTFS alternate data stream instead of a file.
            var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = System.IO.Path.Combine(Folder, $"{DateTime.Now:HHmmss-fff} {safe}.png");
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
