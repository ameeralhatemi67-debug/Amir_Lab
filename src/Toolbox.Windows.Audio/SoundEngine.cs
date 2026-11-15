using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Toolbox.Core.Diagnostics;
using Toolbox.Core.Sound;

namespace Toolbox.Windows.Audio;

/// <summary>
/// Imp Studio's engine: the outputs, the main output's volume, and every app's sound on it, with
/// their levels and mutes, kept up to date by Windows' own notifications (nothing is polled except
/// the level meters, and only while something shows them).
/// </summary>
/// <remarks>
/// Everything runs on one thread of its own in the multithreaded COM apartment: Windows only sends
/// session notifications to such a thread, and no COM call is ever made from the UI. Windows' callbacks
/// arrive on its threads; they only queue work here, because calling back into Core Audio from inside
/// a callback can deadlock.
/// </remarks>
public sealed partial class SoundEngine : ISoundControl, IDisposable
{
    /// <summary>Marks the volume changes Imp Studio makes, so they can be told from the user's own.</summary>
    public static readonly Guid Context = new("7c6b3c2e-5f0a-4b8e-9d55-3a9e7f6d2b41");

    internal static readonly StrategyBasedComWrappers Wrappers = new();

    private readonly SoundMemory? _memory;
    private readonly Func<bool> _remember;
    private readonly Thread _thread;
    private readonly BlockingCollection<Action> _work = [];
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentDictionary<string, float> _pendingVolumes = new(StringComparer.Ordinal);
    private readonly object _levelGate = new();
    private float? _pendingMaster;
    private int _volumesQueued, _masterQueued, _levelsQueued, _watchers;
    private Timer? _levelTimer;
    private volatile SoundSnapshot _current = SoundSnapshot.Empty;
    private volatile bool _disposed;

    // Owned by the engine thread.
    private IMMDeviceEnumerator? _enumerator;
    private DeviceEvents? _deviceEvents;
    private Output? _output;
    private List<SoundDevice> _devices = [];
    private bool _canSwitch;

    /// <param name="memory">Where each app's level is remembered (null: nothing is remembered).</param>
    /// <param name="remember">Whether to set an app back to its remembered level when it starts a sound.</param>
    public SoundEngine(SoundMemory? memory = null, Func<bool>? remember = null)
    {
        _memory = memory;
        _remember = remember ?? (() => memory is not null);
        _thread = new Thread(Run) { IsBackground = true, Name = "Imp Studio sound" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public SoundSnapshot Current => _current;

    public event Action<SoundSnapshot>? Changed;

    public event Action<IReadOnlyDictionary<string, float>>? Levels;

    /// <summary>Waits until the first look at the outputs and apps is done.</summary>
    public bool WaitReady(TimeSpan timeout) => _ready.Wait(timeout);

    // ---- what can be set (any thread) ------------------------------------------------------------

    public void SetAppVolume(string appKey, float volume)
    {
        _pendingVolumes[appKey] = Math.Clamp(volume, 0f, 1f);
        if (Interlocked.Exchange(ref _volumesQueued, 1) == 0) Post(ApplyVolumes);
    }

    public void SetAppMute(string appKey, bool muted) => Post(() =>
    {
        if (_output is not { } o) return;
        foreach (var s in o.Sessions.Values.Where(s => s.Key == appKey))
        {
            Try(() => s.Volume.SetMute(muted ? 1 : 0, Context), "muting " + s.Name);
            s.Muted = muted;
            Remember(s);
        }
        Publish();
    });

    public void SetMaster(float volume)
    {
        lock (_levelGate) _pendingMaster = Math.Clamp(volume, 0f, 1f);
        if (Interlocked.Exchange(ref _masterQueued, 1) == 0) Post(ApplyMaster);
    }

    public void SetMasterMute(bool muted) => Post(() =>
    {
        if (_output is not { } o) return;
        Try(() => o.Endpoint.SetMute(muted ? 1 : 0, Context), "muting the output");
        o.Muted = muted;
        Publish();
    });

    public void SetOutput(string deviceId) => Post(() =>
    {
        if (_output?.Id == deviceId) return;
        if (PolicyConfig.SetDefault(deviceId)) Log.Info($"Imp Studio made {Name(deviceId)} the main output.");
        // Windows then reports the new default, which binds to it.
    });

    public IDisposable WatchLevels()
    {
        lock (_levelGate)
        {
            if (_watchers++ == 0)
                _levelTimer = new Timer(_ => { if (Interlocked.Exchange(ref _levelsQueued, 1) == 0) Post(ReadLevels); }, null, 0, 33);
        }
        return new Watch(this);
    }

    /// <summary>One reading of every app's level, for the sound check (waits for the engine thread).</summary>
    public IReadOnlyDictionary<string, float> SampleLevels(TimeSpan timeout)
    {
        var done = new TaskCompletionSource<IReadOnlyDictionary<string, float>>();
        Post(() => done.TrySetResult(Peaks()));
        return done.Task.Wait(timeout) ? done.Task.Result : new Dictionary<string, float>();
    }

    private sealed class Watch(SoundEngine engine) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return;
            lock (engine._levelGate)
            {
                if (--engine._watchers == 0)
                {
                    engine._levelTimer?.Dispose();
                    engine._levelTimer = null;
                }
            }
        }
    }

    // ---- the engine thread -----------------------------------------------------------------------

    private void Post(Action action)
    {
        if (_disposed) return;
        try { _work.Add(action); }
        catch (InvalidOperationException) { /* shutting down */ }
    }

    private void Run()
    {
        AudioNative.CoInitializeEx(0, 0); // COINIT_MULTITHREADED
        try
        {
            Start();
        }
        catch (Exception ex)
        {
            Log.Error("Imp Studio could not reach Windows' sound system", ex);
        }
        finally
        {
            _ready.Set();
        }
        foreach (var action in _work.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or InvalidOperationException)
            {
                Log.Warn($"Imp Studio: {ex.Message}");
                if (ex is COMException { HResult: unchecked((int)0x88890004) }) Post(Rebind); // AUDCLNT_E_DEVICE_INVALIDATED
            }
        }
        Shutdown();
        AudioNative.CoUninitialize();
    }

    private void Start()
    {
        int hr = AudioNative.CoCreateInstance(AudioIds.MMDeviceEnumerator, 0, AudioIds.ClsctxInproc, AudioIds.IMMDeviceEnumerator, out var ptr);
        Marshal.ThrowExceptionForHR(hr);
        _enumerator = (IMMDeviceEnumerator)Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);
        Marshal.Release(ptr);
        _canSwitch = PolicyConfig.IsAvailable();
        _deviceEvents = new DeviceEvents(this);
        _enumerator.RegisterEndpointNotificationCallback(_deviceEvents);
        LoadDevices();
        Bind();
        Publish();
        Log.Info($"Imp Studio sees {_devices.Count} output(s) and {_output?.Sessions.Count ?? 0} app sound(s) on {Name(_output?.Id)}.");
    }

    private void Shutdown()
    {
        Unbind();
        if (_enumerator is not null && _deviceEvents is not null)
            Try(() => _enumerator.UnregisterEndpointNotificationCallback(_deviceEvents), "letting go of the outputs");
        _deviceEvents = null;
        _enumerator = null;
    }

    private void LoadDevices()
    {
        if (_enumerator is null) return;
        var list = new List<SoundDevice>();
        var collection = _enumerator.EnumAudioEndpoints(AudioIds.Render, AudioIds.DeviceStateActive);
        uint count = collection.GetCount();
        for (uint i = 0; i < count; i++)
        {
            try
            {
                var device = collection.Item(i);
                list.Add(Describe(device));
            }
            catch (COMException ex)
            {
                Log.Warn($"Imp Studio skipped an output: {ex.Message}");
            }
        }
        _devices = list;
    }

    private static SoundDevice Describe(IMMDevice device)
    {
        var id = device.GetId();
        string name = id;
        int form = 10;
        var store = device.OpenPropertyStore(0); // STGM_READ
        if (Read(store, AudioIds.FriendlyName) is { } pv)
        {
            name = pv.AsString() ?? name;
            Clear(pv);
        }
        if (Read(store, AudioIds.FormFactor) is { } ff)
        {
            form = (int)(ff.AsUInt() ?? 10);
            Clear(ff);
        }
        return new SoundDevice(id, name, SoundNames.KindOf(form));

        static PropVariant? Read(IPropertyStore store, PropertyKey key)
        {
            try
            {
                store.GetValue(key, out var value);
                return value;
            }
            catch (COMException) { return null; }
        }

        static void Clear(PropVariant value) => AudioNative.PropVariantClear(ref value);
    }

    private void Bind()
    {
        Unbind();
        if (_enumerator is null) return;
        IMMDevice device;
        try { device = _enumerator.GetDefaultAudioEndpoint(AudioIds.Render, AudioIds.Multimedia); }
        catch (COMException) { return; } // no output at all (everything unplugged)
        var output = new Output(device.GetId(), device);
        var endpointPtr = device.Activate(AudioIds.IAudioEndpointVolume, AudioIds.ClsctxAll, 0);
        output.Endpoint = (IAudioEndpointVolume)Wrappers.GetOrCreateObjectForComInstance(endpointPtr, CreateObjectFlags.None);
        Marshal.Release(endpointPtr);
        output.Master = output.Endpoint.GetMasterVolumeLevelScalar();
        output.Muted = output.Endpoint.GetMute() != 0;
        output.EndpointEvents = new EndpointEvents(this, output);
        output.Endpoint.RegisterControlChangeNotify(output.EndpointEvents);

        var managerPtr = device.Activate(AudioIds.IAudioSessionManager2, AudioIds.ClsctxAll, 0);
        output.Manager = (IAudioSessionManager2)Wrappers.GetOrCreateObjectForComInstance(managerPtr, CreateObjectFlags.None);
        Marshal.Release(managerPtr);
        _output = output;
        // Windows asks for the list to be read once before it sends notifications of new sessions.
        var sessions = output.Manager.GetSessionEnumerator();
        int n = sessions.GetCount();
        for (int i = 0; i < n; i++)
        {
            try { Add(output, sessions.GetSession(i), started: false); }
            catch (Exception ex) when (ex is COMException or InvalidCastException) { Log.Warn($"Imp Studio skipped a sound: {ex.Message}"); }
        }
        output.SessionEvents = new SessionCreated(this, output);
        output.Manager.RegisterSessionNotification(output.SessionEvents);
    }

    private void Unbind()
    {
        if (_output is not { } o) return;
        _output = null;
        o.Gone = true;
        foreach (var s in o.Sessions.Values) Release(s);
        o.Sessions.Clear();
        if (o.Manager is not null && o.SessionEvents is not null) Try(() => o.Manager.UnregisterSessionNotification(o.SessionEvents), "letting go of the apps");
        if (o.Endpoint is not null && o.EndpointEvents is not null) Try(() => o.Endpoint.UnregisterControlChangeNotify(o.EndpointEvents), "letting go of the output");
    }

    private void Rebind()
    {
        LoadDevices();
        Bind();
        Publish();
    }

    /// <summary>Starts following a session; <paramref name="started"/> when it is new (the app just started a sound).</summary>
    private void Add(Output output, IAudioSessionControl control, bool started)
    {
        if (output.Gone || control is not IAudioSessionControl2 c2) return;
        var instance = c2.GetSessionInstanceIdentifier();
        if (output.Sessions.ContainsKey(instance)) return;
        var state = c2.GetState();
        if (state == SessionState.Expired) return;
        var (key, devicePath, isSystem) = SoundApps.FromSessionIdentifier(c2.GetSessionIdentifier());
        isSystem |= c2.IsSystemSoundsSession() == 0;
        if (isSystem) key = SoundApps.SystemKey;
        int pid = (int)c2.GetProcessId();
        var (name, path) = isSystem ? ("", null) : ProcessNames.Resolve(pid, devicePath);
        if (key.Length == 0 && path is not null) key = SoundApps.KeyOf(path);
        var volume = (ISimpleAudioVolume)c2;
        var session = new Session(instance, key, pid, name, path, isSystem, c2, volume, (IAudioMeterInformation)c2)
        {
            State = state,
            Level = volume.GetMasterVolume(),
            Muted = volume.GetMute() != 0,
        };
        session.Events = new SessionEvents(this, output, session);
        c2.RegisterAudioSessionNotification(session.Events);
        output.Sessions[instance] = session;
        if (state == SessionState.Active) _memory?.Heard(key, name, path, DateTimeOffset.UtcNow);

        // The app is back: put it at the level the user gave it last time on this output.
        if (started && _remember() && _memory?.Recall(key, output.Id) is { } level
            && (Math.Abs(level.Volume - session.Level) > 0.005f || level.Muted != session.Muted))
        {
            Try(() => volume.SetMasterVolume(level.Volume, Context), "setting " + name);
            Try(() => volume.SetMute(level.Muted ? 1 : 0, Context), "setting " + name);
            session.Level = level.Volume;
            session.Muted = level.Muted;
            Log.Info($"Imp Studio set {(name.Length > 0 ? name : key)} back to {SoundApps.Percent(level.Volume)}%{(level.Muted ? " (muted)" : "")}.");
        }
    }

    private static void Release(Session s)
    {
        if (s.Events is null) return;
        Try(() => s.Control.UnregisterAudioSessionNotification(s.Events), "letting go of " + s.Name);
        s.Events = null;
    }

    private void ApplyVolumes()
    {
        Interlocked.Exchange(ref _volumesQueued, 0);
        if (_output is not { } o) return;
        foreach (var key in _pendingVolumes.Keys.ToList())
        {
            if (!_pendingVolumes.TryRemove(key, out var value)) continue;
            foreach (var s in o.Sessions.Values.Where(s => s.Key == key))
            {
                Try(() => s.Volume.SetMasterVolume(value, Context), "setting " + s.Name);
                s.Level = value;
                Remember(s);
            }
        }
        Publish();
    }

    private void ApplyMaster()
    {
        Interlocked.Exchange(ref _masterQueued, 0);
        float? value;
        lock (_levelGate) { value = _pendingMaster; _pendingMaster = null; }
        if (value is not { } v || _output is not { } o) return;
        Try(() => o.Endpoint.SetMasterVolumeLevelScalar(v, Context), "setting the output's volume");
        o.Master = v;
        Publish();
    }

    private void Remember(Session s)
    {
        if (_output is { } o && s.Key.Length > 0) _memory?.Remember(s.Key, s.Name, s.Path, o.Id, s.Level, s.Muted);
    }

    private void ReadLevels()
    {
        Interlocked.Exchange(ref _levelsQueued, 0);
        if (_watchers == 0) return;
        Levels?.Invoke(Peaks());
    }

    private Dictionary<string, float> Peaks()
    {
        var peaks = new Dictionary<string, float>(StringComparer.Ordinal);
        if (_output is not { } o) return peaks;
        foreach (var s in o.Sessions.Values)
        {
            float p = 0;
            if (s.State == SessionState.Active && !s.Muted)
            {
                try { p = s.Meter.GetPeakValue(); }
                catch (COMException) { p = 0; }
            }
            peaks[s.Key] = peaks.TryGetValue(s.Key, out var other) ? Math.Max(other, p) : p;
        }
        return peaks;
    }

    private void Publish()
    {
        var o = _output;
        var sessions = o is null ? [] : o.Sessions.Values
            .Select(s => new SoundSession(s.InstanceId, s.Key, s.ProcessId, s.Name, s.Path, s.IsSystem, s.State == SessionState.Active, s.Level, s.Muted))
            .ToList();
        var snap = new SoundSnapshot(_devices, o?.Id, o?.Master ?? 0, o?.Muted ?? false, sessions, _canSwitch);
        _current = snap;
        try { Changed?.Invoke(snap); }
        catch (Exception ex) { Log.Warn($"Imp Studio: a window failed to update: {ex.Message}"); }
    }

    private string Name(string? id) => _devices.FirstOrDefault(d => d.Id == id)?.Label ?? id ?? "no output";

    private static void Try(Action action, string what)
    {
        try { action(); }
        catch (COMException ex) { Log.Warn($"Imp Studio failed {what}: 0x{ex.HResult:X8}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_levelGate)
        {
            _levelTimer?.Dispose();
            _levelTimer = null;
            _watchers = 0;
        }
        _work.CompleteAdding();
        _disposed = true;
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(3));
        _memory?.Flush();
    }

    // ---- what the thread keeps ------------------------------------------------------------------

    private sealed class Output(string id, IMMDevice device)
    {
        public string Id { get; } = id;
        public IMMDevice Device { get; } = device;
        public IAudioEndpointVolume Endpoint { get; set; } = null!;
        public IAudioSessionManager2? Manager { get; set; }
        public EndpointEvents? EndpointEvents { get; set; }
        public SessionCreated? SessionEvents { get; set; }
        public Dictionary<string, Session> Sessions { get; } = new(StringComparer.Ordinal);
        public float Master { get; set; }
        public bool Muted { get; set; }
        public bool Gone { get; set; }
    }

    private sealed class Session(string instance, string key, int pid, string name, string? path, bool isSystem,
        IAudioSessionControl2 control, ISimpleAudioVolume volume, IAudioMeterInformation meter)
    {
        public string InstanceId { get; } = instance;
        public string Key { get; } = key;
        public int ProcessId { get; } = pid;
        public string Name { get; } = name;
        public string? Path { get; } = path;
        public bool IsSystem { get; } = isSystem;
        public IAudioSessionControl2 Control { get; } = control;
        public ISimpleAudioVolume Volume { get; } = volume;
        public IAudioMeterInformation Meter { get; } = meter;
        public SessionEvents? Events { get; set; }
        public SessionState State { get; set; }
        public float Level { get; set; }
        public bool Muted { get; set; }
    }

    // ---- Windows' callbacks (on Windows' threads: they only queue work) ---------------------------

    [GeneratedComClass]
    internal sealed partial class DeviceEvents(SoundEngine engine) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(nint deviceId, uint newState) { engine.Post(engine.Refresh); return 0; }

        public int OnDeviceAdded(nint deviceId) { engine.Post(engine.Refresh); return 0; }

        public int OnDeviceRemoved(nint deviceId) { engine.Post(engine.Refresh); return 0; }

        public int OnDefaultDeviceChanged(int flow, int role, nint deviceId)
        {
            if (flow == AudioIds.Render && role == AudioIds.Multimedia) engine.Post(engine.Rebind);
            return 0;
        }

        public int OnPropertyValueChanged(nint deviceId, PropertyKey key) => 0;
    }

    private void Refresh()
    {
        LoadDevices();
        Publish();
    }

    [GeneratedComClass]
    internal sealed partial class EndpointEvents(SoundEngine engine, object output) : IAudioEndpointVolumeCallback
    {
        public unsafe int OnNotify(nint data)
        {
            if (data == 0) return 0;
            var n = *(VolumeNotification*)data;
            var o = (Output)output;
            engine.Post(() =>
            {
                if (o.Gone) return;
                o.Master = n.Master;
                o.Muted = n.Muted != 0;
                engine.Publish();
            });
            return 0;
        }
    }

    [GeneratedComClass]
    internal sealed partial class SessionCreated(SoundEngine engine, object output) : IAudioSessionNotification
    {
        public int OnSessionCreated(nint session)
        {
            if (session == 0) return 0;
            // Take our own reference now: the pointer is only lent for the call.
            var control = (IAudioSessionControl)Wrappers.GetOrCreateObjectForComInstance(session, CreateObjectFlags.None);
            var o = (Output)output;
            engine.Post(() =>
            {
                if (o.Gone) return;
                engine.Add(o, control, started: true);
                engine.Publish();
            });
            return 0;
        }
    }

    [GeneratedComClass]
    internal sealed partial class SessionEvents(SoundEngine engine, object output, object session) : IAudioSessionEvents
    {
        public int OnDisplayNameChanged(nint name, nint context) => 0;

        public int OnIconPathChanged(nint path, nint context) => 0;

        public unsafe int OnSimpleVolumeChanged(float volume, int muted, nint context)
        {
            bool ours = context != 0 && *(Guid*)context == Context;
            var o = (Output)output;
            var s = (Session)session;
            engine.Post(() =>
            {
                if (o.Gone) return;
                s.Level = volume;
                s.Muted = muted != 0;
                if (!ours) engine.Remember(s); // set in Windows' mixer or by the app: that is the user's level now
                engine.Publish();
            });
            return 0;
        }

        public int OnChannelVolumeChanged(uint channels, nint volumes, uint changed, nint context) => 0;

        public int OnGroupingParamChanged(nint param, nint context) => 0;

        public int OnStateChanged(SessionState state)
        {
            var o = (Output)output;
            var s = (Session)session;
            engine.Post(() =>
            {
                if (o.Gone) return;
                s.State = state;
                if (state == SessionState.Expired)
                {
                    Release(s);
                    o.Sessions.Remove(s.InstanceId);
                }
                else if (state == SessionState.Active)
                {
                    engine._memory?.Heard(s.Key, s.Name, s.Path, DateTimeOffset.UtcNow);
                }
                engine.Publish();
            });
            return 0;
        }

        public int OnSessionDisconnected(int reason)
        {
            var o = (Output)output;
            var s = (Session)session;
            engine.Post(() =>
            {
                if (o.Gone) return;
                Release(s);
                o.Sessions.Remove(s.InstanceId);
                engine.Publish();
            });
            return 0;
        }
    }
}
