namespace HeartBeat.App;

/// <summary>
/// The BPM graph's vertical scale, shared by the Windows and Steam Frame panels.
///  - Fine (dashboard tab, PC window): a fixed 40–160 BPM range that never rescales, a solid
///    labelled line every 10 BPM and a dashed line on the 5s in between. "--" (stored as 0) and
///    anything below 40 dip to the bottom edge.
///  - Coarse (the small wrist panel): 0 at the bottom, top rounded up to the next 20 above the
///    highest value (at least 100), lines at 0 / half / top.
/// </summary>
public static class GraphScale
{
    public const double FixedMin = 40, FixedMax = 160, StretchedMax = 180;

    public readonly record struct Line(double Value, bool Label, bool Dashed);

    /// <summary>The value range shown (bottom, top).</summary>
    public static (double Min, double Max) Range(IReadOnlyList<(double t, double bpm)> history, bool fine)
    {
        // Fixed 40–160; the top stretches to 180 only while a shown value is above 160 (e.g. after exercise).
        if (fine) return (FixedMin, history.Any(p => p.bpm > FixedMax) ? StretchedMax : FixedMax);
        double max = history.Count == 0 ? 0 : history.Max(p => p.bpm);
        return (0, Math.Max(100, Math.Ceiling((max + 5) / 20) * 20));
    }

    public static IEnumerable<Line> Lines(double min, double max, bool fine)
    {
        if (!fine)
        {
            foreach (double v in new[] { min, (min + max) / 2, max }) yield return new Line(v, true, false);
            yield break;
        }
        for (double v = min; v <= max + 1e-9; v += 5)
        {
            bool ten = Math.Abs(v % 10) < 1e-9;
            yield return new Line(v, Label: Math.Abs(v % 20) < 1e-9, Dashed: !ten); // labels on the even tens only
        }
    }

    /// <summary>
    /// The stretches of the history to draw as connected lines. Fine graphs leave a gap for seconds
    /// without a number ("--", stored as 0): drawn on the 40 BPM bottom edge they looked like 40.
    /// The coarse wrist graph keeps them as dips to its labelled 0.
    /// </summary>
    public static IEnumerable<List<(double t, double bpm)>> Runs(IReadOnlyList<(double t, double bpm)> history, bool fine)
    {
        if (!fine) { if (history.Count > 0) yield return history.ToList(); yield break; }
        var run = new List<(double t, double bpm)>();
        foreach (var p in history)
        {
            if (p.bpm > 0) { run.Add(p); continue; }
            if (run.Count > 0) { yield return run; run = new(); }
        }
        if (run.Count > 0) yield return run;
    }

    /// <summary>
    /// At most <paramref name="maxPoints"/> points (about one per pixel), for long windows: each group of
    /// consecutive samples becomes its mean, or a dropout (0) when most of the group had no number.
    /// </summary>
    public static IReadOnlyList<(double t, double bpm)> Thin(IReadOnlyList<(double t, double bpm)> history, int maxPoints)
    {
        if (maxPoints < 2 || history.Count <= maxPoints) return history;
        int size = (int)Math.Ceiling(history.Count / (double)maxPoints);
        var thinned = new List<(double t, double bpm)>(history.Count / size + 1);
        for (int start = 0; start < history.Count; start += size)
        {
            int end = Math.Min(history.Count, start + size);
            double sum = 0, tSum = 0;
            int shown = 0;
            for (int i = start; i < end; i++)
            {
                tSum += history[i].t;
                if (history[i].bpm > 0) { sum += history[i].bpm; shown++; }
            }
            thinned.Add((tSum / (end - start), shown * 2 >= end - start ? sum / shown : 0));
        }
        return thinned;
    }

    /// <summary>Y for a value, clamped to the plot (dropouts and out-of-range values sit on the edge).</summary>
    public static float Y(double bpm, double min, double max, float top, float bottom) =>
        bottom - (float)((Math.Clamp(bpm, min, max) - min) / (max - min)) * (bottom - top);

    /// <summary>
    /// The time at the graph's right edge: the newest sample, not the clock. Samples arrive once
    /// per second; following the clock made the line creep left between them on every redraw.
    /// </summary>
    public static double RightEdge(IReadOnlyList<(double t, double bpm)> history, double now) =>
        history.Count > 0 ? history[^1].t : now;
}
