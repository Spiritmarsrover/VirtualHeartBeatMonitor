using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using HeartBeat.Tracking;
using static HeartBeat.App.DashboardModel;

namespace HeartBeat.App;

/// <summary>
/// The SteamVR dashboard tab on Windows: draws <see cref="DashboardModel"/> (live readout on the
/// left, settings on the right) with System.Drawing; the model handles the laser pointer.
/// </summary>
public sealed class DashboardUi : IDisposable
{
    public const int Width = DashboardModel.Width, Height = DashboardModel.Height;

    private static readonly Color Bg = Color.FromArgb(255, 21, 24, 28);
    private static readonly Color Card = Color.FromArgb(255, 31, 35, 41);
    private static readonly Color Ink = Color.FromArgb(255, 236, 239, 241);
    private static readonly Color Muted = Color.FromArgb(255, 138, 146, 155);
    private static readonly Color Line = Color.FromArgb(255, 58, 64, 72);
    private static readonly Color Accent = Color.FromArgb(255, 227, 72, 90);

    private readonly DashboardModel _model;
    private readonly Bitmap _bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    private readonly Font _big = new("Segoe UI", 72, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _title = new("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _text = new("Segoe UI", 19, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _small = new("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _graphFont = new("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);

    public DashboardUi(MonitorHost host) => _model = new DashboardModel(host, desktopWindowOption: true);

    /// <summary>Applies laser-pointer input. Returns true when something changed (redraw).</summary>
    public bool Handle(IEnumerable<PanelMouseEvent> events) => _model.Handle(events);

    /// <summary>Draws the tab and returns the bitmap (BGRA). The bitmap is reused between calls.</summary>
    public Bitmap Draw(LiveSession? session)
    {
        using (var g = Graphics.FromImage(_bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Bg);
            using (var ink = new SolidBrush(Ink)) g.DrawString(DashboardModel.Title, _title, ink, 28, 14);
            using (var muted = new SolidBrush(Muted)) g.DrawString(DashboardModel.Description, _small, muted, 28, 44);
            // The live card is laid out from y = 0; the settings rows already include the header.
            var state = g.Save();
            g.TranslateTransform(0, DashboardModel.Header);
            DrawLive(g, session);
            g.Restore(state);
            using (var muted = new SolidBrush(Muted)) g.DrawString("Settings", _title, muted, 500, 26 + DashboardModel.Header);
            foreach (var w in _model.Widgets) DrawWidget(g, w);
        }
        return _bitmap;
    }

    public void SavePng(string path) => _bitmap.Save(path, ImageFormat.Png);

    /// <summary>A heart icon for the dashboard tab.</summary>
    public static void WriteThumbnail(string path)
    {
        using var bmp = IconArt.Render(128, IconArt.Red);
        bmp.Save(path, ImageFormat.Png);
    }

    private void DrawLive(Graphics g, LiveSession? session)
    {
        using var ink = new SolidBrush(Ink);
        using var muted = new SolidBrush(Muted);
        float height = Height - DashboardModel.Header; // drawn shifted down by the header
        var card = new RectangleF(28, 24, 440, height - 48);
        using (var path = Rounded(card, 18)) using (var b = new SolidBrush(Card)) g.FillPath(b, path);

        var snap = session?.LatestSnapshot;
        bool shown = snap?.Heart.Displayed == true;
        double sinceBeat = session == null ? 99 : HostClock.Now - session.LastBeatHostTime;
        float pulse = shown ? (float)(1 + 0.25 * Math.Exp(-Math.Max(0, sinceBeat) / 0.11)) : 1f;
        FillHeart(g, new PointF(110, 128), 54 * pulse, shown ? Accent : Line);

        string bpm = snap?.Heart.DisplayText ?? "--";
        using (var b = new SolidBrush(shown ? Ink : Muted)) g.DrawString(bpm, _big, b, 190, 72);
        var size = g.MeasureString(bpm, _big);
        g.DrawString("BPM", _text, muted, 190 + size.Width - 8, 132);

        float y = 196;
        g.DrawString(_model.StatusLine(session), _text, ink, 52, y);
        y += 30;
        if (session != null)
        {
            g.DrawString(BeatsLine(session), _small, muted, 52, y);
            y += 30;

            g.DrawString("Last 2 minutes", _small, muted, 52, y);
            y += 22;
            BpmGraph.Draw(g, new RectangleF(48, y, 400, 240), session.GetHistory(GraphSeconds), HostClock.Now, GraphSeconds,
                new BpmGraph.Style(Accent, Color.FromArgb(50, Accent), Line, Muted, _graphFont));
            y += 248;
            using (var pen = new Pen(Line)) g.DrawLine(pen, 52, y, 444, y);
            y += 12;
            g.DrawString("Devices (tap to leave one out)", _small, muted, 52, y);
            y += 30;
            _model.BeginDeviceRows();
            foreach (var d in session.GetDevices().Take(4))
            {
                var r = snap?.Devices.FirstOrDefault(x => x.Id == d.Id);
                var row = new RectangleF(46, y - 4, 406, 32);
                DrawSwitch(g, new RectangleF(row.Right - 50, y, 44, 22), d.UsedForHeartRate);
                using (var b = new SolidBrush(d.UsedForHeartRate ? Ink : Muted)) g.DrawString(d.Info.Kind, _small, b, 52, y);
                g.DrawString(DeviceState(d, r), _small, muted, 210, y);
                _model.AddDeviceRow(new RectangleF(row.X, row.Y + DashboardModel.Header, row.Width, row.Height), d);
                y += 34;
            }
        }
        g.DrawString(_model.Footer, _small, muted, 52, height - 66);
    }

    private void DrawWidget(Graphics g, Widget widget)
    {
        using var ink = new SolidBrush(Ink);
        using var muted = new SolidBrush(Muted);
        var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        switch (widget)
        {
            case Toggle t:
            {
                g.DrawString(t.Label, _text, ink, t.Bounds.X, t.Bounds.Y + 4);
                var track = t.Track;
                using (var p = Rounded(track, 13)) using (var b = new SolidBrush(t.On ? Accent : Line)) g.FillPath(b, p);
                g.FillEllipse(ink, t.On ? track.Right - 23 : track.X + 3, track.Y + 3, 20, 20);
                break;
            }
            case Slider s:
            {
                g.DrawString(s.Label, _text, ink, s.Bounds.X, s.Bounds.Y + 4);
                var t = s.Track;
                float f = (float)s.Fraction;
                using (var p = Rounded(t, 3)) using (var b = new SolidBrush(Line)) g.FillPath(b, p);
                var filled = new RectangleF(t.X, t.Y, t.Width * f, t.Height);
                if (filled.Width > 6) using (var p = Rounded(filled, 3)) using (var b = new SolidBrush(Accent)) g.FillPath(b, p);
                g.FillEllipse(ink, t.X + t.Width * f - 10, t.Y - 7, 20, 20);
                g.DrawString(s.ValueText, _small, muted, t.Right + 14, s.Bounds.Y + 6);
                break;
            }
            case Segment seg:
            {
                g.DrawString(seg.Label, _text, ink, seg.Bounds.X, seg.Bounds.Y + 4);
                var box = seg.Box;
                using (var p = Rounded(box, 8)) using (var b = new SolidBrush(Line)) g.FillPath(b, p);
                float w = box.Width / seg.Options.Count;
                for (int i = 0; i < seg.Options.Count; i++)
                {
                    var cell = new RectangleF(box.X + i * w, box.Y, w, box.Height);
                    if (i == seg.Selected)
                    {
                        using var p = Rounded(RectangleF.Inflate(cell, -3, -3), 6);
                        using var b = new SolidBrush(Accent);
                        g.FillPath(b, p);
                    }
                    g.DrawString(seg.Options[i], _small, ink, cell, center);
                }
                break;
            }
            case DashboardModel.Button btn:
            {
                var box = btn.Box;
                using (var p = Rounded(box, 8)) using (var b = new SolidBrush(Line)) g.FillPath(b, p);
                g.DrawString(btn.Label, _small, ink, box, center);
                break;
            }
        }
    }

    private static void DrawSwitch(Graphics g, RectangleF track, bool on)
    {
        using (var p = Rounded(track, track.Height / 2)) using (var b = new SolidBrush(on ? Accent : Line)) g.FillPath(b, p);
        float d = track.Height - 6;
        using var knob = new SolidBrush(Ink);
        g.FillEllipse(knob, on ? track.Right - d - 3 : track.X + 3, track.Y + 3, d, d);
    }

    private static void FillHeart(Graphics g, PointF c, float size, Color color)
    {
        var pts = new PointF[72];
        for (int i = 0; i < pts.Length; i++)
        {
            double t = 2 * Math.PI * i / pts.Length;
            double x = 16 * Math.Pow(Math.Sin(t), 3);
            double y = 13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t);
            pts[i] = new PointF(c.X + (float)(x / 17) * size, c.Y - (float)((y + 2.5) / 17) * size);
        }
        using var b = new SolidBrush(color);
        g.FillPolygon(b, pts);
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _bitmap.Dispose();
        _big.Dispose();
        _title.Dispose();
        _text.Dispose();
        _small.Dispose();
        _graphFont.Dispose();
    }
}
