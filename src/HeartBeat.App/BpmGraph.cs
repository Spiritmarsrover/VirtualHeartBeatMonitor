using System.Drawing;
using System.Drawing.Drawing2D;

namespace HeartBeat.App;

/// <summary>
/// Draws the recent heart rate as a line graph. Seconds without a number ("--") are plotted as 0,
/// so dropouts show as dips to the baseline.
/// </summary>
public static class BpmGraph
{
    public readonly record struct Style(Color Line, Color Fill, Color Grid, Color Label, Font Font);

    /// <param name="history">(host time, BPM or 0) samples, oldest first.</param>
    /// <param name="now">Right edge of the graph (host time).</param>
    public static void Draw(Graphics g, RectangleF area, IReadOnlyList<(double t, double bpm)> history, double now, double seconds, Style style)
    {
        // Scale: 0 at the bottom, top rounded up to the next 20 above the highest value (at least 100).
        double max = history.Count == 0 ? 0 : history.Max(p => p.bpm);
        double top = Math.Max(100, Math.Ceiling((max + 5) / 20) * 20);
        var labelSize = g.MeasureString("000", style.Font);
        float labelWidth = labelSize.Width, inset = labelSize.Height / 2 + 1; // room for the top and bottom labels
        var plot = new RectangleF(area.X + labelWidth, area.Y + inset, area.Width - labelWidth, area.Height - 2 * inset);

        float Y(double bpm) => plot.Bottom - (float)(bpm / top) * plot.Height;
        float X(double t) => plot.Right - (float)((now - t) / seconds) * plot.Width;

        using (var grid = new Pen(style.Grid, 1f))
        using (var label = new SolidBrush(style.Label))
        {
            var right = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            foreach (double v in new[] { 0.0, top / 2, top })
            {
                float y = Y(v);
                g.DrawLine(grid, plot.X, y, plot.Right, y);
                g.DrawString(v.ToString("0"), style.Font, label, new RectangleF(area.X - 6, y - inset, labelWidth + 2, 2 * inset), right);
            }
        }

        var points = history.Where(p => now - p.t <= seconds).Select(p => new PointF(X(p.t), Y(p.bpm))).ToList();
        if (points.Count < 2) return;

        var oldClip = g.Clip;
        g.SetClip(plot);
        using (var fill = new GraphicsPath())
        {
            fill.AddLines(points.ToArray());
            fill.AddLine(points[^1], new PointF(points[^1].X, plot.Bottom));
            fill.AddLine(new PointF(points[^1].X, plot.Bottom), new PointF(points[0].X, plot.Bottom));
            fill.CloseFigure();
            using var brush = new SolidBrush(style.Fill);
            g.FillPath(brush, fill);
        }
        using (var pen = new Pen(style.Line, 2f) { LineJoin = LineJoin.Round })
            g.DrawLines(pen, points.ToArray());
        using (var dot = new SolidBrush(style.Line))
            g.FillEllipse(dot, points[^1].X - 3, points[^1].Y - 3, 6, 6);
        g.Clip = oldClip;
    }
}
