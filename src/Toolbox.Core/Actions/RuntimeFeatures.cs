using System.Collections.Concurrent;

namespace Toolbox.Core.Actions;

/// <summary>
/// Features whose availability is only known at runtime (codecs present on this machine,
/// bundled engines). Engines register what they verified; the UI hides or disables the rest.
/// </summary>
public static class RuntimeFeatures
{
    public const string AvifEncode = "avif-encode";
    public const string HeicDecode = "heic-decode";
    public const string Ocr = "ocr";

    private static readonly ConcurrentDictionary<string, (bool Available, string Detail)> State = new(StringComparer.OrdinalIgnoreCase);

    public static void Set(string feature, bool available, string detail) => State[feature] = (available, detail);

    public static bool IsAvailable(string feature) => State.TryGetValue(feature, out var s) && s.Available;

    public static string Describe(string feature) =>
        State.TryGetValue(feature, out var s) ? s.Detail : "Not checked on this computer yet.";

    public static IReadOnlyDictionary<string, (bool Available, string Detail)> Snapshot() =>
        new Dictionary<string, (bool, string)>(State, StringComparer.OrdinalIgnoreCase);
}
