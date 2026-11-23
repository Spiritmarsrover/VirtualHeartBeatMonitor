using System.Globalization;
using System.Text.Json;

namespace HeartBeat.Core.Recordings;

/// <param name="FromPose">No raw IMU was recorded: <see cref="Imu"/> holds the pose angular velocity as the gyro
/// (host-clock times, zero acceleration), as the live app's pose fallback does.</param>
public sealed record RecordedDevice(uint Index, string Kind, string Model, string Serial, IReadOnlyList<ImuSample> Imu, bool FromPose = false)
{
    public DeviceRole DefaultRole => Kind switch
    {
        "HMD" => DeviceRole.Head,
        "Controller-L" or "Controller-R" or "Controller" => DeviceRole.Hand,
        "Tracker" => DeviceRole.Chest, // only for recordings; users assign tracker roles in the app
        _ => DeviceRole.Other,
    };
}

/// <summary>A session written by <c>Probe record</c>: session.json plus imu_&lt;index&gt;_&lt;kind&gt;.csv files.</summary>
public sealed record Recording(
    string Folder, string Label, string Note, double? PulseBeforeBpm, double? PulseAfterBpm, IReadOnlyList<RecordedDevice> Devices)
{
    public static Recording Load(string folder)
    {
        string sessionPath = Path.Combine(folder, "session.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(sessionPath));
        var root = doc.RootElement;

        var devices = new List<RecordedDevice>();
        foreach (var d in root.GetProperty("devices").EnumerateArray())
        {
            uint index = d.GetProperty("index").GetUInt32();
            string kind = d.GetProperty("kind").GetString() ?? "";
            string model = d.GetProperty("model").GetString() ?? "", serial = d.GetProperty("serial").GetString() ?? "";
            string path = Path.Combine(folder, $"imu_{index}_{kind}.csv");
            string posePath = Path.Combine(folder, $"poses_{index}_{kind}.csv");
            if (File.Exists(path))
                devices.Add(new RecordedDevice(index, kind, model, serial, ReadImuCsv(path)));
            else if (File.Exists(posePath))
                devices.Add(new RecordedDevice(index, kind, model, serial, ReadPoseCsv(posePath), FromPose: true));
        }

        return new Recording(folder,
            root.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
            root.TryGetProperty("note", out var n) ? n.GetString() ?? "" : "",
            ReadNullableDouble(root, "pulseBeforeBpm"),
            ReadNullableDouble(root, "pulseAfterBpm"),
            devices);
    }

    private static double? ReadNullableDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>
    /// Reads poses_*.csv (host_t,valid,result,px,py,pz,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz; rows only on new
    /// driver updates) as gyro-only samples: angular velocity in rad/s, host-clock time.
    /// </summary>
    public static List<ImuSample> ReadPoseCsv(string path)
    {
        var samples = new List<ImuSample>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var p = line.Split(',');
            if (p.Length < 16 || p[1] != "1") continue;
            if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
                || !double.TryParse(p[13], NumberStyles.Float, CultureInfo.InvariantCulture, out double wx)
                || !double.TryParse(p[14], NumberStyles.Float, CultureInfo.InvariantCulture, out double wy)
                || !double.TryParse(p[15], NumberStyles.Float, CultureInfo.InvariantCulture, out double wz)) continue;
            if (samples.Count > 0 && t <= samples[^1].Time) continue;
            samples.Add(new ImuSample(t, default, new Vec3(wx, wy, wz)));
        }
        return samples;
    }

    /// <summary>Reads host_t,sample_t,ax,ay,az,gx,gy,gz,offscale; sorted by sample time, duplicates and broken lines dropped.</summary>
    public static List<ImuSample> ReadImuCsv(string path)
    {
        var samples = new List<ImuSample>();
        Span<double> v = stackalloc double[8];
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var span = line.AsSpan();
            int field = 0;
            bool ok = true;
            while (field < 8 && ok)
            {
                int comma = span.IndexOf(',');
                var token = comma >= 0 ? span[..comma] : span;
                ok = double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out v[field]);
                field++;
                span = comma >= 0 ? span[(comma + 1)..] : ReadOnlySpan<char>.Empty;
            }
            if (!ok || field < 8) continue;
            samples.Add(new ImuSample(v[1], new Vec3(v[2], v[3], v[4]), new Vec3(v[5], v[6], v[7])));
        }
        samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        var clean = new List<ImuSample>(samples.Count);
        foreach (var s in samples)
            if (clean.Count == 0 || s.Time > clean[^1].Time) clean.Add(s);

        // Older Probe builds could record samples left over in the driver's buffer from a previous
        // session: a short prefix followed by a jump of seconds to minutes. Drop that prefix.
        for (int i = 1; i < Math.Min(clean.Count, 4000); i++)
        {
            if (clean[i].Time - clean[i - 1].Time > 1.0)
            {
                clean.RemoveRange(0, i);
                break;
            }
        }
        return clean;
    }
}
