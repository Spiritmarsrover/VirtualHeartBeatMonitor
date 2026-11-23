using System.Globalization;
using HeartBeat.Core.Recordings;

namespace HeartBeat.Core.Tests;

/// <summary>Pose fallback (Steam Frame and other headsets without raw IMU): rotation only, ~290 updates/s.</summary>
public class PoseTests
{
    /// <summary>Headset signal as the pose path delivers it: no acceleration, irregular ~290/s updates.</summary>
    private static IEnumerable<ImuSample> PoseLike(double seconds, double bpm)
    {
        var rng = new Random(7);
        double next = 0;
        foreach (var s in Synthetic.Headset(seconds, bpm))
        {
            if (s.Time < next) continue;
            next = s.Time + 1.0 / 290 * (0.7 + 0.6 * rng.NextDouble());
            yield return new ImuSample(s.Time, default, s.Gyro);
        }
    }

    [Theory]
    [InlineData(58)]
    [InlineData(90)]
    public void Heart_rate_is_found_from_rotation_alone(double bpm)
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head, useForBreathing: false);
        var snaps = new List<MonitorSnapshot>();
        foreach (var s in PoseLike(40, bpm))
        {
            engine.Push("hmd", s);
            if (engine.TryUpdate(s.Time, out var snap)) snaps.Add(snap);
        }
        var settled = snaps.Where(s => s.Time > 20).ToList();
        Assert.NotEmpty(settled);
        Assert.All(settled, s => Assert.InRange(s.Heart.Bpm, bpm - 2.5, bpm + 2.5));
        Assert.True(settled.Count(s => s.Heart.Displayed) > settled.Count * 0.8);
    }

    [Fact]
    public void Recording_without_imu_loads_poses_as_gyro()
    {
        string dir = Path.Combine(Path.GetTempPath(), "hb-pose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "session.json"),
                """{"devices":[{"index":0,"kind":"HMD","model":"Deckard MP","serial":"x"}]}""");
            var lines = new List<string> { "host_t,valid,result,px,py,pz,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz" };
            for (int i = 0; i < 100; i++)
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"{i * 0.0035:F6},{(i == 50 ? 0 : 1)},200,0,0,0,1,0,0,0,0,0,0,0.01,{i * 1e-4},-0.02"));
            File.WriteAllLines(Path.Combine(dir, "poses_0_HMD.csv"), lines);

            var device = Assert.Single(Recording.Load(dir).Devices);
            Assert.True(device.FromPose);
            Assert.Equal(99, device.Imu.Count); // the invalid row is skipped
            Assert.Equal(0.01, device.Imu[0].Gyro.X, 9);
            Assert.Equal(0, device.Imu[10].Accel.X);
        }
        finally { Directory.Delete(dir, true); }
    }
}
