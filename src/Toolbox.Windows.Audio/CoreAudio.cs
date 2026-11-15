using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Toolbox.Windows.Audio;

// Windows' Core Audio interfaces (mmdeviceapi.h, audiopolicy.h, endpointvolume.h), in their
// header order: a COM interface is its method order, so nothing here may be reordered.
// Pointers that may be null (LPCGUID in callbacks) and strings handed to callbacks are taken as
// nint and read by hand, so a callback never frees memory it doesn't own.

internal static class AudioIds
{
    public static readonly Guid MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    public static readonly Guid IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public const uint DeviceStateActive = 0x1;
    public const int Render = 0;              // EDataFlow.eRender
    public const int Console = 0, Multimedia = 1; // ERole
    public const uint ClsctxAll = 0x17, ClsctxInproc = 0x1;

    public static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    public static readonly PropertyKey FormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid fmtid, uint pid)
{
    public readonly Guid FormatId = fmtid;
    public readonly uint Id = pid;
}

/// <summary>PROPVARIANT, enough of it for strings and numbers (24 bytes on x64).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort Type;
    public ushort Reserved1, Reserved2, Reserved3;
    public nint Value;
    public nint Value2;

    public const ushort Lpwstr = 31, UInt32 = 19;

    public readonly string? AsString() => Type == Lpwstr && Value != 0 ? Marshal.PtrToStringUni(Value) : null;

    public readonly uint? AsUInt() => Type == UInt32 ? (uint)(Value & 0xFFFFFFFF) : null;
}

[StructLayout(LayoutKind.Sequential)]
internal struct VolumeNotification
{
    public Guid Context;
    public int Muted;
    public float Master;
    public uint Channels;
}

internal enum SessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 2,
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    IMMDeviceCollection EnumAudioEndpoints(int dataFlow, uint stateMask);
    IMMDevice GetDefaultAudioEndpoint(int dataFlow, int role);
    IMMDevice GetDevice(string id);
    void RegisterEndpointNotificationCallback(IMMNotificationClient client);
    void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    uint GetCount();
    IMMDevice Item(uint index);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    nint Activate(in Guid iid, uint clsctx, nint activationParams);
    IPropertyStore OpenPropertyStore(uint access);
    string GetId();
    uint GetState();
}

[GeneratedComInterface]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal partial interface IPropertyStore
{
    uint GetCount();
    PropertyKey GetAt(uint index);
    void GetValue(in PropertyKey key, out PropVariant value);
    void SetValue(in PropertyKey key, in PropVariant value);
    void Commit();
}

[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged(nint deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded(nint deviceId);
    [PreserveSig] int OnDeviceRemoved(nint deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, nint deviceId);
    [PreserveSig] int OnPropertyValueChanged(nint deviceId, PropertyKey key);
}

[GeneratedComInterface]
[Guid("BFA971F1-4D5E-40BB-935E-967039BFBEE4")]
internal partial interface IAudioSessionManager
{
    nint GetAudioSessionControl(nint sessionGuid, uint streamFlags);
    nint GetSimpleAudioVolume(nint sessionGuid, uint streamFlags);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
internal partial interface IAudioSessionManager2 : IAudioSessionManager
{
    IAudioSessionEnumerator GetSessionEnumerator();
    void RegisterSessionNotification(IAudioSessionNotification notification);
    void UnregisterSessionNotification(IAudioSessionNotification notification);
    void RegisterDuckNotification(string sessionId, nint notification);
    void UnregisterDuckNotification(nint notification);
}

[GeneratedComInterface]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
internal partial interface IAudioSessionEnumerator
{
    int GetCount();
    IAudioSessionControl GetSession(int index);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
internal partial interface IAudioSessionControl
{
    SessionState GetState();
    string GetDisplayName();
    void SetDisplayName(string name, in Guid context);
    string GetIconPath();
    void SetIconPath(string path, in Guid context);
    Guid GetGroupingParam();
    void SetGroupingParam(in Guid param, in Guid context);
    void RegisterAudioSessionNotification(IAudioSessionEvents events);
    void UnregisterAudioSessionNotification(IAudioSessionEvents events);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d")]
internal partial interface IAudioSessionControl2 : IAudioSessionControl
{
    string GetSessionIdentifier();
    string GetSessionInstanceIdentifier();
    uint GetProcessId();

    /// <summary>S_OK (0) for Windows' own sounds, S_FALSE (1) otherwise.</summary>
    [PreserveSig] int IsSystemSoundsSession();

    void SetDuckingPreference(int optOut);
}

[GeneratedComInterface]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
internal partial interface IAudioSessionEvents
{
    [PreserveSig] int OnDisplayNameChanged(nint name, nint context);
    [PreserveSig] int OnIconPathChanged(nint path, nint context);
    [PreserveSig] int OnSimpleVolumeChanged(float volume, int muted, nint context);
    [PreserveSig] int OnChannelVolumeChanged(uint channels, nint volumes, uint changed, nint context);
    [PreserveSig] int OnGroupingParamChanged(nint param, nint context);
    [PreserveSig] int OnStateChanged(SessionState state);
    [PreserveSig] int OnSessionDisconnected(int reason);
}

[GeneratedComInterface]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
internal partial interface IAudioSessionNotification
{
    [PreserveSig] int OnSessionCreated(nint session);
}

[GeneratedComInterface]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
internal partial interface ISimpleAudioVolume
{
    void SetMasterVolume(float level, in Guid context);
    float GetMasterVolume();
    void SetMute(int mute, in Guid context);
    int GetMute();
}

[GeneratedComInterface]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
internal partial interface IAudioMeterInformation
{
    float GetPeakValue();
    uint GetMeteringChannelCount();
    void GetChannelsPeakValues(uint count, nint peaks);
    uint QueryHardwareSupport();
}

[GeneratedComInterface]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal partial interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    uint GetChannelCount();
    void SetMasterVolumeLevel(float levelDb, in Guid context);
    void SetMasterVolumeLevelScalar(float level, in Guid context);
    float GetMasterVolumeLevel();
    float GetMasterVolumeLevelScalar();
    void SetChannelVolumeLevel(uint channel, float levelDb, in Guid context);
    void SetChannelVolumeLevelScalar(uint channel, float level, in Guid context);
    float GetChannelVolumeLevel(uint channel);
    float GetChannelVolumeLevelScalar(uint channel);
    void SetMute(int mute, in Guid context);
    int GetMute();
    void GetVolumeStepInfo(out uint step, out uint stepCount);
    void VolumeStepUp(in Guid context);
    void VolumeStepDown(in Guid context);
    uint QueryHardwareSupport();
    void GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
}

[GeneratedComInterface]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal partial interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(nint data);
}

internal static partial class AudioNative
{
    [LibraryImport("ole32.dll")] internal static partial int CoInitializeEx(nint reserved, uint coInit);
    [LibraryImport("ole32.dll")] internal static partial void CoUninitialize();
    [LibraryImport("ole32.dll")] internal static partial int CoCreateInstance(in Guid clsid, nint outer, uint clsctx, in Guid iid, out nint instance);
    [LibraryImport("ole32.dll")] internal static partial int PropVariantClear(ref PropVariant value);

    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial nint OpenProcess(uint access, int inherit, uint processId);
    [LibraryImport("kernel32.dll")] internal static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    internal static unsafe partial int QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static unsafe partial uint QueryDosDevice(string deviceName, char* target, uint max);

    public const uint ProcessQueryLimitedInformation = 0x1000;
}
