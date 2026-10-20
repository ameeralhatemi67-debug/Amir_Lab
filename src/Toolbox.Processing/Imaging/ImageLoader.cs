using NetVips;
using Toolbox.Core;
using Toolbox.Core.Inputs;

namespace Toolbox.Processing.Imaging;

/// <summary>A decoded image plus the resources it depends on and notes about what was changed.</summary>
public sealed class LoadedImage : IDisposable
{
    private readonly Source? _source;

    internal LoadedImage(Image image, Source? source, IReadOnlyList<string> notes)
    {
        Image = image;
        _source = source;
        Notes = notes;
    }

    public Image Image { get; private set; }
    public IReadOnlyList<string> Notes { get; }
    public int Width => Image.Width;
    public int Height => Image.Height;

    /// <summary>Replaces the image with a derived one (the previous instance is disposed).</summary>
    public void Replace(Image next)
    {
        if (!ReferenceEquals(next, Image)) Image.Dispose();
        Image = next;
    }

    public void Dispose()
    {
        Image.Dispose();
        _source?.Dispose();
    }
}

/// <summary>
/// Decodes images for processing: applies EXIF orientation so the result matches what viewers
/// show, takes the first frame of animated/multi-page files (and says so), rasterises SVG.
/// </summary>
public static class ImageLoader
{
    /// <summary>
    /// Optional platform decoder for formats libvips cannot read here (HEIC through Windows codecs).
    /// Returns tightly packed 8-bit RGBA pixels, or throws a <see cref="ProcessingException"/>.
    /// </summary>
    public static Func<string, (byte[] Rgba, int Width, int Height)>? PlatformHeicDecoder { get; set; }

    public static LoadedImage Load(ProbedFile file, bool autorotate = true, double svgDpi = 144)
    {
        VipsRuntime.EnsureInitialized();
        if (file.Kind == FileKind.Heic) return LoadHeic(file);

        Source? source = null;
        Image? image = null;
        var notes = new List<string>();
        try
        {
            // Load through a Source: libvips treats a trailing "[...]" in file names as load options.
            source = Source.NewFromFile(file.Path);
            var kwargs = file.Kind == FileKind.Svg ? new VOption { { "dpi", svgDpi } } : null;
            image = Image.NewFromSource(source, "", Enums.Access.Random, Enums.FailOn.Error, kwargs);

            if (image.Contains("n-pages") && image.Get("n-pages") is int pages && pages > 1)
                notes.Add(file.Kind == FileKind.Gif || file.Kind == FileKind.WebP
                    ? "Animated image: only the first frame was used."
                    : $"Multi-page image: only the first of {pages} pages was used.");

            if (autorotate && image.Contains("orientation") && image.Get("orientation") is int orientation && orientation > 1)
            {
                var rotated = image.Autorot();
                image.Dispose();
                image = rotated;
            }
            return new LoadedImage(image, source, notes);
        }
        catch (VipsException ex)
        {
            image?.Dispose();
            source?.Dispose();
            throw new ProcessingException($"The image could not be read ({FirstLine(ex.Message)}). It may be damaged or use an unsupported variant.", ex);
        }
    }

    private static LoadedImage LoadHeic(ProbedFile file)
    {
        var decoder = PlatformHeicDecoder
            ?? throw new ProcessingException("HEIC images cannot be decoded on this computer.");
        var (rgba, width, height) = decoder(file.Path);
        var image = Image.NewFromMemoryCopy<byte>(rgba, width, height, 4, Enums.BandFormat.Uchar)
            .Copy(interpretation: Enums.Interpretation.Srgb);
        return new LoadedImage(image, null, ["HEIC decoded with the Windows HEIF codec; metadata is not carried over."]);
    }

    internal static string FirstLine(string message)
    {
        var line = message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? message;
        return line.Trim().TrimEnd(':');
    }
}
