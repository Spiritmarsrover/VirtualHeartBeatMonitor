namespace HeartBeat.Core.Heart;

/// <summary>One heartbeat event.</summary>
/// <param name="Time">When the beat happened (device clock, seconds). For a predicted beat, the predicted time.</param>
/// <param name="EmittedAt">When the engine raised the event; EmittedAt - Time is the detection delay.</param>
/// <param name="Measured">True when a real beat was found in the signal, false when filled in from the rate.</param>
/// <param name="Bpm">Current heart rate from the FFT path.</param>
/// <param name="IntervalSeconds">Time since the previous beat event (NaN for the first after locking on).</param>
/// <param name="Strength">Peak height relative to the background (about 1 = no beat), NaN for predicted beats.</param>
public readonly record struct BeatEvent(
    double Time, double EmittedAt, bool Measured, double Bpm, double IntervalSeconds, double Strength)
{
    public double DelaySeconds => EmittedAt - Time;
}

/// <summary>
/// Rate-locked single-beat detector. The FFT heart rate predicts where the next beat should be;
/// the strongest envelope peak in a window around that prediction is taken as the beat. If no
/// clear peak shows up by a short deadline after the prediction, a predicted beat is emitted so
/// the rhythm continues. While disabled (moving, low confidence) nothing is emitted and the lock
/// is dropped; it re-locks on the next clear peak.
/// </summary>
public sealed class BeatDetector
{
    /// <summary>Search window around the predicted beat, as a fraction of the beat period.</summary>
    public double WindowFraction { get; set; } = 0.25;
    /// <summary>A peak must exceed the background by this factor to count as a measured beat.</summary>
    public double PeakRatio { get; set; } = 1.15;
    /// <summary>How long after a peak to wait before confirming it (it must not keep rising).</summary>
    public double ConfirmDelaySeconds { get; set; } = 0.08;
    /// <summary>How long after the predicted time to wait for a real peak before emitting a predicted beat.</summary>
    public double DeadlineSeconds { get; set; } = 0.12;
    /// <summary>To lock on, a peak must be this much stronger than the background.</summary>
    public double LockRatio { get; set; } = 1.3;
    /// <summary>A peak this strong is emitted as soon as it is confirmed, even before the predicted time.</summary>
    public double StrongRatio { get; set; } = 2.5;
    /// <summary>Width of the preference for peaks near the predicted time, as a fraction of the period.</summary>
    public double ProximitySigma { get; set; } = 0.12;
    /// <summary>Weight given to a peak far from the prediction (1 = no preference for the predicted time).</summary>
    public double ProximityFloor { get; set; } = 0.8;

    private readonly double _baselineAlpha;
    private double _baseline = 1;
    private double _last = double.NaN;
    private double _acquireStart = double.NaN;
    private double _candTime = double.NaN, _candValue = double.NegativeInfinity, _candScore = double.NegativeInfinity;

    public event Action<BeatEvent>? Beat;

    /// <summary>Every input sample (time, combined envelope, background level), e.g. for a live waveform.</summary>
    public event Action<double, double, double>? Sample;

    public BeatDetector(double rate) => _baselineAlpha = 1.0 / (2.0 * rate);

    public bool Locked => !double.IsNaN(_last);

    /// <summary>Diagnostics: samples seen, and samples seen while enabled.</summary>
    public long Processed { get; private set; }
    public long EnabledSamples { get; private set; }

    /// <param name="time">Sample time (device clock).</param>
    /// <param name="value">Combined beat envelope (about 1 between beats).</param>
    /// <param name="bpm">Current FFT heart rate.</param>
    /// <param name="enabled">False while moving or not confident: nothing is emitted and the lock is dropped.</param>
    public void Process(double time, double value, double bpm, bool enabled)
    {
        Processed++;
        if (enabled) EnabledSamples++;
        if (!double.IsNaN(value)) _baseline += _baselineAlpha * (value - _baseline);
        Sample?.Invoke(time, value, _baseline);
        if (!enabled || double.IsNaN(bpm) || bpm <= 0 || double.IsNaN(value))
        {
            Reset();
            return;
        }

        double period = 60.0 / bpm;
        if (double.IsNaN(_last))
        {
            Acquire(time, value, period, bpm);
            return;
        }

        double windowStart = _last + (1 - WindowFraction) * period;
        double predicted = _last + period;
        double deadline = predicted + Math.Min(DeadlineSeconds, WindowFraction * period);
        if (time < windowStart) return;

        // Peaks are ranked by height, weighted by closeness to the prediction, so a noise bump at
        // the edge of the window loses to a real beat near where the rhythm says it should be.
        double z = (time - predicted) / (ProximitySigma * period);
        double score = value * (ProximityFloor + (1 - ProximityFloor) * Math.Exp(-0.5 * z * z));
        if (score > _candScore) { _candScore = score; _candValue = value; _candTime = time; }

        bool clear = _candValue > PeakRatio * _baseline;
        bool confirmed = time - _candTime >= ConfirmDelaySeconds;
        bool veryStrong = _candValue > StrongRatio * _baseline;
        // Decide at the predicted time (or earlier for an unmistakable peak), never after the deadline.
        if (clear && confirmed && (time >= predicted || veryStrong))
        {
            Emit(_candTime, time, true, bpm, _candValue / _baseline);
            return;
        }
        if (time >= deadline)
        {
            if (clear) Emit(_candTime, time, true, bpm, _candValue / _baseline);
            else Emit(predicted, time, false, bpm, double.NaN);
        }
    }

    private void Acquire(double time, double value, double period, double bpm)
    {
        // Watch one full beat period, then lock on to its strongest peak if it is clear enough.
        // Otherwise start another period. Nothing is emitted before the lock.
        if (double.IsNaN(_acquireStart)) _acquireStart = time;
        if (value > _candValue) { _candValue = value; _candTime = time; _candScore = value; }
        if (time - _acquireStart < period || time - _candTime < ConfirmDelaySeconds) return;
        if (_candValue > LockRatio * _baseline)
        {
            _acquireStart = double.NaN;
            Emit(_candTime, time, true, bpm, _candValue / _baseline);
        }
        else
        {
            _acquireStart = time;
            ClearCandidate();
        }
    }

    private void Emit(double beatTime, double now, bool measured, double bpm, double strength)
    {
        double interval = double.IsNaN(_last) ? double.NaN : beatTime - _last;
        _last = beatTime;
        ClearCandidate();
        Beat?.Invoke(new BeatEvent(beatTime, now, measured, bpm, interval, strength));
    }

    private void ClearCandidate() { _candTime = double.NaN; _candValue = double.NegativeInfinity; _candScore = double.NegativeInfinity; }

    public void Reset()
    {
        _last = double.NaN;
        _acquireStart = double.NaN;
        ClearCandidate();
    }
}
