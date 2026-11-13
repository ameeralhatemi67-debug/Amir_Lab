using Toolbox.Processing.Studio;

namespace Toolbox.Processing.Video;

/// <summary>Where a blurred box is at one moment of a video (pixels of the video frame).</summary>
public readonly record struct BlurKey(TimeSpan At, PixelRect Box);

/// <summary>
/// One thing hidden in a video: a box that is on from <see cref="Start"/> to <see cref="End"/> and
/// moves between its keys (in a straight line from one key to the next, held before the first and
/// after the last). Keys are the data; following the content and dragging the box both just write keys.
/// </summary>
public sealed class BlurTrack
{
    private readonly List<BlurKey> _keys = [];

    public int Id { get; init; }

    public RedactionMode Mode { get; set; } = RedactionMode.Blur;

    public TimeSpan Start { get; set; }

    public TimeSpan End { get; set; }

    /// <summary>What it is, when it was found ("E-mail"); null when drawn by hand.</summary>
    public string? Label { get; set; }

    /// <summary>Keys in time order.</summary>
    public IReadOnlyList<BlurKey> Keys => _keys;

    public bool IsOn(TimeSpan t) => t >= Start && t <= End && _keys.Count > 0;

    /// <summary>The box at <paramref name="t"/>, or null while it is off.</summary>
    public PixelRect? BoxAt(TimeSpan t) => IsOn(t) ? Interpolate(t) : null;

    /// <summary>The box at <paramref name="t"/> from the keys alone (also outside Start–End).</summary>
    public PixelRect Interpolate(TimeSpan t)
    {
        if (_keys.Count == 0) throw new InvalidOperationException("The track has no keys.");
        if (t <= _keys[0].At) return _keys[0].Box;
        if (t >= _keys[^1].At) return _keys[^1].Box;
        int hi = 1;
        while (_keys[hi].At < t) hi++;
        var a = _keys[hi - 1];
        var b = _keys[hi];
        double f = (t - a.At).TotalMilliseconds / Math.Max(1e-6, (b.At - a.At).TotalMilliseconds);
        return Lerp(a.Box, b.Box, f);
    }

    internal static PixelRect Lerp(PixelRect a, PixelRect b, double f) => new(
        (int)Math.Round(a.X + (b.X - a.X) * f), (int)Math.Round(a.Y + (b.Y - a.Y) * f),
        (int)Math.Round(a.Width + (b.Width - a.Width) * f), (int)Math.Round(a.Height + (b.Height - a.Height) * f));

    /// <summary>
    /// Puts the box at <paramref name="at"/>: replaces a key closer than <paramref name="near"/>
    /// (the same frame), or adds one in time order.
    /// </summary>
    public void SetKey(TimeSpan at, PixelRect box, TimeSpan near)
    {
        for (int i = 0; i < _keys.Count; i++)
        {
            if ((_keys[i].At - at).Duration() <= near) { _keys[i] = new BlurKey(_keys[i].At, box); return; }
            if (_keys[i].At > at) { _keys.Insert(i, new BlurKey(at, box)); return; }
        }
        _keys.Add(new BlurKey(at, box));
    }

    /// <summary>Removes the keys later than <paramref name="at"/> (before following from there again).</summary>
    public void ClearKeysAfter(TimeSpan at) => _keys.RemoveAll(k => k.At > at);

    /// <summary>Replaces the keys after <paramref name="at"/> with <paramref name="keys"/>, thinned out.</summary>
    public void ReplaceKeysAfter(TimeSpan at, IReadOnlyList<BlurKey> keys, double tolerancePx = 1.0)
    {
        ClearKeysAfter(at);
        var all = new List<BlurKey>(_keys.Count + keys.Count);
        all.AddRange(_keys);
        all.AddRange(keys.Where(k => k.At > at));
        _keys.Clear();
        _keys.AddRange(Simplify(all, tolerancePx));
    }

    public BlurTrack Clone()
    {
        var copy = new BlurTrack { Id = Id, Mode = Mode, Start = Start, End = End, Label = Label };
        copy._keys.AddRange(_keys);
        return copy;
    }

    /// <summary>
    /// Fewer keys for the same movement: drops every key that the straight line between its
    /// neighbours already puts within <paramref name="tolerancePx"/> (Ramer–Douglas–Peucker over time).
    /// A box followed frame by frame keeps only the keys where its movement changes.
    /// </summary>
    public static List<BlurKey> Simplify(IReadOnlyList<BlurKey> keys, double tolerancePx)
    {
        if (keys.Count <= 2) return [.. keys];
        var keep = new bool[keys.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, keys.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            double worst = -1;
            int at = -1;
            for (int i = a + 1; i < b; i++)
            {
                double f = (keys[i].At - keys[a].At).TotalMilliseconds / Math.Max(1e-6, (keys[b].At - keys[a].At).TotalMilliseconds);
                var line = Lerp(keys[a].Box, keys[b].Box, f);
                var k = keys[i].Box;
                double d = Math.Max(Math.Max(Math.Abs(line.X - k.X), Math.Abs(line.Y - k.Y)), Math.Max(Math.Abs(line.Width - k.Width), Math.Abs(line.Height - k.Height)));
                if (d > worst) { worst = d; at = i; }
            }
            if (worst > tolerancePx)
            {
                keep[at] = true;
                stack.Push((a, at));
                stack.Push((at, b));
            }
        }
        var result = new List<BlurKey>();
        for (int i = 0; i < keys.Count; i++) if (keep[i]) result.Add(keys[i]);
        return result;
    }
}

/// <summary>Everything to hide in one video.</summary>
public sealed class VideoBlurPlan
{
    public List<BlurTrack> Tracks { get; init; } = [];

    /// <summary>The boxes on at <paramref name="t"/>, in drawing order.</summary>
    public IEnumerable<(PixelRect Box, RedactionMode Mode)> BoxesAt(TimeSpan t)
    {
        foreach (var track in Tracks)
            if (track.BoxAt(t) is { } box) yield return (box, track.Mode);
    }

    public int NextId() => Tracks.Count == 0 ? 1 : Tracks.Max(t => t.Id) + 1;

    public VideoBlurPlan Clone() => new() { Tracks = Tracks.Select(t => t.Clone()).ToList() };
}
