using System.Drawing;
using HeartBeat.Core;
using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.App;

/// <summary>
/// Optional desktop window: live readout on top, settings below (saved immediately, kept in sync
/// with the SteamVR dashboard tab). A tray icon stays available when the window is hidden.
/// </summary>
public sealed class MainForm : Form
{
    private static readonly Color HeartRed = Color.FromArgb(226, 62, 76);
    private static readonly Color HeartDim = Color.FromArgb(120, 52, 60);
    private static readonly Color Grey = Color.FromArgb(150, 156, 164);

    private readonly MonitorHost _host;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private readonly NotifyIcon _tray = new() { Text = "VirtualHeartBeatMonitor", Visible = true };

    private readonly Label _heart = new() { Text = "♥", AutoSize = true, Font = new Font("Segoe UI Symbol", 40f), ForeColor = Grey, Margin = new Padding(0, 0, 8, 0) };
    private readonly Label _bpm = new() { Text = "--", AutoSize = true, Font = new Font("Segoe UI", 44f, FontStyle.Bold), Margin = new Padding(0) };
    private readonly Label _unit = new() { Text = "BPM", AutoSize = true, Font = new Font("Segoe UI", 14f), ForeColor = Grey, Margin = new Padding(4, 34, 0, 0) };
    private readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 11f) };
    private readonly Label _beats = new() { AutoSize = true, ForeColor = Grey };
    private readonly Label _breathing = new() { AutoSize = true, ForeColor = Grey };
    private readonly GraphPanel _graph = new() { Height = 120, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 6, 0, 0) };
    private readonly Font _graphFont = new("Segoe UI", 8.5f);
    private int _graphPoints = -1;
    private const double GraphSeconds = 120;
    private readonly ListView _devices = new() { View = View.Details, CheckBoxes = true, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill };

    private readonly CheckBox _osc = new() { Text = "Send to VRChat (OSC)", AutoSize = true };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Width = 80 };
    private readonly CheckBox _chatbox = new() { Text = "Show BPM in the chatbox", AutoSize = true };

    private readonly CheckBox _predicted = new() { Text = "Fill in missed beats (predicted)", AutoSize = true };
    private readonly CheckBox _haptics = new() { Text = "Vibrate controllers on each beat", AutoSize = true };
    private readonly TrackBar _hapticStrength = Track(0, 100);
    private readonly ComboBox _hapticHands = Combo("Both", "Left", "Right");

    private readonly ComboBox _hand = Combo("Off", "Left", "Right");
    private readonly TrackBar _panelSize = Track(5, 15);       // cm
    private readonly CheckBox _movePanel = new() { Text = "Move it: grab with your other hand", AutoSize = true };
    private readonly Button _panelReset = new() { Text = "Reset position", AutoSize = true };

    private readonly CheckBox _showWindow = new() { Text = "Show this window (otherwise tray icon)", AutoSize = true };
    private readonly CheckBox _autoStart = new() { Text = "Start with SteamVR", AutoSize = true };
    private readonly Button _diagnostics = new() { Text = "Create diagnostics report", AutoSize = true };
    private readonly CheckedListBox _chest = new() { CheckOnClick = true, Height = 56, Width = 270, IntegralHeight = false };
    private readonly Label _footer = new() { AutoSize = true, ForeColor = Grey, MaximumSize = new Size(620, 0) };

    private bool _loading;
    private int _seenVersion = -1;
    private double _autoStartRequestedAt = double.NegativeInfinity;
    private string? _chestKey;
    private bool _allowShow;

    public MainForm(MonitorHost host, double? closeAfterSeconds = null)
    {
        _host = host;
        Text = "VirtualHeartBeatMonitor";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(640, 940);
        MinimumSize = new Size(560, 640);
        Padding = new Padding(14);
        _allowShow = host.Settings.General.ShowDesktopWindow;

        BuildLayout();
        BuildTray();

        _timer.Tick += (_, _) => RefreshView();
        _timer.Start();
        _host.SteamVrClosed += () => BeginInvoke(Close);
        FormClosing += (_, _) => { _timer.Stop(); _tray.Visible = false; _host.Dispose(); };

        if (host.LaunchedBySteamVr && host.Settings.General.StartMinimizedWithSteamVr) WindowState = FormWindowState.Minimized;
        if (closeAfterSeconds is { } secs)
        {
            var closer = new System.Windows.Forms.Timer { Interval = (int)(secs * 1000) };
            closer.Tick += (_, _) => Close();
            closer.Start();
        }
    }

    // Starts hidden (tray only) when the desktop window is turned off.
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated && !_allowShow)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    private static TrackBar Track(int min, int max) =>
        new() { Minimum = min, Maximum = max, TickStyle = TickStyle.None, AutoSize = false, Height = 26, Width = 170 };

    private static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
        c.Items.AddRange(items);
        return c;
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1 };
        for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 4) };
        header.Controls.AddRange([_heart, _bpm, _unit]);
        root.Controls.Add(header);
        root.Controls.Add(_status);
        root.Controls.Add(_beats);
        root.Controls.Add(_breathing);
        root.Controls.Add(_graph);
        _graph.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var history = _host.Session?.GetHistory(GraphSeconds) ?? [];
            BpmGraph.Draw(e.Graphics, new RectangleF(0, 0, _graph.Width - 4, _graph.Height), history, HostClock.Now, GraphSeconds,
                new BpmGraph.Style(HeartRed, Color.FromArgb(45, HeartRed), Color.FromArgb(70, Grey), Grey, _graphFont));
        };

        _devices.Columns.Add("Device", 220);
        _devices.Columns.Add("Role", 70);
        _devices.Columns.Add("State", 120);
        _devices.Columns.Add("Peak", 120);
        var devicesBox = new GroupBox { Text = "Devices (untick to leave a device out)", Dock = DockStyle.Fill, Padding = new Padding(8), Margin = new Padding(0, 10, 0, 6), MinimumSize = new Size(0, 150) };
        devicesBox.Controls.Add(_devices);
        root.Controls.Add(devicesBox);

        var left = Column(
            Group("VRChat", _osc, Row(Label("Port", 20), _port), _chatbox),
            Group("Beats", _predicted, _haptics, Row(Label("Strength"), _hapticStrength), Row(Label("Hands"), _hapticHands)));
        var right = Column(
            Group("Wrist panel in VR",
                Row(Label("Wrist"), _hand),
                Row(Label("Size"), _panelSize),
                _movePanel,
                _panelReset),
            Group("General", _showWindow, _autoStart, _diagnostics),
            Group("Chest tracker (after restart)", _chest));
        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.Controls.Add(left, 0, 0);
        settings.Controls.Add(right, 1, 0);
        root.Controls.Add(settings);
        root.Controls.Add(_footer);
        Controls.Add(root);

        _osc.CheckedChanged += (_, _) => Apply(() => _host.Settings.Osc.Enabled = _osc.Checked);
        _port.ValueChanged += (_, _) => Apply(() => _host.Settings.Osc.Port = (int)_port.Value);
        _chatbox.CheckedChanged += (_, _) => Apply(() => _host.Settings.Osc.Chatbox = _chatbox.Checked);
        _predicted.CheckedChanged += (_, _) => Apply(() => _host.Settings.Beats.SendPredicted = _predicted.Checked);
        _haptics.CheckedChanged += (_, _) => Apply(() => _host.Settings.Haptics.Enabled = _haptics.Checked);
        _hapticStrength.ValueChanged += (_, _) => Apply(() => _host.Settings.Haptics.Strength = _hapticStrength.Value / 100.0);
        _hapticHands.SelectedIndexChanged += (_, _) => Apply(() => _host.Settings.Haptics.Hands = (HapticHands)_hapticHands.SelectedIndex);
        _hand.SelectedIndexChanged += (_, _) => Apply(() => _host.Settings.Overlay.Hand = (OverlayHand)_hand.SelectedIndex);
        _panelSize.ValueChanged += (_, _) => Apply(() => _host.Settings.Overlay.WidthMeters = _panelSize.Value / 100f);
        _movePanel.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _host.SetMoveWristPanel(_movePanel.Checked);
            _seenVersion = _host.SettingsVersion;
        };
        _panelReset.Click += (_, _) => { Apply(() => _host.Settings.Overlay.ResetPosition()); LoadSettings(); };
        _showWindow.CheckedChanged += (_, _) => Apply(() => _host.Settings.General.ShowDesktopWindow = _showWindow.Checked);
        _devices.ItemChecked += (_, e) =>
        {
            if (_loading || e.Item.Tag is not LiveSession.DeviceEntry entry || entry.UsedForHeartRate == e.Item.Checked) return;
            _host.SetDeviceUsed(entry, e.Item.Checked);
            _seenVersion = _host.SettingsVersion;
        };
        _autoStart.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _autoStartRequestedAt = HostClock.Now;
            _host.RequestAutoStart(_autoStart.Checked);
        };
        _chest.ItemCheck += (_, _) =>
        {
            if (_loading) return;
            // CheckedItems updates after this event, so read it on the next message loop turn.
            BeginInvoke(() => Apply(() =>
            {
                _host.Settings.Devices.ChestTrackerSerials = _chest.CheckedItems.Cast<TrackerItem>()
                    .Where(t => t.Serial.Length > 0).Select(t => t.Serial).ToList();
            }));
        };
    }

    private readonly Icon _trayActive = IconArt.ToIcon(32, IconArt.Red);
    private readonly Icon _trayIdle = IconArt.ToIcon(32, IconArt.Grey);

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("app.ico");
        return stream != null ? new Icon(stream) : IconArt.ToIcon(32, IconArt.Red);
    }

    private void BuildTray()
    {
        Icon = LoadAppIcon();
        _tray.Icon = _trayIdle;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open window", null, (_, _) => ShowWindow());
        menu.Items.Add("Create diagnostics report", null, (_, _) => _host.RequestDiagnostics());
        menu.Items.Add("Quit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _diagnostics.Click += (_, _) => _host.RequestDiagnostics();
        _host.DiagnosticsSaved += path => BeginInvoke(() =>
        {
            _tray.ShowBalloonTip(8000, "Diagnostics report saved", $"{Path.GetFileName(path)} is on your Desktop. Send this file to the developer.", ToolTipIcon.Info);
            if (DiagnosticsReport.OutputOverride == null)
            {
                try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
                catch (System.ComponentModel.Win32Exception) { }
            }
        });
        _host.DiagnosticsFailed += message => BeginInvoke(() =>
            _tray.ShowBalloonTip(8000, "Diagnostics report failed", message, ToolTipIcon.Warning));
    }

    private void ShowWindow()
    {
        _allowShow = true;
        if (!_host.Settings.General.ShowDesktopWindow)
        {
            _host.Settings.General.ShowDesktopWindow = true;
            _host.SettingsChanged();
        }
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private static Label Label(string text, int indent = 0) =>
        new() { Text = text, AutoSize = false, Width = 64, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(indent, 3, 4, 0) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static GroupBox Group(string title, params Control[] controls)
    {
        var inner = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Dock = DockStyle.Fill };
        inner.Controls.AddRange(controls);
        var group = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(8), Margin = new Padding(0, 0, 8, 8), MinimumSize = new Size(285, 0) };
        group.Controls.Add(inner);
        return group;
    }

    private static Control Column(params Control[] groups)
    {
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        stack.Controls.AddRange(groups);
        return stack;
    }

    /// <summary>Copies the settings into the controls (at start and whenever the dashboard changed them).</summary>
    private void LoadSettings()
    {
        _loading = true;
        var s = _host.Settings;
        _osc.Checked = s.Osc.Enabled;
        _port.Value = Math.Clamp(s.Osc.Port, 1, 65535);
        _chatbox.Checked = s.Osc.Chatbox;
        _predicted.Checked = s.Beats.SendPredicted;
        _haptics.Checked = s.Haptics.Enabled;
        SetTrack(_hapticStrength, s.Haptics.Strength * 100);
        _hapticHands.SelectedIndex = (int)s.Haptics.Hands;
        _hand.SelectedIndex = (int)s.Overlay.Hand;
        SetTrack(_panelSize, s.Overlay.WidthMeters * 100);
        _movePanel.Checked = _host.MovingWristPanel;
        _showWindow.Checked = s.General.ShowDesktopWindow;
        _loading = false;
    }

    private static void SetTrack(TrackBar t, double value) => t.Value = (int)Math.Clamp(Math.Round(value), t.Minimum, t.Maximum);

    private void Apply(Action change)
    {
        if (_loading) return;
        change();
        _host.SettingsChanged();
        _seenVersion = _host.SettingsVersion; // our own change: no need to reload the controls
    }

    private void RefreshView()
    {
        if (_host.SettingsVersion != _seenVersion)
        {
            _seenVersion = _host.SettingsVersion;
            LoadSettings();
        }
        UpdateTray();
        bool wantVisible = _host.Settings.General.ShowDesktopWindow;
        if (!wantVisible && Visible) Hide();
        else if (wantVisible && !Visible) { _allowShow = true; Show(); }
        if (!Visible) return;

        var session = _host.Session;
        var snap = session?.LatestSnapshot;
        bool shown = snap?.Heart.Displayed == true;

        _bpm.Text = snap?.Heart.DisplayText ?? "--";
        _bpm.ForeColor = shown ? SystemColors.ControlText : Grey;
        double sinceBeat = session == null ? 99 : HostClock.Now - session.LastBeatHostTime;
        _heart.ForeColor = !shown ? Grey : Blend(HeartRed, HeartDim, Math.Clamp(sinceBeat / 0.35, 0, 1));

        string status = session == null ? _host.Status : OverlayRenderer.StatusText(session);
        _status.Text = char.ToUpperInvariant(status[0]) + status[1..];
        double measured = session == null || session.BeatCount == 0 ? 0 : 100.0 * session.MeasuredBeatCount / session.BeatCount;
        _beats.Text = $"Beats sent: {session?.BeatCount ?? 0} ({measured:0}% measured)   confidence {snap?.Heart.Confidence ?? 0:0.00}";
        _breathing.Text = snap?.Breathing is { } b
            ? b.Valid ? $"Breathing: {(double.IsNaN(b.RatePerMinute) ? "--" : b.RatePerMinute.ToString("0.0"))} /min{(b.IsHold ? $"   hold {b.HoldSeconds:0} s" : "")}" : "Breathing: no movement on the chest tracker"
            : "";

        // The graph gets a new point once per second; redraw only then.
        int points = session?.HistoryCount ?? 0;
        if (points != _graphPoints) { _graphPoints = points; _graph.Invalidate(); }

        RefreshDevices(session, snap);
        RefreshChestTrackers(session);

        _autoStart.Enabled = _host.Connected;
        _diagnostics.Enabled = _host.Diagnostics == null;
        _diagnostics.Text = _host.Diagnostics is { } d ? $"Recording... {d.Remaining:0} s" : "Create diagnostics report";
        if (HostClock.Now - _autoStartRequestedAt > 2 && _autoStart.Checked != _host.AutoStartEnabled)
        {
            _loading = true;
            _autoStart.Checked = _host.AutoStartEnabled;
            _loading = false;
        }

        var osc = _host.Osc;
        string oscText = osc == null ? "OSC off" : $"OSC {_host.Settings.Osc.Host}:{_host.Settings.Osc.Port}, {osc.MessagesSent} messages";
        string vr = _host.Connected ? $"SteamVR {_host.RuntimeVersion}" : _host.Status;
        string notes = string.Join("\n", new[] { _host.LastError, _host.Settings.Haptics.Enabled ? _host.HapticsError : null }.Where(n => n != null));
        _footer.Text = $"{vr}   ·   {oscText}   ·   SteamVR dashboard: \"Heart rate\" tab" + (notes.Length > 0 ? $"\n{notes}" : "");

        _host.DumpForDevelopment("window.png", 16, path =>
        {
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        });
    }

    private void RefreshDevices(LiveSession? session, MonitorSnapshot? snap)
    {
        var devices = session?.GetDevices() ?? [];
        _devices.BeginUpdate();
        while (_devices.Items.Count > devices.Count) _devices.Items.RemoveAt(_devices.Items.Count - 1);
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var r = snap?.Devices.FirstOrDefault(x => x.Id == d.Id);
            string state = !d.UsedForHeartRate ? "not used"
                : d.Status.Length > 0 ? d.Status
                : r == null ? "starting"
                : !r.Receiving ? "no data"
                : r.Resting ? "set down"
                : r.MovingNow ? "moving"
                : r.Prominence > 0 ? "measuring" : "settling";
            string peak = r is { Prominence: > 0 } ? $"{r.PeakBpm:0} BPM (x{r.Prominence:0})" : "";
            string[] cells = [$"{d.Info.Kind} {d.Info.Model}", RoleText(d.Role), state, peak];
            _loading = true;
            if (i < _devices.Items.Count)
            {
                var item = _devices.Items[i];
                item.Tag = d;
                for (int c = 0; c < cells.Length; c++)
                    if (item.SubItems[c].Text != cells[c]) item.SubItems[c].Text = cells[c];
                if (item.Checked != d.UsedForHeartRate) item.Checked = d.UsedForHeartRate;
            }
            else _devices.Items.Add(new ListViewItem(cells) { Tag = d, Checked = d.UsedForHeartRate });
            _loading = false;
        }
        _devices.EndUpdate();
    }

    private static string RoleText(DeviceRole role) => role switch
    {
        DeviceRole.Head => "Head (x2)",
        DeviceRole.Chest => "Chest (x3)",
        DeviceRole.Hand => "Hand",
        _ => "Other (x0.5)",
    };

    private sealed record TrackerItem(string Serial, string Label)
    {
        public override string ToString() => Label;
    }

    private void RefreshChestTrackers(LiveSession? session)
    {
        var trackers = (session?.GetDevices() ?? []).Where(d => d.Info.Class == ETrackedDeviceClass.GenericTracker).ToList();
        string key = string.Join(",", trackers.Select(t => t.Info.Serial));
        if (key == _chestKey) return;
        _chestKey = key;
        _loading = true;
        _chest.Items.Clear();
        foreach (var t in trackers)
        {
            bool chest = _host.Settings.Devices.ChestTrackerSerials.Contains(t.Info.Serial) || t.Role == DeviceRole.Chest;
            _chest.Items.Add(new TrackerItem(t.Info.Serial, $"{t.Info.Model} {t.Info.Serial}"), chest);
        }
        if (trackers.Count == 0) _chest.Items.Add(new TrackerItem("", "(no trackers connected)"), false);
        _loading = false;
    }

    private string _trayText = "";

    /// <summary>Tray icon: red while a heart rate is shown, grey on "--"; the tooltip has the number.</summary>
    private void UpdateTray()
    {
        var snap = _host.Session?.LatestSnapshot;
        bool shown = snap?.Heart.Displayed == true;
        var icon = shown ? _trayActive : _trayIdle;
        if (!ReferenceEquals(_tray.Icon, icon)) _tray.Icon = icon;
        string status = _host.Diagnostics?.Progress ?? (_host.Session == null ? _host.Status : OverlayRenderer.StatusText(_host.Session));
        string text = $"VirtualHeartBeatMonitor: {(shown ? snap!.Heart.DisplayText + " BPM" : "--")}\n{status}";
        if (text.Length > 127) text = text[..127];
        if (text != _trayText) { _tray.Text = text; _trayText = text; }
    }

    /// <summary>Flicker-free panel for the BPM graph.</summary>
    private sealed class GraphPanel : Panel
    {
        public GraphPanel()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
        }
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
}
