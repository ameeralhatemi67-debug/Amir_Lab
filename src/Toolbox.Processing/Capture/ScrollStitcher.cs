namespace Toolbox.Processing.Capture;

/// <summary>A captured rectangle: 32-bit BGRA pixels, top row first, no padding between rows.</summary>
public sealed class Frame
{
    public Frame(int width, int height, byte[] pixels)
    {
        if (pixels.Length != width * height * 4) throw new ArgumentException("Pixel buffer does not match the size.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }
    public int Stride => Width * 4;
}

/// <summary>Brightness per pixel (mean of R, G and B), row-major. What the matching works on.</summary>
public sealed class GrayFrame
{
    public GrayFrame(int width, int height, float[] values)
    {
        Width = width;
        Height = height;
        Values = values;
    }

    public int Width { get; }
    public int Height { get; }
    public float[] Values { get; }

    public static GrayFrame From(Frame frame)
    {
        var values = new float[frame.Width * frame.Height];
        var px = frame.Pixels;
        Parallel.For(0, frame.Height, y =>
        {
            int src = y * frame.Stride, dst = y * frame.Width;
            for (int x = 0; x < frame.Width; x++, src += 4)
                values[dst + x] = (px[src] + px[src + 1] + px[src + 2]) / 3f;
        });
        return new GrayFrame(frame.Width, frame.Height, values);
    }
}

/// <summary>The result of comparing two captures of the same rectangle.</summary>
public sealed record ShiftMatch
{
    /// <summary>Did anything scroll at all?</summary>
    public bool Moved { get; init; }

    /// <summary>Can the new frame be stitched?</summary>
    public bool Ok { get; init; }

    /// <summary>How many rows the content moved up.</summary>
    public int Distance { get; init; }

    /// <summary>Unchanged rows at the top (a sticky header), edge rows included.</summary>
    public int Top { get; init; }

    /// <summary>Unchanged rows at the bottom (a sticky footer), edge rows included.</summary>
    public int Bottom { get; init; }

    /// <summary>Mean |difference| over the overlap (0 = perfect).</summary>
    public double Score { get; init; }

    /// <summary>Mean |difference| of the worst 16-px column stripe of the overlap.</summary>
    public double Worst { get; init; }

    /// <summary>True when the distance was taken from earlier steps because the area is blank.</summary>
    public bool Estimated { get; init; }

    /// <summary>Height of the part that scrolls.</summary>
    public int Region { get; init; }

    /// <summary>
    /// Set when only part of the frame scrolls (<see cref="ShiftFinder.FindPart"/>): where that part
    /// is in the frame. <see cref="Distance"/>, <see cref="Top"/>, <see cref="Bottom"/> and
    /// <see cref="Region"/> are then measured inside it.
    /// </summary>
    public PartRect? Part { get; init; }

    public string Reason { get; init; } = "";
}

/// <summary>
/// Works out how far the content scrolled between two captures. Rows that are identical at the top
/// and bottom of both frames are fixed (sticky header, footer, toolbar); the part in between is
/// slid against itself to find the shift where the old frame's lower part matches the new frame's
/// upper part. Ported from the Python prototype in tools/LongshotPrototype, including its fixes for
/// soft-edged sticky bars and look-alike rows (both found on real web pages).
/// </summary>
public static partial class ShiftFinder
{
    public const float StaticTolerance = 1.5f;     // mean |diff| (0-255) under which a row is unchanged
    public const double MatchTolerance = 6.0;      // refuse when the best overlap is worse than this
    public const double WorstTolerance = 4.0;      // ...or when any 16-px stripe of it is worse than this
    public const double TextureMinimum = 2.0;      // overlap std-dev under which the area is blank
    public const double MaxFixedFraction = 0.4;    // a header/footer may take at most this much
    public const int ClusterPx = 3;                // shifts this close are the same minimum
    public const int EdgePad = 4;                  // rows beside a sticky bar treated as part of it

    public static ShiftMatch Find(GrayFrame prev, GrayFrame cur, double? expected = null)
    {
        if (prev.Width != cur.Width || prev.Height != cur.Height) throw new ArgumentException("Frames must be the same size.");
        int w = prev.Width, h = prev.Height;
        var a = prev.Values;
        var b = cur.Values;

        var isStatic = new bool[h];
        for (int y = 0; y < h; y++) isStatic[y] = RowDiff(a, b, y * w, y * w, w) < StaticTolerance;
        if (isStatic.All(s => s)) return new ShiftMatch { Moved = false, Reason = "Nothing moved" };

        int cap = (int)(h * MaxFixedFraction);
        int top = Array.IndexOf(isStatic, false);
        int bottom = 0;
        while (bottom < h && isStatic[h - 1 - bottom]) bottom++;
        top = Math.Min(top > 0 ? top + EdgePad : 0, cap);
        bottom = Math.Min(bottom > 0 ? bottom + EdgePad : 0, cap);
        int r = h - top - bottom;
        int minOverlap = Math.Max(8, r / 10);
        int maxD = r - minOverlap;
        if (maxD < 1)
            return new ShiftMatch { Moved = true, Top = top, Bottom = bottom, Region = r, Reason = "The selected area is too small to line up" };
        int offset = top * w;

        // Coarse search over every shift, on column-averaged rows.
        int block = Math.Max(1, w / 160);
        int wb = w / block;
        var ac = Reduce(a, offset, r, w, block, wb);
        var bc = Reduce(b, offset, r, w, block, wb);
        var coarse = new double[maxD];
        Parallel.For(1, maxD + 1, d =>
        {
            int count = (r - d) * wb;
            double sum = 0;
            int ai = d * wb;
            for (int i = 0; i < count; i++) sum += Math.Abs(ac[ai + i] - bc[i]);
            coarse[d - 1] = sum / count;
        });

        // The best local minima (and their neighbours), re-scored at full detail.
        var minima = new List<int>();
        for (int i = 0; i < maxD; i++)
        {
            bool left = i == 0 || coarse[i] <= coarse[i - 1];
            bool right = i == maxD - 1 || coarse[i] <= coarse[i + 1];
            if (left && right) minima.Add(i);
        }
        var candidates = new SortedSet<int>();
        foreach (int i in minima.OrderBy(i => coarse[i]).ThenBy(i => i).Take(12))
            for (int d = i; d <= i + 2; d++)
                if (d >= 1 && d <= maxD) candidates.Add(d);
        if (expected is > 0)
        {
            int e = (int)Math.Round(expected.Value);
            for (int d = e - 2; d <= e + 2; d++)
                if (d >= 1 && d <= maxD) candidates.Add(d);
        }
        var scored = candidates
            .Select(d => (Score: MeanDiff(a, b, offset + d * w, offset, (r - d) * w), D: d))
            .OrderBy(x => x.Score).ThenBy(x => x.D)
            .ToList();
        double best = scored[0].Score;

        // Did it really scroll, or is something small animating in place (a caret, a spinner)? Only a
        // near-identical frame counts as "nothing moved": unrelated frames (the page jumped further
        // than the area) must not, or Auto would stop early thinking it reached the bottom.
        double still = MeanDiff(a, b, offset, offset, r * w);
        if (still <= best && still < StaticTolerance) return new ShiftMatch { Moved = false, Reason = "Nothing moved" };

        // One representative per minimum, so d-1 / d+1 never compete with d.
        var reps = new List<(double Score, int D)>();
        foreach (var s in scored)
            if (reps.All(rep => Math.Abs(rep.D - s.D) > ClusterPx)) reps.Add(s);
        // Only true ties (identical repeating rows) count. A pixel-perfect match beats a near miss
        // that differs in a row or two, so the tolerance shrinks with the best score.
        double tolerance = Math.Max(0.005, best * 0.1);
        var near = reps.Where(x => x.Score <= best + tolerance).ToList();
        var chosen = expected is > 0
            ? near.OrderBy(x => Math.Abs(x.D - expected.Value)).ThenBy(x => x.D).First()
            : near.OrderBy(x => x.D).First(); // smallest shift = biggest overlap

        int dist = chosen.D;
        var match = new ShiftMatch
        {
            Moved = true, Distance = dist, Top = top, Bottom = bottom, Score = chosen.Score, Region = r,
            Worst = WorstStripe(a, b, offset + dist * w, offset, r - dist, w),
        };

        if (StdDev(b, offset, (r - dist) * w) < TextureMinimum)
        {
            if (expected is > 0 && Math.Round(expected.Value) >= 1 && Math.Round(expected.Value) <= maxD)
                return match with { Distance = (int)Math.Round(expected.Value), Ok = true, Estimated = true, Reason = "Blank area, used the usual scroll distance" };
            return match with { Reason = "The area is too blank to line up" };
        }
        if (chosen.Score > MatchTolerance)
            return match with { Reason = "Couldn't line up the new part (it may have scrolled too far)" };
        // Look-alike content (lists, tables) can line up almost perfectly at the wrong place when the
        // page jumped further than we can see. Real matches differ evenly; wrong ones differ a lot in a
        // few stripes. Trust it anyway only if it equals the usual scroll step.
        if (match.Worst > WorstTolerance && !(expected is > 0 && Math.Abs(dist - expected.Value) <= 2))
            return match with { Reason = "Part of it doesn't line up (it scrolled too far, or something in the area is moving)" };
        return match with { Ok = true };
    }

    private static double RowDiff(float[] a, float[] b, int ai, int bi, int count)
    {
        double sum = 0;
        for (int i = 0; i < count; i++) sum += Math.Abs(a[ai + i] - b[bi + i]);
        return sum / count;
    }

    private static double MeanDiff(float[] a, float[] b, int ai, int bi, int count)
    {
        if (count <= 0) return double.MaxValue;
        double sum = 0;
        for (int i = 0; i < count; i++) sum += Math.Abs(a[ai + i] - b[bi + i]);
        return sum / count;
    }

    private static float[] Reduce(float[] src, int offset, int rows, int w, int block, int wb)
    {
        var dst = new float[rows * wb];
        for (int y = 0; y < rows; y++)
        {
            int s = offset + y * w;
            for (int j = 0; j < wb; j++)
            {
                float sum = 0;
                for (int k = 0; k < block; k++) sum += src[s + j * block + k];
                dst[y * wb + j] = sum / block;
            }
        }
        return dst;
    }

    private static double WorstStripe(float[] a, float[] b, int ai, int bi, int rows, int w, int stripe = 16)
    {
        int stripes = w / stripe;
        if (stripes == 0 || rows <= 0) return MeanDiff(a, b, ai, bi, rows * w);
        var sums = new double[stripes];
        for (int y = 0; y < rows; y++)
        {
            int ar = ai + y * w, br = bi + y * w;
            for (int s = 0; s < stripes; s++)
            {
                double acc = 0;
                int x0 = s * stripe;
                for (int x = x0; x < x0 + stripe; x++) acc += Math.Abs(a[ar + x] - b[br + x]);
                sums[s] += acc;
            }
        }
        return sums.Max() / (rows * (double)stripe);
    }

    private static double StdDev(float[] v, int start, int count)
    {
        if (count <= 0) return 0;
        double sum = 0, sq = 0;
        for (int i = start; i < start + count; i++)
        {
            sum += v[i];
            sq += (double)v[i] * v[i];
        }
        double mean = sum / count;
        return Math.Sqrt(Math.Max(0, sq / count - mean * mean));
    }
}

/// <summary>A tall image kept as a list of row-chunks so appending stays cheap.</summary>
public sealed class LongImage
{
    private readonly List<byte[]> _chunks = [];

    public LongImage(Frame first)
    {
        Width = first.Width;
        _chunks.Add((byte[])first.Pixels.Clone());
        Height = first.Height;
    }

    public int Width { get; }
    public int Height { get; private set; }
    private int Stride => Width * 4;

    public void Append(byte[] rows)
    {
        if (rows.Length == 0) return;
        _chunks.Add(rows);
        Height += rows.Length / Stride;
    }

    private byte[] _overview = [];
    private int _overviewWidth;
    private int _overviewRows;  // finished rows of the overview (their source rows are all in the image)
    private int _overviewValid; // source rows the finished overview rows may rely on

    /// <summary>
    /// The whole image shrunk to <paramref name="width"/> px wide (box-averaged, sampling at most 4×4
    /// source pixels per output pixel), for the live preview. Kept between calls: only rows added since
    /// the last call are worked out, so it stays cheap however long the image gets. Returns a buffer of
    /// at least <paramref name="height"/> rows of BGRA (the caller copies what it needs).
    /// </summary>
    public byte[] Overview(int width, out int height)
    {
        width = Math.Clamp(width, 1, Width);
        double factor = Width / (double)width; // source pixels per overview pixel, both ways
        height = Math.Max(1, (int)(Height / factor));
        if (width != _overviewWidth)
        {
            _overviewWidth = width;
            _overviewRows = 0;
            _overviewValid = 0;
        }
        // Rows whose source was trimmed (Undo, sticky footers) are worked out again.
        while (_overviewRows > 0 && (int)Math.Ceiling(_overviewRows * factor) > _overviewValid) _overviewRows--;
        int need = height * width * 4;
        if (_overview.Length < need) Array.Resize(ref _overview, Math.Max(need, _overview.Length * 2));

        for (int y = _overviewRows; y < height; y++)
        {
            int y0 = (int)(y * factor), y1 = Math.Min(Height, Math.Max(y0 + 1, (int)((y + 1) * factor)));
            int stepY = Math.Max(1, (y1 - y0) / 4);
            int o = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int x0 = (int)(x * factor), x1 = Math.Min(Width, Math.Max(x0 + 1, (int)((x + 1) * factor)));
                int stepX = Math.Max(1, (x1 - x0) / 4);
                int b = 0, g = 0, r = 0, n = 0;
                for (int sy = y0; sy < y1; sy += stepY)
                {
                    var (data, rowStart) = Row(sy);
                    for (int sx = x0; sx < x1; sx += stepX)
                    {
                        int i = rowStart + sx * 4;
                        b += data[i]; g += data[i + 1]; r += data[i + 2]; n++;
                    }
                }
                _overview[o++] = (byte)(b / n);
                _overview[o++] = (byte)(g / n);
                _overview[o++] = (byte)(r / n);
                _overview[o++] = 255;
            }
        }
        // The last row may still grow; keep only rows whose source was complete.
        _overviewRows = Math.Max(0, Math.Min(height, (int)(Height / factor) - 1));
        _overviewValid = Height;
        return _overview;
    }

    /// <summary>The chunk holding a source row, and where that row starts in it (rows near the bottom are found fastest).</summary>
    private (byte[] Data, int Start) Row(int row)
    {
        int end = Height;
        for (int i = _chunks.Count - 1; i >= 0; i--)
        {
            int rows = _chunks[i].Length / Stride;
            int start = end - rows;
            if (row >= start) return (_chunks[i], (row - start) * Stride);
            end = start;
        }
        return (_chunks[0], 0);
    }

    /// <summary>Removes <paramref name="count"/> rows from the bottom and returns them.</summary>
    public byte[] Trim(int count)
    {
        _overviewValid = Math.Min(_overviewValid, Math.Max(0, Height - count));
        var removed = new List<byte[]>();
        while (count > 0 && _chunks.Count > 0)
        {
            var last = _chunks[^1];
            int rows = last.Length / Stride;
            if (rows <= count)
            {
                removed.Add(last);
                _chunks.RemoveAt(_chunks.Count - 1);
                count -= rows;
                Height -= rows;
            }
            else
            {
                int keep = (rows - count) * Stride;
                removed.Add(last[keep..]);
                _chunks[^1] = last[..keep];
                Height -= count;
                count = 0;
            }
        }
        removed.Reverse();
        return Concat(removed);
    }

    /// <summary>The bottom <paramref name="rows"/> rows (fewer if the image is shorter).</summary>
    public byte[] Tail(int rows)
    {
        int need = Math.Min(rows, Height) * Stride;
        var parts = new List<byte[]>();
        for (int i = _chunks.Count - 1; i >= 0 && need > 0; i--)
        {
            var c = _chunks[i];
            var take = c.Length > need ? c[^need..] : c;
            parts.Add(take);
            need -= take.Length;
        }
        parts.Reverse();
        return Concat(parts);
    }

    public byte[] ToArray() => Concat(_chunks);

    private static byte[] Concat(List<byte[]> parts)
    {
        var result = new byte[parts.Sum(p => (long)p.Length)];
        int at = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, result, at, p.Length);
            at += p.Length;
        }
        return result;
    }
}

/// <summary>How a step was taken, so Undo can scroll back the same amount.</summary>
/// <summary>How a step scrolled: wheel notches and delta, or (when the area was scrolled by itself) where it was before.</summary>
public readonly record struct StepInfo(int Notches, int WheelUnit, double PercentBefore = double.NaN);

public sealed record StitchStep(byte[] Removed, int AddedRows, Frame PreviousFrame, StepInfo Info);

/// <summary>Holds the long image and the last frame that went into it.</summary>
public sealed class ScrollStitcher
{
    private readonly List<StitchStep> _history = [];
    private readonly int _maxUndo;

    public ScrollStitcher(Frame first, int maxUndo = 10)
    {
        Image = new LongImage(first);
        Last = first;
        LastGray = GrayFrame.From(first);
        _maxUndo = maxUndo;
    }

    public LongImage Image { get; private set; }
    public Frame Last { get; private set; }
    public GrayFrame LastGray { get; private set; }
    public int Steps { get; private set; }
    public bool CanUndo => _history.Count > 0;

    public (ShiftMatch Match, GrayFrame Gray) Match(Frame frame, double? expected = null)
    {
        var gray = GrayFrame.From(frame);
        return (ShiftFinder.Find(LastGray, gray, expected), gray);
    }

    public void Apply(Frame frame, GrayFrame gray, ShiftMatch match, StepInfo info = default)
    {
        int stride = frame.Stride;
        var removed = Image.Trim(match.Bottom);
        int from = (frame.Height - match.Bottom - match.Distance) * stride;
        var newRows = frame.Pixels[from..];
        Image.Append(newRows);
        _history.Add(new StitchStep(removed, newRows.Length / stride, Last, info));
        if (_history.Count > _maxUndo) _history.RemoveAt(0);
        Last = frame;
        LastGray = gray;
        Steps++;
    }

    /// <summary>Matches and, if it lines up, appends. Returns the match either way.</summary>
    public ShiftMatch Add(Frame frame, double? expected = null, StepInfo info = default)
    {
        var (match, gray) = Match(frame, expected);
        if (match.Moved && match.Ok) Apply(frame, gray, match, info);
        return match;
    }

    /// <summary>
    /// Like <see cref="Add"/>, but when the whole frame doesn't line up and this is still the first
    /// part, it also looks for a part of the frame that scrolls (<see cref="ShiftFinder.FindPart"/>):
    /// if one does, the capture is narrowed to it (<see cref="Narrow"/>) before the new rows go in.
    /// </summary>
    public ShiftMatch AddOrNarrow(Frame frame, double? expected = null, StepInfo info = default, bool keepRows = false)
    {
        var (match, gray) = Match(frame, expected);
        if (match.Moved && match.Ok && (match.Worst <= ShiftFinder.WorstTolerance || Steps > 0))
        {
            Apply(frame, gray, match, info);
            return match;
        }
        if (Steps == 0 && ShiftFinder.FindPart(LastGray, gray, expected, keepRows) is { Part: { } part } found)
        {
            Narrow(part);
            Apply(frame.Crop(part), gray.Crop(part), found, info);
            return found;
        }
        if (match.Moved && match.Ok) Apply(frame, gray, match, info);
        return match;
    }

    /// <summary>
    /// Only part of the frame scrolls: from now on only that part is captured. What was captured so
    /// far (the first part) and the last frame are cut down to it; the frames that follow must be
    /// cut the same way (or read from that part of the screen alone). Only before the second part.
    /// </summary>
    public void Narrow(PartRect part)
    {
        if (Steps != 0 || _history.Count != 0) throw new InvalidOperationException("Only the first part can be narrowed.");
        if (part.IsWhole(Last.Width, Last.Height)) return;
        var first = Last.Crop(part);
        Image = new LongImage(first);
        LastGray = LastGray.Crop(part);
        Last = first;
    }

    /// <summary>
    /// The area grew downward while the page stayed put (the owner pulled the frame's bottom edge
    /// down to take in what scrolling can't reach): the rows below the old bottom are added as they
    /// are. <paramref name="taller"/> must be the same width, with the old frame as its top part.
    /// Undo can't go back past this point, because the frames are a different size from here on.
    /// </summary>
    public int GrowDown(Frame taller)
    {
        if (taller.Width != Last.Width || taller.Height <= Last.Height)
            throw new ArgumentException("The new frame must be as wide as the last one and taller.", nameof(taller));
        int stride = taller.Stride;
        int added = taller.Height - Last.Height;
        Image.Append(taller.Pixels[(Last.Height * stride)..]);
        _history.Clear();
        Last = taller;
        LastGray = GrayFrame.From(taller);
        Steps++;
        return added;
    }

    public StitchStep? Undo()
    {
        if (_history.Count == 0) return null;
        var step = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Image.Trim(step.AddedRows);
        Image.Append(step.Removed);
        Last = step.PreviousFrame;
        LastGray = GrayFrame.From(step.PreviousFrame);
        Steps--;
        return step;
    }
}
