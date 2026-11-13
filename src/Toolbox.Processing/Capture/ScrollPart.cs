namespace Toolbox.Processing.Capture;

/// <summary>A rectangle inside a frame, in its pixels.</summary>
public readonly record struct PartRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool IsWhole(int width, int height) => X == 0 && Y == 0 && Width == width && Height == height;

    public override string ToString() => $"{Width}x{Height} at {X},{Y}";
}

public static class FrameCrop
{
    /// <summary>The pixels of <paramref name="part"/> as a frame of their own.</summary>
    public static Frame Crop(this Frame frame, PartRect part)
    {
        if (part.IsWhole(frame.Width, frame.Height)) return frame;
        Check(part, frame.Width, frame.Height);
        var px = new byte[part.Width * part.Height * 4];
        int rowBytes = part.Width * 4;
        for (int y = 0; y < part.Height; y++)
            Buffer.BlockCopy(frame.Pixels, ((part.Y + y) * frame.Width + part.X) * 4, px, y * rowBytes, rowBytes);
        return new Frame(part.Width, part.Height, px);
    }

    public static GrayFrame Crop(this GrayFrame frame, PartRect part)
    {
        if (part.IsWhole(frame.Width, frame.Height)) return frame;
        Check(part, frame.Width, frame.Height);
        var values = new float[part.Width * part.Height];
        for (int y = 0; y < part.Height; y++)
            Array.Copy(frame.Values, (part.Y + y) * frame.Width + part.X, values, y * part.Width, part.Width);
        return new GrayFrame(part.Width, part.Height, values);
    }

    private static void Check(PartRect part, int width, int height)
    {
        if (part.X < 0 || part.Y < 0 || part.Width <= 0 || part.Height <= 0 || part.Right > width || part.Bottom > height)
            throw new ArgumentOutOfRangeException(nameof(part), $"{part} is not inside {width}x{height}.");
    }
}

public static partial class ShiftFinder
{
    public const float PartChange = 6f;      // a block whose mean |diff| is above this changed
    public const int PartMinimum = 40;       // the smallest part worth capturing, each way (px)
    public const float SeamDiff = 24f;       // a pixel this far off doesn't line up
    public const double FlatSpread = 2.0;    // a column (or row) whose std-dev is under this is flat
    public const double BorderStep = 10.0;   // a flat line this different from the flat one next to it is an edge
    public const int StrayWidth = 6;         // columns inside the part that may move differently (a caret)

    /// <summary>
    /// When only part of the frame scrolls — a panel beside a fixed sidebar, a list or chat inside a
    /// page that stays put, content beside its own moving scrollbar thumb — finds that part and how
    /// far it moved. Returns the match with <see cref="ShiftMatch.Part"/> set, or null when nothing
    /// scrolled or what changed doesn't move the way scrolling does (a video, an animation).
    /// </summary>
    /// <param name="keepRows">
    /// True when the frame was chosen to fit the part (a picked scroll area, a drawn region): still
    /// rows at its top and bottom are its own sticky header and footer and stay in. False for a whole
    /// window: the capture shrinks to the rows that scroll.
    /// </param>
    public static ShiftMatch? FindPart(GrayFrame prev, GrayFrame cur, double? expected = null, bool keepRows = false) =>
        FindPart(prev, cur, expected, keepRows, out _);

    /// <inheritdoc cref="FindPart(GrayFrame, GrayFrame, double?, bool)"/>
    /// <param name="why">When it returns null: why no part was taken (for the log).</param>
    public static ShiftMatch? FindPart(GrayFrame prev, GrayFrame cur, double? expected, bool keepRows, out string why)
    {
        if (prev.Width != cur.Width || prev.Height != cur.Height) throw new ArgumentException("Frames must be the same size.");
        int w = prev.Width, h = prev.Height;
        why = "the area is too small";
        if (w < PartMinimum || h < PartMinimum) return null;
        var a = prev.Values;
        var b = cur.Values;

        // 1. Where did it change? A coarse map of blocks, 4 rows by a few columns.
        const int bh = 4;
        int bw = Math.Max(4, w / 160);
        int cols = w / bw, rows = h / bh;
        var changed = new bool[rows * cols];
        Parallel.For(0, rows, r =>
        {
            for (int c = 0; c < cols; c++)
            {
                double sum = 0;
                for (int y = r * bh; y < r * bh + bh; y++)
                {
                    int i = y * w + c * bw;
                    for (int x = 0; x < bw; x++) sum += Math.Abs(a[i + x] - b[i + x]);
                }
                if (sum / (bw * bh) > PartChange) changed[r * cols + c] = true;
            }
        });
        var activity = new int[cols];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                if (changed[r * cols + c]) activity[c]++;
        int most = activity.Max();
        why = "nothing changed";
        if (most == 0) return null;

        // 2. The busiest run of changed columns. Short gaps (blank strips inside the content) are
        // bridged; a blinking caret or a small spinner elsewhere is too quiet to count.
        int active = Math.Max(2, most / 25);
        int bestFrom = -1, bestTo = -1;
        long bestScore = 0;
        int from = -1, to = -1, gap = 0;
        long score = 0;
        for (int c = 0; c <= cols; c++)
        {
            bool on = c < cols && activity[c] >= active;
            if (on)
            {
                if (from < 0) { from = c; score = 0; }
                score += activity[c];
                to = c;
                gap = 0;
                continue;
            }
            if (from < 0) continue;
            if (c < cols && ++gap <= 2) continue;
            if (score > bestScore) (bestScore, bestFrom, bestTo) = (score, from, to);
            from = -1;
            gap = 0;
        }
        why = "what changed is too small";
        if (bestFrom < 0) return null;
        int x0 = bestFrom * bw, x1 = bestTo == cols - 1 ? w : (bestTo + 1) * bw;
        // The rows those columns changed in, to the pixel (block rows would take in a still border row).
        int y0 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            if (RowDiff(a, b, y * w + x0, y * w + x0, x1 - x0) >= StaticTolerance) { if (y0 < 0) y0 = y; y1 = y + 1; }
        if (y0 < 0 || x1 - x0 < PartMinimum || y1 - y0 < PartMinimum) return null;

        // 3. How far that core moved (its own sticky rows and any stray stripe aside).
        var core = new PartRect(x0, y0, x1 - x0, y1 - y0);
        var coreMatch = Find(prev.Crop(core), cur.Crop(core), expected);
        why = $"what changed ({core}) doesn't move like scrolling ({coreMatch.Reason})";
        if (!coreMatch.Moved || coreMatch.Distance <= 0 || coreMatch.Score > MatchTolerance) return null;
        int d = coreMatch.Distance;
        // What the part's background looks like: most of its pixels (text and pictures are the few).
        double background = Median(b, w, core);

        // 4. The rows where the core really lines up at that shift: row y of the new frame equals row
        // y + d of the old one (the bottom d rows are new). A popup, a toast or a title bar repainting
        // changes rows that have nothing to do with scrolling, so the longest run of rows that line up
        // is what the rest is measured on.
        why = $"{core} moved {d} px, too far to line up";
        if (y1 - d - y0 < 8) return null;
        var (fy0, fy1) = LongestRun(y0, y1 - d, y => RowFits(a, b, w, x0, x1 - x0, y + d, y));
        why = $"too little of {core} lines up after moving {d} px";
        if (fy1 - fy0 < 8) return null;
        int fitRows = fy1 - fy0;

        // Then the columns that moved by exactly that much: a column is in if (almost) none of its
        // pixels in those rows disagree.
        var bad = new int[w];
        var mean = new double[w];
        var flat = new bool[w];
        Parallel.For(0, w, x =>
        {
            int n = 0;
            double s = 0;
            for (int y = fy0; y < fy1; y++)
            {
                float vb = b[y * w + x];
                if (Math.Abs(vb - a[(y + d) * w + x]) > SeamDiff) n++;
                s += vb;
            }
            bad[x] = n;
            mean[x] = s / fitRows;
            int off = 0;
            for (int y = fy0; y < fy1; y++)
                if (Math.Abs(b[y * w + x] - mean[x]) > 3 * FlatSpread) off++;
            flat[x] = off <= Math.Max(1, fitRows / 100);
        });
        int allowColumn = Math.Max(1, fitRows / 200);
        bool ColumnFits(int x) => bad[x] <= allowColumn;

        // Trim the core's edges that don't move with it (a scrollbar thumb moves less than the page).
        int left = x0, right = x1 - 1;
        while (left <= right && !ColumnFits(left)) left++;
        while (right >= left && !ColumnFits(right)) right--;
        why = $"too little of {core} moves by {d} px";
        if (right - left + 1 < PartMinimum) return null;
        // A caret inside the content is fine; a band moving on its own (a video) is not scrolling.
        int stray = 0;
        for (int x = left; x <= right; x++)
        {
            stray = ColumnFits(x) ? 0 : stray + 1;
            if (stray > StrayWidth)
            {
                why = $"something inside {core} moves on its own (near x {x})";
                return null;
            }
        }
        // Take in the columns beside it that fit too (its blank margins), up to its edge: a column
        // that doesn't fit (the still page beside it) or a flat line in another colour (a border).
        left = Grow(left, -1, w, ColumnFits, flat, mean, background);
        right = Grow(right, 1, w, ColumnFits, flat, mean, background);
        int px0 = left, px1 = right + 1, pw = px1 - px0;
        // Only a thin strip was cut off the side (the page's own scrollbar): it is still the whole
        // page, so its sticky header and footer stay, as they do when everything lines up.
        if (pw >= w - Math.Max(40, w / 20)) keepRows = true;

        // 5. Rows: from the rows that line up, outwards while the rows still move with the part (its
        // blank space included), up to the page around it, a border or whatever moves on its own.
        // Upward, new-frame row y must equal old-frame row y + d; downward, old-frame row y must
        // equal new-frame row y - d.
        int py0 = GrowRows(fy0, -1, a, b, w, h, px0, pw, d, background);
        int py1 = GrowRows(fy1 - 1 + d, 1, a, b, w, h, px0, pw, d, background) + 1;
        if (keepRows)
        {
            // The part's own sticky header and footer: rows that stay put right above and below it
            // (past a few soft-edged rows, such as a shadow under a header, that change as it scrolls).
            bool Still(int y) => RowDiff(a, b, y * w + px0, y * w + px0, pw) < StaticTolerance;
            int cap = (int)(h * MaxFixedFraction);
            int Bar(int from, int dir)
            {
                int skip = 0;
                while (skip < EdgePad && from + dir * skip >= 0 && from + dir * skip < h && !Still(from + dir * skip)) skip++;
                int n = 0;
                while (from + dir * (skip + n) >= 0 && from + dir * (skip + n) < h && Still(from + dir * (skip + n))) n++;
                return n > 0 && skip + n <= cap ? skip + n : 0;
            }
            py0 -= Bar(py0 - 1, -1);
            py1 += Bar(py1, 1);
        }

        var part = new PartRect(px0, py0, pw, py1 - py0);
        why = part.IsWhole(w, h) ? "the whole area moves" : $"{part} is too small";
        if (part.IsWhole(w, h) || part.Width < PartMinimum || part.Height < PartMinimum) return null;
        var match = Find(prev.Crop(part), cur.Crop(part), expected);
        why = $"{part} doesn't line up ({match.Reason})";
        if (!match.Moved || !match.Ok) return null;
        why = "";
        return match with { Part = part, Reason = "Only part of the area scrolls" };
    }

    /// <summary>Does new-frame row <paramref name="newRow"/> equal old-frame row <paramref name="oldRow"/> over these columns (all but a few pixels)?</summary>
    private static bool RowFits(float[] a, float[] b, int w, int x0, int width, int oldRow, int newRow)
    {
        int allow = Math.Max(1, width / 200), n = 0;
        int ai = oldRow * w + x0, bi = newRow * w + x0;
        for (int x = 0; x < width; x++)
            if (Math.Abs(b[bi + x] - a[ai + x]) > SeamDiff && ++n > allow) return false;
        return true;
    }

    /// <summary>The longest run of consecutive values in [from, to) for which <paramref name="ok"/> holds.</summary>
    private static (int Start, int End) LongestRun(int from, int to, Func<int, bool> ok)
    {
        int bestStart = from, bestEnd = from, start = -1;
        for (int y = from; y <= to; y++)
        {
            if (y < to && ok(y)) { if (start < 0) start = y; continue; }
            if (start >= 0 && y - start > bestEnd - bestStart) (bestStart, bestEnd) = (start, y);
            start = -1;
        }
        return (bestStart, bestEnd);
    }

    /// <summary>The median of (a sample of) the values inside <paramref name="r"/>.</summary>
    private static double Median(float[] v, int w, PartRect r)
    {
        var sample = new List<float>();
        int step = Math.Max(1, (int)Math.Sqrt(r.Width * (double)r.Height / 20000));
        for (int y = r.Y; y < r.Bottom; y += step)
            for (int x = r.X; x < r.Right; x += step) sample.Add(v[y * w + x]);
        sample.Sort();
        return sample[sample.Count / 2];
    }

    /// <summary>
    /// Widens from <paramref name="edge"/> one column at a time while the columns move with the part.
    /// It stops at a flat column that isn't the part's background: a border, or the page beside it.
    /// </summary>
    private static int Grow(int edge, int dir, int w, Func<int, bool> fits, bool[] flat, double[] mean, double background)
    {
        for (int x = edge + dir; x >= 0 && x < w && fits(x); x += dir)
        {
            if (flat[x] && Math.Abs(mean[x] - background) > BorderStep) break;
            edge = x;
        }
        return edge;
    }

    /// <summary>
    /// From the first (or last) row that moved, takes in the rows beyond it that still move with the
    /// part (blank space), up to a row that stays put (the page around it) or a border line.
    /// </summary>
    private static int GrowRows(int edge, int dir, float[] a, float[] b, int w, int h, int x0, int width, int d, double background)
    {
        int allow = Math.Max(1, width / 200);
        for (int y = edge + dir; y >= 0 && y < h; y += dir)
        {
            // Upward, row y of the new frame should be row y + d of the old; downward, row y of the
            // old frame should be row y - d of the new.
            int oldRow = dir < 0 ? y + d : y, newRow = dir < 0 ? y : y - d;
            if (oldRow >= h || newRow < 0) break;
            int n = 0;
            double s = 0;
            int ai = oldRow * w + x0, bi = newRow * w + x0;
            for (int x = 0; x < width; x++)
            {
                float vb = b[bi + x];
                if (Math.Abs(vb - a[ai + x]) > SeamDiff) n++;
                s += vb;
            }
            if (n > allow) break;
            double m = s / width;
            int off = 0;
            for (int x = 0; x < width; x++)
                if (Math.Abs(b[bi + x] - m) > 3 * FlatSpread) off++;
            if (off <= Math.Max(1, width / 100) && Math.Abs(m - background) > BorderStep) break;
            edge = y;
        }
        return edge;
    }
}
