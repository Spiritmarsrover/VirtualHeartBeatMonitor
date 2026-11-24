using System.Globalization;
using System.Text;
using System.Text.Json;
using HeartBeat.Core;
using HeartBeat.Core.Dsp;
using HeartBeat.Core.Heart;
using HeartBeat.Core.Recordings;

namespace HeartBeat.EyePpg;

/// <summary>
/// Is the pulse visible in the eye cameras? (Method from the 2026-11-22 Frame tests.)
///
/// 1. Skin region (ROI) per eye: blocks that are lit (mean > 40) and steady (relative brightness
///    std over the recording &lt; 8 %) — eyelids and cheek, not the moving eye. Their relative
///    brightness is averaged into one signal at 30 Hz.
/// 2. Frames where that signal jumps (blinks, squints, dropped frames) and head movement (from the
///    pose/IMU recording) are cut out; band-pass 0.7–3.5 Hz.
/// 3. 12 s sliding FFT, one window per second: the eye's own peak (no reference used) and, given a
///    reference rate, how much power sits at it compared to decoy rates nearby (1x = noise).
/// 4. A per-block map of the same "power at the reference" shows where on the face the pulse is.
///
/// Reference: the headset motion in the same folder (EyePpg records poses), or --poses &lt;Probe
/// folder&gt;, else --ref / the counted pulse. --inject adds a known fake pulse to check the method.
/// </summary>
internal static class AnalyzeCommand
{
    private const double Fs = 30;
    private const double Low = 0.7, High = 3.5;       // band-pass, Hz
    private const double PeakLo = 40, PeakHi = 180;   // BPM searched for the eye's own peak
    private const double WindowSeconds = 12;
    private const double MaxMasked = 0.25;
    private const int Nfft = 2048, BlockNfft = 1024;
    private const double HalfBand = 0.05;             // Hz (±3 BPM) counted as "at the rate"
    private const double RoiMinMean = 40, RoiMaxStd = 0.08;
    private static readonly double[] DecoyOffsets = [-0.3, -0.2, 0.2, 0.3, 0.4, 0.5];

    private sealed record Eye(int Index, EyeFile File, double T0, int N, double[] Roi, float[][] Blocks, bool[] Masked,
        bool[] ValidBlock, int[] RoiBlocks, double FrameRate, double JpegKb, double Brightness, double RoiNoisePct);

    private sealed class Series
    {
        public required string Name;
        public readonly SortedDictionary<int, (double bpm, double prom)> Peaks = new();
        public readonly List<double> AtRef = new(), AtDecoys = new(), Errors = new();
        public double[] AvgSpectrum = new double[Nfft / 2 + 1];
        public int Windows;
    }

    public static int Run(Args a)
    {
        string folder = a.Positional ?? throw new ArgumentException("analyze needs a recording folder");
        if (!Directory.Exists(folder)) throw new ArgumentException($"No folder {folder}");
        var report = new StringBuilder();
        void Say(string s = "") { Console.WriteLine(s); report.AppendLine(s); }

        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "session.json")));
        double? pulseBefore = Num(session.RootElement, "pulseBeforeBpm"), pulseAfter = Num(session.RootElement, "pulseAfterBpm");

        double injectBpm = double.NaN, injectAmp = 0.003;
        if (a.Has("--inject"))
        {
            var parts = a.Get("--inject", "").Split(':');
            injectBpm = double.Parse(parts[0], CultureInfo.InvariantCulture);
            if (parts.Length > 1) injectAmp = double.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        // --- reference heart rate and head movement ---------------------------------------------
        var motionRef = new SortedList<double, double>();
        var moving = new List<(double from, double to)>();
        if (!a.Has("--no-imu-ref"))
        {
            string poseFolder = a.Get("--poses", folder);
            double offset = poseFolder == folder ? 0 : (Started(poseFolder) - Started(folder)).TotalSeconds;
            MotionReference(poseFolder, offset, motionRef, moving, Say);
        }
        double constRef = a.GetDouble("--ref", double.NaN);
        string refSource;
        if (!double.IsNaN(injectBpm)) { constRef = injectBpm; refSource = $"injected test pulse {injectBpm:0} BPM, {injectAmp * 100:0.##} %"; }
        else if (!double.IsNaN(constRef)) refSource = $"--ref {constRef:0} BPM";
        else if (motionRef.Values.Any(v => !double.IsNaN(v))) refSource = "headset motion (heart-rate engine)";
        else if (pulseBefore != null || pulseAfter != null)
        {
            constRef = new[] { pulseBefore, pulseAfter }.Where(p => p != null).Average(p => p!.Value);
            refSource = $"counted pulse {constRef:0} BPM";
        }
        else refSource = "none";
        Func<double, double> refAt = !double.IsNaN(constRef) ? _ => constRef : t => Lookup(motionRef, t);
        bool haveRef = refSource != "none";

        Say($"Recording: {folder}");
        Say($"Reference: {refSource}");
        if (pulseBefore != null || pulseAfter != null) Say($"Counted pulse: before {pulseBefore?.ToString("0") ?? "-"}, after {pulseAfter?.ToString("0") ?? "-"} BPM");

        var eyes = new List<Eye>();
        for (int i = 0; i < 2; i++)
        {
            string path = Path.Combine(folder, $"eye_{i}.bin");
            if (!File.Exists(path)) continue;
            var file = EyeFile.Load(path);
            if (file.Frames.Count < Fs * WindowSeconds * 2) { Say($"eye {i}: only {file.Frames.Count} frames, skipped"); continue; }
            eyes.Add(Prepare(i, file, moving, injectBpm, injectAmp));
        }
        if (eyes.Count == 0) { Say("No usable eye data."); return 3; }

        // --- sliding windows on a shared time grid ------------------------------------------------
        int w = (int)(WindowSeconds * Fs);
        int binLo = (int)Math.Ceiling(Low * Nfft / Fs), binHi = (int)Math.Floor(High * Nfft / Fs);
        int pkLo = (int)Math.Ceiling(PeakLo / 60 * Nfft / Fs), pkHi = (int)Math.Floor(PeakHi / 60 * Nfft / Fs);
        var series = eyes.Select(e => new Series { Name = $"eye {e.Index}" }).Append(new Series { Name = "both eyes" }).ToList();
        int first = (int)Math.Ceiling(eyes.Max(e => e.T0) + WindowSeconds), last = (int)Math.Floor(eyes.Min(e => e.T0 + e.N / Fs));
        var timeline = new List<(double t, double reference, double[] eyes, double both)>();

        for (int sec = first; sec <= last; sec++)
        {
            double reference = haveRef ? refAt(sec - WindowSeconds / 2) : double.NaN;
            var spectra = new double[eyes.Count][];
            for (int k = 0; k < eyes.Count; k++)
            {
                var e = eyes[k];
                int start = (int)Math.Round((sec - WindowSeconds - e.T0) * Fs);
                if (start < 0 || start + w > e.N || e.Masked.AsSpan(start, w).Count(true) > MaxMasked * w) continue;
                var p = Fft.HannPowerSpectrum(e.Roi.AsSpan(start, w), Nfft);
                double band = 0;
                for (int b = binLo; b <= binHi; b++) band += p[b];
                if (band <= 0) continue;
                for (int b = 0; b < p.Length; b++) p[b] /= band;
                spectra[k] = p;
            }
            var valid = spectra.Where(s => s != null).ToList();
            var all = valid.Count == 0 ? null : Enumerable.Range(0, Nfft / 2 + 1).Select(b => valid.Sum(s => s[b]) / valid.Count).ToArray();
            var perEye = new double[eyes.Count];
            for (int k = 0; k <= eyes.Count; k++)
            {
                var p = k < eyes.Count ? spectra[k] : all;
                double bpm = double.NaN;
                if (p != null)
                {
                    var s = series[k];
                    s.Windows++;
                    for (int b = 0; b < p.Length; b++) s.AvgSpectrum[b] += p[b];
                    int arg = pkLo;
                    for (int b = pkLo; b <= pkHi; b++) if (p[b] > p[arg]) arg = b;
                    bpm = arg * Fs / Nfft * 60;
                    s.Peaks[sec] = (bpm, p[arg] / Median(p[pkLo..(pkHi + 1)]));
                    if (!double.IsNaN(reference))
                    {
                        s.Errors.Add(Math.Abs(bpm - reference));
                        s.AtRef.Add(Enrichment(p, reference / 60, Nfft, binLo, binHi));
                        foreach (double off in DecoyOffsets)
                        {
                            double f = reference / 60 + off;
                            if (f > 0.75 && f < 2.9) s.AtDecoys.Add(Enrichment(p, f, Nfft, binLo, binHi));
                        }
                    }
                }
                if (k < eyes.Count) perEye[k] = bpm; else timeline.Add((sec, reference, perEye, bpm));
            }
        }

        // --- report --------------------------------------------------------------------------------
        foreach (var e in eyes)
        {
            Say();
            Say($"== Eye {e.Index} ==");
            Say($"  {e.File.Width}x{e.File.Height}, {e.File.Frames.Count} frames, {e.FrameRate:0.0} fps, JPEG ~{e.JpegKb:0.0} KB/frame, mean brightness {e.Brightness:0}");
            Say($"  skin region: {e.RoiBlocks.Length} blocks; cut out (blinks, gaps, movement): {e.Masked.Count(m => m) * 100.0 / e.N:0} %; noise in the pulse band {e.RoiNoisePct:0.000} %");
        }
        Say();
        Say(haveRef ? "== Pulse in the eyes vs the reference (each eye's own peak; no reference used to find it) ==" : "== Eye peaks (no reference) ==");
        foreach (var s in series)
        {
            if (s.Windows == 0) { Say($"  {s.Name,-9}: no usable windows"); continue; }
            if (s.Errors.Count == 0) { Say($"  {s.Name,-9}: {s.Windows} windows, median peak {Median(s.Peaks.Values.Select(v => v.bpm)):0} BPM"); continue; }
            double atRef = s.AtRef.Average(), atDecoy = s.AtDecoys.Count > 0 ? s.AtDecoys.Average() : double.NaN;
            double within5 = 100.0 * s.Errors.Count(x => x <= 5) / s.Errors.Count;
            Say($"  {s.Name,-9}: {s.Windows,3} windows | power at reference {atRef:0.0}x vs decoys {atDecoy:0.0}x | peak within 5 BPM {within5:0} % (chance ~7 %) | mean error {s.Errors.Average():0.0} BPM | {Verdict(atRef / atDecoy, within5)}");
        }

        var csv = new StringBuilder("t,ref_bpm," + string.Join(",", eyes.Select(e => $"eye{e.Index}_bpm")) + ",eyes_bpm\n");
        foreach (var r in timeline)
            csv.Append(string.Create(CultureInfo.InvariantCulture, $"{r.t},{r.reference:0.0},{string.Join(",", r.eyes.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture)))},{r.both:0.0}\n"));
        File.WriteAllText(Path.Combine(folder, "eyeppg_timeline.csv"), csv.ToString().Replace("NaN", ""));

        // --- plots: per-block map, spectra, timeline, waveform ----------------------------------
        foreach (var e in eyes)
        {
            double[] values;
            string title;
            if (haveRef)
            {
                values = BlockMap(e, refAt);
                title = $"Eye {e.Index}: power at the reference rate per block (1x = noise); cyan = skin region";
            }
            else
            {
                values = new double[e.File.BlockCount];
                foreach (int k in e.RoiBlocks) values[k] = 2;
                title = $"Eye {e.Index}: skin region used (no reference for a pulse map)";
            }
            string snapshot = Directory.GetFiles(folder, $"eye_{e.Index}_*s.jpg").Order().FirstOrDefault() ?? "";
            Plots.PulseMap(Path.Combine(folder, $"eyeppg_map_eye{e.Index}.png"), snapshot, e.File, values, e.ValidBlock, e.RoiBlocks, title);
        }
        double refMedian = haveRef ? Median(timeline.Select(r => r.reference)) : double.NaN;
        Plots.Spectra(Path.Combine(folder, "eyeppg_spectrum.png"),
            series.Where(s => s.Windows > 0).Select(s => (s.Name, s.AvgSpectrum.Select(v => v / s.Windows).ToArray())).ToList(), Fs, Nfft, refMedian);
        Plots.Timeline(Path.Combine(folder, "eyeppg_timeline.png"), timeline, eyes.Select(e => e.Index).ToArray());
        var best = eyes.MaxBy(e => e.RoiBlocks.Length)!;
        Plots.Waveform(Path.Combine(folder, "eyeppg_waveform.png"), best.Roi, best.Masked, Fs, best.Index);

        File.WriteAllText(Path.Combine(folder, "eyeppg_report.txt"), report.ToString());
        Console.WriteLine();
        Console.WriteLine($"Wrote eyeppg_report.txt, eyeppg_timeline.csv and eyeppg_*.png to {folder}");
        return 0;
    }

    private static string Verdict(double ratio, double within5) =>
        ratio >= 2 && within5 >= 50 ? "PULSE FOUND"
        : ratio >= 1.4 || within5 >= 25 ? "weak pulse"
        : "no pulse above chance";

    // ---------------------------------------------------------------------------------------------

    private static Eye Prepare(int index, EyeFile f, List<(double from, double to)> moving, double injectBpm, double injectAmp)
    {
        int nb = f.BlockCount;
        double t0 = Math.Ceiling(f.Times[0] * Fs) / Fs;
        int n = (int)Math.Floor((f.Times[^1] - t0) * Fs) + 1;

        // Average the frames landing in each 1/30 s slot (60 fps streams average two frames).
        var x = new float[nb][];
        for (int k = 0; k < nb; k++) x[k] = new float[n];
        var count = new int[n];
        for (int i = 0; i < f.Frames.Count; i++)
        {
            int j = (int)Math.Round((f.Times[i] - t0) * Fs);
            if (j < 0 || j >= n) continue;
            count[j]++;
            var fr = f.Frames[i];
            for (int k = 0; k < nb; k++) x[k][j] += (float)(fr[k] / EyeFile.Scale);
        }
        var gap = new bool[n];
        for (int j = 0; j < n; j++)
        {
            gap[j] = count[j] == 0;
            if (count[j] > 1) for (int k = 0; k < nb; k++) x[k][j] /= count[j];
        }
        for (int k = 0; k < nb; k++) Interpolate(x[k], gap);
        // Frames arrive unevenly (network), so single empty slots are normal and just interpolated;
        // only runs of 100 ms or more count as real gaps.
        var realGap = new bool[n];
        for (int j = 0; j < n;)
        {
            if (!gap[j]) { j++; continue; }
            int end = j;
            while (end < n && gap[end]) end++;
            if (end - j >= (int)Math.Round(0.1 * Fs)) for (int q = j; q < end; q++) realGap[q] = true;
            j = end;
        }
        gap = realGap;

        if (!double.IsNaN(injectBpm))
        {
            double phase = 0;
            for (int j = 0; j < n; j++)
            {
                phase += 2 * Math.PI * injectBpm / 60 * (1 + 0.03 * Math.Sin(2 * Math.PI * 0.1 * j / Fs)) / Fs;
                double p = Math.Sin(phase) + 0.4 * Math.Sin(2 * phase - 0.8);
                for (int k = 0; k < nb; k++) x[k][j] *= (float)(1 + injectAmp * p);
            }
        }

        // Skin region: lit and steady blocks.
        var mean = new double[nb];
        var relStd = new double[nb];
        for (int k = 0; k < nb; k++)
        {
            double m = 0;
            for (int j = 0; j < n; j++) m += x[k][j];
            m /= n;
            double v = 0;
            for (int j = 0; j < n; j++) { double r = x[k][j] / m - 1; v += r * r; }
            mean[k] = m;
            relStd[k] = m > 0 ? Math.Sqrt(v / n) : double.PositiveInfinity;
        }
        var validBlock = mean.Select(m => m > 12 && m < 240).ToArray();
        var roi = Enumerable.Range(0, nb).Where(k => mean[k] > RoiMinMean && mean[k] < 240 && relStd[k] < RoiMaxStd).ToArray();
        if (roi.Length < 20) // dim or restless picture: take the 20 steadiest lit blocks
            roi = Enumerable.Range(0, nb).Where(k => mean[k] > RoiMinMean && mean[k] < 240).OrderBy(k => relStd[k]).Take(20).ToArray();

        var s = new double[n];
        for (int j = 0; j < n; j++)
        {
            double acc = 0;
            foreach (int k in roi) acc += x[k][j] / mean[k];
            s[j] = acc / Math.Max(1, roi.Length) - 1;
        }

        // Cut out jumps in the skin signal (blinks, squints), gaps and head movement.
        var d = new double[n];
        for (int j = 1; j < n; j++) d[j] = Math.Abs(s[j] - s[j - 1]);
        double med = Median(d.Skip(1));
        var bad = new bool[n];
        for (int j = 0; j < n; j++)
        {
            double t = t0 + j / Fs;
            bad[j] = gap[j] || d[j] > med * (1 + 8 * 1.4826) || moving.Any(m => t >= m.from && t <= m.to);
        }
        var masked = new bool[n];
        int pad = (int)(0.2 * Fs);
        for (int j = 0; j < n; j++)
            if (bad[j]) for (int q = -pad; q <= pad; q++) if (j + q >= 0 && j + q < n) masked[j + q] = true;

        var roiSignal = BandPass(s, masked);
        double noise = 0; int nn = 0;
        for (int j = 0; j < n; j++) if (!masked[j]) { noise += roiSignal[j] * roiSignal[j]; nn++; }

        // Per-block signals for the map (lit blocks only).
        var blocks = new float[nb][];
        Parallel.For(0, nb, k =>
        {
            if (!validBlock[k]) { blocks[k] = []; return; }
            var r = new double[n];
            for (int j = 0; j < n; j++) r[j] = x[k][j] / mean[k] - 1;
            blocks[k] = BandPass(r, masked).Select(v => (float)v).ToArray();
        });

        double span = f.Times[^1] - f.Times[0];
        return new Eye(index, f, t0, n, roiSignal, blocks, masked, validBlock, roi,
            (f.Times.Count - 1) / span, Median(f.JpegBytes.Select(b => b / 1024.0)), mean.Average(), Math.Sqrt(noise / Math.Max(1, nn)) * 100);
    }

    /// <summary>Bridges cut-out samples, band-passes 0.7–3.5 Hz, then zeroes the cut-out samples.</summary>
    private static double[] BandPass(double[] input, bool[] masked)
    {
        var s = (double[])input.Clone();
        int n = s.Length, last = -1;
        for (int j = 0; j <= n; j++)
        {
            if (j < n && masked[j]) continue;
            if (j - last > 1)
            {
                double a = last >= 0 ? s[last] : (j < n ? s[j] : 0), b = j < n ? s[j] : a;
                for (int i = last + 1; i < j; i++) s[i] = a + (b - a) * (i - last) / (j - last);
            }
            last = j;
        }
        var bp = Butterworth.BandPass(Fs, Low, High, 2);
        var y = new double[n];
        for (int j = 0; j < n; j++) { y[j] = bp.Process(s[j]); if (masked[j]) y[j] = 0; }
        return y;
    }

    /// <summary>Per block: average power at the reference rate over all usable windows (1x = noise).</summary>
    private static double[] BlockMap(Eye e, Func<double, double> refAt)
    {
        int w = (int)(WindowSeconds * Fs);
        int binLo = (int)Math.Ceiling(Low * BlockNfft / Fs), binHi = (int)Math.Floor(High * BlockNfft / Fs);
        var starts = new List<(int start, double hz)>();
        for (int s = 0; s + w <= e.N; s += (int)Fs)
        {
            double r = refAt(e.T0 + (s + w / 2.0) / Fs);
            if (!double.IsNaN(r) && e.Masked.AsSpan(s, w).Count(true) <= MaxMasked * w) starts.Add((s, r / 60));
        }
        var map = new double[e.File.BlockCount];
        Parallel.For(0, map.Length, k =>
        {
            map[k] = double.NaN;
            if (!e.ValidBlock[k] || starts.Count == 0) return;
            var seg = new double[w];
            double sum = 0;
            foreach (var (start, hz) in starts)
            {
                for (int i = 0; i < w; i++) seg[i] = e.Blocks[k][start + i];
                var p = Fft.HannPowerSpectrum(seg, BlockNfft);
                double band = 0;
                for (int b = binLo; b <= binHi; b++) band += p[b];
                if (band > 0) { for (int b = 0; b < p.Length; b++) p[b] /= band; sum += Enrichment(p, hz, BlockNfft, binLo, binHi); }
            }
            map[k] = sum / starts.Count;
        });
        return map;
    }

    /// <summary>Share of band power within ±HalfBand of f, relative to an even spread (1 = noise). p is normalised to the band.</summary>
    private static double Enrichment(double[] p, double f, int nfft, int binLo, int binHi)
    {
        int lo = Math.Max(binLo, (int)Math.Ceiling((f - HalfBand) * nfft / Fs));
        int hi = Math.Min(binHi, (int)Math.Floor((f + HalfBand) * nfft / Fs));
        if (hi < lo) return double.NaN;
        double inside = 0;
        for (int b = lo; b <= hi; b++) inside += p[b];
        return inside / ((double)(hi - lo + 1) / (binHi - binLo + 1));
    }

    /// <summary>
    /// Heart rate from the headset motion (the app's engine) and the moments the head moved.
    /// Poses (Frame) are on the recording's host clock; raw IMU is mapped from the device clock.
    /// </summary>
    private static void MotionReference(string folder, double offset, SortedList<double, double> bpm, List<(double, double)> moving, Action<string> say)
    {
        Recording rec;
        try { rec = Recording.Load(folder); }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { return; }
        var head = rec.Devices.FirstOrDefault(d => d.Kind == "HMD");
        if (head == null || head.Imu.Count < 1000) return;

        double headOffset = offset;
        if (!head.FromPose)
        {
            var offsets = new List<double>();
            foreach (var line in File.ReadLines(Path.Combine(folder, $"imu_{head.Index}_{head.Kind}.csv")).Skip(1))
            {
                var p = line.Split(',');
                if (p.Length > 2 && double.TryParse(p[0], CultureInfo.InvariantCulture, out double h) && double.TryParse(p[1], CultureInfo.InvariantCulture, out double dv))
                    offsets.Add(h - dv);
            }
            headOffset += Median(offsets);
        }

        // Same devices as the app: all pose devices share the host clock; raw IMU only the head.
        var engine = new MonitorEngine();
        var used = head.FromPose ? rec.Devices.Where(d => d.FromPose).ToList() : [head];
        foreach (var d in used)
            engine.AddDevice(d.Kind, d.DefaultRole, useForBreathing: false,
                restBelowDps: d.FromPose && d.DefaultRole == DeviceRole.Hand ? engine.Settings.PoseHandRestDps : double.NaN);
        var events = used.SelectMany(d => d.Imu.Select(s => (d.Kind, s))).OrderBy(e => e.s.Time).ToList();
        foreach (var (kind, s) in events)
        {
            engine.Push(kind, s);
            if (engine.TryUpdate(s.Time, out var snap))
                bpm[snap.Time + headOffset] = snap.Heart.Displayed ? snap.Heart.Bpm : double.NaN;
        }

        var gate = new MotionGate(200, engine.Settings.MotionThresholdDps, engine.Settings.MotionHangoverSeconds);
        var resampler = new UniformResampler(200);
        double since = double.NaN;
        foreach (var s in head.Imu)
            resampler.Push(s, (double t, in Vec3 _, in Vec3 g) =>
            {
                gate.Push(g);
                if (gate.Moving && double.IsNaN(since)) since = t + headOffset;
                else if (!gate.Moving && !double.IsNaN(since)) { moving.Add((since, t + headOffset)); since = double.NaN; }
            });
        if (!double.IsNaN(since)) moving.Add((since, double.MaxValue));

        int shown = bpm.Values.Count(v => !double.IsNaN(v));
        say($"Headset motion ({head.Model}, {(head.FromPose ? "pose" : "raw IMU")}, {string.Join("+", used.Select(d => d.Kind))}): heart rate shown {100.0 * shown / Math.Max(1, bpm.Count):0} % of seconds, median {Median(bpm.Values):0} BPM; head moving {moving.Sum(m => Math.Min(m.Item2, head.Imu[^1].Time + headOffset) - m.Item1):0} s");
    }

    // --- helpers -----------------------------------------------------------------------------------

    private static DateTime Started(string folder) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "session.json"))).RootElement.GetProperty("startedUtc").GetDateTime();

    private static void Interpolate(float[] s, bool[] gap)
    {
        int n = s.Length, last = -1;
        for (int j = 0; j <= n; j++)
        {
            if (j < n && gap[j]) continue;
            if (j - last > 1)
            {
                float a = last >= 0 ? s[last] : (j < n ? s[j] : 0);
                float b = j < n ? s[j] : a;
                for (int i = last + 1; i < j; i++) s[i] = a + (b - a) * (i - last) / (j - last);
            }
            last = j;
        }
    }

    private static double Lookup(SortedList<double, double> series, double t)
    {
        if (series.Count == 0) return double.NaN;
        var keys = series.Keys;
        int lo = 0, hi = keys.Count - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (keys[mid] <= t) lo = mid; else hi = mid - 1; }
        return Math.Abs(keys[lo] - t) <= 1.5 ? series.Values[lo] : double.NaN;
    }

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    internal static double Median(IEnumerable<double> values)
    {
        var s = values.Where(v => !double.IsNaN(v)).Order().ToList();
        return s.Count == 0 ? double.NaN : s[s.Count / 2];
    }
}
