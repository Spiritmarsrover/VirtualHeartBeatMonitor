using HeartBeat.Core;
using HeartBeat.Core.Heart;

namespace HeartBeat.Core.Tests;

public class BeatEventTests
{
    private static (List<BeatEvent> beats, List<MonitorSnapshot> snaps) Run(IEnumerable<ImuSample> samples)
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        var beats = new List<BeatEvent>();
        engine.Beat += beats.Add;
        var snaps = new List<MonitorSnapshot>();
        foreach (var s in samples)
        {
            engine.Push("hmd", s);
            if (engine.TryUpdate(s.Time, out var snap)) snaps.Add(snap);
        }
        return (beats, snaps);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(100)]
    public void Beat_events_land_on_real_beats(double bpm)
    {
        var truth = Synthetic.BeatTimes(60, bpm);
        var (beats, _) = Run(Synthetic.Headset(60, bpm));
        var late = beats.Where(b => b.Time > 20).ToList();
        int expected = truth.Count(t => t > 20 && t < 59.5);

        Assert.InRange(late.Count, expected * 0.9, expected * 1.1);
        // Events trail the beat onset by a constant delay (filters + the vibration peaking after
        // onset). That is fine; what matters is that every event sits at the same offset.
        var offsets = late.Select(b => b.Time - truth.Where(t => t <= b.Time + 0.05).DefaultIfEmpty(double.NaN).Max()).ToList();
        double typical = offsets.OrderBy(x => x).ElementAt(offsets.Count / 2);
        Assert.InRange(typical, 0.0, 0.15);
        double onBeat = offsets.Count(o => Math.Abs(o - typical) <= 0.08) / (double)late.Count;
        Assert.True(onBeat >= 0.85, $"{onBeat:P0} of events within 80 ms of the typical offset ({typical * 1000:0} ms)");
        Assert.True(late.Count(b => b.Measured) >= 0.7 * late.Count, "most events should be measured, not predicted");
        Assert.All(late, b => Assert.InRange(b.DelaySeconds, 0, 0.35));
    }

    [Fact]
    public void No_beat_events_without_a_heartbeat()
    {
        var (beats, snaps) = Run(Synthetic.Headset(60, 70, heartbeat: false));
        // Events may only appear while a number is displayed, which is rare on pure noise.
        int displayedSeconds = snaps.Count(s => s.Heart.Displayed);
        Assert.True(beats.Count <= displayedSeconds * 2 + 2, $"{beats.Count} events with only {displayedSeconds} s displayed");
    }

    [Fact]
    public void Number_comes_back_quickly_after_moving()
    {
        var (_, snaps) = Run(Synthetic.Headset(70, 70, moveStart: 35, moveEnd: 40));
        // Needs MinWindowSeconds (6 s) of clean data plus the motion hangover, not a full 12 s window.
        var back = snaps.FirstOrDefault(s => s.Time > 40 && s.Heart.Displayed);
        Assert.NotNull(back);
        Assert.True(back.Time < 48.5, $"number came back at {back.Time:0.0} s, movement ended at 40 s");
        Assert.InRange(back.Heart.Bpm, 66, 74);
    }

    [Fact]
    public void A_still_device_carries_on_while_another_moves()
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        engine.AddDevice("left", DeviceRole.Hand);
        var head = Synthetic.Headset(70, 66, seed: 1, moveStart: 35, moveEnd: 50).Select(s => ("hmd", s));
        var hand = Synthetic.Headset(70, 66, rate: 250, seed: 7).Select(s => ("left", s));
        var snaps = new List<MonitorSnapshot>();
        foreach (var (id, s) in head.Concat(hand).OrderBy(x => x.Item2.Time))
        {
            engine.Push(id, s);
            if (engine.TryUpdate(s.Time, out var snap)) snaps.Add(snap);
        }
        var during = snaps.Where(s => s.Time is > 38 and < 50).ToList();
        Assert.All(during, s => Assert.False(s.UserMoving));
        Assert.True(during.Count(s => s.Heart.Displayed) >= during.Count * 0.8, "the still controller should keep the number up");
        Assert.All(during.Where(s => s.Heart.Displayed), s => Assert.InRange(s.Heart.Bpm, 62, 70));
    }

    [Fact]
    public void A_device_can_be_switched_off_while_running()
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        engine.AddDevice("left", DeviceRole.Hand);
        var head = Synthetic.Headset(60, 66, seed: 1).Select(s => ("hmd", s));
        var hand = Synthetic.Headset(60, 66, rate: 250, seed: 7).Select(s => ("left", s));
        var snaps = new List<MonitorSnapshot>();
        foreach (var (id, s) in head.Concat(hand).OrderBy(x => x.Item2.Time))
        {
            if (s.Time > 40) engine.SetUseForHeartRate("left", false);
            engine.Push(id, s);
            if (engine.TryUpdate(s.Time, out var snap)) snaps.Add(snap);
        }
        Assert.Contains(snaps, s => s.Time is > 30 and < 40 && s.Heart.DevicesUsed == 2);
        Assert.All(snaps.Where(s => s.Time > 41), s => Assert.True(s.Heart.DevicesUsed <= 1));
        Assert.Contains(snaps, s => s.Time > 41 && s.Heart.Displayed);
    }

    [Fact]
    public void Number_and_beats_stop_while_moving()
    {
        var (beats, snaps) = Run(Synthetic.Headset(70, 70, moveStart: 35, moveEnd: 45));
        Assert.Contains(beats, b => b.Time is > 25 and < 34);           // working before
        Assert.DoesNotContain(beats, b => b.Time is > 37 and < 45);     // silent while moving (1-2 s to notice)
        Assert.All(snaps.Where(s => s.Time is > 37 and < 45), s => Assert.Equal("--", s.Heart.DisplayText));
        Assert.Contains(snaps, s => s.Time > 37 && s.UserMoving);
    }
}
