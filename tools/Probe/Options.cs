using System.Globalization;

namespace HeartBeat.Probe;

internal sealed class Options
{
    public string Command { get; private set; } = "check";
    public double? Seconds { get; private set; }
    public double Hz { get; private set; } = 1000;
    public string Label { get; private set; } = "";
    public string Note { get; private set; } = "";
    public bool ShowHelp { get; private set; }

    public const string Usage = """
        Probe — Phase 0 data-access check for VirtualHeartBeatMonitor.
        SteamVR must already be running (Probe never starts it).

        Usage:
          Probe [check]  [--seconds 10] [--hz 1000]
              Lists devices, tries to open each device's raw IMU stream, and measures how
              often each device's pose actually updates. Writes a report to probe-reports/.
              Keep the devices still (worn or resting) during the measurement.

          Probe record   [--seconds 120] [--hz 1000] [--label name] [--note "text"]
              Records all body devices (pose updates + raw IMU if available) to
              recordings/<time>_<label>/ as CSV, plus session.json. Ctrl+C stops early.

          Probe live     [--hz 1000]
              Live per-device readout: update rate, rotation-rate and velocity RMS, IMU rate.

        Options:
          --seconds N   duration (check: 10, record: 120, live: until Ctrl+C)
          --hz N        pose polling rate (default 1000). Try 2000 to check whether updates
                        are truly that fast or just extrapolated per poll.
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        int i = 0;
        if (args.Length > 0 && !args[0].StartsWith('-'))
            o.Command = args[i++].ToLowerInvariant();

        for (; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            switch (a)
            {
                case "--seconds": o.Seconds = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--hz": o.Hz = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--label": o.Label = Next(); break;
                case "--note": o.Note = Next(); break;
                case "-h" or "--help" or "help": o.ShowHelp = true; break;
                default: throw new ArgumentException($"Unknown option '{a}'");
            }
        }
        if (o.Command is "help" or "-h" or "--help") o.ShowHelp = true;
        else if (o.Command is not ("check" or "record" or "live"))
            throw new ArgumentException($"Unknown command '{o.Command}'");
        if (o.Hz is < 10 or > 10000) throw new ArgumentException("--hz must be between 10 and 10000");
        return o;
    }
}
