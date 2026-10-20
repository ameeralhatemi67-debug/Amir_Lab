using NetVips;
using Toolbox.Core;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Imaging;
using Toolbox.Processing.Pdf;

namespace Toolbox.Processing.Studio;

/// <summary>
/// The original-quality pixels an editing session works from, decoded once into memory (so the
/// file is not held open while the user edits) and normalised to 8-bit sRGB. A downscaled proxy
/// drives the live preview; the full image is used for export.
/// </summary>
public sealed class StudioSource : IDisposable
{
    private readonly object _gate = new();
    private Image? _proxy;
    private int _proxyEdge;

    private StudioSource(ProbedFile file, Image full, int page, int pageCount, double dpi, IReadOnlyList<string> notes)
    {
        File = file;
        Full = full;
        Page = page;
        PageCount = pageCount;
        Dpi = dpi;
        Notes = notes;
    }

    public ProbedFile File { get; }
    public Image Full { get; }
    public int Page { get; }
    public int PageCount { get; }
    public double Dpi { get; }
    public IReadOnlyList<string> Notes { get; }
    public int Width => Full.Width;
    public int Height => Full.Height;
    public bool HasAlpha => Full.HasAlpha();

    public static StudioSource Open(string path, int page = 1, double dpi = 300)
    {
        VipsRuntime.EnsureInitialized();
        var file = FileProbe.Probe(path);
        if (!file.IsUsable) throw new ProcessingException(file.Problem ?? "The file cannot be opened.");

        if (file.Kind == FileKind.Pdf)
        {
            using var pdf = PdfDocument.Open(path);
            int p = Math.Clamp(page, 1, pdf.PageCount);
            var notes = new List<string>();
            var rendered = pdf.RenderPage(p, dpi, out var effective);
            if (effective < dpi - 0.5) notes.Add($"The page is very large; it was rendered at {effective:0} DPI.");
            notes.Add($"Page {p} of the PDF is edited as an image; the result is an image, not a PDF.");
            var memory = rendered.CopyMemory();
            rendered.Dispose();
            return new StudioSource(file, memory, p, pdf.PageCount, effective, notes);
        }

        if (!file.Kind.IsImage()) throw new ProcessingException($"{file.Kind.Label()} files cannot be edited.");
        using var loaded = ImageLoader.Load(file);
        var normal = Normalize(loaded.Image);
        var full = normal.CopyMemory();
        normal.Dispose();
        return new StudioSource(file, full, 1, 1, 0, loaded.Notes);
    }

    /// <summary>Downscaled copy (longest side ≤ <paramref name="maxEdge"/>), cached.</summary>
    public Image Proxy(int maxEdge, out double scale)
    {
        lock (_gate)
        {
            if (_proxy is null || _proxyEdge != maxEdge)
            {
                _proxy?.Dispose();
                if (Math.Max(Width, Height) <= maxEdge)
                {
                    _proxy = Full.Copy();
                }
                else
                {
                    using var thumb = Full.ThumbnailImage(maxEdge, height: maxEdge, size: Enums.Size.Down);
                    _proxy = thumb.CopyMemory();
                }
                _proxyEdge = maxEdge;
            }
            scale = (double)_proxy.Width / Width;
            return _proxy;
        }
    }

    /// <summary>8-bit sRGB with 3 bands, or 4 when the source has transparency. ICC profiles are converted to sRGB.</summary>
    internal static Image Normalize(Image image)
    {
        var current = image.Copy();
        void Swap(Image next) { current.Dispose(); current = next; }

        if (current.Contains("icc-profile-data"))
        {
            try
            {
                Swap(current.IccTransform("srgb", intent: Enums.Intent.Perceptual, embedded: true));
            }
            catch (VipsException)
            {
                // A broken profile: fall back to plain colourspace conversion below.
            }
        }
        if (current.Interpretation != Enums.Interpretation.Srgb || current.Format != Enums.BandFormat.Uchar)
            Swap(current.Colourspace(Enums.Interpretation.Srgb));
        if (current.Format != Enums.BandFormat.Uchar)
            Swap(current.Cast(Enums.BandFormat.Uchar));
        if (current.Bands == 2)
        {
            using var gray = current.ExtractBand(0);
            using var alpha = current.ExtractBand(1);
            Swap(gray.Bandjoin(gray, gray, alpha));
        }
        else if (current.Bands == 1)
        {
            Swap(current.Bandjoin(current, current));
        }
        else if (current.Bands > 4)
        {
            Swap(current.ExtractBand(0, n: 4));
        }
        var result = current.Copy(interpretation: Enums.Interpretation.Srgb);
        current.Dispose();
        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _proxy?.Dispose();
            _proxy = null;
        }
        Full.Dispose();
    }
}
