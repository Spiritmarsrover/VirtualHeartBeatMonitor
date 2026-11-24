using System.Drawing;
using HeartBeat.Core;
using HeartBeat.Tracking;

namespace HeartBeat.App;

/// <summary>
/// The SteamVR dashboard tab without the drawing: the settings widgets, their layout and the
/// laser-pointer handling, shared by the Windows and Steam Frame versions. Live readout on the
/// left, settings on the right; each platform draws it its own way.
/// </summary>
public sealed class DashboardModel
{
    public const int Width = 960, Height = 840;

    /// <summary>Height of the title band at the top (title + one-line description).</summary>
    public const float Header = 60;
    public const string Title = "Virtual Heart Rate Monitor";
    public const string Description = "Each beat of your heart moves your body slightly. VR devices can read that to measure your heart rate while you stay still.";
    public const double GraphSeconds = 120;

    private readonly MonitorHost _host;
    private readonly List<Widget> _widgets = new();
    private readonly List<(RectangleF box, LiveSession.DeviceEntry device)> _deviceSwitches = new();
    private Widget? _pressed;

    public IReadOnlyList<Widget> Widgets => _widgets;

    /// <param name="desktopWindowOption">Offer "Show desktop window" (Windows only).</param>
    public DashboardModel(MonitorHost host, bool desktopWindowOption)
    {
        _host = host;
        var s = host.Settings;
        float x = 500, w = 430, y = 66 + Header, step = 36;
        void Add(Widget wd) { wd.Bounds = new RectangleF(x, y, w, step - 6); y += step; _widgets.Add(wd); }

        Add(new Toggle("Send to VRChat (OSC)", () => s.Osc.Enabled, v => s.Osc.Enabled = v));
        Add(new Toggle("Show BPM in the chatbox", () => s.Osc.Chatbox, v => s.Osc.Chatbox = v));
        Add(new Toggle("Fill in missed beats (predicted)", () => s.Beats.SendPredicted, v => s.Beats.SendPredicted = v));
        Add(new Toggle("Vibrate controllers on each beat", () => s.Haptics.Enabled, v => s.Haptics.Enabled = v));
        Add(new Slider("Vibration strength", 0, 1, () => s.Haptics.Strength, v => s.Haptics.Strength = v, v => $"{v * 100:0}%"));
        Add(new Segment("Vibrate", ["Both", "Left", "Right"], () => (int)s.Haptics.Hands, i => s.Haptics.Hands = (HapticHands)i));
        Add(new Toggle("Heart HUD in view", () => s.Hud.Enabled, v => s.Hud.Enabled = v));
        if (desktopWindowOption)
            Add(new Toggle("Show desktop window", () => s.General.ShowDesktopWindow, v => s.General.ShowDesktopWindow = v));
        Add(new Toggle("Start with SteamVR", () => host.AutoStartEnabled, v => host.RequestAutoStart(v), savesSettings: false));
        Add(new Segment("Wrist panel", ["Off", "Left", "Right"], () => (int)s.Overlay.Hand, i => s.Overlay.Hand = (OverlayHand)i));
        Add(new Slider("Panel size", 0.05, 0.15, () => s.Overlay.WidthMeters, v => s.Overlay.WidthMeters = (float)v, v => $"{v * 100:0} cm"));
        Add(new Toggle("Move wrist panel (grab it)", () => host.MovingWristPanel, v => host.SetMoveWristPanel(v), savesSettings: false));
        // Buttons last, together.
        Add(new Button("Reset panel position", () => s.Overlay.ResetPosition()));
        Add(new Button("Create diagnostics report", () => host.RequestDiagnostics()));
    }

    /// <summary>The drawer lists the device rows it drew, so taps on them can switch devices.</summary>
    public void BeginDeviceRows() => _deviceSwitches.Clear();
    public void AddDeviceRow(RectangleF box, LiveSession.DeviceEntry device) => _deviceSwitches.Add((box, device));

    /// <summary>Applies laser-pointer input. Returns true when something changed (redraw).</summary>
    public bool Handle(IEnumerable<PanelMouseEvent> events)
    {
        bool changed = false;
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                // Clicks act on press: on the Steam Frame the dashboard may not deliver the release.
                // Sliders follow the pointer until the release (or the next press).
                case PanelMouse.Down:
                    if (_pressed is Slider { SavesSettings: true }) _host.SettingsChanged(); // release never came
                    _pressed = null;
                    if (_deviceSwitches.FirstOrDefault(s => s.box.Contains(e.X, e.Y)).device is { } dev)
                    {
                        _host.SetDeviceUsed(dev, !dev.UsedForHeartRate);
                        changed = true;
                        break;
                    }
                    var hit = _widgets.FirstOrDefault(w => w.Bounds.Contains(e.X, e.Y));
                    if (hit is Slider sd) { sd.Drag(e.X); _pressed = sd; changed = true; }
                    else if (hit != null)
                    {
                        hit.Click(e.X);
                        if (hit.SavesSettings) _host.SettingsChanged();
                        changed = true;
                    }
                    break;
                case PanelMouse.Move:
                    if (_pressed is Slider drag) { drag.Drag(e.X); changed = true; }
                    break;
                case PanelMouse.Up:
                    if (_pressed is Slider done)
                    {
                        done.Drag(e.X);
                        _host.SettingsChanged();
                        changed = true;
                    }
                    _pressed = null;
                    break;
            }
        }
        return changed;
    }


    // ---- Texts both versions show -------------------------------------------------------------

    public string StatusLine(LiveSession? session)
    {
        string status = session == null ? _host.Status : session.StatusText();
        return status.Length == 0 ? "" : char.ToUpperInvariant(status[0]) + status[1..];
    }

    public static string BeatsLine(LiveSession session)
    {
        var snap = session.LatestSnapshot;
        double measured = session.BeatCount == 0 ? 0 : 100.0 * session.MeasuredBeatCount / session.BeatCount;
        string line = $"Beats {session.BeatCount} ({measured:0}% measured) · confidence {snap?.Heart.Confidence ?? 0:0.00}";
        if (snap?.Breathing is { Valid: true } br)
            line += $" · breathing {(double.IsNaN(br.RatePerMinute) ? "--" : br.RatePerMinute.ToString("0"))}/min{(br.IsHold ? " (hold)" : "")}";
        return line;
    }

    public static string DeviceState(LiveSession.DeviceEntry d, DeviceReport? r) =>
        !d.UsedForHeartRate ? "not used"
        : d.Status.Length > 0 ? d.Status
        : r == null ? "starting" : !r.Receiving ? "no data" : r.Resting ? "set down" : r.MovingNow ? "moving" : r.Prominence > 0 ? $"{r.PeakBpm:0} BPM" : "settling";

    public string Footer => _host.Osc == null ? "OSC off" : $"OSC {_host.Settings.Osc.Host}:{_host.Settings.Osc.Port}";

    // ---- Widgets ------------------------------------------------------------------------------

    public abstract class Widget(string label, bool savesSettings = true)
    {
        public RectangleF Bounds;
        public string Label { get; } = label;
        public bool SavesSettings { get; } = savesSettings;
        public virtual void Click(float x) { }
    }

    public sealed class Toggle(string label, Func<bool> get, Action<bool> set, bool savesSettings = true) : Widget(label, savesSettings)
    {
        public bool On => get();
        public RectangleF Track => new(Bounds.Right - 58, Bounds.Y + 5, 54, 26);
        public override void Click(float x) => set(!get());
    }

    public sealed class Slider(string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> format) : Widget(label)
    {
        public RectangleF Track => new(Bounds.X + 190, Bounds.Y + 15, Bounds.Width - 190 - 76, 6);
        public double Fraction => Math.Clamp((get() - min) / (max - min), 0, 1);
        public string ValueText => format(get());

        public void Drag(float x)
        {
            double f = Math.Clamp((x - Track.X) / Track.Width, 0, 1);
            set(min + f * (max - min));
        }
    }

    public sealed class Segment(string label, string[] options, Func<int> get, Action<int> set) : Widget(label)
    {
        public IReadOnlyList<string> Options => options;
        public int Selected => get();
        public RectangleF Box => new(Bounds.X + 190, Bounds.Y + 2, Bounds.Width - 190, Bounds.Height - 4);

        public override void Click(float x)
        {
            var box = Box;
            int i = (int)((x - box.X) / (box.Width / options.Length));
            if (i >= 0 && i < options.Length) set(i);
        }
    }

    public sealed class Button(string label, Action click) : Widget(label)
    {
        public RectangleF Box => new(Bounds.X, Bounds.Y + 2, 230, Bounds.Height - 4);
        public override void Click(float x) => click();
    }
}
