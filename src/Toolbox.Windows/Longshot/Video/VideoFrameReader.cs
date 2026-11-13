using System.Runtime.InteropServices;

namespace Toolbox.Windows.Longshot.Video;

/// <summary>
/// Reads a video's frames one by one, exactly, with their times: Windows' Media Foundation source
/// reader, asked for 32-bit RGB (BGRx, top row first in what this class hands out). Video blur needs
/// every frame as pixels (to follow boxes, to show the frame as it will be saved, and to save it),
/// which the WinRT media classes don't give quickly. Used from one thread at a time.
/// </summary>
/// <remarks>
/// Media Foundation's COM interfaces are called through their function tables (the slot numbers
/// are the interfaces' method order in mfidl.h / mfreadwrite.h), so no interop types are needed.
/// </remarks>
internal sealed unsafe partial class VideoFrameReader : IDisposable
{
    private const uint MF_VERSION = 0x00020070;
    private const uint FirstVideoStream = 0xFFFFFFFC, FirstAudioStream = 0xFFFFFFFD, AllStreams = 0xFFFFFFFE, MediaSource = 0xFFFFFFFF;
    private const uint EndOfStream = 0x2, MediaTypeChanged = 0x20;
    private static readonly Guid SourceReaderEnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid VideoFormatRgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    private static readonly Guid FrameSizeKey = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRateKey = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid DefaultStrideKey = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MinimumDisplayApertureKey = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    private static readonly Guid PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid Imf2DBuffer = new("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb");

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public long Value;
    }

    [LibraryImport("mfplat.dll")] private static partial int MFStartup(uint version, uint flags);
    [LibraryImport("mfplat.dll")] private static partial int MFShutdown();
    [LibraryImport("mfplat.dll")] private static partial int MFCreateAttributes(out IntPtr attributes, uint initialSize);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(out IntPtr type);
    [LibraryImport("mfreadwrite.dll", StringMarshalling = StringMarshalling.Utf16)] private static partial int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
    [LibraryImport("ole32.dll")] private static partial int CoInitializeEx(IntPtr reserved, uint coInit);

    private IntPtr _reader;
    private int _codedWidth, _codedHeight, _stride, _left, _top;
    private bool _started;

    public VideoFrameReader(string path)
    {
        CoInitializeEx(IntPtr.Zero, 0); // this thread may not have COM yet (a no-op, or a refusal, when it has)
        Check(MFStartup(MF_VERSION, 0), "Starting Media Foundation");
        _started = true;
        try
        {
            Check(MFCreateAttributes(out var attributes, 1), "Creating the reader's settings");
            try
            {
                var key = SourceReaderEnableVideoProcessing;
                Check(((delegate* unmanaged<IntPtr, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, 1), "Asking for colour conversion"); // IMFAttributes::SetUINT32
                Check(MFCreateSourceReaderFromURL(path, attributes, out _reader), "Opening the video");
            }
            finally { Marshal.Release(attributes); }

            ((delegate* unmanaged<IntPtr, uint, int, int>)Slot(_reader, 4))(_reader, AllStreams, 0);   // SetStreamSelection: none…
            Check(((delegate* unmanaged<IntPtr, uint, int, int>)Slot(_reader, 4))(_reader, FirstVideoStream, 1), "Choosing the picture"); // …but the picture

            // What the file holds: its visible size (the decoder works on whole 16-px blocks) and frame rate.
            IntPtr native;
            Check(((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Slot(_reader, 5))(_reader, FirstVideoStream, 0, &native), "Reading the video's format");
            try
            {
                FrameRate = Ratio(native, FrameRateKey) is { } fps && fps > 0 ? fps : 30;
                (Width, Height) = Size(native, FrameSizeKey);
                if (Aperture(native) is { } a) (_left, _top, Width, Height) = a;
            }
            finally { Marshal.Release(native); }

            Check(MFCreateMediaType(out var wanted), "Describing the pictures wanted");
            try
            {
                var major = MajorType; var video = MediaTypeVideo; var sub = Subtype; var rgb = VideoFormatRgb32;
                Check(((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Slot(wanted, 24))(wanted, &major, &video), "Describing the pictures wanted"); // SetGUID
                Check(((delegate* unmanaged<IntPtr, Guid*, Guid*, int>)Slot(wanted, 24))(wanted, &sub, &rgb), "Describing the pictures wanted");
                Check(((delegate* unmanaged<IntPtr, uint, uint*, IntPtr, int>)Slot(_reader, 7))(_reader, FirstVideoStream, null, wanted), "Asking for RGB pictures"); // SetCurrentMediaType
            }
            finally { Marshal.Release(wanted); }
            ReadOutputType();

            // How long it is.
            var pv = new PropVariant();
            var durationKey = PresentationDuration;
            if (((delegate* unmanaged<IntPtr, uint, Guid*, PropVariant*, int>)Slot(_reader, 12))(_reader, MediaSource, &durationKey, &pv) >= 0)
                Duration = TimeSpan.FromTicks(pv.Value);

            // Is there sound (which a copy made from these frames won't have)?
            IntPtr audio;
            if (((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, int>)Slot(_reader, 5))(_reader, FirstAudioStream, 0, &audio) >= 0 && audio != IntPtr.Zero)
            {
                HasSound = true;
                Marshal.Release(audio);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The visible picture's size (what <see cref="Read"/> fills).</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double FrameRate { get; }
    public TimeSpan Duration { get; }
    public bool HasSound { get; }

    /// <summary>One frame's time: a frame lasts this long.</summary>
    public TimeSpan FrameTime => TimeSpan.FromSeconds(1 / FrameRate);

    /// <summary>
    /// The next frame into <paramref name="into"/> (Width × Height × 4 bytes, BGRx, top row first)
    /// and its time; false at the end.
    /// </summary>
    public bool Read(byte[] into, out TimeSpan time)
    {
        time = default;
        while (true)
        {
            uint stream, flags;
            long stamp;
            IntPtr sample;
            Check(((delegate* unmanaged<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(_reader, 9))(_reader, FirstVideoStream, 0, &stream, &flags, &stamp, &sample), "Reading a frame");
            if ((flags & MediaTypeChanged) != 0) ReadOutputType();
            if (sample == IntPtr.Zero)
            {
                if ((flags & EndOfStream) != 0) return false;
                continue; // a gap in the stream
            }
            try
            {
                time = TimeSpan.FromTicks(stamp);
                Copy(sample, into);
                return true;
            }
            finally { Marshal.Release(sample); }
        }
    }

    /// <summary>
    /// Goes to the frame shown at <paramref name="at"/> and reads it: Windows jumps to the key frame
    /// before it, then frames are read up to it.
    /// </summary>
    public bool SeekRead(TimeSpan at, byte[] into, out TimeSpan time)
    {
        Seek(at);
        var half = TimeSpan.FromTicks(FrameTime.Ticks / 2);
        bool any = false;
        time = default;
        while (Read(into, out var t))
        {
            any = true;
            time = t;
            if (t + half >= at) return true;
        }
        return any;
    }

    public void Seek(TimeSpan at)
    {
        var pv = new PropVariant { Vt = 20, Value = Math.Max(0, at.Ticks) }; // VT_I8, in 100 ns
        var timeFormat = Guid.Empty;
        Check(((delegate* unmanaged<IntPtr, Guid*, PropVariant*, int>)Slot(_reader, 8))(_reader, &timeFormat, &pv), "Moving in the video");
    }

    private void Copy(IntPtr sample, byte[] into)
    {
        IntPtr buffer;
        Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer), "Reading a frame's pixels"); // IMFSample::ConvertToContiguousBuffer
        try
        {
            byte* first;
            int pitch;
            bool twoD = Marshal.QueryInterface(buffer, in Imf2DBuffer, out var buffer2D) >= 0;
            if (twoD)
            {
                Check(((delegate* unmanaged<IntPtr, byte**, int*, int>)Slot(buffer2D, 3))(buffer2D, &first, &pitch), "Reading a frame's pixels"); // IMF2DBuffer::Lock2D
            }
            else
            {
                uint max, length;
                byte* start;
                Check(((delegate* unmanaged<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &start, &max, &length), "Reading a frame's pixels"); // IMFMediaBuffer::Lock
                pitch = _stride;
                first = pitch < 0 ? start + (long)(_codedHeight - 1) * -pitch : start; // bottom-up: the top row is last
            }
            try
            {
                int row = Width * 4;
                fixed (byte* dst = into)
                    for (int y = 0; y < Height; y++)
                        Buffer.MemoryCopy(first + (long)(y + _top) * pitch + _left * 4, dst + (long)y * row, row, row);
            }
            finally
            {
                if (twoD)
                {
                    ((delegate* unmanaged<IntPtr, int>)Slot(buffer2D, 4))(buffer2D); // Unlock2D
                    Marshal.Release(buffer2D);
                }
                else ((delegate* unmanaged<IntPtr, int>)Slot(buffer, 4))(buffer); // Unlock
            }
        }
        finally { Marshal.Release(buffer); }
    }

    /// <summary>The pictures as they come out: coded size and row stride (negative when bottom-up).</summary>
    private void ReadOutputType()
    {
        IntPtr current;
        Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(_reader, 6))(_reader, FirstVideoStream, &current), "Reading the picture format");
        try
        {
            (_codedWidth, _codedHeight) = Size(current, FrameSizeKey);
            var strideKey = DefaultStrideKey;
            uint stride;
            _stride = ((delegate* unmanaged<IntPtr, Guid*, uint*, int>)Slot(current, 7))(current, &strideKey, &stride) >= 0 ? (int)stride : _codedWidth * 4; // GetUINT32
            if (Width == 0 || Height == 0 || _left + Width > _codedWidth || _top + Height > _codedHeight)
                (_left, _top, Width, Height) = (0, 0, _codedWidth, _codedHeight);
        }
        finally { Marshal.Release(current); }
    }

    private static (int Width, int Height) Size(IntPtr type, Guid key)
    {
        ulong v;
        if (((delegate* unmanaged<IntPtr, Guid*, ulong*, int>)Slot(type, 8))(type, &key, &v) < 0) return (0, 0); // GetUINT64
        return ((int)(v >> 32), (int)(v & 0xFFFFFFFF));
    }

    private static double? Ratio(IntPtr type, Guid key)
    {
        var (num, den) = Size(type, key);
        return den > 0 ? (double)(uint)num / (uint)den : null;
    }

    /// <summary>The part of the coded picture to show (MFVideoArea), when the file says.</summary>
    private static (int Left, int Top, int Width, int Height)? Aperture(IntPtr type)
    {
        var key = MinimumDisplayApertureKey;
        var area = stackalloc int[4]; // MFOffset x (fract, value as shorts), MFOffset y, SIZE cx, cy
        uint size;
        if (((delegate* unmanaged<IntPtr, Guid*, byte*, uint, uint*, int>)Slot(type, 15))(type, &key, (byte*)area, 16, &size) < 0 || size < 16) return null; // GetBlob
        int left = (short)(area[0] >> 16), top = (short)(area[1] >> 16);
        return area[2] > 0 && area[3] > 0 ? (left, top, area[2], area[3]) : null;
    }

    private static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8}).", Marshal.GetExceptionForHR(hr));
    }

    public void Dispose()
    {
        if (_reader != IntPtr.Zero)
        {
            Marshal.Release(_reader);
            _reader = IntPtr.Zero;
        }
        if (_started)
        {
            _started = false;
            MFShutdown();
        }
    }
}
