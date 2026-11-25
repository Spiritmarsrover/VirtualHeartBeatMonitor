using System.Drawing;
using HeartBeat.App;
using HeartBeat.Tracking;
using static HeartBeat.App.DashboardModel;

namespace HeartBeat.Native;

/// <summary>Shapes shared by the Frame panels (same look as the Windows panels).</summary>
internal static class Shapes
{
    /// <summary>The classic parametric heart, centred on c, about 2*size wide.</summary>
    public static PointF[] Heart(PointF c, float size)
    {
        var pts = new PointF[72];
        for (int i = 0; i < pts.Length; i++)
        {
            double t = 2 * Math.PI * i / pts.Length;
            double x = 16 * Math.Pow(Math.Sin(t), 3);
            double y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            pts[i] = new PointF(c.X + (float)(x / 17) * size, c.Y - (float)((y + 2.5) / 17) * size);
        }
        return pts;
    }

    /// <summary>
    /// Recent heart rate as a line graph (scale: <see cref="GraphScale"/>; as on Windows, see
    /// BpmGraph). "--" is stored as 0, so dropouts dip to the bottom edge.
    /// </summary>
    public static void Graph(Canvas c, RectangleF area, IReadOnlyList<(double t, double bpm)> history, double now, double seconds,
        Rgba line, Rgba fill, Rgba grid, Rgba label, UiFont font, bool fine = true)
    {
        var (min, max) = GraphScale.Range(history, fine);
        float labelWidth = font.Measure("000") + 4, inset = font.LineHeight / 2 + 1;
        var plot = new RectangleF(area.X + labelWidth, area.Y + inset, area.Width - 2 * labelWidth, area.Height - 2 * inset); // labels on both sides
        double end = GraphScale.RightEdge(history, now);
        float Y(double bpm) => GraphScale.Y(bpm, min, max, plot.Top, plot.Bottom);
        float X(double t) => plot.Right - (float)((end - t) / seconds) * plot.Width;

        var dashed = grid.WithAlpha(grid.A * 2 / 3);
        foreach (var l in GraphScale.Lines(min, max, fine))
        {
            float y = Y(l.Value);
            if (l.Dashed)
                for (float x = plot.X; x < plot.Right; x += 6) c.FillRect(new RectangleF(x, y - 0.5f, Math.Min(3, plot.Right - x), 1), dashed);
            else c.FillRect(new RectangleF(plot.X, y - 0.5f, plot.Width, 1), grid);
            if (l.Label)
            {
                string text = l.Value.ToString("0");
                c.DrawTextRight(text, font, label, plot.X - 4, y);
                c.DrawText(text, font, label, plot.Right + 4, y - font.LineHeight / 2);
            }
        }

        c.SetClip(plot);
        var recent = GraphScale.Thin(history.Where(p => end - p.t <= seconds).ToList(), (int)plot.Width);
        foreach (var run in GraphScale.Runs(recent, fine))
        {
            var points = run.Select(p => new PointF(X(p.t), Y(p.bpm))).ToList();
            if (points.Count == 1) { c.FillCircle(points[0].X, points[0].Y, 1.5f, line); continue; }
            var area2 = new List<PointF>(points) { new(points[^1].X, plot.Bottom), new(points[0].X, plot.Bottom) };
            c.FillPolygon(area2, fill);
            c.StrokePolyline(points, 2f, line);
        }
        c.ResetClip();
    }
}

/// <summary>The wrist panel on the Frame: pulsing heart, BPM, status line, last minute graph.</summary>
internal sealed class WristDrawer
{
    public const int Width = 320, Height = 220;
    private const double GraphSeconds = 60;

    private static readonly Rgba Panel = Rgba.Argb(225, 18, 20, 24);
    private static readonly Rgba Heart = Rgba.Argb(255, 232, 72, 85);
    private static readonly Rgba HeartIdle = Rgba.Argb(255, 110, 116, 124);
    private static readonly Rgba Ink = Rgba.Argb(255, 240, 242, 244);
    private static readonly Rgba Muted = Rgba.Argb(255, 150, 158, 166);

    public Canvas Canvas { get; } = new(Width, Height);

    /// <summary>What the panel shows apart from the beat animation; redraw when it changes.</summary>
    public static string ContentKey(LiveSession session)
    {
        var snap = session.LatestSnapshot;
        return $"{snap?.Heart.DisplayText}|{snap?.Heart.Displayed}|{session.StatusText()}|{session.HistoryCount}";
    }

    public Canvas Draw(LiveSession session, string? moveHint = null)
    {
        var c = Canvas;
        var snap = session.LatestSnapshot;
        bool shown = snap?.Heart.Displayed == true;
        double sinceBeat = HostClock.Now - session.LastBeatHostTime;

        c.Clear(new Rgba(0, 0, 0, 0));
        c.FillRoundedRect(new RectangleF(2, 2, Width - 4, Height - 4), 22, Panel);

        // Heart: pops up on each beat and settles back within about 0.3 s.
        float pulse = shown ? (float)(1 + 0.28 * Math.Exp(-Math.Max(0, sinceBeat) / 0.11)) : 1f;
        c.FillPolygon(Shapes.Heart(new PointF(64, 62), 38 * pulse), shown ? Heart : HeartIdle);

        string bpm = snap?.Heart.DisplayText ?? "--";
        float w = c.DrawText(bpm, UiFont.Bold44, shown ? Ink : Muted, 126, 22);
        c.DrawText("BPM", UiFont.Regular19, Muted, 126 + w + 4, 52);

        if (moveHint != null)
        {
            c.StrokeRoundedRect(new RectangleF(4, 4, Width - 8, Height - 8), 20, 4, Heart);
            c.DrawText(moveHint, UiFont.Regular16, Ink, 22, 104);
        }
        else c.DrawText(session.StatusText(), UiFont.Regular16, Muted, 22, 104);

        Shapes.Graph(c, new RectangleF(14, 134, Width - 34, 70), session.GetHistory(GraphSeconds), HostClock.Now, GraphSeconds,
            Heart, Heart.WithAlpha(60), Muted.WithAlpha(70), Muted, UiFont.Regular13, fine: false);
        return c;
    }
}

/// <summary>The SteamVR dashboard tab on the Frame: draws <see cref="DashboardModel"/> like the Windows version.</summary>
internal sealed class DashboardDrawer
{
    public const int Width = DashboardModel.Width, Height = DashboardModel.Height;

    private static readonly Rgba Bg = Rgba.Argb(255, 21, 24, 28);
    private static readonly Rgba Card = Rgba.Argb(255, 31, 35, 41);
    private static readonly Rgba Ink = Rgba.Argb(255, 236, 239, 241);
    private static readonly Rgba Muted = Rgba.Argb(255, 138, 146, 155);
    private static readonly Rgba Line = Rgba.Argb(255, 58, 64, 72);
    private static readonly Rgba Accent = Rgba.Argb(255, 227, 72, 90);

    public DashboardModel Model { get; }
    public Canvas Canvas { get; } = new(Width, Height);

    public DashboardDrawer(MonitorHost host) => Model = new DashboardModel(host, desktopWindowOption: false);

    public Canvas Draw(LiveSession? session)
    {
        var c = Canvas;
        c.Clear(Bg);
        c.DrawText(DashboardModel.Title, UiFont.Bold22, Ink, 28, 14);
        c.DrawText(DashboardModel.Description, UiFont.Regular16, Muted, 28, 44);
        DrawLive(c, session);
        Model.GraphMenu.Shown = session != null;
        c.DrawText("Settings", UiFont.Bold22, Muted, 500, 26 + DashboardModel.Header);
        foreach (var w in Model.Widgets) DrawWidget(c, w);
        return c;
    }

    /// <summary>The live card, below the title band (every y includes <see cref="DashboardModel.Header"/>).</summary>
    private void DrawLive(Canvas c, LiveSession? session)
    {
        const float o = DashboardModel.Header;
        c.FillRoundedRect(new RectangleF(28, 24 + o, 440, Height - 48 - o), 18, Card);
        var snap = session?.LatestSnapshot;
        bool shown = snap?.Heart.Displayed == true;
        double sinceBeat = session == null ? 99 : HostClock.Now - session.LastBeatHostTime;
        float pulse = shown ? (float)(1 + 0.25 * Math.Exp(-Math.Max(0, sinceBeat) / 0.11)) : 1f;
        c.FillPolygon(Shapes.Heart(new PointF(110, 128 + o), 54 * pulse), shown ? Accent : Line);

        string bpm = snap?.Heart.DisplayText ?? "--";
        float w = c.DrawText(bpm, UiFont.Bold72, shown ? Ink : Muted, 194, 64 + o);
        c.DrawText("BPM", UiFont.Regular19, Muted, 194 + w + 6, 132 + o);

        float y = 196 + o;
        c.DrawText(Model.StatusLine(session), UiFont.Regular19, Ink, 52, y);
        y += 30;
        if (session != null)
        {
            c.DrawText(BeatsLine(session), UiFont.Regular16, Muted, 52, y);
            y += 30;
            y += 22; // the graph's time menu (a widget, drawn with the settings)
            double seconds = Model.GraphSeconds;
            Shapes.Graph(c, new RectangleF(48, y, 400, 240), session.GetHistory(seconds), HostClock.Now, seconds,
                Accent, Accent.WithAlpha(50), Line, Muted, UiFont.Regular13);
            y += 248;
            c.FillRect(new RectangleF(52, y, 392, 1), Line);
            y += 12;
            c.DrawText("Devices (tap to leave one out)", UiFont.Regular16, Muted, 52, y);
            y += 30;
            Model.BeginDeviceRows();
            foreach (var d in session.GetDevices().Take(4))
            {
                var r = snap?.Devices.FirstOrDefault(x => x.Id == d.Id);
                var row = new RectangleF(46, y - 4, 406, 32);
                DrawSwitch(c, new RectangleF(row.Right - 50, y, 44, 22), d.UsedForHeartRate);
                c.DrawText(d.Info.Kind, UiFont.Regular16, d.UsedForHeartRate ? Ink : Muted, 52, y);
                c.DrawText(DeviceState(d, r), UiFont.Regular16, Muted, 210, y);
                Model.AddDeviceRow(row, d);
                y += 34;
            }
        }
        c.DrawText(Model.Footer, UiFont.Regular16, Muted, 52, Height - 66);
    }

    private static void DrawWidget(Canvas c, Widget widget)
    {
        switch (widget)
        {
            case Toggle t:
            {
                c.DrawText(t.Label, UiFont.Regular19, Ink, t.Bounds.X, t.Bounds.Y + 4);
                var track = t.Track;
                c.FillRoundedRect(track, 13, t.On ? Accent : Line);
                c.FillCircle(t.On ? track.Right - 13 : track.X + 13, track.Y + 13, 10, Ink);
                break;
            }
            case Slider s:
            {
                c.DrawText(s.Label, UiFont.Regular19, Ink, s.Bounds.X, s.Bounds.Y + 4);
                var t = s.Track;
                float f = (float)s.Fraction;
                c.FillRoundedRect(t, 3, Line);
                if (t.Width * f > 6) c.FillRoundedRect(new RectangleF(t.X, t.Y, t.Width * f, t.Height), 3, Accent);
                c.FillCircle(t.X + t.Width * f, t.Y + 3, 10, Ink);
                c.DrawText(s.ValueText, UiFont.Regular16, Muted, t.Right + 14, s.Bounds.Y + 6);
                break;
            }
            case Segment seg:
            {
                c.DrawText(seg.Label, UiFont.Regular19, Ink, seg.Bounds.X, seg.Bounds.Y + 4);
                var box = seg.Box;
                c.FillRoundedRect(box, 8, Line);
                float w = box.Width / seg.Options.Count;
                for (int i = 0; i < seg.Options.Count; i++)
                {
                    var cell = new RectangleF(box.X + i * w, box.Y, w, box.Height);
                    if (i == seg.Selected) c.FillRoundedRect(RectangleF.Inflate(cell, -3, -3), 6, Accent);
                    c.DrawTextCentered(seg.Options[i], UiFont.Regular16, Ink, cell);
                }
                break;
            }
            case DashboardModel.Button btn:
                c.FillRoundedRect(btn.Box, 8, Line);
                c.DrawTextCentered(btn.Label, UiFont.Regular16, Ink, btn.Box);
                break;
            case Dropdown { Shown: false }:
                break;
            case Dropdown dd:
            {
                var box = dd.Bounds;
                var font = UiFont.Regular16;
                c.FillRoundedRect(box, 7, Line);
                c.DrawText(dd.Options[dd.Selected], font, Ink, box.X + 10, box.Y + (box.Height - font.LineHeight) / 2);
                float ax = box.Right - 18, ay = box.Y + box.Height / 2;
                c.FillPolygon(dd.Open
                    ? [new PointF(ax - 5, ay + 3), new PointF(ax + 5, ay + 3), new PointF(ax, ay - 3)]
                    : [new PointF(ax - 5, ay - 3), new PointF(ax + 5, ay - 3), new PointF(ax, ay + 3)], Ink);
                if (!dd.Open) break;
                var list = dd.List;
                c.FillRoundedRect(RectangleF.Inflate(list, 2, 2), 8, Line); // border
                c.FillRoundedRect(list, 7, Card);
                for (int i = 0; i < dd.Options.Count; i++)
                {
                    var row = dd.OptionBox(i);
                    if (i == dd.Selected) c.FillRoundedRect(RectangleF.Inflate(row, -3, -3), 6, Accent);
                    c.DrawText(dd.Options[i], font, Ink, row.X + 10, row.Y + (row.Height - font.LineHeight) / 2);
                }
                break;
            }
        }
    }

    private static void DrawSwitch(Canvas c, RectangleF track, bool on)
    {
        c.FillRoundedRect(track, track.Height / 2, on ? Accent : Line);
        float d = track.Height - 6;
        c.FillCircle(on ? track.Right - 3 - d / 2 : track.X + 3 + d / 2, track.Y + track.Height / 2, d / 2, Ink);
    }

    /// <summary>A heart icon for the dashboard tab (PNG, 128 px).</summary>
    public static byte[] Thumbnail()
    {
        var c = new Canvas(128, 128);
        c.Clear(new Rgba(0, 0, 0, 0));
        c.FillPolygon(Shapes.Heart(new PointF(64, 66), 58), Accent);
        return Canvas.EncodePng(c.Pixels, 128, 128);
    }
}

/// <summary>Draws the HUD on the Frame: a heart in the status colour with the BPM inside (as HudRenderer on Windows).</summary>
internal sealed class HudDrawer
{
    public const int Size = HudStatus.Size;
    public Canvas Canvas { get; } = new(Size, Size);

    public Canvas Draw(HudView view)
    {
        var c = Canvas;
        c.Clear(new Rgba(0, 0, 0, 0));
        var (r, g, b) = HudStatus.Color(view.State);
        c.FillPolygon(Shapes.Heart(new PointF(64, 68), 54 * view.Pulse), new Rgba(r, g, b, 255)); // room for the beat pulse
        var font = UiFont.Bold44; // three digits still fit the heart's wide part
        float w = font.Measure(view.Text), x = (Size - w) / 2, y = 55 - font.LineHeight / 2;
        c.DrawText(view.Text, font, Rgba.Argb(110, 0, 0, 0), x + 1.5f, y + 2);
        c.DrawText(view.Text, font, Rgba.Argb(255, 255, 255, 255), x, y);
        return c;
    }
}
