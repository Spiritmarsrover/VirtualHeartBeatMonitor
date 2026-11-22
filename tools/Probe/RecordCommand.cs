using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.Probe;

/// <summary>
/// Records every body device to CSV for offline analysis:
///   poses_&lt;index&gt;_&lt;kind&gt;.csv  — one row per new driver update (velocity changed)
///   imu_&lt;index&gt;_&lt;kind&gt;.csv    — one row per raw IMU sample (if available)
///   session.json                       — devices, settings, manual pulse notes
/// </summary>
internal static class RecordCommand
{
    private readonly record struct Row(bool IsImu, uint Device, double HostTime, PoseSample Pose, ImuSample_t Imu);

    private const double DropoutSeconds = 2.0;

    private const string PoseHeader = "host_t,valid,result,px,py,pz,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz";
    private const string ImuHeader = "host_t,sample_t,ax,ay,az,gx,gy,gz,offscale";

    public static int Run(VrRuntime vr, Options options, CancellationToken ct)
    {
        double seconds = options.Seconds ?? 120;
        var devices = DeviceInfo.Enumerate(vr.System);
        var body = devices.Where(d => d.IsBodyDevice).ToList();
        if (body.Count == 0)
        {
            Console.Error.WriteLine("No HMD, controllers or trackers connected.");
            return 3;
        }

        Console.WriteLine("Devices:");
        foreach (var d in body) Console.WriteLine($"  {d.Label}");
        var streams = Common.OpenImuStreams(vr, body, report: null);
        Console.WriteLine(streams.Count > 0
            ? $"Raw IMU: {string.Join(", ", streams.Select(s => s.Device.Kind))}"
            : "Raw IMU: not available, recording poses only.");
        if (!body.Any(d => d.Class == ETrackedDeviceClass.GenericTracker))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Note: no tracker connected, so there is no chest reference or breathing for this recording.");
            Console.ResetColor();
        }

        double? pulseBefore = AskPulse("Manually counted pulse BEFORE recording (BPM, Enter to skip): ");

        string name = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
            + (options.Label.Length > 0 ? "_" + Common.SafeFileName(options.Label) : "");
        string folder = Path.Combine(Environment.CurrentDirectory, "recordings", name);
        Directory.CreateDirectory(folder);

        var poseWriters = body.ToDictionary(d => d.Index, d => OpenCsv(folder, $"poses_{d.Index}_{d.Kind}.csv", PoseHeader));
        var imuWriters = streams.ToDictionary(s => s.Device.Index, s => OpenCsv(folder, $"imu_{s.Device.Index}_{s.Device.Kind}.csv", ImuHeader));

        var channel = Channel.CreateUnbounded<Row>(new UnboundedChannelOptions { SingleReader = true });
        var writerTask = Task.Run(async () =>
        {
            await foreach (var row in channel.Reader.ReadAllAsync())
            {
                if (row.IsImu) imuWriters[row.Device].WriteLine(FormatImu(row.HostTime, row.Imu));
                else poseWriters[row.Device].WriteLine(FormatPose(row.Pose));
            }
        });

        long poseRows = 0, imuRows = 0;
        using var poller = new PosePoller(vr.System, body.Select(d => d.Index), options.Hz, (in PoseSample s) =>
        {
            if (!s.VelocityChanged) return;
            poseRows++;
            channel.Writer.TryWrite(new Row(false, s.Device, s.HostTime, s, default));
        });
        // Last host time each IMU stream delivered a sample, to catch devices dropping out mid-recording.
        var lastImu = new double[streams.Count];
        var streamIndex = streams.Select((s, i) => (s, i)).ToDictionary(p => p.s, p => p.i);
        using var imuReader = new ImuReader(streams, (ImuStream stream, double hostTime, in ImuSample_t sample) =>
        {
            imuRows++;
            Volatile.Write(ref lastImu[streamIndex[stream]], hostTime);
            channel.Writer.TryWrite(new Row(true, stream.Device.Index, hostTime, default, sample));
        });

        Console.WriteLine($"Recording to {folder}");
        Console.WriteLine("Press Ctrl+C to stop early.");
        DateTime startedUtc = DateTime.UtcNow;
        double start = HostClock.Now;
        for (int i = 0; i < lastImu.Length; i++) lastImu[i] = start;
        var dropouts = new List<(string device, double start, double end)>();
        var droppedSince = new double[streams.Count];
        Array.Fill(droppedSince, double.NaN);
        poller.Start();
        imuReader.Start();
        while (!ct.IsCancellationRequested && HostClock.Now - start < seconds)
        {
            Thread.Sleep(250);
            if (!vr.PumpEvents()) break;
            double now = HostClock.Now;
            var silent = new List<string>();
            for (int i = 0; i < streams.Count; i++)
            {
                double last = Volatile.Read(ref lastImu[i]);
                bool dropped = now - last > DropoutSeconds;
                if (dropped)
                {
                    if (double.IsNaN(droppedSince[i]))
                    {
                        droppedSince[i] = last;
                        if (OperatingSystem.IsWindows()) Console.Beep(880, 150);
                    }
                    silent.Add($"{streams[i].Device.Kind} {now - last:0}s");
                }
                else if (!double.IsNaN(droppedSince[i]))
                {
                    dropouts.Add((streams[i].Device.Kind, droppedSince[i] - start, last - start));
                    droppedSince[i] = double.NaN;
                }
            }
            Console.Write($"\r  {now - start:0} / {seconds:0} s   pose rows {poseRows}   imu rows {imuRows}   ");
            if (silent.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"NO DATA: {string.Join(", ", silent)}   ");
                Console.ResetColor();
            }
            else Console.Write(new string(' ', 40));
        }
        for (int i = 0; i < streams.Count; i++)
            if (!double.IsNaN(droppedSince[i])) dropouts.Add((streams[i].Device.Kind, droppedSince[i] - start, HostClock.Now - start));
        poller.Stop();
        imuReader.Stop();
        double elapsed = HostClock.Now - start;
        Console.WriteLine();

        channel.Writer.Complete();
        writerTask.Wait();
        foreach (var w in poseWriters.Values.Concat(imuWriters.Values)) w.Dispose();
        foreach (var s in streams) s.Dispose();

        if (dropouts.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Devices stopped sending data during this recording:");
            foreach (var (device, from, to) in dropouts)
                Console.WriteLine($"  {device}: {from:0} s to {to:0} s ({to - from:0} s)");
            Console.WriteLine("Check battery / pairing and consider re-recording this session.");
            Console.ResetColor();
        }

        double? pulseAfter = AskPulse("Manually counted pulse AFTER recording (BPM, Enter to skip): ");

        var session = new
        {
            startedUtc,
            hostClockAtStart = start,
            durationSeconds = elapsed,
            label = options.Label,
            note = options.Note,
            pollHz = options.Hz,
            actualPollHz = poller.PollCount / elapsed,
            latePolls = poller.LatePolls,
            runtimeVersion = vr.RuntimeVersion,
            pulseBeforeBpm = pulseBefore,
            pulseAfterBpm = pulseAfter,
            imuStreams = streams.Select(s => new { device = s.Device.Index, path = s.Path }),
            dropouts = dropouts.Select(d => new { device = d.device, fromSeconds = Math.Round(d.start, 1), toSeconds = Math.Round(d.end, 1) }),
            devices = body.Select(d => new
            {
                index = d.Index,
                kind = d.Kind,
                model = d.Model,
                serial = d.Serial,
                trackingSystem = d.TrackingSystem,
                manufacturer = d.Manufacturer,
                controllerType = d.ControllerType,
            }),
            columns = new
            {
                poses = "host_t: s (host clock); p: m; q: unit quaternion; v: m/s; w: rad/s; raw uncalibrated universe; rows only on new driver updates",
                imu = "host_t: s (host clock when read); sample_t: s (device clock); a, g: raw driver units",
            },
        };
        File.WriteAllText(Path.Combine(folder, "session.json"),
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Saved {poseRows} pose rows and {imuRows} IMU rows to {folder}");
        return 0;
    }

    private static StreamWriter OpenCsv(string folder, string file, string header)
    {
        var writer = new StreamWriter(Path.Combine(folder, file), append: false, encoding: new System.Text.UTF8Encoding(false), bufferSize: 1 << 16);
        writer.WriteLine(header);
        return writer;
    }

    private static string FormatPose(in PoseSample s)
    {
        var (px, py, pz) = PoseMath.Position(s.Matrix);
        var (qw, qx, qy, qz) = PoseMath.Rotation(s.Matrix);
        var v = s.Velocity;
        var w = s.AngularVelocity;
        return string.Create(CultureInfo.InvariantCulture,
            $"{s.HostTime:F6},{(s.Valid ? 1 : 0)},{(int)s.Result},{px:G9},{py:G9},{pz:G9},{qw:G9},{qx:G9},{qy:G9},{qz:G9},{v.v0:G9},{v.v1:G9},{v.v2:G9},{w.v0:G9},{w.v1:G9},{w.v2:G9}");
    }

    private static string FormatImu(double hostTime, in ImuSample_t s) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{hostTime:F6},{s.fSampleTime:F7},{s.vAccel.v0:G10},{s.vAccel.v1:G10},{s.vAccel.v2:G10},{s.vGyro.v0:G10},{s.vGyro.v1:G10},{s.vGyro.v2:G10},{s.unOffScaleFlags}");

    private static double? AskPulse(string prompt)
    {
        if (Console.IsInputRedirected) return null;
        Console.Write(prompt);
        string? line = Console.ReadLine();
        return double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) && bpm > 0 ? bpm : null;
    }
}
