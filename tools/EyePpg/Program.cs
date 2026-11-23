using System.Globalization;
using HeartBeat.EyePpg;
using HeartBeat.Tracking;

const string Usage = """
    EyePpg — experiment: heart rate from the Steam Frame's eye-tracking cameras.
    Needs FrameEyeCameraFeed running on the headset (see tools/EyePpg/README.md).

    Usage:
      EyePpg record  [--url http://frame:8090] [--seconds 120] [--label name] [--note text] [--no-imu] [--no-pulse]
      EyePpg analyze <recording folder> [--ref BPM] [--inject BPM[:amplitude]] [--no-imu-ref]

      record   saves both eye cameras (8x8 block means) plus the SteamVR IMU (if available) to recordings/
      analyze  pulse map per eye, heart rate from the eyes vs the IMU / your counted pulse
        --ref     reference heart rate when there is no IMU (default: IMU, else your counted pulse)
        --inject  adds a fake pulse to the recorded skin blocks (default amplitude 0.003 = 0.3 %),
                  to check the analysis finds a pulse that is known to be there
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

var a = new Args(args);
using var timerResolution = new TimerResolutionScope();
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

try
{
    return args[0] switch
    {
        "record" => RecordCommand.Run(a, stop.Token),
        "analyze" or "analyse" => AnalyzeCommand.Run(a),
        _ => Fail($"Unknown command '{args[0]}'."),
    };
}
catch (ArgumentException e)
{
    return Fail(e.Message);
}

int Fail(string message)
{
    Console.Error.WriteLine(message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 1;
}

namespace HeartBeat.EyePpg
{
    /// <summary>Tiny "--name value" parser; args[0] is the command, a bare argument after it is the folder.</summary>
    internal sealed class Args(string[] args)
    {
        public string? Positional => args.Skip(1).FirstOrDefault(s => !s.StartsWith("--") && !IsValueOfOption(s));

        private bool IsValueOfOption(string s)
        {
            int i = Array.IndexOf(args, s);
            return i > 0 && args[i - 1].StartsWith("--") && !Flags.Contains(args[i - 1]);
        }

        private static readonly string[] Flags = ["--no-imu", "--no-pulse", "--no-imu-ref"];

        public bool Has(string name) => args.Contains(name);

        public string Get(string name, string fallback)
        {
            int i = Array.IndexOf(args, name);
            if (i < 0) return fallback;
            if (i + 1 >= args.Length) throw new ArgumentException($"{name} needs a value");
            return args[i + 1];
        }

        public double GetDouble(string name, double fallback) =>
            Has(name) ? double.Parse(Get(name, ""), CultureInfo.InvariantCulture) : fallback;
    }
}
