using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HeartBeat.EyePpg;

/// <summary>PNG plots for the analysis (System.Drawing, no dependencies).</summary>
internal static class Plots
{
    private static readonly Color Bg = Color.FromArgb(250, 250, 248);
    private static readonly Color Ink = Color.FromArgb(40, 40, 40);
    private static readonly Color Grid = Color.FromArgb(225, 225, 220);
    private static readonly Color[] Series = [Color.FromArgb(32, 110, 200), Color.FromArgb(220, 110, 30), Color.FromArgb(150, 40, 160)];
    private static readonly Font Font = new("Segoe UI", 9f);
    private static readonly Font Title = new("Segoe UI", 10.5f, FontStyle.Bold);

    /// <summary>Eye image with each block coloured by its value (yellow/red = high); selected blocks outlined.</summary>
    public static void PulseMap(string path, string snapshotJpg, EyeFile f, double[] values, bool[] valid, int[] selected, string title)
    {
        const int scale = 2, top = 28, legend = 40;
        int w = f.Width * scale, h = f.Height * scale;
        using var bmp = new Bitmap(w, h + top + legend);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Bg);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        if (File.Exists(snapshotJpg))
        {
            using var img = Image.FromFile(snapshotJpg);
            g.DrawImage(img, new Rectangle(0, top, w, h));
        }
        else
        {
            // no snapshot: draw the first frame's block means
            var fr = f.Frames[0];
            for (int k = 0; k < f.BlockCount; k++)
            {
                int v = Math.Clamp((int)(fr[k] / EyeFile.Scale), 0, 255);
                using var b = new SolidBrush(Color.FromArgb(v, v, v));
                g.FillRectangle(b, (k % f.Cols) * EyeFile.Block * scale, top + (k / f.Cols) * EyeFile.Block * scale, EyeFile.Block * scale, EyeFile.Block * scale);
            }
        }

        var shown = values.Where((v, k) => valid[k] && !double.IsNaN(v)).Order().ToList();
        double lo = 1, hi = Math.Max(lo + 0.5, shown.Count > 0 ? shown[(int)(0.995 * (shown.Count - 1))] : 2);
        int cell = EyeFile.Block * scale;
        for (int k = 0; k < values.Length; k++)
        {
            if (!valid[k] || double.IsNaN(values[k])) continue;
            double t = Math.Clamp((values[k] - lo) / (hi - lo), 0, 1);
            if (t <= 0.02) continue;
            using var b = new SolidBrush(Heat(t, (int)(40 + 170 * t)));
            g.FillRectangle(b, (k % f.Cols) * cell, top + (k / f.Cols) * cell, cell, cell);
        }
        using (var pen = new Pen(Color.FromArgb(0, 230, 255), 1))
            foreach (int k in selected) g.DrawRectangle(pen, (k % f.Cols) * cell, top + (k / f.Cols) * cell, cell - 1, cell - 1);

        using var ink = new SolidBrush(Ink);
        g.DrawString(title, Title, ink, 6, 5);
        int ly = top + h + 10;
        for (int x = 0; x < 200; x++)
        {
            using var p = new Pen(Heat(x / 199.0, 255));
            g.DrawLine(p, 10 + x, ly, 10 + x, ly + 12);
        }
        g.DrawString($"{lo:0.0}x", Font, ink, 10, ly + 13);
        g.DrawString($"{hi:0.0}x", Font, ink, 185, ly + 13);
        g.DrawString("cyan outline = blocks used for the reference-free heart rate", Font, ink, 230, ly);
        bmp.Save(path, ImageFormat.Png);
    }

    public static void Spectra(string path, List<(string name, double[] spectrum)> spectra, double fs, int nfft, double refBpm)
    {
        var (bmp, g, area) = Canvas(720, 340, "Combined eye signal: average spectrum");
        using (bmp) using (g)
        {
            double bpmLo = 40, bpmHi = 200;
            AxesX(g, area, bpmLo, bpmHi, 20, "BPM");
            if (!double.IsNaN(refBpm))
            {
                float x = area.Left + (float)((refBpm - bpmLo) / (bpmHi - bpmLo) * area.Width);
                using var pen = new Pen(Color.FromArgb(200, 30, 30), 1.5f) { DashStyle = DashStyle.Dash };
                g.DrawLine(pen, x, area.Top, x, area.Bottom);
                g.DrawString($"reference {refBpm:0}", Font, Brushes.Firebrick, x + 3, area.Top);
            }
            for (int si = 0; si < spectra.Count; si++)
            {
                var s = spectra[si].spectrum;
                if (s.Length == 0) continue;
                var pts = new List<PointF>();
                double max = 0;
                for (int b = 0; b < s.Length; b++) { double bpm = b * fs / nfft * 60; if (bpm >= bpmLo && bpm <= bpmHi) max = Math.Max(max, s[b]); }
                for (int b = 0; b < s.Length; b++)
                {
                    double bpm = b * fs / nfft * 60;
                    if (bpm < bpmLo || bpm > bpmHi || max <= 0) continue;
                    pts.Add(new PointF(area.Left + (float)((bpm - bpmLo) / (bpmHi - bpmLo) * area.Width), area.Bottom - (float)(s[b] / max * area.Height)));
                }
                if (pts.Count > 1) using (var pen = new Pen(Series[si % Series.Length], 1.6f)) g.DrawLines(pen, pts.ToArray());
                using var brush = new SolidBrush(Series[si % Series.Length]);
                g.DrawString(spectra[si].name, Font, brush, area.Right - 60, area.Top + 14 * si);
            }
            bmp.Save(path, ImageFormat.Png);
        }
    }

    public static void Timeline(string path, List<(double t, double reference, double[] eyes, double both)> rows, int[] eyeIndices)
    {
        var (bmp, g, area) = Canvas(900, 360, "Heart rate per second: eyes (no reference used) vs reference");
        using (bmp) using (g)
        {
            if (rows.Count == 0) { bmp.Save(path, ImageFormat.Png); return; }
            double t0 = rows[0].t, t1 = Math.Max(t0 + 1, rows[^1].t), lo = 40, hi = 180;
            AxesX(g, area, t0, t1, t1 - t0 > 200 ? 30 : 10, "s");
            for (double b = lo; b <= hi; b += 20)
            {
                float y = area.Bottom - (float)((b - lo) / (hi - lo) * area.Height);
                using var p = new Pen(Grid);
                g.DrawLine(p, area.Left, y, area.Right, y);
                g.DrawString($"{b:0}", Font, Brushes.DimGray, area.Left - 28, y - 7);
            }
            PointF P(double t, double v) => new(area.Left + (float)((t - t0) / (t1 - t0) * area.Width), area.Bottom - (float)((Math.Clamp(v, lo, hi) - lo) / (hi - lo) * area.Height));
            void Line(Func<int, double> value, Pen pen)
            {
                var run = new List<PointF>();
                for (int i = 0; i <= rows.Count; i++)
                {
                    double v = i < rows.Count ? value(i) : double.NaN;
                    if (!double.IsNaN(v)) { run.Add(P(rows[i].t, v)); continue; }
                    if (run.Count > 1) g.DrawLines(pen, run.ToArray());
                    else if (run.Count == 1) g.FillEllipse(new SolidBrush(pen.Color), run[0].X - 2, run[0].Y - 2, 4, 4);
                    run.Clear();
                }
            }
            using (var pen = new Pen(Color.Black, 2.2f)) Line(i => rows[i].reference, pen);
            for (int e = 0; e < eyeIndices.Length; e++)
                using (var pen = new Pen(Color.FromArgb(150, Series[e]), 1.2f)) Line(i => rows[i].eyes[e], pen);
            using (var pen = new Pen(Series[2], 1.8f)) Line(i => rows[i].both, pen);
            g.DrawString("black: reference", Font, Brushes.Black, area.Left + 6, area.Top + 2);
            for (int e = 0; e < eyeIndices.Length; e++)
                g.DrawString($"eye {eyeIndices[e]}", Font, new SolidBrush(Series[e]), area.Left + 110 + 50 * e, area.Top + 2);
            g.DrawString("both eyes", Font, new SolidBrush(Series[2]), area.Left + 220, area.Top + 2);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    /// <summary>15 s of the combined eye signal from the stretch with the fewest cut-out samples.</summary>
    public static void Waveform(string path, double[] signal, bool[] masked, double fs, int eye)
    {
        var (bmp, g, area) = Canvas(900, 260, $"Eye {eye}: combined skin-brightness signal, 15 s (band-passed)");
        using (bmp) using (g)
        {
            int w = (int)(15 * fs);
            if (signal.Length < w) { bmp.Save(path, ImageFormat.Png); return; }
            int best = 0, bestMasked = int.MaxValue;
            for (int s = 0; s + w <= signal.Length; s += (int)fs)
            {
                int m = masked.AsSpan(s, w).Count(true);
                if (m < bestMasked) { bestMasked = m; best = s; }
            }
            AxesX(g, area, best / fs, (best + w) / fs, 1, "s");
            double max = 1e-12;
            for (int i = 0; i < w; i++) max = Math.Max(max, Math.Abs(signal[best + i]));
            var pts = new PointF[w];
            for (int i = 0; i < w; i++)
                pts[i] = new PointF(area.Left + (float)i / (w - 1) * area.Width, area.Top + area.Height / 2f - (float)(signal[best + i] / max * area.Height / 2.2));
            using var pen = new Pen(Series[0], 1.4f);
            g.DrawLines(pen, pts);
            bmp.Save(path, ImageFormat.Png);
        }
    }

    // ---------------------------------------------------------------------------------------------

    private static (Bitmap, Graphics, Rectangle) Canvas(int w, int h, string title)
    {
        var bmp = new Bitmap(w, h);
        var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Bg);
        g.DrawString(title, Title, new SolidBrush(Ink), 8, 6);
        return (bmp, g, new Rectangle(44, 32, w - 60, h - 64));
    }

    private static void AxesX(Graphics g, Rectangle area, double lo, double hi, double step, string unit)
    {
        using var grid = new Pen(Grid);
        for (double v = Math.Ceiling(lo / step) * step; v <= hi + 1e-9; v += step)
        {
            float x = area.Left + (float)((v - lo) / (hi - lo) * area.Width);
            g.DrawLine(grid, x, area.Top, x, area.Bottom);
            g.DrawString($"{v:0}", Font, Brushes.DimGray, x - 8, area.Bottom + 3);
        }
        g.DrawString(unit, Font, Brushes.DimGray, area.Right - 14, area.Bottom + 16);
        using var axis = new Pen(Color.Gray);
        g.DrawRectangle(axis, area);
    }

    private static Color Heat(double t, int alpha)
    {
        // dark red -> red -> orange -> yellow
        int r = (int)(120 + 135 * Math.Min(1, t * 2));
        int gg = (int)(255 * Math.Clamp((t - 0.35) / 0.65, 0, 1));
        return Color.FromArgb(Math.Clamp(alpha, 0, 255), Math.Clamp(r, 0, 255), Math.Clamp(gg, 0, 255), 0);
    }
}
