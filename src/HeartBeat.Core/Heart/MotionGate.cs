using HeartBeat.Core.Dsp;

namespace HeartBeat.Core.Heart;

/// <summary>
/// Measures gross motion from the rotation rate, band-passed 0.1–4 Hz (the high-pass removes the
/// gyro's constant bias, which raw IMU data still contains), in deg/s:
///  - <see cref="FastDps"/>: a short (about 0.3 s) running RMS, used to mark individual moving
///    moments so they can be cut out of the analysis window;
///  - <see cref="LastBlockDps"/>: RMS per 1-second block, for display.
/// </summary>
public sealed class MotionGate
{
    private const double RadToDeg = 180.0 / Math.PI;
    private readonly FilterChain[] _band;
    private readonly int _blockSize;
    private readonly double _fastAlpha;
    private double _fastSq;
    private double _sumSq;
    private int _inBlock;
    private int _hangover, _hangoverSamples;

    public double ThresholdDps { get; set; }
    public double LastBlockDps { get; private set; }
    public double FastDps => Math.Sqrt(_fastSq) * RadToDeg;

    /// <summary>Moving right now, or within the hangover after moving (filters still ringing).</summary>
    public bool Moving => FastDps > ThresholdDps || _hangover > 0;

    public MotionGate(double rate, double thresholdDps = 5.0, double hangoverSeconds = 0.5)
    {
        _band = Enumerable.Range(0, 3).Select(_ => Butterworth.BandPass(rate, 0.1, 4.0, 2)).ToArray();
        _blockSize = (int)Math.Round(rate);
        _fastAlpha = 1.0 / (0.3 * rate);
        _hangoverSamples = (int)Math.Round(hangoverSeconds * rate);
        ThresholdDps = thresholdDps;
    }

    public void Push(in Vec3 gyro)
    {
        double x = _band[0].Process(gyro.X), y = _band[1].Process(gyro.Y), z = _band[2].Process(gyro.Z);
        double sq = x * x + y * y + z * z;
        _fastSq += _fastAlpha * (sq - _fastSq);
        if (FastDps > ThresholdDps) _hangover = _hangoverSamples;
        else if (_hangover > 0) _hangover--;

        _sumSq += sq;
        if (++_inBlock < _blockSize) return;
        LastBlockDps = Math.Sqrt(_sumSq / _inBlock) * RadToDeg;
        _sumSq = 0;
        _inBlock = 0;
    }
}
