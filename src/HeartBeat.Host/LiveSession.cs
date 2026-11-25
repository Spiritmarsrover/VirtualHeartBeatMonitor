using HeartBeat.Core;
using HeartBeat.Core.Breathing;
using HeartBeat.Core.Heart;
using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.App;

/// <summary>
/// Connects SteamVR to the engine: finds devices, opens their raw IMU streams, maps each
/// device's clock onto the PC clock, and runs the engine on the IMU reader thread.
/// Devices without raw IMU (Steam Frame and other inside-out headsets) fall back to the pose's
/// angular velocity, polled at 1 kHz and fed to the engine in place of the gyro.
/// Call <see cref="Poll"/> regularly from the main thread (events, presence, new devices).
/// </summary>
public sealed class LiveSession : IDisposable
{
    public sealed class DeviceEntry
    {
        public required DeviceInfo Info;
        public required DeviceRole Role;
        public required string Id;
        public volatile bool UsedForHeartRate;
        public ImuStream? Stream;
        /// <summary>No raw IMU: the pose's angular velocity is used instead.</summary>
        public volatile bool UsesPose;
        public string Source => Stream != null ? "raw IMU" : UsesPose ? "pose" : "none";
        public string Status = "";
        public double ClockOffset = double.NaN; // host time - device sample time
        public long Samples;
        public double LastHostTime = double.NaN; // when the last sample arrived (host clock)
        public long Reopens;
        /// <summary>Raw IMU paths tried and SteamVR's answer, for diagnostics.</summary>
        public List<string> ImuAttempts = new();
    }

    private readonly VrRuntime _vr;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, DeviceEntry> _devices = new();
    private readonly object _devicesGate = new();
    private readonly Dictionary<ImuStream, DeviceEntry> _byStream = new();
    private ImuReader? _reader;
    private PosePoller? _posePoller;
    private Dictionary<uint, DeviceEntry> _poseByIndex = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(DeviceEntry entry, double t, Vec3 gyro)> _poseQueue = new();
    /// <summary>Pose polling rate for devices without raw IMU (Frame HMD updates ~290/s, controllers ~195/s).</summary>
    private const double PosePollHz = 1000;
    private double _latest = double.NegativeInfinity;
    private double _nextRefresh;

    public MonitorEngine Engine { get; }
    public double StartedAt { get; } = HostClock.Now;
    /// <summary>A copy of the device list, safe to use from any thread.</summary>
    public List<DeviceEntry> GetDevices() { lock (_devicesGate) return _devices.Values.ToList(); }

    // Latest outputs for the UI (written on the reader thread, read on the main thread).
    public volatile MonitorSnapshot? LatestSnapshot;
    public BeatEvent? LastBeat { get; private set; }
    public double LastBeatHostTime { get; private set; } = double.NegativeInfinity;
    public long BeatCount { get; private set; }
    public long MeasuredBeatCount { get; private set; }

    /// <summary>Every raw IMU sample (reader thread), used by the diagnostics recorder.</summary>
    public event Action<DeviceEntry, double, ImuSample_t>? RawImu;

    /// <summary>Every new pose of a pose-fallback device (pose thread).</summary>
    public event Action<DeviceEntry, PoseSample>? RawPose;

    /// <summary>A message that replaces the status line everywhere (e.g. diagnostics progress).</summary>
    public volatile string? Notice;

    public VrRuntime Runtime => _vr;

    /// <summary>Raised on the reader thread.</summary>
    public event Action<MonitorSnapshot>? SnapshotReady;
    public event Action<BeatEvent>? Beat;
    public event Action<BreathingState>? Breathing;

    public LiveSession(VrRuntime vr, AppSettings settings)
    {
        _vr = vr;
        _settings = settings;
        Engine = new MonitorEngine(new EngineSettings { MotionThresholdDps = settings.Detection.MotionThresholdDps });
        Engine.Fusion.ConfidentThreshold = settings.Detection.DisplayThreshold;
        Engine.Beat += b =>
        {
            // Predicted beats (filled in from the rate when no clear peak was found) can be turned
            // off, so that only beats actually detected reach OSC, vibration and the heart animation.
            if (!b.Measured && !_settings.Beats.SendPredicted) return;
            LastBeat = b;
            LastBeatHostTime = HostClock.Now;
            BeatCount++;
            if (b.Measured) MeasuredBeatCount++;
            Beat?.Invoke(b);
        };
        Engine.BreathingUpdated += (_, state) => Breathing?.Invoke(state);
        RefreshDevices();
    }

    /// <summary>Main-thread housekeeping. Returns false when SteamVR is shutting down.</summary>
    public bool Poll()
    {
        if (!_vr.PumpEvents()) return false;


        if (HostClock.Now >= _nextRefresh) RefreshDevices();
        return true;
    }

    /// <summary>Picks up devices that connected since the last check and (re)starts IMU reading.</summary>
    private void RefreshDevices()
    {
        _nextRefresh = HostClock.Now + 5;
        var found = DeviceInfo.Enumerate(_vr.System).Where(d => d.IsBodyDevice).ToList();
        var added = new List<DeviceEntry>();
        foreach (var info in found)
        {
            string id = $"{info.Kind}:{info.Serial}";
            if (_devices.ContainsKey(id)) continue;
            var role = Classify(info);
            bool use = _settings.Devices.UseDevice.TryGetValue(info.Serial, out bool chosen) ? chosen : DefaultUse(role);
            var entry = new DeviceEntry { Info = info, Role = role, Id = id, UsedForHeartRate = use };
            lock (_devicesGate) _devices[id] = entry;
            added.Add(entry);
        }

        // Streams that went quiet (device slept, driver restarted the stream) get reopened.
        var silent = new List<DeviceEntry>();
        lock (_devicesGate)
            silent.AddRange(_devices.Values.Where(d => d.Stream != null && !double.IsNaN(d.LastHostTime) && HostClock.Now - d.LastHostTime > 5));
        if (added.Count == 0 && silent.Count == 0 && _reader != null) return;

        _reader?.Stop(); // engine is only touched by the reader thread; stop it while changing devices
        foreach (var entry in silent)
        {
            _byStream.Remove(entry.Stream!);
            entry.Stream!.Dispose();
            entry.Stream = null;
            entry.LastHostTime = double.NaN;
            entry.Reopens++;
            OpenStream(entry);
        }
        bool posesChanged = false;
        foreach (var entry in added)
        {
            if (OpenStream(entry))
                Engine.AddDevice(entry.Id, entry.Role, useForHeartRate: entry.UsedForHeartRate, useForBreathing: entry.Role == DeviceRole.Chest);
            else
            {
                // Pose fallback: rotation only (breathing needs the accelerometer's gravity tilt).
                entry.UsesPose = true;
                entry.Status = "";
                // Held controllers carry the heartbeat well (Frame TEST 3), resting ones only add noise
                // (TEST 1), so pose-based hand devices sit out while they lie still ("set down").
                Engine.AddDevice(entry.Id, entry.Role, useForHeartRate: entry.UsedForHeartRate, useForBreathing: false,
                    restBelowDps: entry.Role == DeviceRole.Hand ? Engine.Settings.PoseHandRestDps : double.NaN);
                posesChanged = true;
            }
        }
        if (posesChanged) RestartPosePoller();
        _reader = new ImuReader(_byStream.Keys.ToList(), OnImu) { Tick = OnTick };
        _reader.Start();
    }

    private void RestartPosePoller()
    {
        _posePoller?.Stop();
        List<DeviceEntry> poseDevices;
        lock (_devicesGate) poseDevices = _devices.Values.Where(d => d.UsesPose).ToList();
        _poseByIndex = poseDevices.ToDictionary(d => d.Info.Index);
        if (poseDevices.Count == 0) { _posePoller = null; return; }
        var byIndex = _poseByIndex;
        _posePoller = new PosePoller(_vr.System, byIndex.Keys, PosePollHz, (in PoseSample p) =>
        {
            // Velocities only change when the driver delivers a new update; repeats are extrapolation.
            if (!p.VelocityChanged || !p.Valid || !byIndex.TryGetValue(p.Device, out var entry)) return;
            var w = p.AngularVelocity;
            _poseQueue.Enqueue((entry, p.HostTime, new Vec3(w.v0, w.v1, w.v2)));
            RawPose?.Invoke(entry, p);
        });
        _posePoller.Start();
    }

    private bool OpenStream(DeviceEntry entry)
    {
        if (_vr.IOBuffer == null) { entry.Status = "raw IMU not available"; return false; }
        var (stream, attempts) = ImuStream.TryOpen(_vr.IOBuffer, entry.Info);
        entry.ImuAttempts = attempts.Select(a => $"{a.Path} -> {a.Result}").ToList();
        if (stream == null) { entry.Status = entry.Reopens > 0 ? "no data (reconnecting)" : "raw IMU not available"; return false; }
        entry.Status = "";
        entry.Stream = stream;
        _byStream[stream] = entry;
        return true;
    }

    private DeviceRole Classify(DeviceInfo d) => d.Class switch
    {
        ETrackedDeviceClass.HMD => DeviceRole.Head,
        ETrackedDeviceClass.Controller => DeviceRole.Hand,
        ETrackedDeviceClass.GenericTracker when _settings.Devices.ChestTrackerSerials.Contains(d.Serial)
            || d.ControllerType.Contains("chest", StringComparison.OrdinalIgnoreCase) => DeviceRole.Chest,
        _ => DeviceRole.Other,
    };

    /// <summary>Used unless switched off: headset, controllers and chest trackers. Other trackers (hips, feet) start off.</summary>
    public static bool DefaultUse(DeviceRole role) => role != DeviceRole.Other;

    /// <summary>Switches a device in or out of the heart rate and beats, right away.</summary>
    public void SetDeviceUsed(string id, bool used)
    {
        DeviceEntry? entry;
        lock (_devicesGate) _devices.TryGetValue(id, out entry);
        if (entry == null) return;
        entry.UsedForHeartRate = used;
        Engine.SetUseForHeartRate(id, used);
    }

    // ---- Reader thread ------------------------------------------------------------------------

    private void OnImu(ImuStream stream, double hostTime, in ImuSample_t s)
    {
        var entry = _byStream[stream];
        // Device clocks have their own origin. Map them onto the PC clock with the smallest
        // observed (host - device) difference, which is the delivery delay at its best. A tiny
        // upward leak lets the estimate follow slow clock drift.
        double offset = hostTime - s.fSampleTime;
        entry.ClockOffset = double.IsNaN(entry.ClockOffset) ? offset : Math.Min(entry.ClockOffset + 1e-6, offset);
        double t = s.fSampleTime + entry.ClockOffset;
        entry.Samples++;
        entry.LastHostTime = hostTime;
        RawImu?.Invoke(entry, hostTime, s);

        Engine.Push(entry.Id, new ImuSample(t,
            new Vec3(s.vAccel.v0, s.vAccel.v1, s.vAccel.v2),
            new Vec3(s.vGyro.v0, s.vGyro.v1, s.vGyro.v2)));

        _latest = Math.Max(_latest, t);
    }

    // Updates run on the clock, not on incoming samples, so silence is noticed: devices that stop
    // sending are dropped and the number turns to "--" instead of freezing on the last value.
    private static readonly double HistorySeconds = GraphSettings.LongestSeconds; // the graph's longest window
    private readonly object _historyGate = new();
    private readonly Queue<(double t, double bpm)> _history = new();

    /// <summary>Heart rate over the last <paramref name="seconds"/> (host time, BPM; 0 while it showed "--").</summary>
    public List<(double t, double bpm)> GetHistory(double seconds)
    {
        lock (_historyGate) return _history.Where(p => HostClock.Now - p.t <= seconds + 1).ToList();
    }

    public int HistoryCount { get { lock (_historyGate) return _history.Count; } }

    private void OnTick()
    {
        // Pose-fallback samples are queued by the pose thread; the engine is only touched here.
        while (_poseQueue.TryDequeue(out var p))
        {
            p.entry.Samples++;
            p.entry.LastHostTime = p.t;
            Engine.Push(p.entry.Id, new ImuSample(p.t, default, p.gyro));
            _latest = Math.Max(_latest, p.t);
        }

        double now = Math.Max(_latest, HostClock.Now - 0.02);
        if (Engine.TryUpdate(now, out var snap))
        {
            lock (_historyGate)
            {
                _history.Enqueue((HostClock.Now, snap.Heart.Displayed ? snap.Heart.Bpm : 0));
                while (_history.Count > 0 && HostClock.Now - _history.Peek().t > HistorySeconds) _history.Dequeue();
            }
            LatestSnapshot = snap;
            SnapshotReady?.Invoke(snap);
        }
    }

    /// <summary>One short line for the panels and the console: what the measurement is doing right now.</summary>
    public string StatusText()
    {
        if (Notice is { } notice) return notice;
        var snap = LatestSnapshot;
        if (snap == null || HostClock.Now - StartedAt < 8) return "warming up, stay still";
        if (snap.Devices.Count > 0 && snap.Devices.All(d => !d.Receiving)) return "no data from devices";
        if (snap.UserMoving) return "moving, paused";
        if (!snap.Heart.Displayed) return "measuring, stay still";
        if (snap.Breathing is { Valid: true } b)
            return b.IsHold ? $"breath hold {b.HoldSeconds:0} s" : $"breathing {(double.IsNaN(b.RatePerMinute) ? "--" : b.RatePerMinute.ToString("0"))}/min";
        return $"{snap.Heart.DevicesUsed} device{(snap.Heart.DevicesUsed == 1 ? "" : "s")}";
    }

    public void Dispose()
    {
        _posePoller?.Stop();
        _reader?.Stop();
        foreach (var s in _byStream.Keys) s.Dispose();
    }
}
