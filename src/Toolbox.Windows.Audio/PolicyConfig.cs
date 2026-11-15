using System.Runtime.InteropServices;
using Toolbox.Core.Diagnostics;

namespace Toolbox.Windows.Audio;

/// <summary>
/// Makes an output Windows' main one. Windows has no public call for this; Settings, the quick
/// settings' output picker and tools such as EarTrumpet and SoundSwitch all use the same internal
/// interface (IPolicyConfig), unchanged since Windows 7. It is called through its function table
/// (SetDefaultEndpoint is its 11th method), and Imp Studio checks it can be created before it
/// offers the choice.
/// </summary>
internal static unsafe class PolicyConfig
{
    private static readonly Guid PolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");
    private static readonly Guid IPolicyConfig = new("f8679f50-850a-41cf-9c72-430f290290c8");
    private const int SetDefaultEndpointSlot = 3 + 10; // after IUnknown's three: GetMixFormat … SetPropertyValue, then SetDefaultEndpoint

    /// <summary>True when the interface can be created on this PC (the output picker is shown then).</summary>
    public static bool IsAvailable()
    {
        if (AudioNative.CoCreateInstance(PolicyConfigClient, 0, AudioIds.ClsctxInproc, IPolicyConfig, out var config) < 0 || config == 0) return false;
        Marshal.Release(config);
        return true;
    }

    /// <summary>Makes the output the default for apps and media (Windows' "console" and "multimedia" roles).</summary>
    public static bool SetDefault(string deviceId)
    {
        int hr = AudioNative.CoCreateInstance(PolicyConfigClient, 0, AudioIds.ClsctxInproc, IPolicyConfig, out var config);
        if (hr < 0 || config == 0)
        {
            Log.Warn($"Imp Studio can't switch outputs here (0x{hr:X8}).");
            return false;
        }
        try
        {
            var set = (delegate* unmanaged<nint, char*, int, int>)(*(nint**)config)[SetDefaultEndpointSlot];
            fixed (char* id = deviceId)
            {
                int a = set(config, id, AudioIds.Console);
                int b = set(config, id, AudioIds.Multimedia);
                if (a < 0 || b < 0) Log.Warn($"Imp Studio could not make {deviceId} the main output (0x{Math.Min(a, b):X8}).");
                return a >= 0 && b >= 0;
            }
        }
        finally
        {
            Marshal.Release(config);
        }
    }
}
