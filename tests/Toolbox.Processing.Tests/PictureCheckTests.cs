using SkiaSharp;
using Toolbox.Processing.Imaging;

namespace Toolbox.Processing.Tests;

/// <summary>
/// The director's tools for really looking at a picture (whole, and every part at full size) and for
/// measuring an edit (how much changed outside the part that was meant to change).
/// </summary>
public sealed class PictureCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ftb-look-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A 1672×941 picture (Codex's 16:9 size) with a flat background and one red square.</summary>
    private string Picture(string name, int width = 1672, int height = 941, SKRectI? square = null, SKColor? squareColour = null, Action<SKCanvas>? extra = null)
    {
        var path = Path.Combine(_dir, name);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(0xE8, 0xE4, 0xDA));
            using var paint = new SKPaint { Color = squareColour ?? new SKColor(0xC0, 0x20, 0x20) };
            canvas.DrawRect(square ?? SKRectI.Create(100, 100, 200, 150), paint);
            extra?.Invoke(canvas);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    [Fact]
    public void LookSavesTheWholeAndSixFullSizePartsThatCoverEveryPixel()
    {
        var picture = Picture("kf03-master-v1.png");
        var look = PictureCheck.Look(picture, Path.Combine(_dir, "look"));

        Assert.Equal(1672, look.Width);
        Assert.Equal(941, look.Height);
        Assert.True(File.Exists(look.Whole));
        Assert.Equal(["A1", "B1", "C1", "A2", "B2", "C2"], look.Tiles.Select(t => t.Name));
        foreach (var tile in look.Tiles)
        {
            Assert.True(File.Exists(tile.Path));
            using var part = SKBitmap.Decode(tile.Path);
            // Full resolution: a part is its own pixels, never scaled.
            Assert.Equal(tile.Area.Width, part.Width);
            Assert.Equal(tile.Area.Height, part.Height);
        }
        // Together the parts cover the whole picture, and neighbours overlap so nothing on an edge is cut in half.
        for (int y = 0; y < look.Height; y += 47)
            for (int x = 0; x < look.Width; x += 53)
                Assert.Contains(look.Tiles, t => t.Area.Contains(x, y));
        Assert.True(look.Tiles[0].Area.Right > look.Tiles[1].Area.X);
        Assert.True(look.Tiles[0].Area.Bottom > look.Tiles[3].Area.Y);
        // The whole picture is small enough to read at once, and keeps its shape.
        using var whole = SKBitmap.Decode(look.Whole);
        Assert.True(whole.Width <= 1672);
        Assert.Equal(1672.0 / 941, (double)whole.Width / whole.Height, 2);
        Assert.Contains("Part C2", PictureCheck.Describe(look));
    }

    [Fact]
    public void AnEditInsideTheBoxKeepsTheRestAndIsSaidSo()
    {
        var before = Picture("kf08-presenters-v1.png");
        // The fix: the square changes colour (inside the box); nothing else moves.
        var after = Picture("kf08-presenters-v2.png", squareColour: new SKColor(0x20, 0x60, 0xC0));
        var diff = PictureCheck.Diff(before, after, [new PixelBox(90, 90, 220, 170)], folder: _dir);

        Assert.False(diff.SizeChanged);
        Assert.Equal(0, diff.ChangedOutsidePercent);
        Assert.True(diff.ChangedInsidePercent > 70);
        Assert.True(File.Exists(diff.HeatMap));
        Assert.Contains("the rest is kept", PictureCheck.Describe(diff));
    }

    [Fact]
    public void ChangesOutsideTheBoxAreFoundAndPlaced()
    {
        var before = Picture("kf01-v1.png");
        // The fix was meant for the square, but a second shape appeared in the bottom right (part C2).
        var after = Picture("kf01-v2.png", squareColour: new SKColor(0x20, 0x60, 0xC0), extra: c =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            c.DrawRect(SKRectI.Create(1300, 700, 250, 180), paint);
        });
        var diff = PictureCheck.Diff(before, after, [PixelBox.Parse("90,90,220,170")], folder: _dir);

        Assert.True(diff.ChangedOutsidePercent > 1);
        Assert.Equal(new PixelBox(1300, 700, 250, 180), diff.ChangedBounds);
        Assert.Equal("C2", diff.Tiles.OrderByDescending(t => t.ChangedPercent).First().Name);
        var text = PictureCheck.Describe(diff);
        Assert.Contains("Outside:", text);
        Assert.Contains("C2", text);
    }

    [Fact]
    public void ANewSizeIsReportedAndStillCompared()
    {
        var before = Picture("kf02-doorway-v2.png");
        var after = Picture("kf02-doorway-v3.png", width: 1671);
        var diff = PictureCheck.Diff(before, after, folder: _dir);

        Assert.True(diff.SizeChanged);
        Assert.Equal(1671, diff.NewWidth);
        Assert.Null(diff.ChangedOutsidePercent);
        Assert.Contains("Size changed: 1672x941 → 1671x941", PictureCheck.Describe(diff));
    }

    [Fact]
    public void BoxesAreReadAsPixelsAndBadOnesAreRefused()
    {
        Assert.Equal([new PixelBox(10, 20, 30, 40), new PixelBox(1, 2, 3, 4)], PixelBox.ParseMany("10,20,30,40; 1,2,3,4"));
        Assert.Empty(PixelBox.ParseMany(null));
        Assert.Throws<FormatException>(() => PixelBox.Parse("10,20,30"));
        Assert.Throws<FormatException>(() => PixelBox.Parse("10,20,0,5"));
        Assert.Throws<FileNotFoundException>(() => PictureCheck.Look(Path.Combine(_dir, "missing.png")));
    }
}
