using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeartBeat.Core;
using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.App;

/// <summary>
/// Records a diagnostics report: one minute of raw IMU samples and pose updates from every device,
/// what the engine made of it, the device list with IMU availability, logs and settings, zipped
/// onto the Desktop. Users of other headsets can send it; the recording uses the same layout as
/// Probe recordings, so tools/Replay can run on it directly. Device serials are masked.
/// </summary>
public sealed class DiagnosticsCapture
{
    public const double Seconds = 60;

    private readonly MonitorHost _host;
    private readonly LiveSession _session;
    private readonly double _start = HostClock.Now;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<(double host, ImuSample_t s)>> _imu = new();
    private readonly ConcurrentDictionary<uint, ConcurrentQueue<PoseSample>> _poses = new();
    private readonly ConcurrentQueue<MonitorSnapshot> _snapshots = new();
    private readonly List<LiveSession.DeviceEntry> _devices;
    private readonly PosePoller _poller;
    private Task<string>? _writing;

    public DiagnosticsCapture(MonitorHost host, LiveSession session)
    {
        _host = host;
        _session = session;
        _devices = session.GetDevices();
        session.RawImu += OnImu;
        session.SnapshotReady += OnSnapshot;
        _poller = new PosePoller(session.Runtime.System, _devices.Select(d => d.Info.Index), 1000, (in PoseSample p) =>
        {
            if (p.VelocityChanged) _poses.GetOrAdd(p.Device, _ => new()).Enqueue(p);
        });
        _poller.Start();
        AppLog.Write($"Diagnostics capture started ({_devices.Count} devices)");
    }

    public double Remaining => Math.Max(0, Seconds - (HostClock.Now - _start));
    public string Progress => _writing == null
        ? $"recording diagnostics, stay still ({Remaining:0} s)"
        : "saving diagnostics report";

    /// <summary>Call from the host loop. Returns the zip path once written, otherwise null.</summary>
    public string? Tick(bool stopNow = false)
    {
        if (_writing == null && (stopNow || Remaining <= 0))
        {
            _poller.Stop();
            _session.RawImu -= OnImu;
            _session.SnapshotReady -= OnSnapshot;
            double duration = HostClock.Now - _start;
            _writing = Task.Run(() => DiagnosticsReport.Write(_host, _session, this, duration));
        }
        if (_writing is { IsCompleted: true } done) return done.IsCompletedSuccessfully ? done.Result : throw done.Exception!.GetBaseException();
        return null;
    }

    private void OnImu(LiveSession.DeviceEntry device, double host, ImuSample_t s) =>
        _imu.GetOrAdd(device.Id, _ => new()).Enqueue((host, s));

    private void OnSnapshot(MonitorSnapshot snap) => _snapshots.Enqueue(snap);

    internal DateTime StartedUtc => _startedUtc;
    internal double StartHost => _start;
    internal IReadOnlyList<LiveSession.DeviceEntry> Devices => _devices;
    internal IReadOnlyCollection<(double host, ImuSample_t s)> Imu(string id) => _imu.TryGetValue(id, out var q) ? q : [];
    internal IReadOnlyCollection<PoseSample> Poses(uint index) => _poses.TryGetValue(index, out var q) ? q : [];
    internal IReadOnlyCollection<MonitorSnapshot> Snapshots => _snapshots;
}

public static class DiagnosticsReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Writes the report zip. Without a capture (SteamVR not connected) only logs and system info are included.</summary>
    public static string Write(MonitorHost host, LiveSession? session, DiagnosticsCapture? capture, double duration)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", Inv);
        string work = Path.Combine(Path.GetTempPath(), $"VirtualHeartBeatMonitor-diagnostics-{stamp}");
        Directory.CreateDirectory(work);
        var serials = (capture?.Devices ?? session?.GetDevices() ?? []).Select(d => d.Info.Serial).Where(s => s.Length > 0).Distinct().ToList();
        string Mask(string text) => serials.Aggregate(text, (t, s) => t.Replace(s, MaskSerial(s)));

        var report = new StringBuilder();
        report.AppendLine("VirtualHeartBeatMonitor diagnostics report");
        report.AppendLine($"Created      {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"App          {typeof(DiagnosticsReport).Assembly.GetName().Version}  ({Environment.ProcessPath})");
        report.AppendLine($"Windows      {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
        report.AppendLine($"SteamVR      {(host.Connected ? host.RuntimeVersion : "not connected: " + host.Status)}");
        report.AppendLine($"Last error   {host.LastError ?? "-"}");
        report.AppendLine($"Vibration    {host.HapticsError ?? "ok"}");

        if (capture != null && session != null)
        {
            report.AppendLine($"Capture      {duration:0} s");
            report.AppendLine();
            WriteDevices(report, capture, duration);
            WriteEngine(report, capture);
            WriteRecording(work, host, capture, duration);
        }
        else report.AppendLine("No capture: SteamVR was not connected. Only logs and settings are included.");

        File.WriteAllText(Path.Combine(work, "report.txt"), Mask(report.ToString()));
        foreach (var name in new[] { "app.log", "app.previous.log", "crash.log", "settings.json" })
        {
            string src = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(src)) File.WriteAllText(Path.Combine(work, name), Mask(File.ReadAllText(src)));
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string folder = OutputOverride ?? (Directory.Exists(desktop) ? desktop : AppContext.BaseDirectory);
        Directory.CreateDirectory(folder);
        string zip = Path.Combine(folder, $"VirtualHeartBeatMonitor-diagnostics-{stamp}.zip");
        ZipFile.CreateFromDirectory(work, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
        Directory.Delete(work, recursive: true);
        AppLog.Write($"Diagnostics report saved: {zip}");
        return zip;
    }

    /// <summary>HEARTBEAT_DIAGNOSTICS_DIR overrides where reports are saved (development).</summary>
    public static string? OutputOverride => Environment.GetEnvironmentVariable("HEARTBEAT_DIAGNOSTICS_DIR");

    public static string MaskSerial(string serial)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(serial));
        string prefix = serial.Length > 4 ? serial[..4] : "";
        return $"{prefix}~{Convert.ToHexString(hash, 0, 3)}";
    }

    private static void WriteDevices(StringBuilder r, DiagnosticsCapture c, double duration)
    {
        r.AppendLine("== Devices ==");
        foreach (var d in c.Devices)
        {
            var i = d.Info;
            r.AppendLine($"#{i.Index} {i.Kind}  model '{i.Model}'  maker '{i.Manufacturer}'  tracking '{i.TrackingSystem}'  type '{i.ControllerType}'  serial {MaskSerial(i.Serial)}");
            r.AppendLine($"    role {d.Role}, used {(d.UsedForHeartRate ? "yes" : "no")}, raw IMU {(d.Stream != null ? "open" : "NOT available")}, signal from {d.Source}{(d.Status.Length > 0 ? $" ({d.Status})" : "")}");
            foreach (var a in d.ImuAttempts) r.AppendLine($"    IMU open: {a}");

            var imu = c.Imu(d.Id).ToList();
            if (imu.Count > 2)
            {
                var times = imu.Select(x => x.s.fSampleTime).OrderBy(x => x).ToList();
                double span = times[^1] - times[0];
                var dts = times.Zip(times.Skip(1), (a, b) => b - a).OrderBy(x => x).ToList();
                double accel = imu.Average(x => Math.Sqrt(x.s.vAccel.v0 * x.s.vAccel.v0 + x.s.vAccel.v1 * x.s.vAccel.v1 + x.s.vAccel.v2 * x.s.vAccel.v2));
                double gyroStd = Std(imu.Select(x => x.s.vGyro.v1 * 180 / Math.PI));
                r.AppendLine(string.Create(Inv, $"    IMU: {imu.Count} samples, {(imu.Count - 1) / span:0} Hz (device clock), median dt {dts[dts.Count / 2] * 1000:0.00} ms, gaps >15 ms {dts.Count(x => x > 0.015)}, |accel| {accel:0.000}, gyro-y std {gyroStd:0.000} deg/s"));
            }
            else r.AppendLine("    IMU: no samples");

            var poses = c.Poses(i.Index).ToList();
            double valid = poses.Count == 0 ? 0 : 100.0 * poses.Count(p => p.Valid) / poses.Count;
            r.AppendLine(string.Create(Inv, $"    Pose: {poses.Count / Math.Max(duration, 1):0} new updates/s, valid {valid:0}%"));
        }
        r.AppendLine();
    }

    private static void WriteEngine(StringBuilder r, DiagnosticsCapture c)
    {
        var snaps = c.Snapshots.ToList();
        r.AppendLine("== Engine during the capture ==");
        if (snaps.Count == 0) { r.AppendLine("(no updates)"); return; }
        var shown = snaps.Where(s => s.Heart.Displayed).ToList();
        r.AppendLine(string.Create(Inv, $"Number shown {100.0 * shown.Count / snaps.Count:0}% of the time, median {Median(shown.Select(s => s.Heart.Bpm)):0.0} BPM, user moving {100.0 * snaps.Count(s => s.UserMoving) / snaps.Count:0}%"));
        foreach (var d in c.Devices)
        {
            var rows = snaps.Select(s => s.Devices.FirstOrDefault(x => x.Id == d.Id)).Where(x => x != null).ToList();
            if (rows.Count == 0) continue;
            r.AppendLine(string.Create(Inv,
                $"  {d.Info.Kind,-14} receiving {100.0 * rows.Count(x => x!.Receiving) / rows.Count:0}%  moving {100.0 * rows.Count(x => x!.MovingNow) / rows.Count:0}%  " +
                $"peak {Median(rows.Where(x => x!.Prominence > 0).Select(x => x!.PeakBpm)):0.0} BPM  prominence {Median(rows.Select(x => x!.Prominence)):0.0}  motion {Median(rows.Select(x => x!.MotionDps)):0.0} deg/s"));
        }
        r.AppendLine();
    }

    /// <summary>Same layout as `Probe record`: session.json, imu_*.csv, poses_*.csv.</summary>
    private static void WriteRecording(string dir, MonitorHost host, DiagnosticsCapture c, double duration)
    {
        foreach (var d in c.Devices)
        {
            var imu = c.Imu(d.Id).ToList();
            if (imu.Count > 0)
            {
                using var w = new StreamWriter(Path.Combine(dir, $"imu_{d.Info.Index}_{d.Info.Kind}.csv"));
                w.WriteLine("host_t,sample_t,ax,ay,az,gx,gy,gz,offscale");
                foreach (var (h, s) in imu)
                    w.WriteLine(string.Create(Inv, $"{h - c.StartHost:F6},{s.fSampleTime:F7},{s.vAccel.v0:G10},{s.vAccel.v1:G10},{s.vAccel.v2:G10},{s.vGyro.v0:G10},{s.vGyro.v1:G10},{s.vGyro.v2:G10},{s.unOffScaleFlags}"));
            }
            var poses = c.Poses(d.Info.Index).ToList();
            if (poses.Count > 0)
            {
                using var w = new StreamWriter(Path.Combine(dir, $"poses_{d.Info.Index}_{d.Info.Kind}.csv"));
                w.WriteLine("host_t,valid,result,px,py,pz,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz");
                foreach (var p in poses)
                {
                    var (px, py, pz) = PoseMath.Position(p.Matrix);
                    var (qw, qx, qy, qz) = PoseMath.Rotation(p.Matrix);
                    w.WriteLine(string.Create(Inv,
                        $"{p.HostTime - c.StartHost:F6},{(p.Valid ? 1 : 0)},{(int)p.Result},{px:G9},{py:G9},{pz:G9},{qw:G9},{qx:G9},{qy:G9},{qz:G9},{p.Velocity.v0:G9},{p.Velocity.v1:G9},{p.Velocity.v2:G9},{p.AngularVelocity.v0:G9},{p.AngularVelocity.v1:G9},{p.AngularVelocity.v2:G9}"));
                }
            }
        }

        var session = new
        {
            startedUtc = c.StartedUtc,
            durationSeconds = duration,
            label = "diagnostics",
            note = "Recorded by the app's diagnostics report",
            pollHz = 500,
            runtimeVersion = host.RuntimeVersion,
            pulseBeforeBpm = (double?)null,
            pulseAfterBpm = (double?)null,
            devices = c.Devices.Select(d => new
            {
                index = d.Info.Index,
                kind = d.Info.Kind,
                model = d.Info.Model,
                serial = MaskSerial(d.Info.Serial),
                trackingSystem = d.Info.TrackingSystem,
                manufacturer = d.Info.Manufacturer,
                controllerType = d.Info.ControllerType,
                role = d.Role.ToString(),
                used = d.UsedForHeartRate,
                rawImu = d.Stream != null,
                source = d.Source,
            }),
        };
        File.WriteAllText(Path.Combine(dir, "session.json"), JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static double Std(IEnumerable<double> values)
    {
        var a = values.ToArray();
        if (a.Length < 2) return double.NaN;
        double m = a.Average();
        return Math.Sqrt(a.Sum(x => (x - m) * (x - m)) / (a.Length - 1));
    }

    private static double Median(IEnumerable<double> values)
    {
        var a = values.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
        return a.Length == 0 ? double.NaN : a[a.Length / 2];
    }
}
