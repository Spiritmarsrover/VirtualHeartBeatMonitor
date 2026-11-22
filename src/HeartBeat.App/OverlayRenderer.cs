using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using HeartBeat.Tracking;

namespace HeartBeat.App;

/// <summary>
/// Draws the wrist panel: a heart that pulses on each beat event, the BPM (or "--"), and a
/// status line. Output is RGBA bytes for <see cref="WristOverlay.SetImage"/>.
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    public const int Width = 320, Height = 220;
    public const double GraphSeconds = 60;

    private static readonly Color Panel = Color.FromArgb(225, 18, 20, 24);
    private static readonly Color Heart = Color.FromArgb(255, 232, 72, 85);
    private static readonly Color HeartIdle = Color.FromArgb(255, 110, 116, 124);
    private static readonly Color Ink = Color.FromArgb(255, 240, 242, 244);
    private static readonly Color Muted = Color.FromArgb(255, 150, 158, 166);

    private readonly Bitmap _bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    private readonly Font _bpmFont = new("Segoe UI", 44, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _unitFont = new("Segoe UI", 18, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _statusFont = new("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _graphFont = new("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly PointF[] _heartShape = HeartShape();

    /// <summary>What the panel shows apart from the beat animation; redraw when it changes.</summary>
    public static string ContentKey(LiveSession session)
    {
        var snap = session.LatestSnapshot;
        return $"{snap?.Heart.DisplayText}|{snap?.Heart.Displayed}|{StatusText(session)}|{session.HistoryCount}";
    }

    /// <summary>Draws the panel and returns the bitmap (BGRA). The bitmap is reused between calls.</summary>
    /// <param name="moveHint">When set, the panel is outlined and shows this hint instead of the status (move mode).</param>
    public Bitmap Draw(LiveSession session, string? moveHint = null)
    {
        var snap = session.LatestSnapshot;
        bool shown = snap?.Heart.Displayed == true;
        double sinceBeat = HostClock.Now - session.LastBeatHostTime;

        using (var g = Graphics.FromImage(_bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using (var panel = RoundedRect(new RectangleF(2, 2, Width - 4, Height - 4), 22))
            using (var brush = new SolidBrush(Panel))
                g.FillPath(brush, panel);

            // Heart: pops up on each beat and settles back within about 0.3 s.
            float pulse = shown ? (float)(1 + 0.28 * Math.Exp(-Math.Max(0, sinceBeat) / 0.11)) : 1f;
            DrawHeart(g, new PointF(64, 62), 38 * pulse, shown ? Heart : HeartIdle);

            string bpm = snap?.Heart.DisplayText ?? "--";
            using (var ink = new SolidBrush(shown ? Ink : Muted))
            {
                g.DrawString(bpm, _bpmFont, ink, new PointF(122, 22));
                var size = g.MeasureString(bpm, _bpmFont);
                using var muted = new SolidBrush(Muted);
                g.DrawString("BPM", _unitFont, muted, new PointF(122 + size.Width - 6, 52));
            }

            if (moveHint != null)
            {
                using (var outline = RoundedRect(new RectangleF(4, 4, Width - 8, Height - 8), 20))
                using (var pen = new Pen(Heart, 4))
                    g.DrawPath(pen, outline);
                using var ink2 = new SolidBrush(Ink);
                g.DrawString(moveHint, _statusFont, ink2, new RectangleF(20, 104, Width - 40, 24));
            }
            else
            {
                using var muted = new SolidBrush(Muted);
                g.DrawString(StatusText(session), _statusFont, muted, new RectangleF(20, 104, Width - 40, 24));
            }

            BpmGraph.Draw(g, new RectangleF(14, 134, Width - 34, 70), session.GetHistory(GraphSeconds), HostClock.Now, GraphSeconds,
                new BpmGraph.Style(Heart, Color.FromArgb(60, Heart), Color.FromArgb(70, Muted), Muted, _graphFont));
        }

        return _bitmap;
    }

    /// <summary>Saves the last rendered frame as PNG (development aid).</summary>
    public void SavePng(string path) => _bitmap.Save(path, ImageFormat.Png);

    public static string StatusText(LiveSession session)
    {
        if (session.Notice is { } notice) return notice;
        var snap = session.LatestSnapshot;
        if (snap == null || HostClock.Now - session.StartedAt < 8) return "warming up, stay still";
        if (snap.Devices.Count > 0 && snap.Devices.All(d => !d.Receiving)) return "no data from devices";
        if (snap.UserMoving) return "moving, paused";
        if (!snap.Heart.Displayed) return "measuring, stay still";
        if (snap.Breathing is { Valid: true } b)
            return b.IsHold ? $"breath hold {b.HoldSeconds:0} s" : $"breathing {(double.IsNaN(b.RatePerMinute) ? "--" : b.RatePerMinute.ToString("0"))}/min";
        return $"{snap.Heart.DevicesUsed} device{(snap.Heart.DevicesUsed == 1 ? "" : "s")}";
    }

    private void DrawHeart(Graphics g, PointF center, float size, Color color)
    {
        var pts = _heartShape.Select(p => new PointF(center.X + p.X * size, center.Y + p.Y * size)).ToArray();
        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, pts);
    }

    private static PointF[] HeartShape()
    {
        // Classic parametric heart, scaled to roughly unit size and centred.
        var pts = new PointF[72];
        for (int i = 0; i < pts.Length; i++)
        {
            double t = 2 * Math.PI * i / pts.Length;
            double x = 16 * Math.Pow(Math.Sin(t), 3);
            double y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            pts[i] = new PointF((float)(x / 17), (float)(-(y + 2.5) / 17));
        }
        return pts;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _bitmap.Dispose();
        _bpmFont.Dispose();
        _unitFont.Dispose();
        _statusFont.Dispose();
        _graphFont.Dispose();
    }
}
