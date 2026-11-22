using HeartBeat.Core;
using HeartBeat.Core.Breathing;
using HeartBeat.Core.Heart;

namespace HeartBeat.Osc;

public class VRChatOutputSettings
{
    public string ParameterPrefix { get; set; } = "/avatar/parameters/";
    /// <summary>HR mapped to HRPercent = (HR - HrMin) / (HrMax - HrMin), clamped to 0..1.</summary>
    public int HrMin { get; set; } = 0;
    public int HrMax { get; set; } = 255;
    /// <summary>How long isHRBeat stays true for each beat.</summary>
    public int BeatPulseMs { get; set; } = 100;
    /// <summary>Re-send all values this often even if unchanged (avatar reloads, world changes).</summary>
    public double ResendSeconds { get; set; } = 5;
    /// <summary>Also show the heart rate in the VRChat chatbox (off by default).</summary>
    public bool Chatbox { get; set; } = false;
    public string ChatboxFormat { get; set; } = "♥ {bpm} BPM";
    public double ChatboxIntervalSeconds { get; set; } = 3;
    /// <summary>Breathing phase updates per second (only when a chest device is present).</summary>
    public double BreathPhaseRate { get; set; } = 10;
}

/// <summary>
/// Sends the engine's outputs to VRChat avatar parameters over OSC.
/// Heart-rate names follow the convention existing heart-rate avatars already use
/// (HR, onesHR / tensHR / hundredsHR, HRPercent, FullHRPercent, isHRConnected, isHRActive, isHRBeat).
/// Extra: isHRBeatMeasured, BreathRate, BreathPhase, isBreathHold.
/// While the number reads "--", isHRActive is false and the last heart rate is kept (not sent as 0).
/// </summary>
public sealed class VRChatOutput : IDisposable
{
    public static class Names
    {
        public const string HR = "HR", Ones = "onesHR", Tens = "tensHR", Hundreds = "hundredsHR";
        public const string Percent = "HRPercent", FullPercent = "FullHRPercent";
        public const string Connected = "isHRConnected", Active = "isHRActive", Beat = "isHRBeat", BeatMeasured = "isHRBeatMeasured";
        public const string BreathRate = "BreathRate", BreathPhase = "BreathPhase", BreathHold = "isBreathHold";
    }

    private readonly IOscSink _sink;
    private readonly VRChatOutputSettings _s;
    private readonly Timer _beatOff;
    private readonly Dictionary<string, object> _lastSent = new();
    private readonly Dictionary<string, double> _lastSentAt = new();
    private readonly object _gate = new();
    private double _lastChatbox = double.NegativeInfinity;
    private bool _chatboxShowsNumber; // the last chatbox message was a number, not "--"
    private double _lastBreathPhase = double.NegativeInfinity;

    public long MessagesSent { get; private set; }
    public long BeatsSent { get; private set; }

    public VRChatOutput(IOscSink sink, VRChatOutputSettings? settings = null)
    {
        _sink = sink;
        _s = settings ?? new VRChatOutputSettings();
        _beatOff = new Timer(_ => { Send(Names.Beat, false, force: true); Send(Names.BeatMeasured, false, force: true); });
    }

    /// <param name="now">Any monotonic clock in seconds (used for re-send and rate limits).</param>
    public void OnSnapshot(MonitorSnapshot snap, double now)
    {
        Send(Names.Connected, true, now);
        bool active = snap.Heart.Displayed;
        Send(Names.Active, active, now);
        if (!active)
        {
            // When the reading drops, the chatbox gets one last message with "--" and then goes
            // quiet (VRChat clears it after a while), instead of leaving a stale number up.
            if (_s.Chatbox && _chatboxShowsNumber && now - _lastChatbox >= MinChatboxGapSeconds)
                SendChatbox("--", now);
            return;
        }

        int bpm = (int)Math.Clamp(Math.Round(snap.Heart.Bpm), 0, 999);
        Send(Names.HR, bpm, now);
        Send(Names.Ones, bpm % 10, now);
        Send(Names.Tens, bpm / 10 % 10, now);
        Send(Names.Hundreds, bpm / 100 % 10, now);
        float percent = (float)Math.Clamp((bpm - _s.HrMin) / (double)Math.Max(1, _s.HrMax - _s.HrMin), 0, 1);
        Send(Names.Percent, percent, now);
        Send(Names.FullPercent, percent * 2 - 1, now);

        if (_s.Chatbox && now - _lastChatbox >= _s.ChatboxIntervalSeconds)
            SendChatbox(bpm.ToString(), now);
    }

    // VRChat drops chatbox messages sent faster than about one every 1.5 s.
    private const double MinChatboxGapSeconds = 1.5;

    private void SendChatbox(string bpmText, double now)
    {
        _lastChatbox = now;
        _chatboxShowsNumber = bpmText != "--";
        Raw("/chatbox/input", _s.ChatboxFormat.Replace("{bpm}", bpmText), true, false);
    }

    public void OnBeat(BeatEvent beat)
    {
        Send(Names.Beat, true, force: true);
        Send(Names.BeatMeasured, beat.Measured, force: true);
        BeatsSent++;
        _beatOff.Change(_s.BeatPulseMs, Timeout.Infinite);
    }

    public void OnBreathing(BreathingState b, double now)
    {
        if (!b.Valid) return;
        if (now - _lastBreathPhase >= 1.0 / _s.BreathPhaseRate)
        {
            _lastBreathPhase = now;
            Send(Names.BreathPhase, (float)b.Phase, force: true);
        }
        if (!double.IsNaN(b.RatePerMinute)) Send(Names.BreathRate, (float)Math.Round(b.RatePerMinute, 1), now);
        Send(Names.BreathHold, b.IsHold, now);
    }

    /// <summary>Tells avatars the monitor is gone (call on exit).</summary>
    public void Disconnect()
    {
        Send(Names.Active, false, force: true);
        Send(Names.Connected, false, force: true);
        Send(Names.Beat, false, force: true);
    }

    private void Send(string name, object value, double now = double.NaN, bool force = false)
    {
        lock (_gate)
        {
            bool changed = !_lastSent.TryGetValue(name, out var last) || !last.Equals(value);
            bool due = double.IsNaN(now) || !_lastSentAt.TryGetValue(name, out var at) || now - at >= _s.ResendSeconds;
            if (!force && !changed && !due) return;
            _lastSent[name] = value;
            if (!double.IsNaN(now)) _lastSentAt[name] = now;
            Raw(_s.ParameterPrefix + name, value);
        }
    }

    private void Raw(string address, params object[] args)
    {
        _sink.Send(address, args);
        MessagesSent++;
    }

    public void Dispose() => _beatOff.Dispose();
}
