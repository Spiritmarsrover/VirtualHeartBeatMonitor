using HeartBeat.Core.Dsp;

namespace HeartBeat.Core.Heart;

/// <summary>
/// Sharp per-beat envelope for single-beat detection (the FFT path uses a smoother one).
/// Rotation and acceleration are band-passed to the heartbeat vibration band, each normalised by
/// its running variance, combined into one magnitude, lightly smoothed (about 40 ms), and finally
/// divided by its running mean. The output sits near 1 between beats and rises on each beat.
/// </summary>
public sealed class BeatEnvelope
{
    private readonly FilterChain[] _band;
    private readonly FilterChain _smooth;
    private readonly int _decimation;
    private readonly double _varAlpha, _meanAlpha;
    private double _gyroVar = double.NaN, _accelVar = double.NaN, _mean = double.NaN;
    private int _phase;
    private long _count;
    private readonly long _warmup;

    public double OutputRate { get; }

    public BeatEnvelope(double inputRate, double outputRate = 100, double bandLow = 4, double bandHigh = 20)
    {
        _decimation = Math.Max(1, (int)Math.Round(inputRate / outputRate));
        OutputRate = inputRate / _decimation;
        _band = Enumerable.Range(0, 6).Select(_ => Butterworth.BandPass(inputRate, bandLow, bandHigh, 2)).ToArray();
        _smooth = Butterworth.LowPass(inputRate, 8.0, 2);
        _varAlpha = 1.0 / (10 * inputRate);
        _meanAlpha = 1.0 / (5 * inputRate);
        _warmup = (long)(3 * inputRate);
    }

    /// <summary>Feeds one sample at the input rate. Returns true when an output sample is produced.</summary>
    public bool Push(in Vec3 gyro, in Vec3 accel, out double ratio)
    {
        ratio = double.NaN;
        double gx = _band[0].Process(gyro.X), gy = _band[1].Process(gyro.Y), gz = _band[2].Process(gyro.Z);
        double ax = _band[3].Process(accel.X), ay = _band[4].Process(accel.Y), az = _band[5].Process(accel.Z);
        double g2 = gx * gx + gy * gy + gz * gz, a2 = ax * ax + ay * ay + az * az;

        _gyroVar = double.IsNaN(_gyroVar) ? g2 : _gyroVar + _varAlpha * (g2 - _gyroVar);
        _accelVar = double.IsNaN(_accelVar) ? a2 : _accelVar + _varAlpha * (a2 - _accelVar);
        double e = Math.Sqrt(g2 / Math.Max(_gyroVar, 1e-12) + a2 / Math.Max(_accelVar, 1e-12));
        double s = _smooth.Process(e);
        _mean = double.IsNaN(_mean) ? s : _mean + _meanAlpha * (s - _mean);

        _count++;
        if (++_phase < _decimation) return false;
        _phase = 0;
        if (_count < _warmup || _mean <= 0) return false;
        ratio = s / _mean;
        return true;
    }
}
