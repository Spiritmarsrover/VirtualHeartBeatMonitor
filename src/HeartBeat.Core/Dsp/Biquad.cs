namespace HeartBeat.Core.Dsp;

/// <summary>Second-order IIR section (transposed direct form II). RBJ cookbook designs.</summary>
public sealed class Biquad
{
    private readonly double _b0, _b1, _b2, _a1, _a2;
    private double _z1, _z2;

    private Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
    }

    public double Process(double x)
    {
        double y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }

    public void Reset() => _z1 = _z2 = 0;

    public double DcGain => (_b0 + _b1 + _b2) / (1 + _a1 + _a2);

    /// <summary>Sets the internal state as if the input had been constant at <paramref name="x"/> forever.</summary>
    public void Prime(double x)
    {
        double y = x * DcGain;
        _z2 = _b2 * x - _a2 * y;
        _z1 = _b1 * x - _a1 * y + _z2;
    }

    public static Biquad LowPass(double fs, double fc, double q)
    {
        double w = 2 * Math.PI * fc / fs, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        return new Biquad((1 - c) / 2, 1 - c, (1 - c) / 2, 1 + alpha, -2 * c, 1 - alpha);
    }

    public static Biquad HighPass(double fs, double fc, double q)
    {
        double w = 2 * Math.PI * fc / fs, c = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        return new Biquad((1 + c) / 2, -(1 + c), (1 + c) / 2, 1 + alpha, -2 * c, 1 - alpha);
    }
}

/// <summary>A cascade of biquads applied to one channel.</summary>
public sealed class FilterChain
{
    private readonly Biquad[] _stages;

    public FilterChain(IEnumerable<Biquad> stages) => _stages = stages.ToArray();

    public double Process(double x)
    {
        foreach (var s in _stages) x = s.Process(x);
        return x;
    }

    public void Reset() { foreach (var s in _stages) s.Reset(); }

    /// <summary>Sets every stage's state as if the input had been constant at <paramref name="x"/> forever.</summary>
    public void Prime(double x)
    {
        foreach (var s in _stages)
        {
            s.Prime(x);
            x *= s.DcGain;
        }
    }
}

/// <summary>Butterworth designs built from cascaded biquads.</summary>
public static class Butterworth
{
    private static double[] Qs(int order) => order switch
    {
        2 => [0.70710678118654752],
        4 => [0.54119610014619698, 1.30656296487637652],
        6 => [0.51763809020504152, 0.70710678118654752, 1.93185165257813657],
        _ => throw new ArgumentOutOfRangeException(nameof(order), "Supported orders: 2, 4, 6"),
    };

    public static FilterChain LowPass(double fs, double fc, int order = 4) =>
        new(Qs(order).Select(q => Biquad.LowPass(fs, fc, q)));

    public static FilterChain HighPass(double fs, double fc, int order = 4) =>
        new(Qs(order).Select(q => Biquad.HighPass(fs, fc, q)));

    public static FilterChain BandPass(double fs, double low, double high, int order = 4) =>
        new(Qs(order).Select(q => Biquad.HighPass(fs, low, q))
            .Concat(Qs(order).Select(q => Biquad.LowPass(fs, high, q))));
}
