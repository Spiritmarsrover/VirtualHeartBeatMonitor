using HeartBeat.Core.Dsp;

namespace HeartBeat.Core.Heart;

/// <summary>
/// Turns 3-axis rotation rate into the heartbeat "envelope": band-pass each axis to the
/// vibration band (default 4–20 Hz), take the vector magnitude, band-pass that to 0.5–5 Hz,
/// and decimate. Each beat becomes one bump, so the beat rate is the envelope's main period.
/// </summary>
public sealed class VibrationEnvelope
{
    private readonly FilterChain[] _band;
    private readonly FilterChain _post;
    private readonly int _decimation;
    private int _phase;

    public double InputRate { get; }
    public double OutputRate { get; }

    public VibrationEnvelope(double inputRate, double outputRate = 50, double bandLow = 4, double bandHigh = 20)
    {
        InputRate = inputRate;
        _decimation = Math.Max(1, (int)Math.Round(inputRate / outputRate));
        OutputRate = inputRate / _decimation;
        _band = Enumerable.Range(0, 3).Select(_ => Butterworth.BandPass(inputRate, bandLow, bandHigh, 4)).ToArray();
        _post = Butterworth.BandPass(inputRate, 0.5, Math.Min(5.0, 0.4 * OutputRate), 2);
    }

    /// <summary>Feeds one sample at <see cref="InputRate"/>. Returns true when an output sample is produced.</summary>
    public bool Push(in Vec3 gyro, out double envelope)
    {
        double x = _band[0].Process(gyro.X), y = _band[1].Process(gyro.Y), z = _band[2].Process(gyro.Z);
        double e = _post.Process(Math.Sqrt(x * x + y * y + z * z));
        envelope = e;
        if (++_phase < _decimation) return false;
        _phase = 0;
        return true;
    }
}
