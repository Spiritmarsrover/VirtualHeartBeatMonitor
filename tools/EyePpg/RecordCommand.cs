using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.EyePpg;

/// <summary>
/// Records both eye cameras (block means, see <see cref="EyeFile"/>) and, when SteamVR is running,
/// the raw IMU of every body device in the same folder and on the same clock. The IMU files use
/// Probe's format, so Replay and <c>EyePpg analyze</c> can read them.
/// </summary>
internal static class RecordCommand
{
    private const string ImuHeader = "host_t,sample_t,ax,ay,az,gx,gy,gz,offscale";
    private static readonly double[] SnapshotTimes = [5, 30, 60, 90, 120, 180, 240];

    private sealed class EyeState(int index)
    {
        public readonly int Index = index;
        public EyeFile.Writer? Writer;
        public long Frames, Errors, Reconnects;
        public double LastFrame = double.NaN, LastMean = double.NaN;
        public int NextSnapshot;
        public string Status = "connecting";
    }

    public static int Run(Args a, CancellationToken ct)
    {
        string baseUrl = a.Get("--url", "http://frame:8090").TrimEnd('/');
        double seconds = a.GetDouble("--seconds", 120);
        string label = a.Get("--label", "");
        bool noImu = a.Has("--no-imu");
        bool ask = !a.Has("--no-pulse") && !Console.IsInputRedirected;

        string name = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_eyes"
            + (label.Length > 0 ? "_" + SafeName(label) : "");
        string folder = Path.Combine(Environment.CurrentDirectory, "recordings", name);

        // Check the headset stream before creating anything.
        Console.WriteLine($"Eye streams: {baseUrl}/0 and {baseUrl}/1");
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string status = http.GetStringAsync(baseUrl + "/status", ct).GetAwaiter().GetResult();
            Console.WriteLine("Headset stream status: " + OneLine(status));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Can't reach {baseUrl}/status ({e.Message}).");
            Console.Error.WriteLine("Is FrameEyeCameraFeed running on the headset, and is the headset on the same network? Try --url http://<headset-ip>:8090");
            return 2;
        }

        // Optional IMU via SteamVR.
        VrRuntime? vr = null;
        List<ImuStream> imuStreams = new();
        List<DeviceInfo> body = new();
        if (!noImu)
        {
            try
            {
                vr = VrRuntime.Connect();
                body = DeviceInfo.Enumerate(vr.System).Where(d => d.IsBodyDevice).ToList();
                if (vr.IOBuffer != null)
                    foreach (var d in body)
                        if (ImuStream.TryOpen(vr.IOBuffer, d).Stream is { } s) imuStreams.Add(s);
                Console.WriteLine(imuStreams.Count > 0
                    ? $"Raw IMU: {string.Join(", ", imuStreams.Select(s => s.Device.Kind + " " + s.Device.Model))}"
                    : "Raw IMU: not available from SteamVR, recording the eyes only (give a reference pulse below).");
            }
            catch (Exception e) when (e is VrInitException or DllNotFoundException)
            {
                Console.WriteLine($"SteamVR not available ({e.Message}); recording the eyes only.");
                vr?.Dispose();
                vr = null;
            }
        }

        double? pulseBefore = ask ? AskPulse("Pulse counted BEFORE recording (BPM, Enter to skip): ") : null;

        Directory.CreateDirectory(folder);
        Console.WriteLine($"Recording to {folder}");
        Console.WriteLine("Sit still, look straight ahead, relax your face. Ctrl+C stops early.");

        double start = HostClock.Now;
        double Clock() => HostClock.Now - start;
        DateTime startedUtc = DateTime.UtcNow;

        // IMU: write CSV rows from a channel, same columns as Probe.
        var imuWriters = imuStreams.ToDictionary(s => s, s => OpenCsv(folder, $"imu_{s.Device.Index}_{s.Device.Kind}.csv"));
        var channel = Channel.CreateUnbounded<(ImuStream s, double t, ImuSample_t x)>(new UnboundedChannelOptions { SingleReader = true });
        var imuWriterTask = Task.Run(async () =>
        {
            await foreach (var (s, t, x) in channel.Reader.ReadAllAsync())
                imuWriters[s].WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{t:F6},{x.fSampleTime:F7},{x.vAccel.v0:G10},{x.vAccel.v1:G10},{x.vAccel.v2:G10},{x.vGyro.v0:G10},{x.vGyro.v1:G10},{x.vGyro.v2:G10},{x.unOffScaleFlags}"));
        });
        long imuRows = 0;
        ImuReader? imuReader = null;
        if (imuStreams.Count > 0)
        {
            foreach (var s in imuStreams) s.DiscardBacklog();
            imuReader = new ImuReader(imuStreams, (ImuStream s, double hostTime, in ImuSample_t x) =>
            {
                Interlocked.Increment(ref imuRows);
                channel.Writer.TryWrite((s, hostTime - start, x));
            });
            imuReader.Start();
        }

        // Eyes: one task per camera, reconnecting if the stream drops.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var eyes = new[] { new EyeState(0), new EyeState(1) };
        var eyeTasks = eyes.Select(e => Task.Run(() => EyeLoop(e, baseUrl, folder, Clock, stop.Token))).ToArray();

        while (!ct.IsCancellationRequested && Clock() < seconds)
        {
            Thread.Sleep(500);
            vr?.PumpEvents();
            double now = Clock();
            var sb = new StringBuilder($"\r  {now,4:0} / {seconds:0} s ");
            foreach (var e in eyes)
            {
                double age = now - e.LastFrame;
                string state = double.IsNaN(e.LastFrame) || age > 2 ? e.Status : $"{e.Frames / Math.Max(1, now):0} fps, mean {e.LastMean:0}";
                sb.Append($"  eye{e.Index}: {state,-26}");
            }
            if (imuReader != null) sb.Append($"  imu {Interlocked.Read(ref imuRows)}");
            Console.Write(sb.ToString().PadRight(110));
        }
        double elapsed = Clock();
        stop.Cancel();
        try { Task.WaitAll(eyeTasks, TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        imuReader?.Stop();
        channel.Writer.Complete();
        imuWriterTask.Wait();
        foreach (var w in imuWriters.Values) w.Dispose();
        foreach (var s in imuStreams) s.Dispose();
        foreach (var e in eyes) e.Writer?.Dispose();
        Console.WriteLine();

        double? pulseAfter = ask ? AskPulse("Pulse counted AFTER recording (BPM, Enter to skip): ") : null;

        var session = new
        {
            kind = "eyes",
            startedUtc,
            durationSeconds = elapsed,
            label,
            note = a.Get("--note", ""),
            pulseBeforeBpm = pulseBefore,
            pulseAfterBpm = pulseAfter,
            eyeUrl = baseUrl,
            eyes = eyes.Select(e => new { index = e.Index, frames = e.Frames, errors = e.Errors, reconnects = e.Reconnects, file = $"eye_{e.Index}.bin" }),
            runtimeVersion = vr?.RuntimeVersion,
            devices = imuStreams.Select(s => new
            {
                index = s.Device.Index,
                kind = s.Device.Kind,
                model = s.Device.Model,
                serial = s.Device.Serial,
                trackingSystem = s.Device.TrackingSystem,
                imuPath = s.Path,
            }),
            columns = new
            {
                eyes = "eye_<n>.bin: block means, see tools/EyePpg/EyeFile.cs; times are seconds since start (host clock, frame arrival)",
                imu = "host_t: s since start (host clock when read); sample_t: s (device clock); a: m/s^2; g: rad/s",
            },
        };
        File.WriteAllText(Path.Combine(folder, "session.json"),
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
        vr?.Dispose();

        Console.WriteLine($"Saved {eyes[0].Frames} + {eyes[1].Frames} eye frames and {imuRows} IMU rows.");
        if (eyes.Any(e => e.Frames == 0))
            Console.WriteLine("An eye stream sent nothing: the cameras only run while the headset is worn.");
        Console.WriteLine($"Analyse with:  EyePpg analyze \"{folder}\"");
        return 0;
    }

    private static async Task EyeLoop(EyeState e, string baseUrl, string folder, Func<double> clock, CancellationToken ct)
    {
        string url = $"{baseUrl}/{e.Index}";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                e.Status = "connecting";
                await Mjpeg.ReadAsync(url, clock, (jpeg, length, t) =>
                {
                    GreyFrame frame;
                    try { frame = GreyFrame.Decode(jpeg, length); }
                    catch (ArgumentException) { e.Errors++; return; } // a torn / bad JPEG
                    e.Writer ??= new EyeFile.Writer(Path.Combine(folder, $"eye_{e.Index}.bin"));
                    e.Writer.Write(t, length, frame);
                    e.Frames++;
                    e.LastFrame = t;
                    long sum = 0;
                    foreach (byte p in frame.Pixels) sum += p;
                    e.LastMean = (double)sum / frame.Pixels.Length;
                    if (e.NextSnapshot < SnapshotTimes.Length && t >= SnapshotTimes[e.NextSnapshot])
                    {
                        File.WriteAllBytes(Path.Combine(folder, $"eye_{e.Index}_{SnapshotTimes[e.NextSnapshot]:000}s.jpg"), jpeg[..length]);
                        e.NextSnapshot++;
                    }
                    e.Status = "receiving";
                }, ct);
                e.Status = "stream ended";
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                e.Status = "no connection";
            }
            e.Reconnects++;
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static StreamWriter OpenCsv(string folder, string file)
    {
        var w = new StreamWriter(Path.Combine(folder, file), false, new UTF8Encoding(false), 1 << 16);
        w.WriteLine(ImuHeader);
        return w;
    }

    private static double? AskPulse(string prompt)
    {
        Console.Write(prompt);
        string? line = Console.ReadLine();
        return double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) && bpm > 0 ? bpm : null;
    }

    private static string OneLine(string s)
    {
        s = string.Join(' ', s.Split((char[])['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    private static string SafeName(string text) =>
        string.Concat(text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsWhiteSpace(c) ? '-' : c));
}
