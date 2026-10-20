using System.Buffers.Binary;

namespace Toolbox.Processing.Metadata;

/// <summary>
/// Edits an EXIF (TIFF-structured) block in place without moving any data, so every offset stays
/// valid. Removed entries are unlinked from their directory and their bytes are overwritten with
/// zeros, so the values are actually gone rather than merely hidden.
/// </summary>
internal sealed class ExifEditor
{
    public const ushort ExifIfdPointer = 0x8769;
    public const ushort GpsIfdPointer = 0x8825;
    public const ushort InteropIfdPointer = 0xA005;
    public const ushort OrientationTag = 0x0112;
    public const ushort ThumbnailOffsetTag = 0x0201;
    public const ushort ThumbnailLengthTag = 0x0202;

    private readonly byte[] _tiff;
    private readonly bool _little;

    private ExifEditor(byte[] tiff, bool little)
    {
        _tiff = tiff;
        _little = little;
    }

    public byte[] Data => _tiff;

    /// <summary>Parses a TIFF block (the bytes after "Exif\0\0"). Returns null when it is not valid.</summary>
    public static ExifEditor? TryOpen(byte[] tiff)
    {
        if (tiff.Length < 8) return null;
        bool little = tiff[0] == 'I' && tiff[1] == 'I';
        bool big = tiff[0] == 'M' && tiff[1] == 'M';
        if (!little && !big) return null;
        var editor = new ExifEditor((byte[])tiff.Clone(), little);
        if (editor.U16(2) != 42) return null;
        return editor;
    }

    private uint Ifd0Offset => U32(4);

    public bool HasGps => FindEntry(Ifd0Offset, GpsIfdPointer) is not null;

    public bool HasThumbnail => Ifd0Offset != 0 && Valid(Ifd0Offset) && NextIfd(Ifd0Offset) is var n && n != 0 && Valid(n);

    public ushort? Orientation
    {
        get
        {
            var e = FindEntry(Ifd0Offset, OrientationTag);
            return e is { } entry ? U16(entry.Position + 8) : null;
        }
    }

    /// <summary>Removes the GPS directory: unlinks it and zeroes every byte it used.</summary>
    public bool RemoveGps()
    {
        var pointer = FindEntry(Ifd0Offset, GpsIfdPointer);
        if (pointer is not { } p) return false;
        uint gps = U32(p.Position + 8);
        if (Valid(gps)) ZeroDirectory(gps, recurse: false);
        RemoveEntry(Ifd0Offset, GpsIfdPointer);
        return true;
    }

    /// <summary>Removes IFD1 (the embedded thumbnail, which can show the uncropped original).</summary>
    public bool RemoveThumbnail()
    {
        uint ifd0 = Ifd0Offset;
        if (!Valid(ifd0)) return false;
        uint ifd1 = NextIfd(ifd0);
        if (ifd1 == 0 || !Valid(ifd1)) return false;
        var offset = FindEntry(ifd1, ThumbnailOffsetTag);
        var length = FindEntry(ifd1, ThumbnailLengthTag);
        if (offset is { } o && length is { } l)
        {
            uint start = ScalarValue(o), size = ScalarValue(l);
            if (start < _tiff.Length) Array.Clear(_tiff, (int)start, (int)Math.Min(size, (uint)_tiff.Length - start));
        }
        ZeroDirectory(ifd1, recurse: false);
        SetNextIfd(ifd0, 0);
        return true;
    }

    /// <summary>Removes the given tags from IFD0, the Exif sub-IFD and the Interop sub-IFD. Returns how many were removed.</summary>
    public int RemoveTags(IReadOnlySet<ushort> tags)
    {
        int removed = 0;
        foreach (var ifd in Directories().ToList())
        {
            // Re-read after every removal: removing an entry shifts the ones after it.
            while (Entries(ifd).FirstOrDefault(e => tags.Contains(e.Tag)) is { Tag: not 0 } entry)
            {
                ZeroEntryData(entry);
                RemoveEntry(ifd, entry.Tag);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>A minimal EXIF block containing only an orientation tag (so viewers keep showing the photo upright).</summary>
    public static byte[] MinimalWithOrientation(ushort orientation)
    {
        var tiff = new byte[8 + 2 + 12 + 4];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(10), OrientationTag);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(12), 3); // SHORT
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(18), orientation);
        return tiff; // next-IFD offset (last 4 bytes) is already 0
    }

    // ---- structure helpers ------------------------------------------------------------------

    private readonly record struct Entry(int Position, ushort Tag, ushort Type, uint Count);

    private IEnumerable<uint> Directories()
    {
        uint ifd0 = Ifd0Offset;
        if (!Valid(ifd0)) yield break;
        yield return ifd0;
        if (FindEntry(ifd0, ExifIfdPointer) is { } exif && U32(exif.Position + 8) is var exifOffset && Valid(exifOffset))
        {
            yield return exifOffset;
            if (FindEntry(exifOffset, InteropIfdPointer) is { } interop && U32(interop.Position + 8) is var io && Valid(io))
                yield return io;
        }
    }

    private List<Entry> Entries(uint ifd)
    {
        var list = new List<Entry>();
        if (!Valid(ifd)) return list;
        int count = U16((int)ifd);
        for (int i = 0; i < count; i++)
        {
            int pos = (int)ifd + 2 + i * 12;
            if (pos + 12 > _tiff.Length) break;
            list.Add(new Entry(pos, U16(pos), U16(pos + 2), U32(pos + 4)));
        }
        return list;
    }

    private Entry? FindEntry(uint ifd, ushort tag)
    {
        foreach (var e in Entries(ifd)) if (e.Tag == tag) return e;
        return null;
    }

    private uint NextIfd(uint ifd)
    {
        int pos = (int)ifd + 2 + U16((int)ifd) * 12;
        return pos + 4 <= _tiff.Length ? U32(pos) : 0;
    }

    private void SetNextIfd(uint ifd, uint value)
    {
        int pos = (int)ifd + 2 + U16((int)ifd) * 12;
        if (pos + 4 <= _tiff.Length) W32(pos, value);
    }

    /// <summary>Removes one entry by shifting the following entries and the next-IFD pointer left; zeroes the freed slot.</summary>
    private void RemoveEntry(uint ifd, ushort tag)
    {
        int count = U16((int)ifd);
        int start = (int)ifd + 2;
        int index = -1;
        for (int i = 0; i < count; i++)
            if (U16(start + i * 12) == tag) { index = i; break; }
        if (index < 0) return;
        int end = start + count * 12 + 4; // through the next-IFD pointer
        if (end > _tiff.Length) return;
        int from = start + (index + 1) * 12;
        Buffer.BlockCopy(_tiff, from, _tiff, from - 12, end - from);
        Array.Clear(_tiff, end - 12, 12);
        W16((int)ifd, (ushort)(count - 1));
    }

    private void ZeroDirectory(uint ifd, bool recurse)
    {
        foreach (var e in Entries(ifd)) ZeroEntryData(e);
        int count = U16((int)ifd);
        int size = 2 + count * 12 + 4;
        if (ifd + size <= _tiff.Length) Array.Clear(_tiff, (int)ifd, size);
    }

    private void ZeroEntryData(Entry e)
    {
        long size = (long)TypeSize(e.Type) * e.Count;
        if (size <= 4)
        {
            Array.Clear(_tiff, e.Position + 8, 4);
            return;
        }
        uint offset = U32(e.Position + 8);
        if (offset < _tiff.Length) Array.Clear(_tiff, (int)offset, (int)Math.Min(size, _tiff.Length - offset));
    }

    private uint ScalarValue(Entry e) => e.Type == 3 ? U16(e.Position + 8) : U32(e.Position + 8);

    private static int TypeSize(ushort type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 or 13 => 4,
        5 or 10 or 12 => 8,
        _ => 1,
    };

    private bool Valid(uint offset) => offset >= 8 && offset + 2 <= _tiff.Length;

    private ushort U16(int pos) => _little ? BinaryPrimitives.ReadUInt16LittleEndian(_tiff.AsSpan(pos)) : BinaryPrimitives.ReadUInt16BigEndian(_tiff.AsSpan(pos));

    private uint U32(int pos) => _little ? BinaryPrimitives.ReadUInt32LittleEndian(_tiff.AsSpan(pos)) : BinaryPrimitives.ReadUInt32BigEndian(_tiff.AsSpan(pos));

    private void W16(int pos, ushort v)
    {
        if (_little) BinaryPrimitives.WriteUInt16LittleEndian(_tiff.AsSpan(pos), v);
        else BinaryPrimitives.WriteUInt16BigEndian(_tiff.AsSpan(pos), v);
    }

    private void W32(int pos, uint v)
    {
        if (_little) BinaryPrimitives.WriteUInt32LittleEndian(_tiff.AsSpan(pos), v);
        else BinaryPrimitives.WriteUInt32BigEndian(_tiff.AsSpan(pos), v);
    }
}
