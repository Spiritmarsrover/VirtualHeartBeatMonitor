using HeartBeat.App;
using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.Native;

/// <summary>
/// Steam Frame panels: the wrist panel (with grab-to-move) and the SteamVR dashboard tab, drawn by
/// the software <see cref="Canvas"/> and uploaded with SetOverlayRaw. Same behaviour as the
/// Windows panels (WindowsPanels), lower dashboard frame rate (raw uploads cost more).
/// </summary>
internal sealed class FramePanels : IPanels
{
    private readonly MonitorHost _host;
    private readonly VrRuntime _vr;
    private readonly ControllerHaptics _haptics;
    private readonly WristDrawer _wristDrawer = new();
    private readonly DashboardDrawer _dashboardDrawer;
    private readonly WristGrab _grab = new();
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];

    private WristOverlay? _wrist;
    private DashboardPanel? _dashboard;
    private double _nextPanelAttempt;
    private double _nextWristFrame, _lastDashboardDraw = double.NegativeInfinity;
    private string _wristKey = "", _appliedWrist = "";
    private bool _wristWasVisible;
    private VulkanTextures? _vulkan;
    private VulkanTextures.Target? _wristTexture, _dashboardTexture;

    public static PanelsFactory Factory => (host, vr, haptics) => new FramePanels(host, vr, haptics);

    private FramePanels(MonitorHost host, VrRuntime vr, ControllerHaptics haptics)
    {
        _host = host;
        _vr = vr;
        _haptics = haptics;
        _dashboardDrawer = new DashboardDrawer(host);
        _vulkan = VulkanTextures.TryCreate(vr.System, out string? vkError);
        AppLog.Write(_vulkan != null ? "Panels: Vulkan textures" : $"Panels: raw uploads ({vkError})");
    }

    /// <summary>Vulkan texture if available (no flicker), else SetOverlayRaw; falls back for good on a Vulkan error.</summary>
    private void Present(Canvas c, ref VulkanTextures.Target? target, Func<Texture_t, EVROverlayError> setTexture, Action raw)
    {
        if (_vulkan != null)
        {
            try
            {
                target ??= _vulkan.CreateTarget(c.Width, c.Height);
                target.Upload(c.Pixels, setTexture);
                return;
            }
            catch (InvalidOperationException e)
            {
                AppLog.Write($"Vulkan upload failed, using raw uploads: {e.Message}");
                _wristTexture?.Dispose();
                _dashboardTexture?.Dispose();
                _hudTexture?.Dispose();
                _wristTexture = _dashboardTexture = _hudTexture = target = null;
                _vulkan.Dispose();
                _vulkan = null;
            }
        }
        raw();
    }

    public void Update(LiveSession session)
    {
        CreatePanels();
        UpdateWrist(session);
        UpdateDashboard(session);
        UpdateHud(session);
    }

    // ---- HUD: a heart with the BPM fixed in the view (optional) ---------------------------------

    private HudOverlay? _hud;
    private VulkanTextures.Target? _hudTexture;
    private readonly HudDrawer _hudDrawer = new();
    private double _nextHudAttempt, _nextHudFrame;
    private string _hudKey = "";

    private void UpdateHud(LiveSession session)
    {
        bool on = _host.Settings.Hud.Enabled;
        if (on && _hud == null && HostClock.Now >= _nextHudAttempt)
        {
            _nextHudAttempt = HostClock.Now + 5;
            _hud = TryCreate(() => new HudOverlay(_vr.Overlay!, "virtualheartbeatmonitor.hud", "Heart rate HUD"));
        }
        if (_hud is not { } hud) return;
        if (!on) { hud.Show(false); _hudKey = ""; return; }

        var view = HudStatus.For(session);
        bool animating = view.Pulse > 1.01f;
        if (view.Key != _hudKey || (animating && HostClock.Now >= _nextHudFrame) || HostClock.Now >= _nextHudFrame + 1)
        {
            _nextHudFrame = HostClock.Now + 1.0 / 30;
            _hudKey = view.Key;
            var c = _hudDrawer.Draw(view);
            Present(c, ref _hudTexture, hud.SetTexture, () => hud.SetImage(c.Pixels, c.Width, c.Height));
            if (hud.LastError != null) _host.ReportError($"HUD: {hud.LastError}");
        }
        hud.Show(true);
        hud.UpdatePlacement(HostClock.Now);
        if (MonitorHost.DumpDir != null) _host.DumpForDevelopment("hud.png", 15, path => _hudDrawer.Draw(view).SavePng(path));
    }

    private void CreatePanels()
    {
        if ((_wrist != null && _dashboard != null) || HostClock.Now < _nextPanelAttempt) return;
        _nextPanelAttempt = HostClock.Now + 5;
        _wrist ??= TryCreate(() => new WristOverlay(_vr.Overlay!, _vr.System, "virtualheartbeatmonitor.wrist", "Heart rate"));
        if (_dashboard == null && (_dashboard = TryCreate(() => new DashboardPanel(_vr.Overlay!, "virtualheartbeatmonitor.dashboard", "Heart rate", DashboardDrawer.Width, DashboardDrawer.Height))) != null)
        {
            string thumb = Path.Combine(Path.GetTempPath(), "virtualheartbeatmonitor_icon.png");
            File.WriteAllBytes(thumb, DashboardDrawer.Thumbnail());
            _dashboard.SetThumbnail(thumb);
            AppLog.Write($"Dashboard input: {_dashboard.InputInfo}");
            // The first clicks of each session go to app.log, so input problems can be diagnosed.
            int traced = 0;
            _dashboard.Trace = line => { if (line.Contains("MouseButtonDown") && traced++ < 10) AppLog.Write($"Dashboard click: {line}"); };
        }
        if (_wrist != null && _dashboard != null) { _host.ReportError(null); AppLog.Write("Panels created"); }
    }

    private void UpdateWrist(LiveSession session)
    {
        if (_wrist is not { } wrist) return;
        var o = _host.Settings.Overlay;
        string wanted = $"{o.Hand}|{o.WidthMeters}|{string.Join(",", o.Placement ?? [])}";
        if (wanted != _appliedWrist && !_grab.Holding)
        {
            wrist.WidthMeters = o.WidthMeters;
            wrist.Placement = o.PlacementMatrix();
            wrist.Invalidate();
            _appliedWrist = wanted;
        }
        wrist.Update(o.Hand);

        bool moving = _host.MovingWristPanel && wrist.Visible;
        _haptics.GrabActive = moving;
        if (moving || _grab.Holding)
        {
            _vr.System.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding, 0f, _poses);
            if (_grab.Update(wrist, _vr.System, _poses, _haptics, moving) == WristGrab.Result.Released)
            {
                o.Placement = Rigid.ToArray(wrist.Placement).ToList();
                _appliedWrist = $"{o.Hand}|{o.WidthMeters}|{string.Join(",", o.Placement)}";
                _host.SettingsChanged();
                AppLog.Write("Wrist panel moved");
            }
        }
        if (wrist.Visible != _wristWasVisible)
        {
            AppLog.Write($"Wrist panel {(wrist.Visible ? "shown" : "hidden")}");
            _wristWasVisible = wrist.Visible;
            _wristKey = "";
        }
        if (wrist.Visible)
        {
            string key = WristDrawer.ContentKey(session) + (moving ? "|moving" + _grab.Holding : "");
            bool animating = HostClock.Now - session.LastBeatHostTime < 0.45;
            bool due = key != _wristKey || (animating && HostClock.Now >= _nextWristFrame) || HostClock.Now >= _nextWristFrame + 1;
            if (due)
            {
                _nextWristFrame = HostClock.Now + 1.0 / 30;
                _wristKey = key;
                var c = _wristDrawer.Draw(session, moving ? (_grab.Holding ? "Let go to place it here" : "Grab with your other hand") : null);
                Present(c, ref _wristTexture, wrist.SetTexture, () => wrist.SetImage(c.Pixels, c.Width, c.Height));
                if (wrist.LastError != null) _host.ReportError($"Wrist panel: {wrist.LastError}");
            }
        }
        if (MonitorHost.DumpDir != null)
            _host.DumpForDevelopment("overlay.png", 15, path => _wristDrawer.Draw(session).SavePng(path));
    }

    private void UpdateDashboard(LiveSession session)
    {
        if (_dashboard is not { } dashboard) return;
        bool changed = _dashboardDrawer.Model.Handle(dashboard.PollMouse());
        // 4 fps normally, 15 fps while the heart pulses (a 960x640 upload is ~2.5 MB).
        bool pulsing = HostClock.Now - session.LastBeatHostTime < 0.45;
        double sinceDraw = HostClock.Now - _lastDashboardDraw;
        if (dashboard.Visible && (changed || sinceDraw >= 0.25 || (pulsing && sinceDraw >= 1.0 / 15)))
        {
            _lastDashboardDraw = HostClock.Now;
            var c = _dashboardDrawer.Draw(session);
            Present(c, ref _dashboardTexture, dashboard.SetTexture, () => dashboard.SetImage(c.Pixels, c.SavePng));
        }
        if (MonitorHost.DumpDir != null)
            _host.DumpForDevelopment("dashboard.png", 15, path => _dashboardDrawer.Draw(session).SavePng(path));
        if (dashboard.LastError != null) _host.ReportError($"Dashboard: {dashboard.LastError}");
    }

    private T? TryCreate<T>(Func<T> create) where T : class
    {
        try { return create(); }
        catch (Exception e) when (e is InvalidOperationException or NullReferenceException)
        {
            _host.ReportError($"{typeof(T).Name} unavailable: {e.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _wrist?.Dispose();
        _wristTexture?.Dispose();
        _dashboardTexture?.Dispose();
        _hudTexture?.Dispose();
        _hud?.Dispose();
        _vulkan?.Dispose();
        _dashboard?.Dispose();
    }
}
