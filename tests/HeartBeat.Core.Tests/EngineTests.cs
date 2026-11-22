using HeartBeat.Core;
using HeartBeat.Core.Breathing;

namespace HeartBeat.Core.Tests;

public class EngineTests
{
    private static List<MonitorSnapshot> Run(MonitorEngine engine, string id, IEnumerable<ImuSample> samples)
    {
        var snapshots = new List<MonitorSnapshot>();
        foreach (var s in samples)
        {
            engine.Push(id, s);
            if (engine.TryUpdate(s.Time, out var snap)) snapshots.Add(snap);
        }
        return snapshots;
    }

    [Theory]
    [InlineData(52)]
    [InlineData(72)]
    [InlineData(110)]
    public void Headset_heartbeat_is_found(double bpm)
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        var snaps = Run(engine, "hmd", Synthetic.Headset(40, bpm));

        var settled = snaps.Where(s => s.Time > 20).ToList();
        Assert.NotEmpty(settled);
        Assert.All(settled, s => Assert.InRange(s.Heart.Bpm, bpm - 2.5, bpm + 2.5));
        Assert.True(settled.Average(s => s.Heart.Confidence) > 0.6, "should be confident");
    }

    [Fact]
    public void Harmonics_do_not_double_the_rate()
    {
        // Sharp bursts have strong harmonics; the estimate must stay on the fundamental.
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        var snaps = Run(engine, "hmd", Synthetic.Headset(40, 60, beatAmplitude: 0.03, noise: 0.002)).Where(s => s.Time > 20).ToList();
        Assert.True(snaps.Count >= 15);
        Assert.All(snaps, s => Assert.InRange(s.Heart.Bpm, 57, 63));
    }

    [Fact]
    public void Noise_alone_is_not_reported_confidently()
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        var snaps = Run(engine, "hmd", Synthetic.Headset(60, 70, heartbeat: false));
        var late = snaps.Where(s => s.Time > 15).ToList();
        double shownFraction = late.Count(s => s.Heart.Displayed) / (double)late.Count;
        Assert.True(shownFraction < 0.2, $"a number was shown {shownFraction:P0} of the time on pure noise");
        Assert.Contains(late, s => s.Heart.DisplayText == "--" && s.Heart.DisplayBpm == null);
    }

    [Fact]
    public void Devices_are_fused()
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        engine.AddDevice("left", DeviceRole.Hand);
        var a = Synthetic.Headset(40, 66, seed: 1).Select(s => ("hmd", s));
        var b = Synthetic.Headset(40, 66, rate: 250, seed: 7, beatAmplitude: 0.008).Select(s => ("left", s));
        var merged = a.Concat(b).OrderBy(x => x.Item2.Time).ToList();
        var snaps = new List<MonitorSnapshot>();
        foreach (var (id, s) in merged)
        {
            engine.Push(id, s);
            if (engine.TryUpdate(s.Time, out var snap)) snaps.Add(snap);
        }
        var last = snaps[^1];
        Assert.Equal(2, last.Heart.DevicesUsed);
        Assert.InRange(last.Heart.Bpm, 64, 68);
    }

    [Fact]
    public void A_device_that_stops_sending_is_dropped()
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        engine.AddDevice("tracker", DeviceRole.Chest, useForHeartRate: false);
        var hmd = Synthetic.Headset(60, 66, seed: 1).Select(s => ("hmd", s));
        var tracker = Synthetic.Headset(30, 66, rate: 250, seed: 5).Select(s => ("tracker", s)); // stops at 30 s
        var snaps = new List<MonitorSnapshot>();
        foreach (var (id, s) in hmd.Concat(tracker).OrderBy(x => x.Item2.Time))
        {
            engine.Push(id, s);
            if (engine.TryUpdate(s.Time, out var snap)) snaps.Add(snap);
        }
        var trackerReports = snaps.Where(s => s.Time > 32).Select(s => s.Devices.Single(d => d.Id == "tracker")).ToList();
        Assert.NotEmpty(trackerReports);
        Assert.All(trackerReports, r => Assert.False(r.Receiving));
        Assert.All(trackerReports, r => Assert.True(double.IsNaN(r.PeakBpm)));
    }

    [Fact]
    public void Breathing_rate_and_hold_are_detected()
    {
        var engine = new MonitorEngine();
        engine.AddDevice("chest", DeviceRole.Chest, useForHeartRate: false);
        var trace = new List<BreathingState>();
        engine.BreathingUpdated += (_, b) => trace.Add(b);
        Run(engine, "chest", Synthetic.Chest(90, 15, holdStart: 50, holdEnd: 62));

        var before = trace.Where(b => b.Time is > 40 and < 49).ToList();
        Assert.True(before.Count > 50);
        Assert.All(before, b => Assert.True(b.Valid));
        Assert.All(before, b => Assert.InRange(b.RatePerMinute, 13.5, 16.5));
        Assert.DoesNotContain(trace, b => b.IsHold && b.Time < 50);

        Assert.Contains(trace, b => b.IsHold && b.Time is > 55 and < 62);
        Assert.DoesNotContain(trace, b => b.IsHold && b.Time > 65);
    }

    [Fact]
    public void No_breathing_signal_from_a_motionless_chest_device()
    {
        var engine = new MonitorEngine();
        engine.AddDevice("chest", DeviceRole.Chest, useForHeartRate: false);
        var trace = new List<BreathingState>();
        engine.BreathingUpdated += (_, b) => trace.Add(b);
        Run(engine, "chest", Synthetic.Chest(60, 15, amplitudeDeg: 0));
        Assert.True(trace.Count > 400);
        Assert.DoesNotContain(trace, b => b.Valid || b.IsHold);
    }
}
