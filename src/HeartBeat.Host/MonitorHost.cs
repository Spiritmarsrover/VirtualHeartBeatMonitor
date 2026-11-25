using System.Collections.Concurrent;
using HeartBeat.Core.Heart;
using HeartBeat.Osc;
using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.App;

/// <summary>
/// Owns the SteamVR connection on a background thread: waits for SteamVR, runs a
/// <see cref="LiveSession"/>, drives OSC, controller vibration, the wrist panel and the SteamVR
/// dashboard tab, and reconnects when SteamVR restarts. When SteamVR itself launched the app,
/// the app follows SteamVR and exits with it. The window only reads state and posts requests.
/// </summary>
public sealed class MonitorHost : IDisposable
{
    private readonly string _settingsPath;
    private readonly bool _noOsc;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ConcurrentQueue<BeatEvent> _beats = new();
    private bool _oscDirty = true;
    private bool? _autoStartRequest;
    private UdpOscSink? _sink;

    public AppSettings Settings { get; }
    public bool LaunchedBySteamVr { get; }

    public volatile LiveSession? Session;
    public volatile VRChatOutput? Osc;
    public string Status { get; private set; } = "Starting";
    public string RuntimeVersion { get; private set; } = "";
    public bool AutoStartEnabled { get; private set; }
    public bool Connected { get; private set; }
    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (value != null && value != _lastError) AppLog.Write(value);
            _lastError = value;
        }
    }
    private string? _lastError;
    public string? HapticsError { get; private set; }
    /// <summary>Increments on every settings change (from the window or the dashboard).</summary>
    public int SettingsVersion => Volatile.Read(ref _settingsVersion);
    private int _settingsVersion;

    /// <summary>Raised (on the host thread) when SteamVR closes and SteamVR had launched the app.</summary>
    public event Action? SteamVrClosed;

    private readonly PanelsFactory? _panels;

    /// <param name="panels">Creates the platform's in-VR panels for each SteamVR session (null: none).</param>
    public MonitorHost(AppSettings settings, string settingsPath, bool launchedBySteamVr, bool noOsc, PanelsFactory? panels = null)
    {
        Settings = settings;
        _settingsPath = settingsPath;
        LaunchedBySteamVr = launchedBySteamVr;
        _noOsc = noOsc;
        _panels = panels;
        _thread = new Thread(Run) { IsBackground = true, Name = "MonitorHost" };
    }

    /// <summary>Shows a problem in the window/dashboard and writes it to the log.</summary>
    public void ReportError(string? message) => LastError = message;

    public void Start() => _thread.Start();

    /// <summary>Saves the settings and applies those that can change while running.</summary>
    public void SettingsChanged()
    {
        lock (_gate)
        {
            _oscDirty = true;
            Interlocked.Increment(ref _settingsVersion);
            try { Settings.Save(_settingsPath); }
            catch (IOException e) { LastError = $"Could not save settings: {e.Message}"; }
        }
    }

    /// <summary>The diagnostics recording in progress, if any.</summary>
    public DiagnosticsCapture? Diagnostics => _diagnostics;
    private volatile DiagnosticsCapture? _diagnostics;
    private volatile bool _diagnosticsRequested;
    private double _diagnosticsNoticeUntil;

    /// <summary>Raised with the zip path when a diagnostics report is saved (any thread).</summary>
    public event Action<string>? DiagnosticsSaved;
    /// <summary>Raised with a message when a diagnostics report could not be written (any thread).</summary>
    public event Action<string>? DiagnosticsFailed;

    /// <summary>
    /// Starts a diagnostics report: one minute of device data plus logs, zipped to the Desktop.
    /// Without SteamVR it writes logs and system info right away.
    /// </summary>
    /// <param name="waitForSteamVr">Wait until SteamVR is connected and record device data (used by --diagnostics).</param>
    public void RequestDiagnostics(bool waitForSteamVr = false)
    {
        if (_diagnostics != null) return;
        if (Session == null && !waitForSteamVr)
        {
            Task.Run(() => FinishDiagnostics(() => DiagnosticsReport.Write(this, null, null, 0)));
            return;
        }
        _diagnosticsRequested = true;
        _wake.Set();
    }

    private void FinishDiagnostics(Func<string> write)
    {
        try
        {
            string path = write();
            DiagnosticsSaved?.Invoke(path);
        }
        catch (Exception e)
        {
            AppLog.Write($"Diagnostics report failed: {e}");
            DiagnosticsFailed?.Invoke(e.Message);
        }
    }

    private readonly HeartRateLog _log = new();
    private bool _logErrorShown;

    /// <summary>Raised with the CSV path when the heart rate log was exported (any thread).</summary>
    public event Action<string>? LogExported;
    /// <summary>Raised with a message when the export failed (any thread).</summary>
    public event Action<string>? LogExportFailed;

    /// <summary>Joins the heart rate log into one CSV on the Desktop (in the background).</summary>
    public void ExportLog() => Task.Run(() =>
    {
        try
        {
            string path = HeartRateLog.Export(DiagnosticsReport.OutputFolder());
            ShowNotice("heart rate log saved to the Desktop");
            LogExported?.Invoke(path);
        }
        catch (Exception e)
        {
            AppLog.Write($"Heart rate log export failed: {e}");
            ShowNotice("heart rate log export failed");
            LogExportFailed?.Invoke(e.Message);
        }
    });

    /// <summary>A short message in the status line for 10 seconds (not during a diagnostics recording).</summary>
    private void ShowNotice(string text)
    {
        if (Session is not { } session || _diagnostics != null) return;
        _diagnosticsNoticeUntil = HostClock.Now + 10;
        session.Notice = text;
    }

    private void LogSnapshot(LiveSession session)
    {
        if (!Settings.HeartRateLog.Enabled) { _log.Close(); return; }
        try
        {
            _log.Write(DateTimeOffset.Now, HudStatus.For(session), session.LatestSnapshot?.Heart.Confidence ?? 0);
            _logErrorShown = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Close();
            if (_logErrorShown) return;
            _logErrorShown = true;
            LastError = $"Heart rate log: {e.Message}";
        }
    }

    /// <summary>
    /// "Move wrist panel" mode: while on, the panel can be grabbed with the other hand and placed.
    /// Not saved; turn it off when the panel is where you want it.
    /// </summary>
    public bool MovingWristPanel => _movingWristPanel;
    private volatile bool _movingWristPanel;

    public void SetMoveWristPanel(bool on)
    {
        _movingWristPanel = on;
        Interlocked.Increment(ref _settingsVersion); // lets the window and dashboard update their toggle
        AppLog.Write(on ? "Wrist panel: move mode on" : "Wrist panel: move mode off");
    }

    /// <summary>Switches a device in or out of the measurement and remembers the choice.</summary>
    public void SetDeviceUsed(LiveSession.DeviceEntry device, bool used)
    {
        Session?.SetDeviceUsed(device.Id, used);
        lock (_gate) Settings.Devices.UseDevice[device.Info.Serial] = used;
        AppLog.Write($"{device.Info.Kind} {device.Info.Serial} {(used ? "included" : "excluded")}");
        SettingsChanged();
    }

    public void RequestAutoStart(bool enabled)
    {
        lock (_gate) _autoStartRequest = enabled;
    }

    private void Run()
    {
        using var timer = new TimerResolutionScope();
        while (!_stop.IsCancellationRequested)
        {
            Status = "Waiting for SteamVR";
            VrRuntime vr;
            try
            {
                vr = VrRuntime.ConnectOverlayIfRunning();
            }
            catch (VrInitException e) when (e.Error == EVRInitError.Init_NoServerForBackgroundApp)
            {
                Wait(2);
                continue;
            }
            catch (Exception e) when (e is VrInitException or DllNotFoundException)
            {
                LastError = e.Message;
                Wait(5);
                continue;
            }

            bool steamVrQuit;
            try
            {
                steamVrQuit = RunSession(vr);
            }
            catch (Exception e)
            {
                // Never let one failure kill the app; reconnect instead.
                AppLog.Write($"Session failed: {e}");
                LastError = $"Restarting after an error: {e.Message}";
                steamVrQuit = false;
            }
            if (steamVrQuit && LaunchedBySteamVr)
            {
                Status = "SteamVR closed";
                SteamVrClosed?.Invoke();
                return;
            }
            Wait(3);
        }
    }

    /// <summary>Runs until SteamVR quits (returns true) or the host is stopped (returns false).</summary>
    private bool RunSession(VrRuntime vr)
    {
        using (vr)
        {
            RuntimeVersion = vr.RuntimeVersion;
            AppLog.Write($"Connected to SteamVR {RuntimeVersion}");
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HeartBeatMonitor.exe");
            if (SteamVrAutoStart.Register(vr, exe) is { } registerError) AppLog.Write(registerError);
            AutoStartEnabled = SteamVrAutoStart.IsEnabled(vr);
            using var session = new LiveSession(vr, Settings);
            session.SnapshotReady += snap =>
            {
                Osc?.OnSnapshot(snap, HostClock.Now);
                LogSnapshot(session);
            };
            session.Beat += b =>
            {
                Osc?.OnBeat(b);
                _beats.Enqueue(b);
                _wake.Set(); // vibrate right away, not on the next loop pass
            };
            session.Breathing += b => Osc?.OnBreathing(b, HostClock.Now);
            Session = session;
            Connected = true;
            Status = "Running";

            var haptics = new ControllerHaptics(vr, AppContext.BaseDirectory);
            HapticsError = haptics.Error;
            if (haptics.Error != null) AppLog.Write($"Vibration: {haptics.Error}");
            using var panels = _panels?.Invoke(this, vr, haptics);

            bool quit = false;
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        if (!session.Poll()) { quit = true; AppLog.Write("SteamVR is closing"); break; }
                        ApplyRequests(vr);
                        TickDiagnostics(session, stopNow: false);

                        // Heartbeat vibration.
                        haptics.Update();
                        while (_beats.TryDequeue(out _))
                        {
                            var h = Settings.Haptics;
                            if (h.Enabled) haptics.Pulse(h.Strength, h.Hands, h.UseLegacyApi);
                        }
                        if (haptics.Error != HapticsError)
                        {
                            HapticsError = haptics.Error;
                            if (HapticsError != null) AppLog.Write($"Vibration: {HapticsError}");
                        }

                        panels?.Update(session);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        LastError = $"{e.GetType().Name}: {e.Message}";
                        AppLog.Write($"Error in update loop: {e}");
                        Thread.Sleep(500);
                    }
                    _wake.WaitOne(MovingWristPanel ? 11 : 30); // follow the hand smoothly while moving the panel
                }
            }
            finally
            {
                AppLog.Write("Session ending");
                if (_diagnostics != null)
                {
                    // SteamVR is closing (or the app is): save what was recorded so far.
                    var started = HostClock.Now;
                    while (_diagnostics != null && HostClock.Now - started < 10) { TickDiagnostics(session, stopNow: true); Thread.Sleep(50); }
                }
                Osc?.Disconnect();
                _log.Close();
                Session = null;
                Connected = false;
            }
            return quit;
        }
    }

    private void TickDiagnostics(LiveSession session, bool stopNow)
    {
        if (_diagnosticsRequested && _diagnostics == null)
        {
            _diagnosticsRequested = false;
            _diagnostics = new DiagnosticsCapture(this, session);
        }
        if (_diagnostics is { } capture)
        {
            session.Notice = capture.Progress;
            try
            {
                if (capture.Tick(stopNow) is { } path)
                {
                    _diagnostics = null;
                    session.Notice = "diagnostics report saved to the Desktop";
                    _diagnosticsNoticeUntil = HostClock.Now + 10;
                    FinishDiagnostics(() => path);
                }
            }
            catch (Exception e)
            {
                _diagnostics = null;
                session.Notice = null;
                LastError = $"Diagnostics report failed: {e.Message}";
                DiagnosticsFailed?.Invoke(e.Message);
            }
        }
        else if (session.Notice != null && HostClock.Now > _diagnosticsNoticeUntil) session.Notice = null;
    }

    private void ApplyRequests(VrRuntime vr)
    {
        bool rebuildOsc;
        bool? autoStart;
        lock (_gate)
        {
            rebuildOsc = _oscDirty;
            _oscDirty = false;
            autoStart = _autoStartRequest;
            _autoStartRequest = null;
        }

        if (rebuildOsc)
        {
            var o = Settings.Osc;
            bool wanted = o.Enabled && !_noOsc;
            bool same = Osc != null && wanted && _oscTarget == (o.Host, o.Port);
            if (!same)
            {
                var old = Osc;
                Osc = null;
                old?.Disconnect();
                old?.Dispose();
                _sink?.Dispose();
                _sink = null;
                _oscTarget = default;
                if (wanted)
                {
                    try
                    {
                        _sink = new UdpOscSink(o.Host, o.Port);
                        Osc = new VRChatOutput(_sink, o);
                        _oscTarget = (o.Host, o.Port);
                    }
                    catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException)
                    {
                        LastError = $"OSC: {e.Message}";
                    }
                }
            }
        }

        if (autoStart is { } enable)
        {
            string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HeartBeatMonitor.exe");
            LastError = SteamVrAutoStart.Set(vr, enable, exe);
            AutoStartEnabled = SteamVrAutoStart.IsEnabled(vr);
            Interlocked.Increment(ref _settingsVersion);
        }
    }

    private (string Host, int Port) _oscTarget;

    // HEARTBEAT_DUMP_DIR=<folder> saves pictures of the panels once (development aid).
    private readonly HashSet<string> _dumped = new();
    public static string? DumpDir => Environment.GetEnvironmentVariable("HEARTBEAT_DUMP_DIR");

    public void DumpForDevelopment(string file, double afterSeconds, Action<string> save)
    {
        if (DumpDir is not { } dir || _dumped.Contains(file) || Session is not { } s || HostClock.Now - s.StartedAt < afterSeconds) return;
        _dumped.Add(file);
        Directory.CreateDirectory(dir);
        save(Path.Combine(dir, file));
    }

    private void Wait(double seconds) => _stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(seconds));

    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return; // window close and program exit both call this
        _stop.Cancel();
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(5));
        Osc?.Dispose();
        _sink?.Dispose();
        _wake.Dispose();
    }
}
