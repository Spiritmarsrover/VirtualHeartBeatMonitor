using HeartBeat.Core.Dsp;

namespace HeartBeat.Core.Breathing;

public readonly record struct BreathingState(
    double Time,
    bool Valid,
    double RatePerMinute,
    double Phase,          // 0..1, chest position normalised to recent breathing depth
    double DepthDegrees,   // trough-to-peak tilt of the last full breath
    bool IsHold,
    double HoldSeconds,
    double SignalDegrees,  // band-passed tilt (0.07–0.7 Hz), for plotting
    double TiltDegrees);   // low-passed tilt without high-pass, used for hold detection

/// <summary>
/// Breathing from a chest-worn device: the tilt of the low-passed accelerometer (direction of
/// gravity) along its main axis of change, band-passed to 0.07–0.7 Hz.
/// Breaths are found as trough → peak turning points with hysteresis scaled to recent depth.
/// A hold is a stretch where the chest moves much slower than it does while breathing. It is
/// measured on the tilt before high-pass filtering, because the high-pass filter keeps drifting
/// for seconds after the chest stops and would hide the hold.
/// </summary>
public sealed class BreathingEstimator
{
    private const double RadToDeg = 180.0 / Math.PI;

    private readonly FilterChain[] _lowPass;
    private readonly int _decimation;
    private readonly double _rate;
    private readonly FilterChain _band;
    private readonly FilterChain _smooth;
    private readonly Queue<double> _tiltLastSecond = new();
    private int _phaseCount;
    private long _samples;

    // Running mean / covariance of the gravity direction, and its principal axis.
    private Vec3 _mean;
    private readonly double[,] _cov = new double[3, 3];
    private Vec3 _axis = new(1, 0, 0);
    private bool _initialised;

    private readonly Queue<(double t, double v)> _history = new();

    // Turning-point detector.
    private bool _rising = true;
    private double _extreme = double.NaN, _extremeTime;
    private double _lastTrough = double.NaN;
    private readonly List<double> _troughTimes = new();
    private readonly List<double> _depths = new();
    private double _lastDepth;

    // Hold detector: chest movement per second (smoothed) vs its median over the history.
    private double _movementFast = double.NaN;
    private readonly Queue<double> _movementHistory = new();
    private double _stillSince = double.NaN;

    public double OutputRate => _rate;
    public double MeanTimeConstant { get; set; } = 20;          // s, gravity-direction average
    public double MinBreathingStdDegrees { get; set; } = 0.3;   // below this there is no breathing signal
    public double HistorySeconds { get; set; } = 60;
    public double HysteresisFraction { get; set; } = 0.3;       // of the median recent breath depth
    public double HoldMovementFraction { get; set; } = 0.5;     // movement below this × median movement = still
    public double HoldMinSeconds { get; set; } = 5;

    public BreathingState Current { get; private set; }

    /// <summary>Raised for every output sample (about 10 Hz), e.g. to drive a chest animation.</summary>
    public event Action<BreathingState>? Updated;

    public BreathingEstimator(double inputRate, double outputRate = 10)
    {
        _decimation = Math.Max(1, (int)Math.Round(inputRate / outputRate));
        _rate = inputRate / _decimation;
        _lowPass = Enumerable.Range(0, 3).Select(_ => Butterworth.LowPass(inputRate, 1.0, 4)).ToArray();
        _band = Butterworth.BandPass(_rate, 0.07, 0.7, 2);
        _smooth = Butterworth.LowPass(_rate, 0.7, 2);
    }

    /// <summary>Feeds one accelerometer sample at the input rate.</summary>
    public void Push(double time, in Vec3 accel)
    {
        var a = new Vec3(_lowPass[0].Process(accel.X), _lowPass[1].Process(accel.Y), _lowPass[2].Process(accel.Z));
        if (++_phaseCount < _decimation) return;
        _phaseCount = 0;
        if (++_samples < _rate * 2) return; // let the low-pass settle

        var u = a.Normalized();
        if (!_initialised)
        {
            _mean = u;
            _initialised = true;
        }
        double alpha = 1.0 / (MeanTimeConstant * _rate);
        _mean = _mean + (u - _mean) * alpha;
        var d = u - _mean;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                _cov[i, j] += alpha * (d[i] * d[j] - _cov[i, j]);

        // One power-iteration step per sample tracks the principal axis; keep its sign stable.
        var next = new Vec3(
            _cov[0, 0] * _axis.X + _cov[0, 1] * _axis.Y + _cov[0, 2] * _axis.Z,
            _cov[1, 0] * _axis.X + _cov[1, 1] * _axis.Y + _cov[1, 2] * _axis.Z,
            _cov[2, 0] * _axis.X + _cov[2, 1] * _axis.Y + _cov[2, 2] * _axis.Z).Normalized();
        if (next.Length > 0) _axis = next.Dot(_axis) < 0 ? next * -1 : next;

        double tilt = d.Dot(_axis) * RadToDeg;
        Update(time, _band.Process(tilt), _smooth.Process(tilt));
    }

    private void Update(double t, double v, double tilt)
    {
        _history.Enqueue((t, v));
        while (_history.Count > 0 && t - _history.Peek().t > HistorySeconds) _history.Dequeue();
        double std = StdDev();
        bool valid = std >= MinBreathingStdDegrees && _history.Count > _rate * 20;

        DetectTurningPoints(t, v, std);
        bool hold = DetectHold(t, tilt, valid, out double stillFor);

        Current = new BreathingState(t, valid, valid ? Rate(t) : double.NaN,
            Math.Clamp(0.5 + v / (4 * Math.Max(std, 1e-6)), 0, 1),
            _lastDepth, hold, hold ? stillFor : 0, v, tilt);
        Updated?.Invoke(Current);
    }

    private void DetectTurningPoints(double t, double v, double std)
    {
        if (double.IsNaN(_extreme)) { _extreme = v; _extremeTime = t; return; }

        double typicalDepth = _depths.Count >= 3 ? Median(_depths.TakeLast(8)) : 2 * std;
        double h = Math.Max(0.1, HysteresisFraction * typicalDepth);

        if (_rising)
        {
            if (v > _extreme) { _extreme = v; _extremeTime = t; }
            else if (v < _extreme - h)
            {
                // Peak confirmed: one breath is trough → peak.
                if (!double.IsNaN(_lastTrough))
                {
                    _lastDepth = _extreme - _lastTrough;
                    _depths.Add(_lastDepth);
                    if (_depths.Count > 20) _depths.RemoveAt(0);
                }
                _rising = false;
                _extreme = v; _extremeTime = t;
            }
        }
        else
        {
            if (v < _extreme) { _extreme = v; _extremeTime = t; }
            else if (v > _extreme + h)
            {
                // Trough confirmed: marks the start of a breath.
                _lastTrough = _extreme;
                _troughTimes.Add(_extremeTime);
                while (_troughTimes.Count > 0 && t - _troughTimes[0] > HistorySeconds) _troughTimes.RemoveAt(0);
                _rising = true;
                _extreme = v; _extremeTime = t;
            }
        }
    }

    private bool DetectHold(double t, double tilt, bool valid, out double stillFor)
    {
        stillFor = 0;
        _tiltLastSecond.Enqueue(tilt);
        if (_tiltLastSecond.Count <= _rate) return false;
        double movement = Math.Abs(tilt - _tiltLastSecond.Dequeue()); // degrees moved over the last second
        double alpha = 1.0 / (0.5 * _rate);
        _movementFast = double.IsNaN(_movementFast) ? movement : _movementFast + alpha * (movement - _movementFast);

        _movementHistory.Enqueue(movement);
        while (_movementHistory.Count > HistorySeconds * _rate) _movementHistory.Dequeue();
        double typical = Median(_movementHistory);

        bool still = valid && _movementFast < HoldMovementFraction * typical;
        // The movement measure spans one second, so the chest has been still since a second earlier.
        if (still && double.IsNaN(_stillSince)) _stillSince = t - 1.0;
        if (!still) _stillSince = double.NaN;
        stillFor = double.IsNaN(_stillSince) ? 0 : t - _stillSince;
        return stillFor >= HoldMinSeconds;
    }

    private double Rate(double now)
    {
        // Median breath period over the last 30 s, from trough to trough.
        var recent = _troughTimes.Where(c => now - c <= 30).ToList();
        if (recent.Count < 3) return double.NaN;
        var periods = new List<double>();
        for (int i = 1; i < recent.Count; i++) periods.Add(recent[i] - recent[i - 1]);
        return 60.0 / Median(periods);
    }

    private static double Median(IEnumerable<double> values)
    {
        var a = values.OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a[a.Length / 2];
    }

    private double StdDev()
    {
        if (_history.Count < 2) return 0;
        double mean = 0, sq = 0;
        foreach (var (_, x) in _history) mean += x;
        mean /= _history.Count;
        foreach (var (_, x) in _history) sq += (x - mean) * (x - mean);
        return Math.Sqrt(sq / (_history.Count - 1));
    }
}
