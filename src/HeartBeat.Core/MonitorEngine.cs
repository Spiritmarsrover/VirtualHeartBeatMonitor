using HeartBeat.Core.Breathing;
using HeartBeat.Core.Dsp;
using HeartBeat.Core.Heart;

namespace HeartBeat.Core;

public sealed class EngineSettings
{
    public double ProcessingRate { get; init; } = 200;
    public double EnvelopeRate { get; init; } = 50;
    public double BeatEnvelopeRate { get; init; } = 100;
    public double WindowSeconds { get; init; } = 12;
    public double UpdateIntervalSeconds { get; init; } = 1;
    public double MotionThresholdDps { get; init; } = 8;
    /// <summary>Shortest analysis window used right after settling; it grows back to WindowSeconds.</summary>
    public double MinWindowSeconds { get; init; } = 6;
    /// <summary>Largest share of a window that may be cut out because the device was moving.</summary>
    public double MaxMaskedFraction { get; init; } = 0.25;
    /// <summary>How long after moving a device's samples are still cut out (filters settling).</summary>
    public double MotionHangoverSeconds { get; init; } = 1.0;
    /// <summary>A device that has sent nothing for this long is treated as disconnected and its window is dropped.</summary>
    public double StaleSeconds { get; init; } = 1.0;
    /// <summary>
    /// Trust per body position, multiplied into each device's weight in the heart rate and the
    /// beat events. The head and chest move with the heartbeat most directly; hands also pick up
    /// grip and arm movement.
    /// </summary>
    public double HeadWeight { get; init; } = 2.0;
    public double ChestWeight { get; init; } = 3.0;
    public double HandWeight { get; init; } = 1.0;
    public double OtherWeight { get; init; } = 0.5;

    public double WeightFor(DeviceRole role) => role switch
    {
        DeviceRole.Head => HeadWeight,
        DeviceRole.Chest => ChestWeight,
        DeviceRole.Hand => HandWeight,
        _ => OtherWeight,
    };

    /// <summary>How long the beat path waits for slower devices before combining without them.</summary>
    public double BeatAlignWaitSeconds { get; init; } = 0.15;
}

public sealed record DeviceReport(
    string Id, DeviceRole Role, bool UsedForHeartRate, double PeakBpm, double Prominence, bool Still, double MotionDps,
    bool Receiving = true, bool MovingNow = false, double WindowSeconds = 0);

public sealed record MonitorSnapshot(
    double Time, HeartRateEstimate Heart, BreathingState? Breathing, IReadOnlyList<DeviceReport> Devices,
    bool UserMoving = false, bool UserPresent = true);

/// <summary>
/// The whole real-time pipeline. Push raw IMU samples per device (any rate, device-clock seconds),
/// then call <see cref="TryUpdate"/> with the current time; it produces a snapshot once per
/// update interval. The same code runs live and on recordings.
///
/// Two outputs for the heart:
///  - the number: FFT over the last 6–12 s (moving moments cut out), fused across still devices,
///    smoothed (<see cref="MonitorSnapshot.Heart"/>);
///  - beat events: single beats found in the signal, filled in from the FFT rate when no clear
///    peak shows up (<see cref="Beat"/>).
/// Both stop while the user is moving or confidence is low: the number reads "--" and no beat
/// events are raised.
/// </summary>
public sealed class MonitorEngine
{
    private const int BeatRingSize = 512;

    private sealed class Device
    {
        public required string Id;
        public required DeviceRole Role;
        public volatile bool UseForHeartRate;
        public required UniformResampler Resampler;
        public required VibrationEnvelope Envelope;
        public required SpectralHeartRate Spectrum;
        public required MotionGate Motion;
        public required BeatEnvelope BeatEnvelope;
        public BreathingEstimator? Breathing;
        public HeartRateSpectrum? Last;
        public double LastSampleTime = double.NegativeInfinity;
        public readonly double[] BeatRing = new double[BeatRingSize];
        public readonly long[] BeatRingIndex = Enumerable.Repeat(-1L, BeatRingSize).ToArray();
        public long BeatLatest = -1;
        public double BeatWeight;      // refreshed each update: 0 = not used for beats right now
        public bool Receiving;
        public bool MovingNow => Motion.Moving;
    }

    private readonly EngineSettings _settings;
    private readonly List<Device> _devices = new();
    private readonly BeatDetector _beats;
    private double _nextUpdate = double.NaN;
    private long _beatCursor = -1;
    private double _latestTime = double.NegativeInfinity;
    private HeartRateEstimate _heart;
    private bool _userMoving;

    public HeartRateFusion Fusion { get; } = new();

    /// <summary>
    /// Set false when the headset is not being worn (e.g. its proximity sensor). While false the
    /// number reads "--" and no beat events are sent.
    /// </summary>
    public bool UserPresent { get; set; } = true;
    public BeatDetector BeatDetector => _beats;
    public EngineSettings Settings => _settings;

    /// <summary>Per-sample breathing output (about 10 Hz) from each breathing device.</summary>
    public event Action<string, BreathingState>? BreathingUpdated;

    /// <summary>One event per heartbeat while the heart rate is shown and the user is still.</summary>
    public event Action<BeatEvent>? Beat;

    public MonitorEngine(EngineSettings? settings = null)
    {
        _settings = settings ?? new EngineSettings();
        _beats = new BeatDetector(_settings.BeatEnvelopeRate);
        _beats.Beat += e => Beat?.Invoke(e);
    }

    /// <param name="useForHeartRate">Whether this device feeds the heart rate and beat events.</param>
    /// <param name="useForBreathing">Whether breathing is estimated from this device (chest devices).</param>
    public void AddDevice(string id, DeviceRole role, bool? useForHeartRate = null, bool? useForBreathing = null)
    {
        var s = _settings;
        var device = new Device
        {
            Id = id,
            Role = role,
            UseForHeartRate = useForHeartRate ?? true,
            Resampler = new UniformResampler(s.ProcessingRate),
            Envelope = new VibrationEnvelope(s.ProcessingRate, s.EnvelopeRate),
            Spectrum = null!,
            Motion = new MotionGate(s.ProcessingRate, s.MotionThresholdDps, s.MotionHangoverSeconds),
            BeatEnvelope = new BeatEnvelope(s.ProcessingRate, s.BeatEnvelopeRate),
            Breathing = (useForBreathing ?? role == DeviceRole.Chest) ? new BreathingEstimator(s.ProcessingRate) : null,
        };
        device.Spectrum = new SpectralHeartRate(device.Envelope.OutputRate, s.WindowSeconds)
        {
            MinWindowSeconds = s.MinWindowSeconds,
            MaxMaskedFraction = s.MaxMaskedFraction,
        };
        if (device.Breathing != null) device.Breathing.Updated += state => BreathingUpdated?.Invoke(id, state);
        _devices.Add(device);
    }

    /// <summary>Switches a device in or out of the heart rate and beat events (safe to call from any thread).</summary>
    public void SetUseForHeartRate(string id, bool use)
    {
        var d = _devices.Find(x => x.Id == id);
        if (d != null) d.UseForHeartRate = use;
    }

    public void Push(string id, in ImuSample sample)
    {
        var d = _devices.Find(x => x.Id == id) ?? throw new ArgumentException($"Unknown device '{id}'");
        d.LastSampleTime = Math.Max(d.LastSampleTime, sample.Time);
        _latestTime = Math.Max(_latestTime, sample.Time);
        double beatRate = d.BeatEnvelope.OutputRate;
        d.Resampler.Push(sample, (double t, in Vec3 accel, in Vec3 gyro) =>
        {
            d.Motion.Push(gyro);
            // Moving moments are marked so the spectrum can cut them out instead of discarding the window.
            if (d.Envelope.Push(gyro, out double env)) d.Spectrum.Push(env, d.Motion.Moving);
            // Always computed, so a device switched on later contributes beats right away.
            if (d.BeatEnvelope.Push(gyro, accel, out double ratio))
            {
                // Output times can sit exactly half-way between grid points (e.g. 29.005 s at 100 Hz);
                // floor keeps consecutive outputs on consecutive indices (Math.Round would pair them up).
                long index = (long)Math.Floor(t * beatRate + 1e-6);
                d.BeatRing[index % BeatRingSize] = ratio;
                d.BeatRingIndex[index % BeatRingSize] = index;
                d.BeatLatest = index;
            }
            d.Breathing?.Push(t, accel);
        });
        ProcessBeatFrames();
    }

    /// <summary>
    /// Combines the devices' beat envelopes sample by sample (they share a global time grid) and
    /// feeds the beat detector. Waits briefly for slower devices so their samples line up.
    /// </summary>
    private void ProcessBeatFrames()
    {
        long newest = -1;
        foreach (var d in _devices) newest = Math.Max(newest, d.BeatLatest);
        if (newest < 0) return;
        if (_beatCursor < 0) _beatCursor = newest - 1;
        double rate = _settings.BeatEnvelopeRate;
        long maxWait = (long)Math.Round(_settings.BeatAlignWaitSeconds * rate);

        while (_beatCursor < newest)
        {
            long m = _beatCursor + 1;
            bool waiting = _devices.Any(d => d.BeatWeight > 0 && d.Receiving && d.BeatLatest < m);
            if (waiting && newest - m < maxWait) break;

            double sum = 0, weight = 0;
            foreach (var d in _devices)
            {
                if (d.BeatWeight <= 0 || d.MovingNow || d.BeatRingIndex[m % BeatRingSize] != m) continue;
                sum += d.BeatWeight * d.BeatRing[m % BeatRingSize];
                weight += d.BeatWeight;
            }
            bool enabled = weight > 0 && _heart.Displayed && !_userMoving && UserPresent;
            _beats.Process(m / rate, weight > 0 ? sum / weight : double.NaN, _heart.Bpm, enabled);
            _beatCursor = m;
        }
    }

    public bool TryUpdate(double now, out MonitorSnapshot snapshot)
    {
        snapshot = null!;
        if (double.IsNaN(_nextUpdate)) _nextUpdate = now + _settings.UpdateIntervalSeconds;
        if (now < _nextUpdate) return false;
        _nextUpdate += _settings.UpdateIntervalSeconds;
        if (_nextUpdate < now) _nextUpdate = now + _settings.UpdateIntervalSeconds;

        // Each device is judged on its own: a moving device sits out, the still ones carry on.
        // The user counts as moving only when every heart-rate device is moving; then nothing is
        // shown and no beats are sent.
        var hrDevices = _devices.Where(d => d.UseForHeartRate).ToList();
        foreach (var d in _devices) d.Receiving = now - d.LastSampleTime <= _settings.StaleSeconds;
        var live = hrDevices.Where(d => d.Receiving).ToList();
        _userMoving = live.Count > 0 && live.All(d => d.MovingNow);

        var reports = new List<DeviceReport>();
        var forFusion = new List<HeartRateSpectrum>();
        var factors = new List<double>();
        foreach (var d in _devices)
        {
            // A device that stopped sending (turned off, out of range, battery) must not keep
            // contributing its last window: drop the window so it refills with fresh data.
            if (!d.Receiving) d.Spectrum.Clear();
            d.Last = d.Receiving ? d.Spectrum.Compute() : null;
            bool still = d.Receiving && !d.MovingNow;
            bool usable = d.Last != null && still && d.UseForHeartRate && !_userMoving && UserPresent;
            double factor = _settings.WeightFor(d.Role);
            if (usable) { forFusion.Add(d.Last!); factors.Add(factor); }
            d.BeatWeight = usable ? HeartRateFusion.DeviceWeight(d.Last!) * factor : 0;
            reports.Add(new DeviceReport(d.Id, d.Role, d.UseForHeartRate,
                d.Last?.PeakBpm ?? double.NaN, d.Last?.Prominence ?? 0, still, d.Motion.FastDps, d.Receiving, d.MovingNow,
                d.Spectrum.LastWindowSeconds));
        }

        _heart = Fusion.Update(now, forFusion, factors);
        var breathingDevice = _devices.FirstOrDefault(d => d.Breathing != null && d.Receiving);
        var breathing = breathingDevice?.Breathing?.Current;
        snapshot = new MonitorSnapshot(now, _heart, UserPresent ? breathing : null, reports, _userMoving, UserPresent);
        return true;
    }
}
