using HeartBeat.Core;

namespace HeartBeat.Core.Tests;

/// <summary>Synthetic IMU signals with roughly the magnitudes seen in real recordings.</summary>
internal static class Synthetic
{
    /// <summary>
    /// A worn headset at <paramref name="rate"/> Hz: a damped 9 Hz rotation burst per heartbeat
    /// (with beat-to-beat jitter), slow breathing sway, gyro bias and white noise.
    /// </summary>
    public static IEnumerable<ImuSample> Headset(double seconds, double bpm, double rate = 1000, int seed = 1,
        double beatAmplitude = 0.012, double noise = 0.004, bool heartbeat = true,
        double moveStart = double.NaN, double moveEnd = double.NaN)
    {
        var rng = new Random(seed);
        var beats = MakeBeats(rng, seconds, bpm);

        var bias = new Vec3(0.004, -0.006, 0.002);
        var gravity = new Vec3(0, 9.81, 0);
        int b = 0;
        for (int i = 0; i < seconds * rate; i++)
        {
            double t = i / rate;
            while (b + 1 < beats.Count && beats[b + 1] <= t) b++;
            double burst = 0;
            if (heartbeat)
            {
                double since = t - beats[b];
                if (since >= 0 && since < 0.25) burst = beatAmplitude * Math.Exp(-since / 0.05) * Math.Sin(2 * Math.PI * 9 * since);
            }
            double sway = 0.02 * Math.Sin(2 * Math.PI * 0.25 * t);
            if (t >= moveStart && t < moveEnd) sway += 0.6 * Math.Sin(2 * Math.PI * 0.8 * t); // looking around, ~35 deg/s
            var gyro = bias + new Vec3(noise * Gaussian(rng), burst + sway + noise * Gaussian(rng), 0.5 * burst + noise * Gaussian(rng));
            var accel = gravity + new Vec3(0.02 * Gaussian(rng), 0.02 * Gaussian(rng), 0.02 * Gaussian(rng));
            yield return new ImuSample(t, accel, gyro);
        }
    }

    /// <summary>
    /// A chest tracker at <paramref name="rate"/> Hz whose tilt follows breathing at
    /// <paramref name="breathsPerMinute"/>, with an optional hold (tilt frozen) in [holdStart, holdEnd).
    /// </summary>
    public static IEnumerable<ImuSample> Chest(double seconds, double breathsPerMinute, double rate = 250,
        double holdStart = double.NaN, double holdEnd = double.NaN, double amplitudeDeg = 1.5, int seed = 2)
    {
        var rng = new Random(seed);
        double f = breathsPerMinute / 60.0;
        double frozen = double.NaN;
        for (int i = 0; i < seconds * rate; i++)
        {
            double t = i / rate;
            double angle;
            if (t >= holdStart && t < holdEnd)
            {
                if (double.IsNaN(frozen)) frozen = amplitudeDeg * Math.Sin(2 * Math.PI * f * t);
                angle = frozen;
            }
            else
            {
                angle = amplitudeDeg * Math.Sin(2 * Math.PI * f * t);
                frozen = double.NaN;
            }
            double a = angle * Math.PI / 180;
            var accel = new Vec3(0.03 * Gaussian(rng), 9.81 * Math.Cos(a) + 0.03 * Gaussian(rng), 9.81 * Math.Sin(a) + 0.03 * Gaussian(rng));
            var gyro = new Vec3(0.003 * Gaussian(rng), 0.003 * Gaussian(rng), 0.003 * Gaussian(rng));
            yield return new ImuSample(t, accel, gyro);
        }
    }

    /// <summary>The true beat times <see cref="Headset"/> uses for the same seconds, bpm and seed.</summary>
    public static List<double> BeatTimes(double seconds, double bpm, int seed = 1) => MakeBeats(new Random(seed), seconds, bpm);

    private static List<double> MakeBeats(Random rng, double seconds, double bpm)
    {
        var beats = new List<double>();
        for (double t = 0.3; t < seconds + 2; t += 60.0 / bpm * (1 + 0.03 * Gaussian(rng))) beats.Add(t);
        return beats;
    }

    internal static double Gaussian(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
