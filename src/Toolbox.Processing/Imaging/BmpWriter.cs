using NetVips;

namespace Toolbox.Processing.Imaging;

/// <summary>Writes uncompressed 24-bit BMP (BITMAPINFOHEADER), which every Windows program reads.</summary>
internal static class BmpWriter
{
    public static void Write(Image image, string path) => File.WriteAllBytes(path, Encode(image));

    public static byte[] Encode(Image image)
    {
        using var rgb = ToRgb8(image);
        int width = rgb.Width, height = rgb.Height;
        var pixels = rgb.WriteToMemory<byte>();

        int rowSize = (width * 3 + 3) & ~3;
        long imageSize = (long)rowSize * height;
        if (imageSize + 54 > int.MaxValue) throw new Toolbox.Core.ProcessingException("The image is too large for BMP.");

        var data = new byte[54 + imageSize];
        var span = data.AsSpan();
        // BITMAPFILEHEADER
        span[0] = (byte)'B';
        span[1] = (byte)'M';
        BitConverter.TryWriteBytes(span[2..], (int)data.Length);
        BitConverter.TryWriteBytes(span[10..], 54);
        // BITMAPINFOHEADER
        BitConverter.TryWriteBytes(span[14..], 40);
        BitConverter.TryWriteBytes(span[18..], width);
        BitConverter.TryWriteBytes(span[22..], height); // positive: bottom-up rows
        BitConverter.TryWriteBytes(span[26..], (short)1);
        BitConverter.TryWriteBytes(span[28..], (short)24);
        BitConverter.TryWriteBytes(span[34..], (int)imageSize);
        int ppm = (int)Math.Round(Math.Max(rgb.Xres, 1e-3) * 1000); // libvips resolution is pixels per millimetre
        BitConverter.TryWriteBytes(span[38..], ppm > 1 ? ppm : 3780);
        BitConverter.TryWriteBytes(span[42..], ppm > 1 ? ppm : 3780);

        for (int y = 0; y < height; y++)
        {
            int src = (height - 1 - y) * width * 3;
            int dst = 54 + y * rowSize;
            for (int x = 0; x < width; x++)
            {
                data[dst + x * 3] = pixels[src + x * 3 + 2];
                data[dst + x * 3 + 1] = pixels[src + x * 3 + 1];
                data[dst + x * 3 + 2] = pixels[src + x * 3];
            }
        }
        return data;
    }

    private static Image ToRgb8(Image image)
    {
        var img = image.Copy();
        if (img.Bands == 1 || img.Bands == 2)
        {
            var gray = img.ExtractBand(0);
            img.Dispose();
            img = gray.Bandjoin(gray, gray);
            gray.Dispose();
        }
        else if (img.Bands > 3)
        {
            var three = img.ExtractBand(0, n: 3);
            img.Dispose();
            img = three;
        }
        if (img.Format != Enums.BandFormat.Uchar)
        {
            var cast = img.Cast(Enums.BandFormat.Uchar);
            img.Dispose();
            img = cast;
        }
        return img;
    }
}
