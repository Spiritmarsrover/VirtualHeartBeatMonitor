using HeartBeat.Core;
using HeartBeat.Core.Heart;
using HeartBeat.Osc;

namespace HeartBeat.Core.Tests;

public class OscTests
{
    private sealed class FakeSink : IOscSink
    {
        public readonly List<(string address, object[] args)> Sent = new();
        public void Send(string address, params object[] args) { lock (Sent) Sent.Add((address, args)); }
        public object? Last(string name) { lock (Sent) return Sent.LastOrDefault(m => m.address.EndsWith("/" + name)).args?[0]; }
    }

    [Fact]
    public void Encodes_int_float_bool_per_osc_spec()
    {
        var bytes = OscMessage.Encode("/avatar/parameters/HR", 62, 0.5f, true);
        // "/avatar/parameters/HR" is 21 bytes -> padded to 24; ",ifT" -> 4 + 4 padding; then int and float.
        Assert.Equal(24 + 8 + 4 + 4, bytes.Length);
        Assert.Equal((byte)',', bytes[24]);
        Assert.Equal(new byte[] { (byte)'i', (byte)'f', (byte)'T', 0 }, bytes[25..29]);
        Assert.Equal(new byte[] { 0, 0, 0, 62 }, bytes[32..36]);
        Assert.Equal(new byte[] { 0x3F, 0, 0, 0 }, bytes[36..40]); // 0.5f big-endian
    }

    [Fact]
    public void Address_of_multiple_of_four_still_gets_a_terminator()
    {
        var bytes = OscMessage.Encode("/abc", 1);
        Assert.Equal(8 + 4 + 4, bytes.Length);
        Assert.Equal(0, bytes[4]);
    }

    private static MonitorSnapshot Snap(double bpm, bool displayed) =>
        new(0, new HeartRateEstimate(0, bpm, bpm, 20, displayed ? 1 : 0, !displayed, 1, displayed), null, []);

    [Fact]
    public void Sends_heart_rate_digits_and_activity()
    {
        var sink = new FakeSink();
        using var out_ = new VRChatOutput(sink);
        out_.OnSnapshot(Snap(127.4, true), 1);
        Assert.Equal(127, sink.Last("HR"));
        Assert.Equal(7, sink.Last("onesHR"));
        Assert.Equal(2, sink.Last("tensHR"));
        Assert.Equal(1, sink.Last("hundredsHR"));
        Assert.Equal(true, sink.Last("isHRActive"));
        Assert.Equal(true, sink.Last("isHRConnected"));
        Assert.Equal(127f / 255f, (float)sink.Last("HRPercent")!, 3);
    }

    [Fact]
    public void Dashes_turn_off_active_but_keep_last_number()
    {
        var sink = new FakeSink();
        using var out_ = new VRChatOutput(sink);
        out_.OnSnapshot(Snap(64, true), 1);
        int hrMessages = sink.Sent.Count(m => m.address.EndsWith("/HR"));
        out_.OnSnapshot(Snap(64, false), 2);
        Assert.Equal(false, sink.Last("isHRActive"));
        Assert.Equal(hrMessages, sink.Sent.Count(m => m.address.EndsWith("/HR"))); // no HR=0 sent
    }

    [Fact]
    public void Unchanged_values_are_not_resent_until_due()
    {
        var sink = new FakeSink();
        using var out_ = new VRChatOutput(sink, new VRChatOutputSettings { ResendSeconds = 5 });
        out_.OnSnapshot(Snap(64, true), 1);
        int count = sink.Sent.Count;
        out_.OnSnapshot(Snap(64, true), 2);
        Assert.Equal(count, sink.Sent.Count);
        out_.OnSnapshot(Snap(64, true), 7);
        Assert.True(sink.Sent.Count > count);
    }

    [Fact]
    public void Chatbox_shows_dashes_once_when_the_reading_drops()
    {
        var sink = new FakeSink();
        using var out_ = new VRChatOutput(sink, new VRChatOutputSettings { Chatbox = true, ChatboxIntervalSeconds = 3 });
        out_.OnSnapshot(Snap(64, true), 1);
        out_.OnSnapshot(Snap(64, false), 2);   // too soon after the last message: waits
        out_.OnSnapshot(Snap(64, false), 3);   // now sends "--" once
        out_.OnSnapshot(Snap(64, false), 10);  // stays quiet
        var chat = sink.Sent.Where(m => m.address == "/chatbox/input").Select(m => (string)m.args[0]).ToList();
        Assert.Equal(["♥ 64 BPM", "♥ -- BPM"], chat);
    }

    [Fact]
    public void Beat_is_a_short_pulse()
    {
        var sink = new FakeSink();
        using var out_ = new VRChatOutput(sink, new VRChatOutputSettings { BeatPulseMs = 30 });
        out_.OnBeat(new BeatEvent(1, 1.09, true, 64, 0.94, 2.1));
        Assert.Equal(true, sink.Last("isHRBeat"));
        Assert.Equal(true, sink.Last("isHRBeatMeasured"));
        Thread.Sleep(300);
        Assert.Equal(false, sink.Last("isHRBeat"));
    }

    [Fact]
    public void Nothing_is_shown_or_sent_while_headset_is_not_worn()
    {
        var engine = new MonitorEngine();
        engine.AddDevice("hmd", DeviceRole.Head);
        var beats = new List<BeatEvent>();
        engine.Beat += beats.Add;
        var snaps = new List<MonitorSnapshot>();
        foreach (var s in Synthetic.Headset(60, 70))
        {
            engine.UserPresent = s.Time < 35;
            engine.Push("hmd", s);
            if (engine.TryUpdate(s.Time, out var snap)) snaps.Add(snap);
        }
        Assert.Contains(snaps, s => s.Time < 34 && s.Heart.Displayed);
        Assert.All(snaps.Where(s => s.Time > 36), s => Assert.Equal("--", s.Heart.DisplayText));
        Assert.All(snaps.Where(s => s.Time > 36), s => Assert.False(s.UserPresent));
        Assert.DoesNotContain(beats, b => b.EmittedAt > 35.1);
    }
}
