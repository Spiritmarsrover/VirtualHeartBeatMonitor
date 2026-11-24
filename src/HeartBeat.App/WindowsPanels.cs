using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.App;

/// <summary>
/// Windows panels: the wrist panel (with grab-to-move) and the SteamVR dashboard tab, drawn with
/// System.Drawing and uploaded as Direct3D 11 textures (raw upload as fallback).
/// </summary>
public sealed class WindowsPanels : IPanels
{
    private readonly MonitorHost _host;
    private readonly VrRuntime _vr;
    private readonly ControllerHaptics _haptics;
    private readonly D3D11Textures? _d3d;
    private readonly OverlayRenderer _renderer = new();
    private readonly DashboardUi _dashboardUi;
    private readonly WristGrab _grab = new();
    private readonly TrackedDevicePose_t[] _poses = new TrackedDevicePose_t[Valve.VR.OpenVR.k_unMaxTrackedDeviceCount];

    private WristOverlay? _wrist;
    private DashboardPanel? _dashboard;
    private PanelPresenter? _wristImage, _dashboardImage;
    private double _nextPanelAttempt;
    private double _nextWristFrame, _lastDashboardDraw = double.NegativeInfinity;
    private string _wristKey = "", _appliedWrist = "";
    private bool _wristWasVisible;

    public static PanelsFactory Factory => (host, vr, haptics) => new WindowsPanels(host, vr, haptics);

    private WindowsPanels(MonitorHost host, VrRuntime vr, ControllerHaptics haptics)
    {
        _host = host;
        _vr = vr;
        _haptics = haptics;
        _d3d = D3D11Textures.TryCreate(out string? d3dError);
        if (d3dError != null) AppLog.Write($"{d3dError}; panels use raw uploads");
        _dashboardUi = new DashboardUi(host);
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
    private PanelPresenter? _hudImage;
    private readonly HudRenderer _hudRenderer = new();
    private double _nextHudAttempt, _nextHudFrame;
    private string _hudKey = "";

    private void UpdateHud(LiveSession session)
    {
        bool on = _host.Settings.Hud.Enabled;
        if (on && _hud == null && HostClock.Now >= _nextHudAttempt)
        {
            _nextHudAttempt = HostClock.Now + 5;
            if ((_hud = TryCreate(() => new HudOverlay(_vr.Overlay!, "virtualheartbeatmonitor.hud", "Heart rate HUD"))) != null)
                _hudImage = new PanelPresenter("HUD", _d3d, HudRenderer.Size, HudRenderer.Size);
        }
        if (_hud is not { } hud || _hudImage is not { } image) return;
        if (!on) { hud.Show(false); _hudKey = ""; return; }

        var view = HudStatus.For(session);
        bool animating = view.Pulse > 1.01f;
        if (view.Key != _hudKey || (animating && HostClock.Now >= _nextHudFrame) || HostClock.Now >= _nextHudFrame + 1)
        {
            _nextHudFrame = HostClock.Now + 1.0 / 30;
            _hudKey = view.Key;
            var bmp = _hudRenderer.Draw(view);
            image.Present(bmp, hud.SetTexture, rgba => hud.SetImage(rgba, HudRenderer.Size, HudRenderer.Size));
            if (hud.LastError != null) _host.ReportError($"HUD: {hud.LastError}");
        }
        hud.Show(true);
        hud.UpdatePlacement(HostClock.Now);
        if (MonitorHost.DumpDir != null) _host.DumpForDevelopment("hud.png", 15, _hudRenderer.SavePng);
    }

    /// <summary>Creates the panels; retries if SteamVR still holds the names from a previous run.</summary>
    private void CreatePanels()
    {
        if ((_wrist != null && _dashboard != null) || HostClock.Now < _nextPanelAttempt) return;
        _nextPanelAttempt = HostClock.Now + 5;
        if (_wrist == null && (_wrist = TryCreate(() => new WristOverlay(_vr.Overlay!, _vr.System, "virtualheartbeatmonitor.wrist", "Heart rate"))) != null)
            _wristImage = new PanelPresenter("Wrist", _d3d, OverlayRenderer.Width, OverlayRenderer.Height);
        if (_dashboard == null && (_dashboard = TryCreate(() => new DashboardPanel(_vr.Overlay!, "virtualheartbeatmonitor.dashboard", "Heart rate", DashboardUi.Width, DashboardUi.Height))) != null)
        {
            _dashboardImage = new PanelPresenter("Dashboard", _d3d, DashboardUi.Width, DashboardUi.Height);
            string thumb = Path.Combine(Path.GetTempPath(), "virtualheartbeatmonitor_icon.png");
            DashboardUi.WriteThumbnail(thumb);
            _dashboard.SetThumbnail(thumb);
        }
        if (_wrist != null && _dashboard != null) { _host.ReportError(null); AppLog.Write("Panels created"); }
    }

    /// <summary>Wrist panel: redraw when its content changes, fast only during the beat pulse.</summary>
    private void UpdateWrist(LiveSession session)
    {
        if (_wrist is not { } wrist || _wristImage is not { } image) return;
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

        // Moving the panel: with the option on, grab it with the other hand (grip or trigger, near
        // the panel), carry it, let go. The new placement is saved.
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
            _wristKey = ""; // force a redraw when it comes back
        }
        if (wrist.Visible)
        {
            string key = OverlayRenderer.ContentKey(session) + (moving ? "|moving" + _grab.Holding : "");
            bool animating = HostClock.Now - session.LastBeatHostTime < 0.45;
            bool due = key != _wristKey || (animating && HostClock.Now >= _nextWristFrame) || HostClock.Now >= _nextWristFrame + 1;
            if (due)
            {
                _nextWristFrame = HostClock.Now + 1.0 / 30;
                _wristKey = key;
                var bmp = _renderer.Draw(session, moving ? (_grab.Holding ? "Let go to place it here" : "Grab with your other hand") : null);
                image.Present(bmp, wrist.SetTexture, rgba => wrist.SetImage(rgba, OverlayRenderer.Width, OverlayRenderer.Height));
                if (wrist.LastError != null) _host.ReportError($"Wrist panel: {wrist.LastError}");
            }
        }
        if (MonitorHost.DumpDir != null)
        {
            _renderer.Draw(session);
            _host.DumpForDevelopment("overlay.png", 15, _renderer.SavePng);
        }
    }

    /// <summary>SteamVR dashboard tab: input every pass, redraw while open (or right after input).</summary>
    private void UpdateDashboard(LiveSession session)
    {
        if (_dashboard is not { } dashboard || _dashboardImage is not { } image) return;
        bool changed = _dashboardUi.Handle(dashboard.PollMouse());
        // 4 fps normally, 30 fps while the heart pulses so the animation follows each beat.
        bool pulsing = HostClock.Now - session.LastBeatHostTime < 0.45;
        double sinceDraw = HostClock.Now - _lastDashboardDraw;
        if (dashboard.Visible && (changed || sinceDraw >= 0.25 || (pulsing && sinceDraw >= 1.0 / 30)))
        {
            _lastDashboardDraw = HostClock.Now;
            var bmp = _dashboardUi.Draw(session);
            image.Present(bmp, dashboard.SetTexture, rgba => dashboard.SetImage(rgba, _dashboardUi.SavePng));
        }
        if (MonitorHost.DumpDir != null)
        {
            _dashboardUi.Draw(session);
            _host.DumpForDevelopment("dashboard.png", 15, _dashboardUi.SavePng);
        }
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
        _wristImage?.Dispose();
        _dashboardImage?.Dispose();
        _wrist?.Dispose();
        _dashboard?.Dispose();
        _dashboardUi.Dispose();
        _renderer.Dispose();
        _hudImage?.Dispose();
        _hud?.Dispose();
        _hudRenderer.Dispose();
        _d3d?.Dispose();
    }
}
