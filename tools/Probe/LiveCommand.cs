using HeartBeat.Tracking;
using Valve.VR;
using static HeartBeat.Probe.Common;

namespace HeartBeat.Probe;

/// <summary>Live per-device readout, refreshed twice a second.</summary>
internal static class LiveCommand
{
    private sealed class Window
    {
        public long Updates, ImuSamples;
        public readonly RunningStats RotationRate = new();   // |w| deg/s
        public readonly RunningStats Speed = new();          // |v| mm/s
        public readonly RunningStats GyroRate = new();       // |gyro| deg/s
    }

    public static int Run(VrRuntime vr, Options options, CancellationToken ct)
    {
        var body = DeviceInfo.Enumerate(vr.System).Where(d => d.IsBodyDevice).ToList();
        if (body.Count == 0)
        {
            Console.Error.WriteLine("No HMD, controllers or trackers connected.");
            return 3;
        }
        var streams = OpenImuStreams(vr, body, report: null);

        var gate = new object();
        var windows = body.ToDictionary(d => d.Index, _ => new Window());

        using var poller = new PosePoller(vr.System, body.Select(d => d.Index), options.Hz, (in PoseSample s) =>
        {
            if (!s.Valid || !s.VelocityChanged) return;
            var w = s.AngularVelocity;
            var v = s.Velocity;
            lock (gate)
            {
                var win = windows[s.Device];
                win.Updates++;
                win.RotationRate.Add(Math.Sqrt(w.v0 * w.v0 + w.v1 * w.v1 + w.v2 * w.v2) * RadToDeg);
                win.Speed.Add(Math.Sqrt(v.v0 * v.v0 + v.v1 * v.v1 + v.v2 * v.v2) * 1000);
            }
        });
        using var imuReader = new ImuReader(streams, (ImuStream stream, double _, in ImuSample_t s) =>
        {
            var g = s.vGyro;
            lock (gate)
            {
                var win = windows[stream.Device.Index];
                win.ImuSamples++;
                win.GyroRate.Add(Math.Sqrt(g.v0 * g.v0 + g.v1 * g.v1 + g.v2 * g.v2) * RadToDeg);
            }
        });

        Console.Clear();
        Console.WriteLine("Live readout — Ctrl+C to stop. Values are per 0.5 s window.");
        Console.WriteLine();
        Console.WriteLine($"  {"Device",-14}{"upd/s",8}{"|w| deg/s",12}{"|v| mm/s",11}{"imu/s",8}{"|gyro| deg/s",14}");
        int top = Console.CursorTop;

        double start = HostClock.Now, last = start;
        poller.Start();
        imuReader.Start();
        while (!ct.IsCancellationRequested && (options.Seconds is not { } limit || HostClock.Now - start < limit))
        {
            Thread.Sleep(500);
            if (!vr.PumpEvents()) break;
            double now = HostClock.Now, dt = now - last;
            last = now;

            Dictionary<uint, Window> snapshot;
            lock (gate)
            {
                snapshot = new Dictionary<uint, Window>(windows);
                foreach (var d in body) windows[d.Index] = new Window();
            }

            Console.SetCursorPosition(0, top);
            foreach (var d in body)
            {
                var w = snapshot[d.Index];
                Console.WriteLine($"  {d.Kind,-14}{w.Updates / dt,8:0}{F(w.RotationRate.Mean, "0.000"),12}{F(w.Speed.Mean, "0.00"),11}{w.ImuSamples / dt,8:0}{F(w.GyroRate.Mean, "0.000"),14}   ");
            }
        }
        poller.Stop();
        imuReader.Stop();
        foreach (var s in streams) s.Dispose();
        return 0;
    }
}
