using HeartBeat.Core;
using HeartBeat.Core.Dsp;

namespace HeartBeat.Core.Tests;

public class DspTests
{
    [Fact]
    public void Fft_puts_a_sinusoid_in_the_right_bin()
    {
        const int n = 1024;
        const double fs = 100, f = 12.5;
        var x = Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * f * i / fs)).ToArray();
        var p = Fft.HannPowerSpectrum(x, n);
        int peak = Array.IndexOf(p, p.Max());
        Assert.Equal(f, peak * fs / n, 1);
    }

    [Theory]
    [InlineData(5.0, 1.0, 0.99, 1.01)]   // passband
    [InlineData(5.0, 25.0, 0.0, 0.01)]   // 2+ octaves above cutoff, 4th order
    public void Butterworth_lowpass_gain(double cutoff, double freq, double minGain, double maxGain)
    {
        const double fs = 200;
        var lp = Butterworth.LowPass(fs, cutoff, 4);
        double peak = 0;
        for (int i = 0; i < 4000; i++)
        {
            double y = lp.Process(Math.Sin(2 * Math.PI * freq * i / fs));
            if (i > 2000) peak = Math.Max(peak, Math.Abs(y));
        }
        Assert.InRange(peak, minGain, maxGain);
    }

    [Fact]
    public void Butterworth_highpass_removes_dc()
    {
        var hp = Butterworth.HighPass(200, 0.1, 2);
        double y = 0;
        for (int i = 0; i < 20000; i++) y = hp.Process(3.0);
        Assert.InRange(y, -1e-3, 1e-3);
    }

    [Fact]
    public void Resampler_emits_on_a_global_grid_and_preserves_constants()
    {
        var r = new UniformResampler(200);
        var times = new List<double>();
        var values = new List<double>();
        var rng = new Random(3);
        double t = 10.0003;
        for (int i = 0; i < 1000; i++)
        {
            t += 0.004 * (1 + 0.05 * (rng.NextDouble() - 0.5)); // ~250 Hz with jitter
            r.Push(new ImuSample(t, new Vec3(0, 9.81, 0), new Vec3(0.1, 0, 0)), (double time, in Vec3 a, in Vec3 g) =>
            {
                times.Add(time);
                values.Add(g.X);
            });
        }
        Assert.InRange(r.NativeRate, 240, 260);
        Assert.InRange(times.Count, 790, 805);           // ~4 s at 200 Hz
        Assert.All(times, x => Assert.Equal(0, x * 200 - Math.Round(x * 200), 6)); // on k/200
        Assert.All(values.Skip(50), v => Assert.Equal(0.1, v, 6));
    }

    [Fact]
    public void Resampler_restarts_after_a_long_gap()
    {
        var r = new UniformResampler(200);
        var times = new List<double>();
        UniformResampler.Emit emit = (double time, in Vec3 a, in Vec3 g) => times.Add(time);
        for (int i = 0; i < 500; i++) r.Push(new ImuSample(i * 0.004, Vec3.Zero, Vec3.Zero), emit);
        for (int i = 0; i < 500; i++) r.Push(new ImuSample(100 + i * 0.004, Vec3.Zero, Vec3.Zero), emit);
        Assert.Equal(1, r.GapResets);
        Assert.DoesNotContain(times, x => x > 2.1 && x < 100);
    }
}
