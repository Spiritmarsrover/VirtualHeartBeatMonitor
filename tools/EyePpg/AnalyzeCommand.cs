using System.Globalization;
using System.Text;
using System.Text.Json;
using HeartBeat.Core;
using HeartBeat.Core.Dsp;
using HeartBeat.Core.Heart;
using HeartBeat.Core.Recordings;

namespace HeartBeat.EyePpg;

/// <summary>
/// Feasibility analysis of an eye recording.
///
/// 1. Each 8x8 block becomes a relative-brightness signal at 30 Hz, band-passed 0.7–3.5 Hz;
///    blinks, dropped frames and head movement (IMU) are cut out.
/// 2. Pulse map (needs a reference rate): per block, how much of its power sits at the reference
///    heart rate (and 2x) compared to an even spread ("enrichment", 1 = noise). The same is measured at
///    decoy rates, so a block standing out by chance is visible as such. A real pulse lights up a
///    coherent patch of skin at the true rate and not at the decoys.
/// 3. Heart rate without a reference: blocks with the most peaked spectra that agree on a rate are
///    combined, then a 12 s sliding FFT gives a BPM per second, compared to the reference.
/// </summary>
internal static class AnalyzeCommand
{
    private const double Fs = 30;
    private const double Low = 0.7, High = 3.5;      // Hz analysed (42–210 BPM)
    private const double WindowSeconds = 12;
    private const double MaxMasked = 0.25;
    private const int Nfft = 1024;
    private const double HalfBand = 0.1;              // Hz around the rate counted as "pulse"
    private static readonly double[] DecoyOffsets = [-0.45, -0.3, 0.3, 0.45, 0.6, 0.75];

    private sealed record Eye(int Index, EyeFile File, double T0, int N, float[][] Rel, bool[] Masked, bool[] ValidBlock,
        double MaskedFraction, double FrameRate, double MedianJpegKb, double MeanBrightness);

    private sealed class EyeResult
    {
        public required Eye Eye;
        public double[] Peakiness = [];
        public double[] PeakHz = [];
        public double[]? Enrich;          // vs reference, per block
        public double[]? DecoyEnrich;     // per block, max over decoys
        public List<double> DecoyPool = new();
        public int[] Selected = [];
        public double[] Combined = [];
        public double[] CombinedSpectrum = [];
        public readonly Dictionary<int, (double bpm, double prom)> PerSecond = new();
    }

    public static int Run(Args a)
    {
        string folder = a.Positional ?? throw new ArgumentException("analyze needs a recording folder");
        if (!Directory.Exists(folder)) throw new ArgumentException($"No folder {folder}");
        var report = new StringBuilder();
        void Say(string s = "") { Console.WriteLine(s); report.AppendLine(s); }

        using var session = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "session.json")));
        double? pulseBefore = Num(session.RootElement, "pulseBeforeBpm"), pulseAfter = Num(session.RootElement, "pulseAfterBpm");

        // --- injected test pulse --------------------------------------------------------------
        double injectBpm = double.NaN, injectAmp = 0.003;
        if (a.Has("--inject"))
        {
            var parts = a.Get("--inject", "").Split(':');
            injectBpm = double.Parse(parts[0], CultureInfo.InvariantCulture);
            if (parts.Length > 1) injectAmp = double.Parse(parts[1], CultureInfo.InvariantCulture);
        }

        // --- reference heart rate + head movement from the IMU ---------------------------------
        var imuRef = new SortedList<double, double>();   // recording time -> bpm (NaN when "--")
        var moving = new List<(double from, double to)>();
        string refSource;
        if (!a.Has("--no-imu-ref")) ImuReference(folder, imuRef, moving, Say);
        double constRef = a.GetDouble("--ref", double.NaN);
        if (!double.IsNaN(injectBpm)) { constRef = injectBpm; refSource = $"injected test pulse {injectBpm:0} BPM, amplitude {injectAmp * 100:0.##} %"; }
        else if (!double.IsNaN(constRef)) refSource = $"--ref {constRef:0} BPM";
        else if (imuRef.Values.Any(v => !double.IsNaN(v))) refSource = "headset IMU (HeartBeat.Core engine, unvalidated on the Frame)";
        else if (pulseBefore != null || pulseAfter != null)
        {
            constRef = new[] { pulseBefore, pulseAfter }.Where(p => p != null).Average(p => p!.Value);
            refSource = $"counted pulse {constRef:0} BPM";
        }
        else refSource = "none";
        Func<double, double> refAt = !double.IsNaN(constRef) ? _ => constRef : t => Lookup(imuRef, t);
        bool haveRef = refSource != "none";

        Say($"Recording: {folder}");
        Say($"Reference: {refSource}");
        if (pulseBefore != null || pulseAfter != null) Say($"Counted pulse: before {pulseBefore?.ToString("0") ?? "-"}, after {pulseAfter?.ToString("0") ?? "-"} BPM");

        var results = new List<EyeResult>();
        for (int i = 0; i < 2; i++)
        {
            string path = Path.Combine(folder, $"eye_{i}.bin");
            if (!File.Exists(path)) { Say($"eye {i}: no file"); continue; }
            var file = EyeFile.Load(path);
            if (file.Frames.Count < Fs * WindowSeconds * 2) { Say($"eye {i}: only {file.Frames.Count} frames, skipped"); continue; }
            var eye = Prepare(i, file, moving, injectBpm, injectAmp);
            var r = new EyeResult { Eye = eye };
            Analyse(r, haveRef ? refAt : null);
            results.Add(r);
        }
        if (results.Count == 0) { Say("No usable eye data."); return 3; }

        // --- report ----------------------------------------------------------------------------
        foreach (var r in results)
        {
            var e = r.Eye;
            Say();
            Say($"== Eye {e.Index} ==");
            Say($"  {e.File.Width}x{e.File.Height}, {e.File.Frames.Count} frames, {e.FrameRate:0.0} fps, JPEG ~{e.MedianJpegKb:0.0} KB/frame, mean brightness {e.MeanBrightness:0}");
            Say($"  cut out (blinks, gaps, movement): {e.MaskedFraction * 100:0} %   usable blocks: {e.ValidBlock.Count(v => v)} / {e.File.BlockCount}");
            if (r.Enrich != null)
            {
                var valid = Enumerable.Range(0, r.Enrich.Length).Where(k => e.ValidBlock[k] && !double.IsNaN(r.Enrich[k])).ToList();
                double p99 = Percentile(r.DecoyPool, 0.99);
                int above = valid.Count(k => r.Enrich[k] > p99);
                double bestTrue = valid.Max(k => r.Enrich[k]);
                double bestDecoy = valid.Max(k => r.DecoyEnrich![k]);
                Say($"  pulse map vs reference: best block {bestTrue:0.0}x  (best at decoy rates {bestDecoy:0.0}x)");
                Say($"  blocks above the decoys' 99th percentile ({p99:0.0}x): {above} of {valid.Count} = {100.0 * above / valid.Count:0.0} %  (chance: ~1 %)");
                Say($"  verdict: {Verdict(bestTrue, bestDecoy, above, valid.Count)}");
            }
            double fStar = r.Selected.Length > 0 ? Median(r.Selected.Select(k => r.PeakHz[k])) * 60 : double.NaN;
            Say($"  reference-free: {r.Selected.Length} blocks agree on ~{fStar:0} BPM");
        }

        // per-second timeline: each eye, both eyes combined, reference
        var seconds = results.SelectMany(r => r.PerSecond.Keys).Distinct().Order().ToList();
        var csv = new StringBuilder("t,ref_bpm," + string.Join(",", results.Select(r => $"eye{r.Eye.Index}_bpm,eye{r.Eye.Index}_prom")) + ",eyes_bpm\n");
        var errors = new List<double>();
        var timeline = new List<(double t, double reference, double[] eyes, double both)>();
        foreach (int s in seconds)
        {
            double reference = haveRef ? refAt(s) : double.NaN;
            var per = results.Select(r => r.PerSecond.TryGetValue(s, out var v) ? v : (bpm: double.NaN, prom: double.NaN)).ToArray();
            // both eyes: the more prominent one when they disagree, their mean when they agree
            var ok = per.Where(p => !double.IsNaN(p.bpm)).ToArray();
            double both = ok.Length switch
            {
                0 => double.NaN,
                1 => ok[0].bpm,
                _ => Math.Abs(ok[0].bpm - ok[1].bpm) < 6 ? (ok[0].bpm + ok[1].bpm) / 2 : ok.MaxBy(p => p.prom).bpm,
            };
            if (!double.IsNaN(both) && !double.IsNaN(reference)) errors.Add(Math.Abs(both - reference));
            timeline.Add((s, reference, per.Select(p => p.bpm).ToArray(), both));
            csv.Append(string.Create(CultureInfo.InvariantCulture, $"{s},{reference:0.0},{string.Join(",", per.Select(p => $"{p.bpm:0.0},{p.prom:0.00}"))},{both:0.0}\n"));
        }
        File.WriteAllText(Path.Combine(folder, "eyeppg_timeline.csv"), csv.ToString().Replace("NaN", ""));

        Say();
        Say("== Heart rate from the eyes (no reference used) ==");
        if (errors.Count > 0)
        {
            Say($"  seconds compared: {errors.Count}   mean error {errors.Average():0.0} BPM   median {Median(errors):0.0}");
            Say($"  within 5 BPM: {100.0 * errors.Count(x => x <= 5) / errors.Count:0} %   within 10 BPM: {100.0 * errors.Count(x => x <= 10) / errors.Count:0} %");
        }
        else Say("  nothing to compare (no reference or no usable windows)");

        // --- plots -----------------------------------------------------------------------------
        foreach (var r in results)
        {
            string snapshot = Directory.GetFiles(folder, $"eye_{r.Eye.Index}_*s.jpg").Order().FirstOrDefault() ?? "";
            var values = r.Enrich ?? r.Peakiness;
            Plots.PulseMap(Path.Combine(folder, $"eyeppg_map_eye{r.Eye.Index}.png"), snapshot, r.Eye.File, values, r.Eye.ValidBlock, r.Selected,
                r.Enrich != null ? $"Eye {r.Eye.Index}: power at the reference rate (1x = noise)" : $"Eye {r.Eye.Index}: spectral peakiness (no reference)");
        }
        Plots.Spectra(Path.Combine(folder, "eyeppg_spectrum.png"), results.Select(r => ($"eye {r.Eye.Index}", r.CombinedSpectrum)).ToList(),
            Fs, Nfft, haveRef ? Median(seconds.Select(s => refAt(s)).Where(v => !double.IsNaN(v))) : double.NaN);
        Plots.Timeline(Path.Combine(folder, "eyeppg_timeline.png"), timeline, results.Select(r => r.Eye.Index).ToArray());
        var best = results.MaxBy(r => r.Selected.Length)!;
        Plots.Waveform(Path.Combine(folder, "eyeppg_waveform.png"), best.Combined, best.Eye.Masked, Fs, best.Eye.Index);

        File.WriteAllText(Path.Combine(folder, "eyeppg_report.txt"), report.ToString());
        Console.WriteLine();
        Console.WriteLine($"Wrote eyeppg_report.txt, eyeppg_timeline.csv and eyeppg_*.png to {folder}");
        return 0;
    }

    private static string Verdict(double bestTrue, double bestDecoy, int above, int total)
    {
        double share = (double)above / Math.Max(1, total);
        if (share > 0.05 && (bestTrue > 1.5 * bestDecoy || share > 0.2)) return "PULSE VISIBLE: a clear patch of blocks carries the heart rate";
        if (bestTrue > 1.2 * bestDecoy || share > 0.03) return "maybe: some blocks carry the heart rate, weak";
        return "no pulse found above chance";
    }

    // ---------------------------------------------------------------------------------------------

    private static Eye Prepare(int index, EyeFile f, List<(double from, double to)> moving, double injectBpm, double injectAmp)
    {
        int nb = f.BlockCount;
        double t0 = Math.Ceiling(f.Times[0] * Fs) / Fs;
        int n = (int)Math.Floor((f.Times[^1] - t0) * Fs) + 1;

        // Average the frames landing in each 1/30 s slot.
        var sum = new double[nb][];
        for (int k = 0; k < nb; k++) sum[k] = new double[n];
        var count = new int[n];
        for (int i = 0; i < f.Frames.Count; i++)
        {
            int j = (int)Math.Round((f.Times[i] - t0) * Fs);
            if (j < 0 || j >= n) continue;
            count[j]++;
            var fr = f.Frames[i];
            for (int k = 0; k < nb; k++) sum[k][j] += fr[k] / EyeFile.Scale;
        }
        var masked = new bool[n];
        for (int j = 0; j < n; j++) masked[j] = count[j] == 0;

        var x = new float[nb][];
        var mean = new double[nb];
        for (int k = 0; k < nb; k++)
        {
            x[k] = new float[n];
            double m = 0; int c = 0;
            for (int j = 0; j < n; j++)
                if (count[j] > 0) { x[k][j] = (float)(sum[k][j] / count[j]); m += x[k][j]; c++; }
            mean[k] = c > 0 ? m / c : 0;
            Interpolate(x[k], masked);
        }

        if (!double.IsNaN(injectBpm))
        {
            // PPG-like test pulse with a little heart-rate variability, on every block.
            double phase = 0;
            for (int j = 0; j < n; j++)
            {
                double t = j / Fs;
                phase += 2 * Math.PI * injectBpm / 60 * (1 + 0.03 * Math.Sin(2 * Math.PI * 0.1 * t)) / Fs;
                double p = Math.Sin(phase) + 0.4 * Math.Sin(2 * phase - 0.8);
                for (int k = 0; k < nb; k++) x[k][j] *= (float)(1 + injectAmp * p);
            }
        }

        // Usable blocks: not black, not saturated.
        var valid = new bool[nb];
        for (int k = 0; k < nb; k++) valid[k] = mean[k] > 12 && mean[k] < 240;

        // Blinks / sudden changes: the 80th percentile of per-block relative change, frame to frame.
        var change = new double[n];
        var tmp = new List<double>(nb);
        for (int j = 1; j < n; j++)
        {
            tmp.Clear();
            for (int k = 0; k < nb; k++) if (valid[k]) tmp.Add(Math.Abs(x[k][j] - x[k][j - 1]) / mean[k]);
            change[j] = tmp.Count > 0 ? Percentile(tmp, 0.8) : 0;
        }
        double typical = Median(change.Where((_, j) => !masked[j]));
        var bad = new bool[n];
        for (int j = 0; j < n; j++)
        {
            double t = t0 + j / Fs;
            bad[j] = masked[j] || change[j] > 5 * typical + 0.002 || moving.Any(m => t >= m.from && t <= m.to);
        }
        int pad = (int)(0.3 * Fs);
        for (int j = 0; j < n; j++)
            if (bad[j]) for (int d = -pad; d <= pad; d++) if (j + d >= 0 && j + d < n) masked[j + d] = true;

        // Relative brightness, band-passed; cut-out samples are bridged before and zeroed after filtering.
        var rel = new float[nb][];
        Parallel.For(0, nb, k =>
        {
            rel[k] = new float[n];
            if (!valid[k]) return;
            var s = new float[n];
            for (int j = 0; j < n; j++) s[j] = (float)(x[k][j] / mean[k] - 1);
            Interpolate(s, masked);
            var bp = Butterworth.BandPass(Fs, Low, High, 2);
            bp.Prime(0);
            for (int j = 0; j < n; j++)
            {
                double y = bp.Process(s[j]);
                rel[k][j] = masked[j] ? 0 : (float)y;
            }
        });

        double span = f.Times[^1] - f.Times[0];
        double brightness = mean.Where((_, k) => true).Average();
        return new Eye(index, f, t0, n, rel, masked, valid, masked.Count(m => m) / (double)n,
            (f.Times.Count - 1) / span, Median(f.JpegBytes.Select(b => b / 1024.0)), brightness);
    }

    private static void Analyse(EyeResult r, Func<double, double>? refAt)
    {
        var e = r.Eye;
        int nb = e.File.BlockCount, w = (int)(WindowSeconds * Fs);
        int binLo = (int)Math.Ceiling(Low * Nfft / Fs), binHi = (int)Math.Floor(High * Nfft / Fs);
        double hzPerBin = Fs / Nfft;

        // windows every second with few cut-out samples
        var starts = new List<int>();
        for (int s = 0; s + w <= e.N; s += (int)Fs)
            if (e.Masked.AsSpan(s, w).Count(true) <= MaxMasked * w) starts.Add(s);

        var avgSpec = new double[nb][];
        r.Enrich = refAt != null ? new double[nb] : null;
        r.DecoyEnrich = refAt != null ? new double[nb] : null;
        var decoyPerBlock = new List<double>[nb];
        var refHz = starts.Select(s => refAt?.Invoke(e.T0 + (s + w / 2.0) / Fs) / 60 ?? double.NaN).ToArray();

        Parallel.For(0, nb, k =>
        {
            decoyPerBlock[k] = new List<double>();
            if (!e.ValidBlock[k] || starts.Count == 0) { avgSpec[k] = new double[Nfft / 2 + 1]; return; }
            var acc = new double[Nfft / 2 + 1];
            double enrichSum = 0; int enrichN = 0;
            var decoySum = new double[DecoyOffsets.Length];
            var decoyN = new int[DecoyOffsets.Length];
            var seg = new double[w];
            for (int wi = 0; wi < starts.Count; wi++)
            {
                for (int i = 0; i < w; i++) seg[i] = e.Rel[k][starts[wi] + i];
                var p = Fft.HannPowerSpectrum(seg, Nfft);
                double band = 0;
                for (int b = binLo; b <= binHi; b++) { acc[b] += p[b]; band += p[b]; }
                if (refAt == null || double.IsNaN(refHz[wi]) || band <= 0) continue;
                enrichSum += Enrichment(p, band, refHz[wi], hzPerBin, binLo, binHi); enrichN++;
                for (int d = 0; d < DecoyOffsets.Length; d++)
                {
                    double f = refHz[wi] + DecoyOffsets[d];
                    if (f < 0.75 || f > 3.0 || Math.Abs(f - 2 * refHz[wi]) < 0.15 || Math.Abs(2 * f - refHz[wi]) < 0.15) continue;
                    decoySum[d] += Enrichment(p, band, f, hzPerBin, binLo, binHi); decoyN[d]++;
                }
            }
            avgSpec[k] = acc;
            if (refAt != null)
            {
                r.Enrich![k] = enrichN > 0 ? enrichSum / enrichN : double.NaN;
                for (int d = 0; d < DecoyOffsets.Length; d++) if (decoyN[d] > 0) decoyPerBlock[k].Add(decoySum[d] / decoyN[d]);
                r.DecoyEnrich![k] = decoyPerBlock[k].Count > 0 ? decoyPerBlock[k].Max() : double.NaN;
            }
        });
        for (int k = 0; k < nb; k++) if (e.ValidBlock[k]) r.DecoyPool.AddRange(decoyPerBlock[k]);

        // Peakiness and peak rate of each block's average spectrum.
        r.Peakiness = new double[nb];
        r.PeakHz = new double[nb];
        for (int k = 0; k < nb; k++)
        {
            if (!e.ValidBlock[k] || starts.Count == 0) continue;
            var band = avgSpec[k][binLo..(binHi + 1)];
            int arg = Array.IndexOf(band, band.Max());
            double med = Median(band);
            r.Peakiness[k] = med > 0 ? band[arg] / med : 0;
            r.PeakHz[k] = (binLo + arg) * hzPerBin;
        }

        // Reference-free selection: the most peaked 5 % of blocks, keeping those near their common rate.
        var ranked = Enumerable.Range(0, nb).Where(k => e.ValidBlock[k] && r.Peakiness[k] > 0).OrderByDescending(k => r.Peakiness[k]).ToList();
        var top = ranked.Take(Math.Max(10, ranked.Count / 20)).ToList();
        if (top.Count == 0) return;
        var hist = new Dictionary<int, double>();
        foreach (int k in top) { int h = (int)Math.Round(r.PeakHz[k] / 0.05); hist[h] = hist.GetValueOrDefault(h) + r.Peakiness[k] - 1; }
        double fStar = hist.MaxBy(kv => kv.Value).Key * 0.05;
        r.Selected = top.Where(k => Math.Abs(r.PeakHz[k] - fStar) <= 0.12).ToArray();

        // Combine: z-score, align polarity to the strongest block, weight by peakiness.
        var combined = new double[e.N];
        var lead = e.Rel[r.Selected[0]];
        foreach (int k in r.Selected)
        {
            var s = e.Rel[k];
            double ss = 0, dot = 0;
            for (int j = 0; j < e.N; j++) { ss += s[j] * s[j]; dot += s[j] * lead[j]; }
            double sd = Math.Sqrt(ss / e.N);
            if (sd <= 0) continue;
            double weight = (r.Peakiness[k] - 1) * Math.Sign(dot == 0 ? 1 : dot) / sd;
            for (int j = 0; j < e.N; j++) combined[j] += weight * s[j];
        }
        r.Combined = combined;

        // Sliding 12 s FFT on the combined signal: harmonic sum P(f) + 0.5 P(2f).
        r.CombinedSpectrum = new double[Nfft / 2 + 1];
        var segC = new double[w];
        foreach (int s in starts)
        {
            Array.Copy(combined, s, segC, 0, w);
            var p = Fft.HannPowerSpectrum(segC, Nfft);
            for (int b = 0; b < p.Length; b++) r.CombinedSpectrum[b] += p[b];
            var scores = new List<(double bpm, double score)>();
            for (int b = binLo; b <= (int)(3.0 * Nfft / Fs); b++)
                scores.Add((b * hzPerBin * 60, p[b] + 0.5 * (2 * b < p.Length ? p[2 * b] : 0)));
            var bestScore = scores.MaxBy(x => x.score);
            double med = Median(scores.Select(x => x.score));
            int second = (int)Math.Round(e.T0 + (s + w) / Fs);   // stamped at the window's end, like the live app
            r.PerSecond[second] = (bestScore.bpm, med > 0 ? bestScore.score / med : 0);
        }
    }

    /// <summary>Power within ±HalfBand of f (and 2f) relative to what an even spread would put there.</summary>
    private static double Enrichment(double[] p, double band, double f, double hzPerBin, int binLo, int binHi)
    {
        double inside = 0; int bins = 0;
        foreach (double c in new[] { f, 2 * f })
        {
            int lo = Math.Max(binLo, (int)Math.Ceiling((c - HalfBand) / hzPerBin));
            int hi = Math.Min(binHi, (int)Math.Floor((c + HalfBand) / hzPerBin));
            for (int b = lo; b <= hi; b++) { inside += p[b]; bins++; }
        }
        if (bins == 0) return double.NaN;
        return (inside / band) / ((double)bins / (binHi - binLo + 1));
    }

    private static void ImuReference(string folder, SortedList<double, double> bpm, List<(double, double)> moving, Action<string> say)
    {
        Recording rec;
        try { rec = Recording.Load(folder); } catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException) { return; }
        var head = rec.Devices.FirstOrDefault(d => d.Kind == "HMD") ?? rec.Devices.FirstOrDefault();
        if (head == null || head.Imu.Count < 1000) return;

        // device clock -> recording time, from the host_t column
        var offsets = new List<double>();
        foreach (var line in File.ReadLines(Path.Combine(folder, $"imu_{head.Index}_{head.Kind}.csv")).Skip(1))
        {
            var p = line.Split(',');
            if (p.Length > 2 && double.TryParse(p[0], CultureInfo.InvariantCulture, out double h) && double.TryParse(p[1], CultureInfo.InvariantCulture, out double d))
                offsets.Add(h - d);
        }
        double offset = Median(offsets);

        var engine = new MonitorEngine();
        engine.AddDevice("head", DeviceRole.Head);
        var gate = new MotionGate(200, engine.Settings.MotionThresholdDps, engine.Settings.MotionHangoverSeconds);
        var resampler = new UniformResampler(200);
        double movingSince = double.NaN;
        foreach (var s in head.Imu)
        {
            engine.Push("head", s);
            if (engine.TryUpdate(s.Time, out var snap))
                bpm[snap.Time + offset] = snap.Heart.Displayed ? snap.Heart.Bpm : double.NaN;
            resampler.Push(s, (double t, in Vec3 _, in Vec3 g) =>
            {
                gate.Push(g);
                if (gate.Moving && double.IsNaN(movingSince)) movingSince = t + offset;
                else if (!gate.Moving && !double.IsNaN(movingSince)) { moving.Add((movingSince, t + offset)); movingSince = double.NaN; }
            });
        }
        if (!double.IsNaN(movingSince)) moving.Add((movingSince, double.MaxValue));
        int shown = bpm.Values.Count(v => !double.IsNaN(v));
        say($"Headset IMU ({head.Model}): heart rate shown {100.0 * shown / Math.Max(1, bpm.Count):0} % of seconds, median {Median(bpm.Values.Where(v => !double.IsNaN(v))):0} BPM; moving {moving.Sum(m => Math.Min(m.Item2, head.Imu[^1].Time + offset) - m.Item1):0} s");
    }

    // --- small helpers ---------------------------------------------------------------------------

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

    internal static double Median(IEnumerable<double> values) => Percentile(values.ToList(), 0.5);

    internal static double Percentile(List<double> values, double q)
    {
        if (values.Count == 0) return double.NaN;
        var s = values.Where(v => !double.IsNaN(v)).Order().ToList();
        if (s.Count == 0) return double.NaN;
        return s[(int)Math.Clamp(Math.Round(q * (s.Count - 1)), 0, s.Count - 1)];
    }
}
