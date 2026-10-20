using NetVips;
using Toolbox.Core.Actions;
using Toolbox.Core.Inputs;
using Toolbox.Core.Jobs;
using Toolbox.Processing.Imaging;
using Toolbox.Processing.Studio;
using Ids = Toolbox.Core.Actions.CapabilityMap.Ids;

namespace Toolbox.Processing.Tests;

public sealed class StudioTests : IDisposable
{
    private readonly Workspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private static EditDocument Doc(string path) => new() { SourcePath = path };

    [Fact]
    public void Crop_keeps_exactly_the_selected_pixels()
    {
        var src = _ws.Png("grad.png", 400, 200);
        using var source = StudioSource.Open(src);
        var doc = Doc(src) with { Crop = new CropSettings { Rect = new PixelRect(40, 20, 100, 50) } };

        using var result = StudioRenderer.Render(doc, source, RenderStage.Final, null, out _);
        Assert.Equal((100, 50), (result.Width, result.Height));
        var expected = source.Full.Getpoint(40, 20);
        var actual = result.Getpoint(0, 0);
        for (int b = 0; b < 3; b++) Assert.InRange(actual[b], expected[b] - 1, expected[b] + 1);
    }

    [Fact]
    public void Rotation_swaps_dimensions_and_crop_uses_rotated_coordinates()
    {
        var src = _ws.Png("wide.png", 300, 100);
        using var source = StudioSource.Open(src);
        var rotated = Doc(src) with { Crop = new CropSettings { Rotation = 90 } };
        Assert.Equal((100, 300), StudioRenderer.FinalSize(rotated, source));

        var cropped = rotated with { Crop = rotated.Crop with { Rect = new PixelRect(0, 0, 100, 150) } };
        using var result = StudioRenderer.Render(cropped, source, RenderStage.Final, null, out _);
        Assert.Equal((100, 150), (result.Width, result.Height));
    }

    [Theory]
    [InlineData("auto", 600, 400, 840, 640)]
    [InlineData("1:1", 600, 400, 840, 840)]
    [InlineData("16:9", 600, 400, 1138, 640)]
    [InlineData("4:5", 600, 400, 840, 1050)]
    public void Background_canvas_size_follows_padding_and_ratio(string ratio, int w, int h, int expectedW, int expectedH)
    {
        var bg = new BackgroundSettings { Enabled = true, Padding = 120, Ratio = ratio };
        Assert.Equal((expectedW, expectedH), StudioRenderer.CanvasSize(w, h, bg, 1));
    }

    [Fact]
    public void Background_rounds_corners_over_the_fill_and_is_opaque()
    {
        var src = _ws.Png("shot.png", 400, 300);
        using var source = StudioSource.Open(src);
        var doc = Doc(src) with { Background = new BackgroundSettings { Enabled = true, Fill = "#FF0000", Padding = 50, Corner = 60, Shadow = 0 } };

        using var result = StudioRenderer.Render(doc, source, RenderStage.Final, null, out _);
        Assert.Equal((500, 400), (result.Width, result.Height));
        Assert.Equal(3, result.Bands); // opaque result
        // Exactly at the content's corner the rounded clip shows the red background.
        var corner = result.Getpoint(51, 51);
        Assert.True(corner[0] > 240 && corner[1] < 20 && corner[2] < 20, $"corner was {string.Join(",", corner)}");
        // The middle is image content.
        var middle = result.Getpoint(250, 200);
        Assert.False(middle[0] > 240 && middle[1] < 20 && middle[2] < 20);
    }

    [Fact]
    public void Preview_is_a_faithful_miniature_of_the_export()
    {
        var src = _ws.Png("big.png", 3200, 2000);
        using var source = StudioSource.Open(src);
        var doc = Doc(src) with
        {
            Crop = new CropSettings { Rect = new PixelRect(400, 200, 2000, 1500) },
            Adjust = new AdjustSettings { Saturation = 30, Contrast = 15 },
            Background = new BackgroundSettings { Enabled = true, Fill = "preset:lagoon", Padding = 200, Corner = 80, Shadow = 60, Ratio = "1:1" },
        };

        using var full = StudioRenderer.Render(doc, source, RenderStage.Final, null, out _);
        using var preview = StudioRenderer.Render(doc, source, RenderStage.Final, 800, out var scale);
        Assert.InRange(preview.Width, full.Width * scale - 2, full.Width * scale + 2);
        Assert.InRange(preview.Height, full.Height * scale - 2, full.Height * scale + 2);

        using var shrunk = full.Resize((double)preview.Width / full.Width, vscale: (double)preview.Height / full.Height);
        double diff = Math.Abs(shrunk.Avg() - preview.Avg());
        Assert.True(diff < 3, $"average differs by {diff:F2}");
    }

    [Fact]
    public void Saturation_minus_100_gives_grey()
    {
        var src = _ws.Png("colour.png", 120, 80);
        using var source = StudioSource.Open(src);
        var doc = Doc(src) with { Adjust = new AdjustSettings { Saturation = -100 } };
        using var result = StudioRenderer.Render(doc, source, RenderStage.Final, null, out _);
        var p = result.Getpoint(100, 10);
        Assert.InRange(Math.Abs(p[0] - p[1]), 0, 2);
        Assert.InRange(Math.Abs(p[1] - p[2]), 0, 2);
    }

    [Fact]
    public void Solid_redaction_replaces_the_pixels()
    {
        var src = _ws.Png("id card.png", 300, 200);
        using var source = StudioSource.Open(src);
        var doc = Doc(src) with { Redactions = [new Redaction(new PixelRect(50, 50, 100, 40))] };
        using var result = StudioRenderer.Render(doc, source, RenderStage.Final, null, out _);
        using var region = result.ExtractArea(50, 50, 100, 40);
        Assert.Equal(0, region.Max());
    }

    [Fact]
    public void Annotations_draw_into_the_pixels()
    {
        var src = _ws.Png("ui.png", 300, 200);
        using var source = StudioSource.Open(src);
        var doc = Doc(src) with
        {
            Annotations =
            [
                new Annotation { Kind = AnnotationKind.Arrow, Points = [20, 20, 200, 150], Color = "#00FF00", Stroke = 8 },
                new Annotation { Kind = AnnotationKind.Text, Points = [10, 160], Text = "مرحبا Hello", FontSize = 24, Color = "#0000FF" },
                new Annotation { Kind = AnnotationKind.Step, Points = [260, 40], FontSize = 30, Color = "#E5484D" },
            ],
        };
        using var plain = StudioRenderer.Render(Doc(src), source, RenderStage.Final, null, out _);
        using var marked = StudioRenderer.Render(doc, source, RenderStage.Final, null, out _);
        var mid = marked.Getpoint(110, 85);
        Assert.True(mid[1] > 200 && mid[0] < 60, "arrow should pass through the middle in green");
        Assert.NotEqual(plain.Getpoint(260, 40)[0], marked.Getpoint(260, 40)[0]);
    }

    [Fact]
    public void Target_size_is_met_by_lowering_quality_or_size()
    {
        // Noise compresses badly, so a small target forces quality and size reduction.
        using var noise = Image.Gaussnoise(1600, 1200, mean: 128, sigma: 60).Cast(Enums.BandFormat.Uchar);
        using var rgb = noise.Bandjoin(noise, noise).Copy(interpretation: Enums.Interpretation.Srgb);
        const long target = 180 * 1024;
        var result = TargetSize.Search(rgb, FileKind.Jpeg, target, new EncodeSettings());
        Assert.True(result.Met, result.Explanation);
        Assert.True(result.Data.Length <= target);
    }

    [Fact]
    public void Impossible_target_is_reported_not_faked()
    {
        using var noise = Image.Gaussnoise(800, 600, mean: 128, sigma: 60).Cast(Enums.BandFormat.Uchar);
        using var rgb = noise.Bandjoin(noise, noise).Copy(interpretation: Enums.Interpretation.Srgb);
        var result = TargetSize.Search(rgb, FileKind.Jpeg, 2 * 1024, new EncodeSettings(), allowDownscale: false);
        Assert.False(result.Met);
        Assert.Contains("Could not reach", result.Explanation);
    }

    [Fact]
    public async Task Studio_export_job_uses_reference_style_names()
    {
        var src = _ws.Png("Page 001.png", 800, 1000);
        var doc = Doc(src) with
        {
            Crop = new CropSettings { Rect = new PixelRect(0, 0, 800, 600), Aspect = "free" },
            Background = new BackgroundSettings { Enabled = true, Fill = "preset:dawn" },
        };
        var job = await _ws.Run(Ids.StudioExport, src, ("document", doc.ToJson()));
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error);
        Assert.Equal(_ws.PathOf("Page 001 Cropped With Background.png"), item.Outputs[0].Path);
        Assert.Equal((800 + 240, 600 + 240), (item.Outputs[0].Width!.Value, item.Outputs[0].Height!.Value));
    }

    [Fact]
    public async Task Pdf_page_can_be_edited_and_saved_as_an_image()
    {
        var pdf = _ws.Pdf("Statement.pdf", 2);
        var doc = Doc(pdf) with { Page = 2, Dpi = 150, Crop = new CropSettings { Rect = new PixelRect(0, 0, 1275, 800) } };
        var job = await _ws.Run(Ids.StudioExport, pdf, ("document", doc.ToJson()));
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error);
        Assert.Equal(_ws.PathOf("Statement Cropped - Page 02.png"), item.Outputs[0].Path);
        Assert.Equal((1275, 800), (item.Outputs[0].Width!.Value, item.Outputs[0].Height!.Value));
    }

    [Fact]
    public async Task Compress_with_target_reports_the_outcome()
    {
        using var noise = Image.Gaussnoise(1400, 1000, mean: 128, sigma: 50).Cast(Enums.BandFormat.Uchar);
        using var rgb = noise.Bandjoin(noise, noise).Copy(interpretation: Enums.Interpretation.Srgb);
        var src = _ws.PathOf("photo.jpg");
        rgb.Jpegsave(src, q: 95);
        var doc = Doc(src) with { Export = new ExportSettings { TargetBytes = 150 * 1024 } };
        var job = await _ws.Run(Ids.StudioExport, src, ("document", doc.ToJson()), ("suffix", " Compressed"));
        var item = Assert.Single(job.Items);
        Assert.True(item.State == JobItemState.Completed, item.Error);
        Assert.Equal(_ws.PathOf("photo Compressed.jpg"), item.Outputs[0].Path);
        Assert.True(item.Outputs[0].SizeBytes <= 150 * 1024);
        Assert.Contains(item.Notes, n => n.StartsWith("Size target met"));
    }
}
