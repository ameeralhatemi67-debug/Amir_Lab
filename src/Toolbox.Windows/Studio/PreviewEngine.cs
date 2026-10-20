using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Inputs;
using Toolbox.Processing.Imaging;
using Toolbox.Processing.Studio;

namespace Toolbox.Windows.Studio;

/// <summary>A rendered preview frame and the full-resolution size it represents.</summary>
internal sealed record PreviewFrame(WriteableBitmap Bitmap, double LogicalWidth, double LogicalHeight, RenderStage Stage, EditDocument Document);

/// <summary>
/// Renders previews off the UI thread with "latest request wins": while one frame renders, newer
/// requests replace each other, so dragging a slider never queues up stale work.
/// </summary>
internal sealed class PreviewEngine
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<StudioSource> _source;
    private readonly object _gate = new();
    private (EditDocument Doc, RenderStage Stage)? _pending;
    private bool _running;
    private WriteableBitmap? _reuse;

    public PreviewEngine(Dispatcher dispatcher, Func<StudioSource> source)
    {
        _dispatcher = dispatcher;
        _source = source;
    }

    public event Action<PreviewFrame>? Rendered;
    public event Action<string>? Failed;

    public void Request(EditDocument doc, RenderStage stage)
    {
        lock (_gate)
        {
            _pending = (doc, stage);
            if (_running) return;
            _running = true;
        }
        _ = Task.Run(Loop);
    }

    private void Loop()
    {
        while (true)
        {
            (EditDocument Doc, RenderStage Stage) work;
            lock (_gate)
            {
                if (_pending is null)
                {
                    _running = false;
                    return;
                }
                work = _pending.Value;
                _pending = null;
            }

            try
            {
                var (bgra, width, height, scale) = StudioRenderer.RenderPreview(work.Doc, _source(), work.Stage);
                _dispatcher.Invoke(() =>
                {
                    var bitmap = _reuse is { } r && r.PixelWidth == width && r.PixelHeight == height
                        ? r
                        : new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
                    bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
                    _reuse = bitmap;
                    Rendered?.Invoke(new PreviewFrame(bitmap, width / scale, height / scale, work.Stage, work.Doc));
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"Preview render failed: {ex.Message}");
                _dispatcher.BeginInvoke(() => Failed?.Invoke(ex.Message));
            }
        }
    }

    /// <summary>Approximate output size: encodes the preview-size render and scales by pixel count.</summary>
    public static Task<long?> EstimateBytesAsync(EditDocument doc, StudioSource source, FileKind format) => Task.Run<long?>(() =>
    {
        try
        {
            using var image = StudioRenderer.Render(doc, source, RenderStage.Final, 900, out var scale);
            var (fw, fh) = StudioRenderer.FinalSize(doc, source);
            var bytes = ImageEncoder.SaveToBuffer(image, format, new EncodeSettings { Quality = doc.Export.Quality, KeepMetadata = false, Effort = 2 });
            double ratio = (double)fw * fh / Math.Max(1, (double)image.Width * image.Height);
            // Compressed size grows slower than pixel count for smooth content; this is only a guide.
            return (long)(bytes.Length * Math.Pow(ratio, format == FileKind.Png ? 0.9 : 0.85));
        }
        catch
        {
            return null;
        }
    });
}
