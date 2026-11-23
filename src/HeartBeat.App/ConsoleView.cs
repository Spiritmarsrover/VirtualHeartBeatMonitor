using System.Text;
using HeartBeat.Core;
using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.App;

/// <summary>Text status view for console mode (--console) and logs.</summary>
public sealed class ConsoleView
{
    private int _lastLines;
    private double _nextLog;
    private readonly bool _interactive = !Console.IsOutputRedirected;

    public ConsoleView()
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (_interactive)
        {
            Console.Clear();
            Console.CursorVisible = false;
        }
    }

    public void Render(MonitorHost host)
    {
        var lines = Build(host);
        if (!_interactive)
        {
            // Redirected output (logs): the full block every 5 seconds.
            if (HostClock.Now < _nextLog) return;
            _nextLog = HostClock.Now + 5;
            Console.WriteLine($"--- {DateTime.Now:HH:mm:ss}");
            foreach (var line in lines.Skip(2)) Console.WriteLine(line);
            return;
        }
        Console.SetCursorPosition(0, 0);
        int width = Math.Max(20, Console.WindowWidth - 1);
        foreach (var line in lines) Console.WriteLine(line.Length > width ? line[..width] : line.PadRight(width));
        for (int i = lines.Count; i < _lastLines; i++) Console.WriteLine(new string(' ', width));
        _lastLines = lines.Count;
    }

    public void Restore()
    {
        if (_interactive) Console.CursorVisible = true;
    }

    private static List<string> Build(MonitorHost host)
    {
        var settings = host.Settings;
        var session = host.Session;
        var osc = host.Osc;
        var lines = new List<string>
        {
            "VirtualHeartBeatMonitor (console)                                Ctrl+C to quit",
            "",
            session == null
                ? host.Status
                : $"SteamVR {host.RuntimeVersion}",
            osc == null ? "OSC: off" : $"OSC: {settings.Osc.Host}:{settings.Osc.Port}   messages {osc.MessagesSent}   beats sent {osc.BeatsSent}",
        };
        if (host.LastError is { } err) lines.Add($"Note: {err}");
        lines.Add("");
        if (session == null) return lines;

        var snap = session.LatestSnapshot;
        bool flash = HostClock.Now - session.LastBeatHostTime < 0.25;
        lines.Add($"  Heart rate   {snap?.Heart.DisplayText ?? "--",4} BPM   {(flash ? "♥" : " ")}   confidence {(snap == null ? 0 : snap.Heart.Confidence):0.00}");
        lines.Add($"  Status       {OverlayRenderer.StatusText(session)}");
        double measuredPct = session.BeatCount == 0 ? 0 : 100.0 * session.MeasuredBeatCount / session.BeatCount;
        lines.Add($"  Beats        {session.BeatCount} sent, {measuredPct:0}% measured" +
                  (session.LastBeat is { } b ? $", last {(b.Measured ? "measured" : "predicted")}, delay {b.DelaySeconds * 1000:0} ms" : ""));
        if (snap?.Breathing is { } br)
            lines.Add(br.Valid
                ? $"  Breathing    {(double.IsNaN(br.RatePerMinute) ? "--" : br.RatePerMinute.ToString("0.0"))} /min{(br.IsHold ? $"   HOLD {br.HoldSeconds:0} s" : "")}"
                : "  Breathing    -- (no breathing movement on the chest tracker)");

        lines.Add("");
        lines.Add("  Device                          role    state        window  peak");
        foreach (var d in session.GetDevices().OrderBy(d => d.Info.Index))
        {
            var r = snap?.Devices.FirstOrDefault(x => x.Id == d.Id);
            string state = d.Status.Length > 0 ? d.Status
                : r == null ? "starting"
                : !r.Receiving ? "no data"
                : r.Resting ? "set down"
                : r.MovingNow ? "moving"
                : r.Prominence > 0 ? "measuring" : "settling";
            string window = r is { WindowSeconds: > 0 } ? $"{r.WindowSeconds:0} s" : "";
            string peak = r is { Prominence: > 0 } ? $"{r.PeakBpm,5:0.0} x{r.Prominence:0}" : "";
            lines.Add($"  {d.Info.Kind + " " + d.Info.Model,-31} {d.Role,-7} {state,-12} {window,6}  {peak}");
        }
        return lines;
    }

    private static string Activity(EDeviceActivityLevel level) => level switch
    {
        EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction => "worn",
        EDeviceActivityLevel.k_EDeviceActivityLevel_UserInteraction_Timeout => "worn (idle)",
        EDeviceActivityLevel.k_EDeviceActivityLevel_Idle or EDeviceActivityLevel.k_EDeviceActivityLevel_Idle_Timeout => "not worn",
        EDeviceActivityLevel.k_EDeviceActivityLevel_Standby => "standby",
        _ => "unknown",
    };
}
