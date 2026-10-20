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
            var path = System.IO.Path.Combine(Folder, $"{DateTime.Now:HHmmss-fff} {name}.png");
            bitmap.Save(path, ImageFormat.Png);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
