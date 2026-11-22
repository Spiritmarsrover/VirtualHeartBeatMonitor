using HeartBeat.Tracking;
using Valve.VR;
using static HeartBeat.Probe.Common;

namespace HeartBeat.Probe;

/// <summary>Phase 0 check: devices, raw IMU availability, true pose update rates.</summary>
internal static class CheckCommand
{
    private sealed class PoseStats
    {
        public long Polls, Valid, VelocityUpdates, MatrixChanges;
        public readonly List<double> UpdateTimes = new();
        public readonly RunningStats[] AngularVelocity = [new(), new(), new()];
        public readonly RunningStats[] Velocity = [new(), new(), new()];
        public readonly Dictionary<ETrackingResult, long> Results = new();
    }

    private sealed class ImuStats
    {
        public readonly List<double> SampleTimes = new();
        public readonly List<double> HostTimes = new();
        public readonly RunningStats AccelMagnitude = new();
        public readonly RunningStats[] Gyro = [new(), new(), new()];
        public long OffScale, NonMonotonic;
    }

    public static int Run(VrRuntime vr, Options options, CancellationToken ct)
    {
        double seconds = options.Seconds ?? 10;
        var report = new Report();
        report.Line($"VirtualHeartBeatMonitor Probe (check) — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.Line($"SteamVR runtime: {vr.RuntimeVersion}");

        var devices = DeviceInfo.Enumerate(vr.System);
        report.Section("Devices");
        foreach (var d in devices)
            report.Line($"  #{d.Index,-2} {d.Kind,-13} model='{d.Model}' serial='{d.Serial}' system='{d.TrackingSystem}' maker='{d.Manufacturer}' type='{d.ControllerType}'");

        var body = devices.Where(d => d.IsBodyDevice).ToList();
        if (body.Count == 0)
        {
            report.Line("  No HMD, controllers or trackers connected.");
            return 3;
        }

        report.Section("Raw IMU (IVRIOBuffer)");
        var streams = OpenImuStreams(vr, body, report);

        // --- Measure ---------------------------------------------------------------------
        var poseStats = body.ToDictionary(d => d.Index, _ => new PoseStats());
        var imuStats = streams.ToDictionary(s => s, _ => new ImuStats());

        using var poller = new PosePoller(vr.System, body.Select(d => d.Index), options.Hz, (in PoseSample s) =>
        {
            var st = poseStats[s.Device];
            st.Polls++;
            st.Results[s.Result] = st.Results.GetValueOrDefault(s.Result) + 1;
            if (!s.Valid) return;
            st.Valid++;
            if (s.MatrixChanged) st.MatrixChanges++;
            if (!s.VelocityChanged) return;
            st.VelocityUpdates++;
            st.UpdateTimes.Add(s.HostTime);
            st.AngularVelocity[0].Add(s.AngularVelocity.v0 * RadToDeg);
            st.AngularVelocity[1].Add(s.AngularVelocity.v1 * RadToDeg);
            st.AngularVelocity[2].Add(s.AngularVelocity.v2 * RadToDeg);
            st.Velocity[0].Add(s.Velocity.v0 * 1000);
            st.Velocity[1].Add(s.Velocity.v1 * 1000);
            st.Velocity[2].Add(s.Velocity.v2 * 1000);
        });

        using var imuReader = new ImuReader(streams, (ImuStream stream, double hostTime, in ImuSample_t s) =>
        {
            var st = imuStats[stream];
            if (st.SampleTimes.Count > 0 && s.fSampleTime <= st.SampleTimes[^1]) st.NonMonotonic++;
            st.SampleTimes.Add(s.fSampleTime);
            st.HostTimes.Add(hostTime);
            st.AccelMagnitude.Add(Math.Sqrt(s.vAccel.v0 * s.vAccel.v0 + s.vAccel.v1 * s.vAccel.v1 + s.vAccel.v2 * s.vAccel.v2));
            st.Gyro[0].Add(s.vGyro.v0 * RadToDeg);
            st.Gyro[1].Add(s.vGyro.v1 * RadToDeg);
            st.Gyro[2].Add(s.vGyro.v2 * RadToDeg);
            if (s.unOffScaleFlags != 0) st.OffScale++;
        });

        report.Section($"Measuring for {seconds:0} s, polling at {options.Hz:0} Hz. Keep the devices still.");
        double start = HostClock.Now;
        poller.Start();
        imuReader.Start();
        while (!ct.IsCancellationRequested && HostClock.Now - start < seconds)
        {
            Thread.Sleep(250);
            if (!vr.PumpEvents()) break;
            Console.Write($"\r  {HostClock.Now - start:0.0} / {seconds:0} s ");
        }
        poller.Stop();
        imuReader.Stop();
        double elapsed = HostClock.Now - start;
        Console.WriteLine();

        double pollHz = poller.PollCount / elapsed;
        report.Line($"  Elapsed {elapsed:0.0} s, actual polling {pollHz:0} Hz, resyncs {poller.LatePolls}.");

        // --- Pose results ----------------------------------------------------------------
        report.Section("Pose updates per device");
        report.Line("  'updates/s' counts polls where velocity changed (= new data from the driver).");
        report.Line("  'matrix/s' counts polls where the pose matrix changed (includes extrapolation).");
        report.Line();
        report.Line($"  {"Device",-14}{"valid%",7}{"updates/s",11}{"matrix/s",10}{"med ms",8}{"p95 ms",8}{"max ms",8}   {"rot std x/y/z deg/s",-24}{"vel std x/y/z mm/s",-22}");
        foreach (var d in body)
        {
            var st = poseStats[d.Index];
            var iv = IntervalSummary.From(st.UpdateTimes);
            double validPct = st.Polls > 0 ? 100.0 * st.Valid / st.Polls : 0;
            string rot = string.Join("/", st.AngularVelocity.Select(a => F(a.StdDev, "0.00")));
            string vel = string.Join("/", st.Velocity.Select(a => F(a.StdDev, "0.0")));
            report.Line($"  {d.Kind,-14}{validPct,7:0}{F(iv.RateHz, "0"),11}{F(st.MatrixChanges / elapsed, "0"),10}{F(iv.MedianMs, "0.00"),8}{F(iv.P95Ms, "0.00"),8}{F(iv.MaxMs, "0.0"),8}   {rot,-24}{vel,-22}");
        }
        report.Line();
        foreach (var d in body)
        {
            var results = string.Join(", ", poseStats[d.Index].Results.Select(kv => $"{kv.Key.ToString().Replace("TrackingResult_", "")}={kv.Value}"));
            report.Line($"  {d.Kind,-14}tracking results: {results}");
        }

        // --- IMU results -----------------------------------------------------------------
        report.Section("Raw IMU samples");
        if (streams.Count == 0) report.Line("  (none)");
        foreach (var stream in streams)
        {
            var st = imuStats[stream];
            var byDevice = IntervalSummary.From(st.SampleTimes);
            report.Line($"  {stream.Device.Kind} ({stream.Path})");
            report.Line($"    samples {st.SampleTimes.Count}, host rate {F(st.SampleTimes.Count / elapsed, "0")} Hz, device-clock rate {F(byDevice.RateHz, "0")} Hz");
            report.Line($"    device dt median {F(byDevice.MedianMs, "0.000")} ms, p95 {F(byDevice.P95Ms, "0.000")} ms, max {F(byDevice.MaxMs, "0.0")} ms, non-monotonic {st.NonMonotonic}");
            report.Line($"    |accel| mean {F(st.AccelMagnitude.Mean, "0.000")} (≈9.81 → m/s², ≈1 → g), std {F(st.AccelMagnitude.StdDev, "0.0000")}");
            report.Line($"    gyro std x/y/z {string.Join("/", st.Gyro.Select(g => F(g.StdDev, "0.000")))} deg/s, off-scale samples {st.OffScale}, read errors {imuReader.ReadErrors(stream)}");
        }

        // --- Interpretation --------------------------------------------------------------
        report.Section("Notes");
        if (streams.Count == 0)
            report.Line("  * Raw IMU is not readable on this setup; pose velocities will be the signal source.");
        else if (imuStats.Values.All(s => s.SampleTimes.Count == 0))
            report.Line("  * IMU streams opened but delivered no samples; the driver may not publish to them.");
        else
            report.Line("  * Raw IMU is readable. This is the preferred signal source for these devices.");

        foreach (var d in body)
        {
            var iv = IntervalSummary.From(poseStats[d.Index].UpdateTimes);
            if (double.IsNaN(iv.RateHz))
                report.Line($"  * {d.Kind}: too few pose updates (device asleep, off, or not tracking?).");
            else if (iv.RateHz > 0.9 * pollHz)
                report.Line($"  * {d.Kind}: velocity changed on ~every poll. Rerun with --hz 2000 to tell real updates from per-poll extrapolation.");
            else
                report.Line($"  * {d.Kind}: ~{iv.RateHz:0} distinct pose updates/s (vs {pollHz:0} Hz polling).");
        }

        string hmd = devices.FirstOrDefault(d => d.Class == ETrackedDeviceClass.HMD)?.Model ?? "no-hmd";
        string path = report.Save(Path.Combine(Environment.CurrentDirectory, "probe-reports"),
            $"probe_{DateTime.Now:yyyyMMdd_HHmmss}_{SafeFileName(hmd)}.txt");
        Console.WriteLine();
        Console.WriteLine($"Report saved to {path}");

        foreach (var s in streams) s.Dispose();
        return 0;
    }
}
