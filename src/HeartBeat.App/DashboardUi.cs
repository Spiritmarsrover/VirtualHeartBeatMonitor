using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using HeartBeat.Core;
using HeartBeat.Tracking;

namespace HeartBeat.App;

/// <summary>
/// The SteamVR dashboard tab: live readout on the left, settings on the right, drawn with
/// System.Drawing and operated with the laser pointer (toggles, sliders, segmented choices).
/// </summary>
public sealed class DashboardUi : IDisposable
{
    public const int Width = 960, Height = 640;

    private static readonly Color Bg = Color.FromArgb(255, 21, 24, 28);
    private static readonly Color Card = Color.FromArgb(255, 31, 35, 41);
    private static readonly Color Ink = Color.FromArgb(255, 236, 239, 241);
    private static readonly Color Muted = Color.FromArgb(255, 138, 146, 155);
    private static readonly Color Line = Color.FromArgb(255, 58, 64, 72);
    private static readonly Color Accent = Color.FromArgb(255, 227, 72, 90);
    private static readonly Color AccentDim = Color.FromArgb(255, 120, 52, 60);

    private readonly MonitorHost _host;
    private readonly Bitmap _bitmap = new(Width, Height, PixelFormat.Format32bppArgb);
    private readonly Font _big = new("Segoe UI", 72, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _title = new("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
    private readonly Font _text = new("Segoe UI", 19, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _small = new("Segoe UI", 16, FontStyle.Regular, GraphicsUnit.Pixel);
    private readonly Font _graphFont = new("Segoe UI", 13, FontStyle.Regular, GraphicsUnit.Pixel);
    private const double GraphSeconds = 120;
    private readonly List<Widget> _widgets = new();
    private Widget? _pressed;
    private readonly List<(RectangleF box, LiveSession.DeviceEntry device)> _deviceSwitches = new();
    private LiveSession.DeviceEntry? _pressedDevice;

    public DashboardUi(MonitorHost host)
    {
        _host = host;
        var s = host.Settings;
        float x = 500, w = 430, y = 66, step = 36;
        Widget Add(Widget wd) { wd.Bounds = new RectangleF(x, y, w, step - 6); y += step; _widgets.Add(wd); return wd; }

        Add(new Toggle("Send to VRChat (OSC)", () => s.Osc.Enabled, v => s.Osc.Enabled = v));
        Add(new Toggle("Show BPM in the chatbox", () => s.Osc.Chatbox, v => s.Osc.Chatbox = v));
        Add(new Toggle("Fill in missed beats (predicted)", () => s.Beats.SendPredicted, v => s.Beats.SendPredicted = v));
        Add(new Toggle("Vibrate controllers on each beat", () => s.Haptics.Enabled, v => s.Haptics.Enabled = v));
        Add(new Slider("Vibration strength", 0, 1, () => s.Haptics.Strength, v => s.Haptics.Strength = v, v => $"{v * 100:0}%"));
        Add(new Segment("Vibrate", ["Both", "Left", "Right"], () => (int)s.Haptics.Hands, i => s.Haptics.Hands = (HapticHands)i));
        Add(new Segment("Wrist panel", ["Off", "Left", "Right"], () => (int)s.Overlay.Hand, i => s.Overlay.Hand = (OverlayHand)i));
        Add(new Slider("Panel size", 0.05, 0.15, () => s.Overlay.WidthMeters, v => s.Overlay.WidthMeters = (float)v, v => $"{v * 100:0} cm"));
        Add(new Toggle("Move wrist panel (grab it)", () => host.MovingWristPanel, v => host.SetMoveWristPanel(v), savesSettings: false));
        Add(new Button("Reset panel position", () => s.Overlay.ResetPosition()));
        Add(new Button("Create diagnostics report", () => host.RequestDiagnostics()));
        Add(new Toggle("Show desktop window", () => s.General.ShowDesktopWindow, v => s.General.ShowDesktopWindow = v));
        Add(new Toggle("Start with SteamVR", () => host.AutoStartEnabled, v => host.RequestAutoStart(v), savesSettings: false));
    }

    /// <summary>Applies laser-pointer input. Returns true when something changed (redraw).</summary>
    public bool Handle(IEnumerable<PanelMouseEvent> events)
    {
        bool changed = false;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case PanelMouse.Down:
                    _pressedDevice = _deviceSwitches.FirstOrDefault(s => s.box.Contains(e.X, e.Y)).device;
                    _pressed = _widgets.FirstOrDefault(w => w.Bounds.Contains(e.X, e.Y));
                    if (_pressed is Slider sd) { sd.Drag(e.X); changed = true; }
                    break;
                case PanelMouse.Move:
                    if (_pressed is Slider drag) { drag.Drag(e.X); changed = true; }
                    break;
                case PanelMouse.Up:
                    if (_pressedDevice is { } dev && _deviceSwitches.Any(s => s.device == dev && s.box.Contains(e.X, e.Y)))
                    {
                        _host.SetDeviceUsed(dev, !dev.UsedForHeartRate);
                        changed = true;
                    }
                    _pressedDevice = null;
                    if (_pressed != null)
                    {
                        if (_pressed is not Slider && _pressed.Bounds.Contains(e.X, e.Y)) _pressed.Click(e.X);
                        if (_pressed.SavesSettings) _host.SettingsChanged();
                        changed = true;
                    }
                    _pressed = null;
                    break;
            }
        }
        return changed;
    }

    /// <summary>Draws the tab and returns the bitmap (BGRA). The bitmap is reused between calls.</summary>
    public Bitmap Draw(LiveSession? session)
    {
        using (var g = Graphics.FromImage(_bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Bg);
            DrawLive(g, session);
            using (var muted = new SolidBrush(Muted)) g.DrawString("Settings", _title, muted, 500, 26);
            foreach (var w in _widgets) w.Draw(g, this);
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
        var card = new RectangleF(28, 24, 440, Height - 48);
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
        string status = session == null ? _host.Status : OverlayRenderer.StatusText(session);
        g.DrawString(char.ToUpperInvariant(status[0]) + status[1..], _text, ink, 52, y);
        y += 30;
        if (session != null)
        {
            double measured = session.BeatCount == 0 ? 0 : 100.0 * session.MeasuredBeatCount / session.BeatCount;
            string line = $"Beats {session.BeatCount} ({measured:0}% measured) · confidence {snap?.Heart.Confidence ?? 0:0.00}";
            if (snap?.Breathing is { Valid: true } br)
                line += $" · breathing {(double.IsNaN(br.RatePerMinute) ? "--" : br.RatePerMinute.ToString("0"))}/min{(br.IsHold ? " (hold)" : "")}";
            g.DrawString(line, _small, muted, 52, y);
            y += 30;

            g.DrawString("Last 2 minutes", _small, muted, 52, y);
            y += 22;
            BpmGraph.Draw(g, new RectangleF(48, y, 400, 96), session.GetHistory(GraphSeconds), HostClock.Now, GraphSeconds,
                new BpmGraph.Style(Accent, Color.FromArgb(50, Accent), Line, Muted, _graphFont));
            y += 104;
            using (var pen = new Pen(Line)) g.DrawLine(pen, 52, y, 444, y);
            y += 12;
            g.DrawString("Devices (tap to leave one out)", _small, muted, 52, y);
            y += 30;
            _deviceSwitches.Clear();
            foreach (var d in session.GetDevices().Take(4))
            {
                var r = snap?.Devices.FirstOrDefault(x => x.Id == d.Id);
                string state = !d.UsedForHeartRate ? "not used"
                    : d.Status.Length > 0 ? d.Status
                    : r == null ? "starting" : !r.Receiving ? "no data" : r.MovingNow ? "moving" : r.Prominence > 0 ? $"{r.PeakBpm:0} BPM" : "settling";
                var row = new RectangleF(46, y - 4, 406, 32);
                DrawSwitch(g, new RectangleF(row.Right - 50, y, 44, 22), d.UsedForHeartRate);
                using (var b = new SolidBrush(d.UsedForHeartRate ? Ink : Muted)) g.DrawString(d.Info.Kind, _small, b, 52, y);
                g.DrawString(state, _small, muted, 210, y);
                _deviceSwitches.Add((row, d));
                y += 34;
            }
        }
        var osc = _host.Osc;
        string footer = osc == null ? "OSC off" : $"OSC {_host.Settings.Osc.Host}:{_host.Settings.Osc.Port}";
        g.DrawString(footer, _small, muted, 52, Height - 66);
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

    // ---- Widgets ------------------------------------------------------------------------------

    private abstract class Widget(string label, bool savesSettings = true)
    {
        public RectangleF Bounds;
        public string Label { get; } = label;
        public bool SavesSettings { get; } = savesSettings;
        public abstract void Draw(Graphics g, DashboardUi ui);
        public virtual void Click(float x) { }
    }

    private sealed class Toggle(string label, Func<bool> get, Action<bool> set, bool savesSettings = true) : Widget(label, savesSettings)
    {
        public override void Click(float x) => set(!get());

        public override void Draw(Graphics g, DashboardUi ui)
        {
            using var ink = new SolidBrush(Ink);
            g.DrawString(Label, ui._text, ink, Bounds.X, Bounds.Y + 4);
            bool on = get();
            var track = new RectangleF(Bounds.Right - 58, Bounds.Y + 5, 54, 26);
            using (var p = Rounded(track, 13)) using (var b = new SolidBrush(on ? Accent : Line)) g.FillPath(b, p);
            float knobX = on ? track.Right - 23 : track.X + 3;
            using var knob = new SolidBrush(Ink);
            g.FillEllipse(knob, knobX, track.Y + 3, 20, 20);
        }
    }

    private sealed class Slider(string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> format) : Widget(label)
    {
        private RectangleF Track => new(Bounds.X + 190, Bounds.Y + 15, Bounds.Width - 190 - 76, 6);

        public void Drag(float x)
        {
            double f = Math.Clamp((x - Track.X) / Track.Width, 0, 1);
            set(min + f * (max - min));
        }

        public override void Draw(Graphics g, DashboardUi ui)
        {
            using var ink = new SolidBrush(Ink);
            using var muted = new SolidBrush(Muted);
            g.DrawString(Label, ui._text, ink, Bounds.X, Bounds.Y + 4);
            var t = Track;
            double f = Math.Clamp((get() - min) / (max - min), 0, 1);
            using (var p = Rounded(t, 3)) using (var b = new SolidBrush(Line)) g.FillPath(b, p);
            var filled = new RectangleF(t.X, t.Y, (float)(t.Width * f), t.Height);
            if (filled.Width > 6) using (var p = Rounded(filled, 3)) using (var b = new SolidBrush(Accent)) g.FillPath(b, p);
            using (var knob = new SolidBrush(Ink)) g.FillEllipse(knob, t.X + (float)(t.Width * f) - 10, t.Y - 7, 20, 20);
            g.DrawString(format(get()), ui._small, muted, t.Right + 14, Bounds.Y + 6);
        }
    }

    private sealed class Segment(string label, string[] options, Func<int> get, Action<int> set) : Widget(label)
    {
        private RectangleF Box => new(Bounds.X + 190, Bounds.Y + 2, Bounds.Width - 190, Bounds.Height - 4);

        public override void Click(float x)
        {
            var box = Box;
            int i = (int)((x - box.X) / (box.Width / options.Length));
            if (i >= 0 && i < options.Length) set(i);
        }

        public override void Draw(Graphics g, DashboardUi ui)
        {
            using var ink = new SolidBrush(Ink);
            g.DrawString(Label, ui._text, ink, Bounds.X, Bounds.Y + 4);
            var box = Box;
            using (var p = Rounded(box, 8)) using (var b = new SolidBrush(Line)) g.FillPath(b, p);
            float w = box.Width / options.Length;
            int selected = get();
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            for (int i = 0; i < options.Length; i++)
            {
                var cell = new RectangleF(box.X + i * w, box.Y, w, box.Height);
                if (i == selected)
                {
                    var inner = RectangleF.Inflate(cell, -3, -3);
                    using var p = Rounded(inner, 6);
                    using var b = new SolidBrush(Accent);
                    g.FillPath(b, p);
                }
                g.DrawString(options[i], ui._small, ink, cell, fmt);
            }
        }
    }

    private sealed class Button(string label, Action click) : Widget(label)
    {
        public override void Click(float x) => click();

        public override void Draw(Graphics g, DashboardUi ui)
        {
            var box = new RectangleF(Bounds.X, Bounds.Y + 2, 230, Bounds.Height - 4);
            using (var p = Rounded(box, 8)) using (var b = new SolidBrush(Line)) g.FillPath(b, p);
            using var ink = new SolidBrush(Ink);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(Label, ui._small, ink, box, fmt);
        }
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
