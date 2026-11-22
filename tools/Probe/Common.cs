using System.Text;
using HeartBeat.Tracking;

namespace HeartBeat.Probe;

/// <summary>Writes lines to the console and keeps a copy for saving to a file.</summary>
internal sealed class Report
{
    private readonly StringBuilder _text = new();

    public void Line(string line = "")
    {
        Console.WriteLine(line);
        _text.AppendLine(line);
    }

    public void Section(string title)
    {
        Line();
        Line($"== {title} ==");
    }

    public string Save(string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, _text.ToString());
        return path;
    }
}

/// <summary>Welford running mean / standard deviation.</summary>
internal sealed class RunningStats
{
    private double _mean, _m2;
    public long Count { get; private set; }
    public double Mean => _mean;
    public double StdDev => Count > 1 ? Math.Sqrt(_m2 / (Count - 1)) : double.NaN;
    public double Rms => Count > 0 ? Math.Sqrt(_mean * _mean + (Count > 1 ? _m2 / Count : 0)) : double.NaN;

    public void Add(double x)
    {
        Count++;
        double d = x - _mean;
        _mean += d / Count;
        _m2 += d * (x - _mean);
    }
}

internal readonly record struct IntervalSummary(int Count, double RateHz, double MedianMs, double P95Ms, double MaxMs)
{
    public static IntervalSummary From(IReadOnlyList<double> times)
    {
        if (times.Count < 3) return new IntervalSummary(times.Count, double.NaN, double.NaN, double.NaN, double.NaN);
        var dts = new double[times.Count - 1];
        for (int i = 1; i < times.Count; i++) dts[i - 1] = (times[i] - times[i - 1]) * 1000.0;
        Array.Sort(dts);
        double span = times[^1] - times[0];
        return new IntervalSummary(
            times.Count,
            span > 0 ? (times.Count - 1) / span : double.NaN,
            dts[dts.Length / 2],
            dts[(int)Math.Min(dts.Length - 1, Math.Round(dts.Length * 0.95))],
            dts[^1]);
    }
}

internal static class Common
{
    public const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Tries to open a raw IMU stream for each device, logging every attempt.</summary>
    public static List<ImuStream> OpenImuStreams(VrRuntime vr, IEnumerable<DeviceInfo> devices, Report? report)
    {
        var streams = new List<ImuStream>();
        if (vr.IOBuffer == null)
        {
            report?.Line("  IVRIOBuffer interface not available from this runtime.");
            return streams;
        }

        foreach (var device in devices)
        {
            var (stream, attempts) = ImuStream.TryOpen(vr.IOBuffer, device);
            foreach (var attempt in attempts)
                report?.Line($"  {device.Kind,-13} {attempt.Path,-55} -> {attempt.Result}");
            if (stream != null) streams.Add(stream);
        }
        report?.Line(streams.Count > 0
            ? $"  Opened {streams.Count} IMU stream(s)."
            : "  No IMU streams could be opened.");
        return streams;
    }

    public static string SafeFileName(string text)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char c in text.Trim())
            sb.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c);
        return sb.ToString();
    }

    public static string F(double value, string format = "0.0") =>
        double.IsNaN(value) ? "-" : value.ToString(format);
}
