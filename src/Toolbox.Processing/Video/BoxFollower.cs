using Toolbox.Processing.Studio;

namespace Toolbox.Processing.Video;

/// <summary>What happened to a followed box in one frame.</summary>
public enum FollowState
{
    /// <summary>Found where the content moved to.</summary>
    Following,

    /// <summary>Nothing moved.</summary>
    Still,

    /// <summary>The box holds nothing to follow (one plain colour): it stays where it is.</summary>
    Plain,

    /// <summary>The content is sliding off the picture: the box keeps going the way it went.</summary>
    Leaving,

    /// <summary>The content has left the picture: the box can end here.</summary>
    Gone,

    /// <summary>The content changed too much to be found (typing, a new page): the box stays where it was.</summary>
    Lost,
}

public readonly record struct FollowStep(PixelRect Box, FollowState State, double Score);

/// <summary>
/// Follows what is inside a box from one video frame to the next. Screen content mostly scrolls or
/// slides as a whole, so this is the same idea as Longshot's shift finder, in two directions and
/// for one box: the box's picture in the last frame is slid over the new frame (first on shrunk
/// grey copies over a wide area, then pixel by pixel) and the best match wins, preferring the
/// motion seen just before when look-alikes tie. Content sliding off the edge is followed until it
/// is gone. If the content changes too much to be found, the box stays where it was: something
/// private is never left uncovered because the follower lost it.
/// </summary>
public sealed class BoxFollower
{
    /// <summary>Mean |grey difference| (0–255) above which the content is taken as gone or changed.</summary>
    public const double LostScore = 12;

    /// <summary>Below this share of the box on the picture, matching stops and the box coasts out.</summary>
    public const double MinVisible = 0.3;

    /// <summary>A match on only part of the box costs this much more for the part missing.</summary>
    private const double PartialCost = 14;

    /// <summary>How many places of the wide search are checked pixel by pixel.</summary>
    private const int Candidates = 6;

    private readonly int _width, _height, _k, _sw, _sh;
    private readonly byte[] _prev;
    private byte[] _prevSmall;
    private PixelRect _box;
    private int _vx, _vy;
    private readonly bool _plain;
    private bool _covered;   // last frame, part of it had slid under something that stays put

    /// <param name="frame">The frame's brightness, one byte a pixel (<see cref="Luma"/>).</param>
    public BoxFollower(byte[] frame, int width, int height, PixelRect box)
    {
        if (frame.Length != width * height) throw new ArgumentException("Give the frame's brightness (Luma), one byte a pixel.", nameof(frame));
        _width = width;
        _height = height;
        _box = box;
        // Shrunk for the wide search: the box at most about 96 px across, and the picture at most
        // about 360 px on its short side (so a small box on a big screen is quick to search too).
        int k = Math.Max((int)Math.Ceiling(Math.Max(box.Width, box.Height) / 96.0), (int)Math.Ceiling(Math.Min(width, height) / 360.0));
        _k = Math.Clamp(k, 1, Math.Max(1, Math.Min(width, height) / 16));
        _sw = Math.Max(1, width / _k);
        _sh = Math.Max(1, height / _k);
        _prev = (byte[])frame.Clone();
        _prevSmall = Shrink(frame);
        _plain = Texture(frame, box) < 2.0;
    }

    public PixelRect Box => _box;

    /// <summary>The box holds one plain colour: there is nothing to follow.</summary>
    public bool IsPlain => _plain;

    public FollowStep Next(byte[] frame)
    {
        var small = Shrink(frame);
        var step = Find(frame, small);
        Buffer.BlockCopy(frame, 0, _prev, 0, Math.Min(frame.Length, _prev.Length));
        _prevSmall = small;
        _box = step.Box;
        return step;
    }

    private FollowStep Find(byte[] frame, byte[] small)
    {
        if (_plain) return new FollowStep(_box, FollowState.Plain, 0);
        if (Visible(_box) < MinVisible) return Coast();
        double still = ScoreFull(_prev, frame, _box, 0, 0, Stride(_box), out _, out _);
        if (still <= 0.8) { _vx = _vy = 0; return new FollowStep(_box, FollowState.Still, still); }

        // Wide search on the shrunk copies, around the motion seen last; the best few places go on.
        int bx = FloorDiv(_box.X, _k), by = FloorDiv(_box.Y, _k);
        int bw = Math.Max(2, (int)Math.Round(_box.Width / (double)_k)), bh = Math.Max(2, (int)Math.Round(_box.Height / (double)_k));
        int px = (int)Math.Round(_vx / (double)_k), py = (int)Math.Round(_vy / (double)_k);
        int rx = (int)Math.Ceiling(Math.Max(_box.Width * 0.5, _width * 0.1) / _k) + 2;
        int ry = (int)Math.Ceiling(Math.Max(_box.Height * 1.0, _height * 0.25) / _k) + 2;
        var top = new List<(double Score, int Dx, int Dy)>(Candidates + 1);
        double cut = double.MaxValue;
        for (int dy = py - ry; dy <= py + ry; dy++)
            for (int dx = px - rx; dx <= px + rx; dx++)
            {
                double bias = 0.04 * (Math.Abs(dx - px) + Math.Abs(dy - py));
                double s = ScoreSmall(_prevSmall, small, bx, by, bw, bh, dx, dy, cut - bias);
                if (double.IsNaN(s) || s == double.MaxValue) continue;
                s += bias;
                if (top.Count == Candidates && s >= cut) continue;
                int at = top.FindIndex(c => c.Score > s);
                top.Insert(at < 0 ? top.Count : at, (s, dx, dy));
                if (top.Count > Candidates) top.RemoveAt(top.Count - 1);
                if (top.Count == Candidates) cut = top[^1].Score;
            }
        if (top.Count == 0) return Coast();

        // Pixel by pixel around each of them.
        int r = Math.Max(1, _k);
        int stride = Stride(_box);
        double bestFull = double.MaxValue, bestRaw = double.MaxValue;
        int fdx = 0, fdy = 0;
        var tried = new HashSet<(int, int)>();
        foreach (var (_, cdx, cdy) in top)
            for (int dy = cdy * _k - r; dy <= cdy * _k + r; dy++)
                for (int dx = cdx * _k - r; dx <= cdx * _k + r; dx++)
                {
                    if (!tried.Add((dx, dy))) continue;
                    double scored = ScoreFull(_prev, frame, _box, dx, dy, stride, out double visible, out double raw);
                    if (visible < MinVisible || double.IsNaN(scored)) continue;
                    double s = scored + 0.01 * (Math.Abs(dx - _vx) + Math.Abs(dy - _vy));
                    if (s < bestFull) { bestFull = s; bestRaw = raw; fdx = dx; fdy = dy; }
                }
        if (bestFull == double.MaxValue) return Coast();
        // Lost when even the best place differs too much. The limit follows what varies inside the
        // box, and a jump the motion so far doesn't explain must match twice as well: any line of
        // text half-matches any other line (their blank rows line up), so a look-alike elsewhere
        // must not pass for the content when it has really gone.
        double limit = Math.Clamp(0.2 * Spread(_prev, _box), 4, LostScore);
        if (Math.Abs(fdx - _vx) + Math.Abs(fdy - _vy) > 3) limit = Math.Max(3.5, limit / 2);
        if (bestRaw > limit)
        {
            // Sliding under something that stays put (a page's header or footer): the rows still
            // showing match, the rest are covered. Keep following while enough shows; once it is
            // under, it has left the picture as far as anyone can see.
            // The motion it was following is tried first (the best whole-box place is no guide
            // once part of the box is covered), then that place.
            double coverBest = double.MaxValue;
            int cdx = 0, cdy = 0;
            var tries = new List<(int, int)>();
            if (_vx != 0 || _vy != 0)
                for (int oy = -1; oy <= 1; oy++)
                    for (int ox = -1; ox <= 1; ox++) tries.Add((_vx + ox, _vy + oy));
            tries.Add((fdx, fdy));
            foreach (var (tx, ty) in tries)
                if (UnderCover(_prev, frame, _box, tx, ty, limit, out double partial) && partial < coverBest) { coverBest = partial; cdx = tx; cdy = ty; }
            if (coverBest < double.MaxValue)
            {
                _covered = true;
                _vx = cdx;
                _vy = cdy;
                return new FollowStep(_box with { X = _box.X + cdx, Y = _box.Y + cdy }, FollowState.Following, coverBest);
            }
            if (_covered && (_vx != 0 || _vy != 0))
                return new FollowStep(_box with { X = _box.X + _vx, Y = _box.Y + _vy }, FollowState.Gone, bestRaw);
            _vx = _vy = 0;
            return new FollowStep(_box, FollowState.Lost, bestRaw);
        }
        _covered = false;
        _vx = fdx;
        _vy = fdy;
        var moved = _box with { X = _box.X + fdx, Y = _box.Y + fdy };
        return new FollowStep(moved, fdx == 0 && fdy == 0 ? FollowState.Still : FollowState.Following, bestRaw);
    }

    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    /// <summary>Too little of it is left on the picture to match: it keeps going the way it went.</summary>
    private FollowStep Coast()
    {
        if (_vx == 0 && _vy == 0)
            return Visible(_box) <= 0 ? new FollowStep(_box, FollowState.Gone, 0) : new FollowStep(_box, FollowState.Lost, double.NaN);
        var moved = _box with { X = _box.X + _vx, Y = _box.Y + _vy };
        return new FollowStep(moved, Visible(moved) <= 0 ? FollowState.Gone : FollowState.Leaving, double.NaN);
    }

    /// <summary>The share of the box that is on the picture.</summary>
    private double Visible(PixelRect b)
    {
        int w = Math.Min(_width, b.X + b.Width) - Math.Max(0, b.X);
        int h = Math.Min(_height, b.Y + b.Height) - Math.Max(0, b.Y);
        return w <= 0 || h <= 0 ? 0 : (double)w * h / Math.Max(1, (long)b.Width * b.Height);
    }

    private static int Stride(PixelRect b) => Math.Max(1, (int)Math.Sqrt((double)b.Width * b.Height / 20000));

    /// <summary>A frame's brightness (BGRx in, one byte a pixel out): what the follower works on.</summary>
    public static byte[] Luma(byte[] bgra, int width, int height, byte[]? into = null)
    {
        var luma = into ?? new byte[width * height];
        for (int i = 0, j = 0; j < width * height; i += 4, j++) luma[j] = (byte)((bgra[i] + 5 * bgra[i + 1] + 2 * bgra[i + 2]) >> 3);
        return luma;
    }

    private byte[] Shrink(byte[] px)
    {
        var small = new byte[_sw * _sh];
        int k = _k, n = k * k;
        for (int sy = 0; sy < _sh; sy++)
            for (int sx = 0; sx < _sw; sx++)
            {
                int sum = 0;
                for (int y = 0; y < k; y++)
                {
                    int i = (sy * k + y) * _width + sx * k;
                    for (int x = 0; x < k; x++, i++) sum += px[i];
                }
                small[sy * _sw + sx] = (byte)(sum / n);
            }
        return small;
    }

    /// <summary>Mean |difference| between the box in the old shrunk frame and the box moved by (dx, dy) in the new one; NaN when too little overlaps.</summary>
    private double ScoreSmall(byte[] a, byte[] b, int bx, int by, int bw, int bh, int dx, int dy, double giveUpAbove)
    {
        int x0 = Math.Max(0, Math.Max(-bx, -(bx + dx))), x1 = Math.Min(bw, Math.Min(_sw - bx, _sw - (bx + dx)));
        int y0 = Math.Max(0, Math.Max(-by, -(by + dy))), y1 = Math.Min(bh, Math.Min(_sh - by, _sh - (by + dy)));
        int cols = x1 - x0, rows = y1 - y0;
        if (cols <= 0 || rows <= 0 || cols * rows < MinVisible * bw * bh) return double.NaN;
        double share = (double)cols * rows / (bw * bh);
        double penalty = PartialCost * (1 - share);
        double limit = (giveUpAbove - penalty) * cols * rows;
        long sum = 0, tsum = 0, tsq = 0;
        for (int y = y0; y < y1; y++)
        {
            int ia = (by + y) * _sw + bx + x0, ib = (by + dy + y) * _sw + bx + dx + x0;
            for (int x = 0; x < cols; x++)
            {
                int v = a[ia + x];
                sum += Math.Abs(v - b[ib + x]);
                if (share < 0.9) { tsum += v; tsq += v * v; }
            }
            if (sum > limit) return double.MaxValue;
        }
        int n = cols * rows;
        // Only part of the box overlaps: that part must hold something (a blank sliver matches anything).
        if (share < 0.9 && Math.Sqrt(Math.Max(0, (double)tsq / n - (double)tsum / n * tsum / n)) < 2) return double.NaN;
        return (double)sum / n + penalty;
    }

    /// <summary>The same at full size, sampling every <paramref name="stride"/>th pixel.</summary>
    private double ScoreFull(byte[] a, byte[] b, PixelRect box, int dx, int dy, int stride, out double visible, out double mean)
    {
        mean = double.NaN;
        int x0 = Math.Max(0, Math.Max(-box.X, -(box.X + dx))), x1 = Math.Min(box.Width, Math.Min(_width - box.X, _width - (box.X + dx)));
        int y0 = Math.Max(0, Math.Max(-box.Y, -(box.Y + dy))), y1 = Math.Min(box.Height, Math.Min(_height - box.Y, _height - (box.Y + dy)));
        visible = Visible(box with { X = box.X + dx, Y = box.Y + dy });
        if (x1 <= x0 || y1 <= y0) { visible = 0; return double.NaN; }
        long sum = 0, n = 0, tsum = 0, tsq = 0;
        double share = (double)(x1 - x0) * (y1 - y0) / Math.Max(1, (long)box.Width * box.Height);
        for (int y = y0; y < y1; y += stride)
        {
            int ia = (box.Y + y) * _width + box.X + x0, ib = (box.Y + dy + y) * _width + box.X + dx + x0;
            for (int x = x0; x < x1; x += stride, ia += stride, ib += stride, n++)
            {
                int v = a[ia];
                sum += Math.Abs(v - b[ib]);
                tsum += v;
                tsq += v * v;
            }
        }
        if (n == 0) return double.NaN;
        mean = (double)sum / n;
        if (share < 0.9 && Math.Sqrt(Math.Max(0, (double)tsq / n - (double)tsum / n * tsum / n)) < 2) return double.NaN;
        return (double)sum / n + PartialCost * (1 - share);
    }

    /// <summary>
    /// At this move, do the rows at one end of the box match (at least 30% of it, holding something)
    /// while the others don't? That is content sliding under a fixed bar; <paramref name="score"/> is
    /// the matching rows' difference.
    /// </summary>
    private bool UnderCover(byte[] a, byte[] b, PixelRect box, int dx, int dy, double limit, out double score)
    {
        score = double.NaN;
        if (dx == 0 && dy == 0) return false; // part of it changed in place: that is not sliding under anything
        int x0 = Math.Max(0, Math.Max(-box.X, -(box.X + dx))), x1 = Math.Min(box.Width, Math.Min(_width - box.X, _width - (box.X + dx)));
        int y0 = Math.Max(0, Math.Max(-box.Y, -(box.Y + dy))), y1 = Math.Min(box.Height, Math.Min(_height - box.Y, _height - (box.Y + dy)));
        int rows = y1 - y0;
        if (x1 <= x0 || rows < 3) return false;
        int stride = Math.Max(1, (x1 - x0) / 400);
        var mean = new double[rows];
        for (int r = 0; r < rows; r++)
        {
            int y = y0 + r;
            int ia = (box.Y + y) * _width + box.X, ib = (box.Y + dy + y) * _width + box.X + dx;
            long sum = 0, n = 0;
            for (int x = x0; x < x1; x += stride, n++) sum += Math.Abs(a[ia + x] - b[ib + x]);
            mean[r] = (double)sum / Math.Max(1, n);
        }
        int top = 0, bottom = 0;
        while (top < rows && mean[top] <= limit) top++;
        while (bottom < rows && mean[rows - 1 - bottom] <= limit) bottom++;
        bool fromTop = top >= bottom;
        int run = Math.Max(top, bottom);
        if (run >= rows || run < MinVisible * box.Height) return false;
        int first = fromTop ? 0 : rows - run;
        double total = 0, s = 0, sq = 0, still = 0;
        long count = 0;
        for (int r = first; r < first + run; r++)
        {
            total += mean[r];
            int y = y0 + r;
            int ia = (box.Y + y) * _width + box.X, ib = (box.Y + y) * _width + box.X;
            for (int x = x0; x < x1; x += stride, count++)
            {
                int v = a[ia + x];
                s += v;
                sq += v * v;
                if (box.Y + y >= 0 && box.Y + y < _height && box.X + x >= 0 && box.X + x < _width) still += Math.Abs(v - b[ib + x]);
            }
        }
        if (count == 0 || Math.Sqrt(Math.Max(0, sq / count - s / count * (s / count))) < 2) return false; // a blank strip proves nothing
        score = total / run;
        // The matching rows must be content that moved: a bar that stays put matches at any height
        // too, and must not pass for the content.
        return still / count > score + 3;
    }

    /// <summary>The mean distance of the box's brightness from its average (what varies inside it).</summary>
    private double Spread(byte[] px, PixelRect box)
    {
        var b = box.ClampTo(_width, _height);
        int stride = Stride(b);
        long sum = 0, n = 0;
        for (int y = b.Y; y < b.Y + b.Height; y += stride)
            for (int x = b.X; x < b.X + b.Width; x += stride, n++) sum += px[y * _width + x];
        if (n == 0) return 0;
        double avg = (double)sum / n, dev = 0;
        for (int y = b.Y; y < b.Y + b.Height; y += stride)
            for (int x = b.X; x < b.X + b.Width; x += stride) dev += Math.Abs(px[y * _width + x] - avg);
        return dev / n;
    }

    /// <summary>How much the brightness varies inside the box (0 for one plain colour).</summary>
    private double Texture(byte[] px, PixelRect box)
    {
        var b = box.ClampTo(_width, _height);
        int stride = Stride(b);
        double sum = 0, sq = 0;
        long n = 0;
        for (int y = b.Y; y < b.Y + b.Height; y += stride)
            for (int x = b.X; x < b.X + b.Width; x += stride)
            {
                int v = px[y * _width + x];
                sum += v;
                sq += v * v;
                n++;
            }
        if (n == 0) return 0;
        double mean = sum / n;
        return Math.Sqrt(Math.Max(0, sq / n - mean * mean));
    }
}
