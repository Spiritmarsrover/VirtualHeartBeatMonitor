using System.Drawing;
using System.Drawing.Drawing2D;

namespace HeartBeat.App;

/// <summary>
/// Draws the recent heart rate as a line graph (scale: <see cref="GraphScale"/>). Seconds without a
/// number ("--") are stored as 0, so dropouts show as dips to the bottom edge.
/// </summary>
public static class BpmGraph
{
    public readonly record struct Style(Color Line, Color Fill, Color Grid, Color Label, Font Font);

    /// <param name="history">(host time, BPM or 0) samples, oldest first.</param>
    /// <param name="now">Host time now (the right edge follows the newest sample, see GraphScale.RightEdge).</param>
    /// <param name="fine">Fixed 40–160 scale with 10/5 BPM lines (dashboard, window); false: 0 / half / top (wrist panel).</param>
    public static void Draw(Graphics g, RectangleF area, IReadOnlyList<(double t, double bpm)> history, double now, double seconds, Style style, bool fine = true)
    {
        var (min, max) = GraphScale.Range(history, fine);
        var labelSize = g.MeasureString("000", style.Font);
        float labelWidth = labelSize.Width, inset = labelSize.Height / 2 + 1; // room for the top and bottom labels
        var plot = new RectangleF(area.X + labelWidth, area.Y + inset, area.Width - 2 * labelWidth, area.Height - 2 * inset); // labels on both sides
        double end = GraphScale.RightEdge(history, now);

        float Y(double bpm) => GraphScale.Y(bpm, min, max, plot.Top, plot.Bottom);
        float X(double t) => plot.Right - (float)((end - t) / seconds) * plot.Width;

        using (var solid = new Pen(style.Grid, 1f))
        using (var dashed = new Pen(Color.FromArgb(style.Grid.A * 2 / 3, style.Grid), 1f) { DashPattern = [3f, 3f] })
        using (var label = new SolidBrush(style.Label))
        {
            var right = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            var left = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
            foreach (var line in GraphScale.Lines(min, max, fine))
            {
                float y = Y(line.Value);
                g.DrawLine(line.Dashed ? dashed : solid, plot.X, y, plot.Right, y);
                if (!line.Label) continue;
                string text = line.Value.ToString("0");
                g.DrawString(text, style.Font, label, new RectangleF(area.X - 6, y - inset, labelWidth + 2, 2 * inset), right);
                g.DrawString(text, style.Font, label, new RectangleF(plot.Right + 2, y - inset, labelWidth + 4, 2 * inset), left);
            }
        }

        var oldClip = g.Clip;
        g.SetClip(plot);
        var recent = GraphScale.Thin(history.Where(p => end - p.t <= seconds).ToList(), (int)plot.Width);
        using var brush = new SolidBrush(style.Fill);
        using var pen = new Pen(style.Line, 2f) { LineJoin = LineJoin.Round };
        foreach (var run in GraphScale.Runs(recent, fine))
        {
            var points = run.Select(p => new PointF(X(p.t), Y(p.bpm))).ToArray();
            if (points.Length == 1) { g.FillEllipse(pen.Brush, points[0].X - 1.5f, points[0].Y - 1.5f, 3, 3); continue; }
            using (var fill = new GraphicsPath())
            {
                fill.AddLines(points);
                fill.AddLine(points[^1], new PointF(points[^1].X, plot.Bottom));
                fill.AddLine(new PointF(points[^1].X, plot.Bottom), new PointF(points[0].X, plot.Bottom));
                fill.CloseFigure();
                g.FillPath(brush, fill);
            }
            g.DrawLines(pen, points);
        }
        g.Clip = oldClip;
    }
}
