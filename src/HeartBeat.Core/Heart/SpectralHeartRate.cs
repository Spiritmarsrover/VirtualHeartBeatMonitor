using HeartBeat.Core.Dsp;

namespace HeartBeat.Core.Heart;

/// <summary>Heart-rate spectrum of one window: harmonic-sum power on a BPM grid, normalised by its median.</summary>
public sealed class HeartRateSpectrum
{
    public required double[] Bpm { get; init; }
    public required double[] Power { get; init; }
    public double PeakBpm { get; init; }
    /// <summary>Peak power ÷ median power. About 3–5 for noise, higher when a heartbeat is present.</summary>
    public double Prominence { get; init; }

    public static (double bpm, double value, int index) FindPeak(double[] bpm, double[] power)
    {
        int best = 0;
        for (int i = 1; i < power.Length; i++) if (power[i] > power[best]) best = i;
        double refined = bpm[best];
        if (best > 0 && best < power.Length - 1)
        {
            double a = power[best - 1], b = power[best], c = power[best + 1];
            double denom = a - 2 * b + c;
            if (denom < 0) refined += 0.5 * (a - c) / denom * (bpm[1] - bpm[0]);
        }
        return (refined, power[best], best);
    }

    public static double Median(double[] values)
    {
        var copy = (double[])values.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }
}

/// <summary>
/// Sliding-window spectral heart-rate estimator for one envelope stream. Harmonic summing
/// (f + 0.6·2f + 0.3·3f) makes the fundamental win even when a harmonic is the tallest line.
/// </summary>
public sealed class SpectralHeartRate
{
    public static readonly double[] DefaultHarmonicWeights = [1.0, 0.6, 0.3];

    private readonly double[] _buffer;
    private readonly int _nfft;
    private readonly double _rate;
    private readonly double[] _harmonics;
    private readonly bool[] _mask;
    private int _write, _count;

    public double[] BpmGrid { get; }

    public SpectralHeartRate(double rate, double windowSeconds = 12, double minBpm = 40, double maxBpm = 180,
        double gridStepBpm = 0.5, double[]? harmonicWeights = null, int zeroPadFactor = 8)
    {
        _rate = rate;
        _buffer = new double[(int)Math.Round(windowSeconds * rate)];
        _mask = new bool[_buffer.Length];
        _nfft = Fft.NextPowerOfTwo(_buffer.Length * zeroPadFactor);
        _harmonics = harmonicWeights ?? DefaultHarmonicWeights;
        int n = (int)Math.Round((maxBpm - minBpm) / gridStepBpm) + 1;
        BpmGrid = Enumerable.Range(0, n).Select(i => minBpm + i * gridStepBpm).ToArray();
    }

    public bool IsFull => _count >= _buffer.Length;

    /// <summary>Shortest window used right after settling (seconds). The window grows back to full length.</summary>
    public double MinWindowSeconds { get; set; } = 6;
    /// <summary>Largest share of a window that may be cut out (moving moments) and still be analysed.</summary>
    public double MaxMaskedFraction { get; set; } = 0.25;

    /// <summary>Seconds of signal the last <see cref="Compute"/> used (0 when it returned null).</summary>
    public double LastWindowSeconds { get; private set; }

    /// <param name="masked">True for samples taken while the device was moving; they are cut out of the analysis.</param>
    public void Push(double value, bool masked = false)
    {
        _buffer[_write] = value;
        _mask[_write] = masked;
        _write = (_write + 1) % _buffer.Length;
        if (_count < _buffer.Length) _count++;
    }

    public void Clear() { _count = 0; _write = 0; }

    /// <summary>
    /// Spectrum of the longest recent window (between <see cref="MinWindowSeconds"/> and the full
    /// length) in which at most <see cref="MaxMaskedFraction"/> of the samples were taken while
    /// moving. Moving samples are replaced by the window mean so they add nothing. Returns null
    /// when no such window exists yet (still warming up, or moving too much).
    /// </summary>
    public HeartRateSpectrum? Compute()
    {
        LastWindowSeconds = 0;
        int minLength = (int)Math.Round(MinWindowSeconds * _rate);
        if (_count < minLength) return null;

        // Walk back from the newest sample; keep the longest tail that is clean enough.
        int step = (int)Math.Round(_rate);
        int best = 0, masked = 0;
        for (int length = 1; length <= _count; length++)
        {
            int idx = (_write - length + _buffer.Length) % _buffer.Length;
            if (_mask[idx]) masked++;
            if (length >= minLength && (length % step == 0 || length == _count) && masked <= MaxMaskedFraction * length)
                best = length;
        }
        if (best == 0) return null;

        var ordered = new double[best];
        double cleanSum = 0;
        int clean = 0;
        for (int i = 0; i < best; i++)
        {
            int idx = (_write - best + i + _buffer.Length) % _buffer.Length;
            ordered[i] = _buffer[idx];
            if (!_mask[idx]) { cleanSum += ordered[i]; clean++; }
        }
        double cleanMean = clean > 0 ? cleanSum / clean : 0;
        for (int i = 0; i < best; i++)
            if (_mask[(_write - best + i + _buffer.Length) % _buffer.Length]) ordered[i] = cleanMean;
        LastWindowSeconds = best / _rate;

        double[] p = Fft.HannPowerSpectrum(ordered, _nfft);
        double binPerHz = _nfft / _rate;
        var power = new double[BpmGrid.Length];
        for (int i = 0; i < BpmGrid.Length; i++)
        {
            double sum = 0;
            for (int h = 0; h < _harmonics.Length; h++)
                sum += _harmonics[h] * Interpolate(p, BpmGrid[i] / 60.0 * (h + 1) * binPerHz);
            power[i] = sum;
        }

        double median = HeartRateSpectrum.Median(power);
        if (median <= 0) return null;
        for (int i = 0; i < power.Length; i++) power[i] /= median;
        var (bpm, peak, _) = HeartRateSpectrum.FindPeak(BpmGrid, power);
        return new HeartRateSpectrum { Bpm = BpmGrid, Power = power, PeakBpm = bpm, Prominence = peak };
    }

    private static double Interpolate(double[] p, double bin)
    {
        int i = (int)bin;
        if (i >= p.Length - 1) return 0;
        double f = bin - i;
        return p[i] * (1 - f) + p[i + 1] * f;
    }
}
