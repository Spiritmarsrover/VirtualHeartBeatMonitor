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
        bool blocks = a.Has("--blocks");

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
                var poseOnly = body.Where(d => imuStreams.All(s => s.Device.Index != d.Index)).ToList();
                if (imuStreams.Count > 0) Console.WriteLine($"Raw IMU: {string.Join(", ", imuStreams.Select(s => s.Device.Kind + " " + s.Device.Model))}");
                if (poseOnly.Count > 0) Console.WriteLine($"Poses (no raw IMU): {string.Join(", ", poseOnly.Select(d => d.Kind + " " + d.Model))}");
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

        // Poses for devices without raw IMU (Steam Frame): the heart-rate reference and movement mask.
        var poseDevices = vr == null ? new List<DeviceInfo>() : body.Where(d => imuStreams.All(s => s.Device.Index != d.Index)).ToList();
        var poseWriters = poseDevices.ToDictionary(d => d.Index, d =>
        {
            var w = new StreamWriter(Path.Combine(folder, $"poses_{d.Index}_{d.Kind}.csv"), false, new UTF8Encoding(false), 1 << 16);
            w.WriteLine(PoseHeader);
            return w;
        });
        var poseChannel = Channel.CreateUnbounded<PoseSample>(new UnboundedChannelOptions { SingleReader = true });
        var poseWriterTask = Task.Run(async () =>
        {
            await foreach (var p in poseChannel.Reader.ReadAllAsync()) poseWriters[p.Device].WriteLine(FormatPose(p, start));
        });
        long poseRows = 0;
        PosePoller? poller = null;
        if (poseDevices.Count > 0)
        {
            poller = new PosePoller(vr!.System, poseDevices.Select(d => d.Index), 1000, (in PoseSample p) =>
            {
                if (!p.VelocityChanged) return;
                Interlocked.Increment(ref poseRows);
                poseChannel.Writer.TryWrite(p);
            });
            poller.Start();
        }

        // Eyes: one task per camera, reconnecting if the stream drops.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var eyes = new[] { new EyeState(0), new EyeState(1) };
        var eyeTasks = blocks
            ? [Task.Run(() => BlocksLoop(eyes, baseUrl, folder, Clock, stop.Token))]
            : eyes.Select(e => Task.Run(() => EyeLoop(e, baseUrl, folder, Clock, stop.Token))).ToArray();

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
            if (poller != null) sb.Append($"  poses {Interlocked.Read(ref poseRows)}");
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
        poller?.Stop();
        poseChannel.Writer.Complete();
        poseWriterTask.Wait();
        foreach (var w in poseWriters.Values) w.Dispose();
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
            devices = body.Select(d => new
            {
                index = d.Index,
                kind = d.Kind,
                model = d.Model,
                serial = d.Serial,
                trackingSystem = d.TrackingSystem,
                source = imuStreams.Any(s => s.Device.Index == d.Index) ? "imu" : "pose",
            }),
            columns = new
            {
                eyes = "eye_<n>.bin: block means, see tools/EyePpg/EyeFile.cs; times are seconds since start (host clock, frame arrival)",
                imu = "host_t: s since start (host clock when read); sample_t: s (device clock); a: m/s^2; g: rad/s",
                poses = "Probe layout; host_t: s since start; rows only on new driver updates; w: rad/s",
            },
        };
        File.WriteAllText(Path.Combine(folder, "session.json"),
            JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true }));
        vr?.Dispose();

        Console.WriteLine($"Saved {eyes[0].Frames} + {eyes[1].Frames} eye frames, {imuRows} IMU rows and {poseRows} pose rows.");
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

    // --- raw block sums from the headset helper (tools/EyePpg/headset/eyeblocks.c, GET /blocks) ---

    private const int RecordBytes = 96 + 2 * 64 * 50;

    /// <summary>
    /// Reads eyeblocks records: exact 8x8 block sums of the raw frames, the 64 header bytes before
    /// each frame and the headset's capture time, mapped onto the PC clock (smallest observed delay).
    /// Also writes eye_&lt;n&gt;_meta.bin: per frame double headset time, double recording time, 64 bytes.
    /// </summary>
    private static async Task BlocksLoop(EyeState[] eyes, string baseUrl, string folder, Func<double> clock, CancellationToken ct)
    {
        var meta = new BinaryWriter?[2];
        double offset = double.NaN;
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var buf = new byte[RecordBytes];
        // The helper notices a frame on several 3.5 ms polls while the camera is still writing it
        // (3 records per frame at 60 fps). Records closer than 6 ms are one frame; the last is complete.
        var pending = new byte[2][] { new byte[RecordBytes], new byte[RecordBytes] };
        var hasPending = new bool[2];
        var firstT = new double[2];
        var lastT = new double[2];
        var sums = new ushort[64 * 50];

        void Flush(int eye)
        {
            if (!hasPending[eye]) return;
            hasPending[eye] = false;
            var p = pending[eye];
            int cols = BitConverter.ToUInt16(p, 6), rows = BitConverter.ToUInt16(p, 8);
            double when = firstT[eye] + offset;   // when the camera started delivering the frame
            Buffer.BlockCopy(p, 96, sums, 0, sums.Length * 2);
            var st = eyes[eye];
            st.Writer ??= new EyeFile.Writer(Path.Combine(folder, $"eye_{eye}.bin"));
            st.Writer.WriteBlocks(when, cols * EyeFile.Block, rows * EyeFile.Block, sums.AsSpan(0, cols * rows));
            meta[eye] ??= new BinaryWriter(File.Create(Path.Combine(folder, $"eye_{eye}_meta.bin")));
            meta[eye]!.Write(firstT[eye]);
            meta[eye]!.Write(when);
            meta[eye]!.Write(p, 32, 64);
            st.Frames++;
            st.LastFrame = when;
            long total = 0;
            foreach (ushort s in sums) total += s;
            st.LastMean = total / (double)(sums.Length * 64);
            st.Status = "receiving";
            if (st.NextSnapshot < SnapshotTimes.Length && when >= SnapshotTimes[st.NextSnapshot])
            {
                double at = SnapshotTimes[st.NextSnapshot++];
                _ = Task.Run(async () =>
                {
                    try { File.WriteAllBytes(Path.Combine(folder, $"eye_{eye}_{at:000}s.jpg"), await http.GetByteArrayAsync($"{baseUrl}/{eye}.jpg", ct)); }
                    catch (Exception) { /* snapshots are only for the plots */ }
                });
            }
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    foreach (var e in eyes) e.Status = "connecting";
                    using var resp = await http.GetAsync(baseUrl + "/blocks", HttpCompletionOption.ResponseHeadersRead, ct);
                    resp.EnsureSuccessStatusCode();
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    while (!ct.IsCancellationRequested)
                    {
                        await stream.ReadExactlyAsync(buf, ct);
                        double arrival = clock();
                        if (buf[0] != 'E' || buf[1] != 'Y' || buf[2] != 'B' || buf[3] != '1')
                            throw new InvalidDataException("eyeblocks stream out of sync");
                        int eye = buf[4];
                        if (eye > 1) continue;
                        double t = BitConverter.ToDouble(buf, 24);
                        double o = arrival - t;
                        offset = double.IsNaN(offset) ? o : Math.Min(offset + 1e-6, o);
                        if (!hasPending[eye] || t - lastT[eye] > 0.006)
                        {
                            Flush(eye);
                            firstT[eye] = t;
                            hasPending[eye] = true;
                        }
                        lastT[eye] = t;
                        Buffer.BlockCopy(buf, 0, pending[eye], 0, RecordBytes);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or EndOfStreamException or InvalidDataException)
                {
                    foreach (var e in eyes) { e.Status = "no connection"; e.Reconnects++; }
                }
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            Flush(0);
            Flush(1);
            foreach (var m in meta) m?.Dispose();
        }
    }

    private const string PoseHeader = "host_t,valid,result,px,py,pz,qw,qx,qy,qz,vx,vy,vz,wx,wy,wz";

    private static string FormatPose(in PoseSample s, double start)
    {
        var (px, py, pz) = PoseMath.Position(s.Matrix);
        var (qw, qx, qy, qz) = PoseMath.Rotation(s.Matrix);
        var v = s.Velocity;
        var w = s.AngularVelocity;
        return string.Create(CultureInfo.InvariantCulture,
            $"{s.HostTime - start:F6},{(s.Valid ? 1 : 0)},{(int)s.Result},{px:G9},{py:G9},{pz:G9},{qw:G9},{qx:G9},{qy:G9},{qz:G9},{v.v0:G9},{v.v1:G9},{v.v2:G9},{w.v0:G9},{w.v1:G9},{w.v2:G9}");
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
