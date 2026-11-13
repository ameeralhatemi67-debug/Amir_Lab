using Toolbox.Processing.Studio;

namespace Toolbox.Processing.Video;

/// <summary>
/// Hides a box in one video frame (32-bit BGRA or BGRx, rows top-down, no padding), in place and
/// fast enough to run on every frame while a video plays. It looks like the photo editor's Blur:
/// Blur averages the box into cells about a sixth of its height and blurs those with a radius of
/// about 0.42 of its height, so no letter shapes survive; Pixelate uses blocks about half its
/// height; Solid paints it black.
/// </summary>
public static class FrameBlur
{
    public static void Apply(byte[] px, int width, int height, PixelRect box, RedactionMode mode)
    {
        int x0 = Math.Max(0, box.X), y0 = Math.Max(0, box.Y);
        int x1 = Math.Min(width, box.X + box.Width), y1 = Math.Min(height, box.Y + box.Height);
        int w = x1 - x0, h = y1 - y0;
        if (w < 1 || h < 1) return;
        // Sized from the whole box, so a box half off the picture hides the same way.
        int m = Math.Max(1, Math.Min(box.Width, box.Height));
        switch (mode)
        {
            case RedactionMode.Solid: Fill(px, width, x0, y0, w, h); break;
            case RedactionMode.Pixelate: Pixelate(px, width, x0, y0, w, h, Math.Max(6, (int)Math.Round(m / 2.2))); break;
            default: Blur(px, width, x0, y0, w, h, m); break;
        }
    }

    private static void Fill(byte[] px, int width, int x0, int y0, int w, int h)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            int i = (y * width + x0) * 4;
            for (int x = 0; x < w; x++, i += 4) { px[i] = 0; px[i + 1] = 0; px[i + 2] = 0; px[i + 3] = 255; }
        }
    }

    private static void Pixelate(byte[] px, int width, int x0, int y0, int w, int h, int block)
    {
        for (int by = 0; by < h; by += block)
            for (int bx = 0; bx < w; bx += block)
            {
                int bw = Math.Min(block, w - bx), bh = Math.Min(block, h - by);
                long sb = 0, sg = 0, sr = 0;
                for (int y = 0; y < bh; y++)
                {
                    int i = ((y0 + by + y) * width + x0 + bx) * 4;
                    for (int x = 0; x < bw; x++, i += 4) { sb += px[i]; sg += px[i + 1]; sr += px[i + 2]; }
                }
                int n = bw * bh;
                byte b = (byte)(sb / n), g = (byte)(sg / n), r = (byte)(sr / n);
                for (int y = 0; y < bh; y++)
                {
                    int i = ((y0 + by + y) * width + x0 + bx) * 4;
                    for (int x = 0; x < bw; x++, i += 4) { px[i] = b; px[i + 1] = g; px[i + 2] = r; }
                }
            }
    }

    private static void Blur(byte[] px, int width, int x0, int y0, int w, int h, int m)
    {
        // 1. Average into cells.
        int cell = Math.Max(2, (int)Math.Round(m / 6.0));
        int gw = (w + cell - 1) / cell, gh = (h + cell - 1) / cell;
        var grid = new float[gw * gh * 3];
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
            {
                int cw = Math.Min(cell, w - gx * cell), ch = Math.Min(cell, h - gy * cell);
                float sb = 0, sg = 0, sr = 0;
                for (int y = 0; y < ch; y++)
                {
                    int i = ((y0 + gy * cell + y) * width + x0 + gx * cell) * 4;
                    for (int x = 0; x < cw; x++, i += 4) { sb += px[i]; sg += px[i + 1]; sr += px[i + 2]; }
                }
                float n = cw * ch;
                int g = (gy * gw + gx) * 3;
                grid[g] = sb / n; grid[g + 1] = sg / n; grid[g + 2] = sr / n;
            }

        // 2. Blur the cells (a Gaussian of 0.42 × the box's height, in cells; edges held).
        double sigma = Math.Max(4, m * 0.42) / cell;
        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 2.5));
        var kernel = new float[2 * radius + 1];
        float sum = 0;
        for (int i = -radius; i <= radius; i++) sum += kernel[i + radius] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= sum;
        var tmp = new float[grid.Length];
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
                for (int c = 0; c < 3; c++)
                {
                    float v = 0;
                    for (int k = -radius; k <= radius; k++) v += kernel[k + radius] * grid[(gy * gw + Math.Clamp(gx + k, 0, gw - 1)) * 3 + c];
                    tmp[(gy * gw + gx) * 3 + c] = v;
                }
        for (int gy = 0; gy < gh; gy++)
            for (int gx = 0; gx < gw; gx++)
                for (int c = 0; c < 3; c++)
                {
                    float v = 0;
                    for (int k = -radius; k <= radius; k++) v += kernel[k + radius] * tmp[(Math.Clamp(gy + k, 0, gh - 1) * gw + gx) * 3 + c];
                    grid[(gy * gw + gx) * 3 + c] = v;
                }

        // 3. Back to pixels, smoothly between cell centres.
        for (int y = 0; y < h; y++)
        {
            float fy = Math.Clamp((y + 0.5f) / cell - 0.5f, 0, gh - 1);
            int ya = (int)fy, yb = Math.Min(gh - 1, ya + 1);
            float ty = fy - ya;
            int i = ((y0 + y) * width + x0) * 4;
            for (int x = 0; x < w; x++, i += 4)
            {
                float fx = Math.Clamp((x + 0.5f) / cell - 0.5f, 0, gw - 1);
                int xa = (int)fx, xb = Math.Min(gw - 1, xa + 1);
                float tx = fx - xa;
                int a = (ya * gw + xa) * 3, b = (ya * gw + xb) * 3, c = (yb * gw + xa) * 3, d = (yb * gw + xb) * 3;
                for (int ch = 0; ch < 3; ch++)
                {
                    float top = grid[a + ch] + (grid[b + ch] - grid[a + ch]) * tx;
                    float bottom = grid[c + ch] + (grid[d + ch] - grid[c + ch]) * tx;
                    px[i + ch] = (byte)Math.Clamp((int)(top + (bottom - top) * ty + 0.5f), 0, 255);
                }
            }
        }
    }
}
