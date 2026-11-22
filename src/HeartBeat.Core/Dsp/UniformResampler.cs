namespace HeartBeat.Core.Dsp;

/// <summary>
/// Turns irregular timestamped IMU samples into a fixed-rate stream on a global grid
/// (t = k / rate), so every device lands on the same time points. The native rate is detected
/// from the first samples and an anti-alias low-pass runs at that rate before interpolation.
/// Gaps up to <see cref="MaxGapSeconds"/> are bridged linearly (e.g. wireless tracker dropouts).
/// </summary>
public sealed class UniformResampler
{
    private const int RateProbeSamples = 32;

    private readonly double _rate;
    private readonly double _period;
    private readonly List<ImuSample> _probe = new(RateProbeSamples);
    private FilterChain[]? _antiAlias;
    private ImuSample? _previous;
    private long _nextIndex;

    public double OutputRate => _rate;
    public double NativeRate { get; private set; } = double.NaN;
    public double MaxGapSeconds { get; init; } = 0.25;
    public long GapResets { get; private set; }

    public UniformResampler(double outputRate) { _rate = outputRate; _period = 1.0 / outputRate; }

    public delegate void Emit(double time, in Vec3 accel, in Vec3 gyro);

    public void Push(in ImuSample sample, Emit emit)
    {
        if (_antiAlias == null)
        {
            _probe.Add(sample);
            if (_probe.Count < RateProbeSamples) return;
            InitialiseFilters();
            foreach (var s in _probe) Process(s, emit);
            _probe.Clear();
            return;
        }
        Process(sample, emit);
    }

    private void InitialiseFilters()
    {
        var dts = new List<double>();
        for (int i = 1; i < _probe.Count; i++)
        {
            double dt = _probe[i].Time - _probe[i - 1].Time;
            if (dt > 0) dts.Add(dt);
        }
        dts.Sort();
        NativeRate = dts.Count > 0 ? 1.0 / dts[dts.Count / 2] : _rate;
        double cutoff = 0.4 * Math.Min(_rate, NativeRate);
        _antiAlias = Enumerable.Range(0, 6).Select(_ => Butterworth.LowPass(NativeRate, cutoff, 4)).ToArray();
        var first = _probe[0];
        for (int k = 0; k < 3; k++)
        {
            _antiAlias[k].Prime(first.Accel[k]);
            _antiAlias[k + 3].Prime(first.Gyro[k]);
        }
    }

    private void Process(in ImuSample raw, Emit emit)
    {
        var f = _antiAlias!;
        var s = new ImuSample(raw.Time,
            new Vec3(f[0].Process(raw.Accel.X), f[1].Process(raw.Accel.Y), f[2].Process(raw.Accel.Z)),
            new Vec3(f[3].Process(raw.Gyro.X), f[4].Process(raw.Gyro.Y), f[5].Process(raw.Gyro.Z)));

        if (_previous is not { } prev)
        {
            _previous = s;
            _nextIndex = (long)Math.Ceiling(s.Time * _rate);
            return;
        }
        if (s.Time <= prev.Time) return; // out of order or duplicate
        if (s.Time - prev.Time > MaxGapSeconds)
        {
            // Too long to bridge: restart the grid after the gap.
            GapResets++;
            _previous = s;
            _nextIndex = (long)Math.Ceiling(s.Time * _rate);
            return;
        }

        double t;
        while ((t = _nextIndex * _period) <= s.Time)
        {
            double frac = (t - prev.Time) / (s.Time - prev.Time);
            emit(t, Vec3.Lerp(prev.Accel, s.Accel, frac), Vec3.Lerp(prev.Gyro, s.Gyro, frac));
            _nextIndex++;
        }
        _previous = s;
    }
}
