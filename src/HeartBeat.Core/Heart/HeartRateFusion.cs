namespace HeartBeat.Core.Heart;

/// <param name="Bpm">Internal estimate (kept while held). Show <see cref="DisplayText"/> to users instead.</param>
/// <param name="Displayed">Confidence is at or above the display threshold, so a number may be shown.</param>
public readonly record struct HeartRateEstimate(
    double Time,
    double Bpm,
    double RawBpm,
    double Prominence,
    double Confidence,
    bool Held,
    int DevicesUsed,
    bool Displayed)
{
    /// <summary>The heart rate to show, or null when it should read "--".</summary>
    public double? DisplayBpm => Displayed ? Bpm : null;

    /// <summary>Whole BPM, or "--" when confidence is below the display threshold.</summary>
    public string DisplayText => Displayed ? Math.Round(Bpm).ToString("0") : "--";
}

/// <summary>
/// Combines per-device spectra into one estimate: a prominence-weighted average of the
/// median-normalised spectra, a soft prior around the last confident value, a confidence score,
/// and output smoothing. When nothing usable arrives (motion, warm-up) the last value is held
/// and confidence decays.
/// </summary>
public sealed class HeartRateFusion
{
    public double MinProminence { get; set; } = 5.0;        // confidence 0 at or below
    public double FullProminence { get; set; } = 10.0;      // confidence 1 at or above
    public double PriorSigmaBpm { get; set; } = 12.0;
    public double PriorFloor { get; set; } = 0.35;          // prior never suppresses below this factor
    public double SmoothingAlpha { get; set; } = 0.35;
    public double HoldDecayPerSecond { get; set; } = 0.1;
    /// <summary>
    /// Confidence needed to show a number; below it the output reads "--". Chosen from the
    /// 2026-11-19 recordings: at 0.5 still sessions show a value 93–95% of the time, talking only
    /// 5%, and the values shown during small movements were within 5 BPM of the counted pulse 95%
    /// of the time. While held (moving, no usable data) the output reads "--" immediately.
    /// </summary>
    public double ConfidentThreshold { get; set; } = 0.5;
    public double EdgeMarginBpm { get; set; } = 2.0;

    private double _bpm = double.NaN;
    private double _confidence;
    private double _lastTime = double.NaN;

    public double CurrentBpm => _bpm;

    public static double DeviceWeight(HeartRateSpectrum s) => Math.Max(0, s.Prominence - 2.0);

    /// <param name="factors">Optional per-spectrum trust factor (e.g. by body position); multiplies the weight.</param>
    public HeartRateEstimate Update(double time, IReadOnlyList<HeartRateSpectrum> spectra, IReadOnlyList<double>? factors = null)
    {
        double dt = double.IsNaN(_lastTime) ? 1 : Math.Max(0, time - _lastTime);
        _lastTime = time;

        var weights = spectra.Select((s, i) => DeviceWeight(s) * (factors?[i] ?? 1.0)).ToList();
        var usable = spectra.Where((s, i) => weights[i] > 0).ToList();
        var usableWeights = weights.Where(w => w > 0).ToList();
        if (usable.Count == 0) return Hold(time, dt);

        var grid = usable[0].Bpm;
        var fused = new double[grid.Length];
        double totalWeight = 0;
        for (int k = 0; k < usable.Count; k++)
        {
            var s = usable[k];
            double w = usableWeights[k];
            totalWeight += w;
            for (int i = 0; i < fused.Length; i++) fused[i] += w * s.Power[i];
        }
        for (int i = 0; i < fused.Length; i++) fused[i] /= totalWeight;

        double median = HeartRateSpectrum.Median(fused);
        var (rawBpm, rawPeak, _) = HeartRateSpectrum.FindPeak(grid, fused);
        double prominence = rawPeak / median;

        // Soft continuity prior: prefer peaks near the last confident value without forbidding jumps.
        double bpm = rawBpm;
        if (!double.IsNaN(_bpm) && _confidence >= ConfidentThreshold)
        {
            var weighted = new double[fused.Length];
            for (int i = 0; i < fused.Length; i++)
            {
                double z = (grid[i] - _bpm) / PriorSigmaBpm;
                weighted[i] = fused[i] * (PriorFloor + (1 - PriorFloor) * Math.Exp(-0.5 * z * z));
            }
            bpm = HeartRateSpectrum.FindPeak(grid, weighted).bpm;
        }

        double confidence = Math.Clamp((prominence - MinProminence) / (FullProminence - MinProminence), 0, 1);
        // A peak on the edge of the BPM range is low-frequency drift or junk, not a heartbeat.
        if (rawBpm <= grid[0] + EdgeMarginBpm || rawBpm >= grid[^1] - EdgeMarginBpm) confidence = 0;
        if (confidence <= 0) return Hold(time, dt, prominence, usable.Count);

        _bpm = double.IsNaN(_bpm) || _confidence < ConfidentThreshold
            ? bpm
            : _bpm + SmoothingAlpha * (bpm - _bpm);
        _confidence = confidence;
        return new HeartRateEstimate(time, _bpm, rawBpm, prominence, _confidence, false, usable.Count, IsDisplayed());
    }

    private HeartRateEstimate Hold(double time, double dt, double prominence = 0, int devices = 0)
    {
        _confidence = Math.Max(0, _confidence - HoldDecayPerSecond * dt);
        return new HeartRateEstimate(time, _bpm, double.NaN, prominence, _confidence, true, devices, false);
    }

    // Held means no usable spectrum arrived this update (moving, warming up, no clear peak):
    // show "--" right away instead of a stale number.
    private bool IsDisplayed() => !double.IsNaN(_bpm) && _confidence >= ConfidentThreshold;
}
