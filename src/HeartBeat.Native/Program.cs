using System.Globalization;
using HeartBeat.App;
using HeartBeat.Tracking;

namespace HeartBeat.Native;

/// <summary>
/// VirtualHeartBeatMonitor on the Steam Frame itself (standalone). No desktop window: heart rate
/// from the headset/controller poses, OSC to VRChat on the headset, controller vibration. Runs in
/// a terminal (live view) or in the background (one status line in app.log every few seconds).
/// </summary>
internal static class Program
{
    private const string Usage = """
        HeartBeatMonitor (Steam Frame): heart rate from the headset and controllers, sent to VRChat over OSC.

        Usage: ./HeartBeatMonitor [--settings path] [--no-osc] [--osc-port N] [--seconds N] [--diagnostics] [--quiet]

          --settings     settings file (default: settings.json next to the program; created on first run)
          --no-osc       do not send OSC
          --osc-port     send OSC to this port instead of the one in the settings
          --seconds      exit after N seconds (testing)
          --diagnostics  record a diagnostics report (1 minute, sit still) once SteamVR is connected
          --quiet        no live view in the terminal (status goes to app.log)
          --steamvr      set by SteamVR when it starts the app: exit together with SteamVR
        """;

    /// <summary>SteamVR's own client library first (matches the running SteamVR, as framecorder does), then one next to the program.</summary>
    private static readonly string[] OpenVrLibraries =
        ["/opt/steamvr/bin/linuxarm64/libopenvr_api.so", Path.Combine(AppContext.BaseDirectory, "libopenvr_api.so")];

    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Write($"Crash: {e.ExceptionObject}");
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(typeof(Valve.VR.OpenVR).Assembly, (name, _, _) =>
        {
            if (name != "openvr_api") return IntPtr.Zero;
            foreach (var path in OpenVrLibraries)
                if (File.Exists(path) && System.Runtime.InteropServices.NativeLibrary.TryLoad(path, out var handle)) return handle;
            return IntPtr.Zero;
        });
        AppLog.Start();
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        bool noOsc = false, bySteamVr = false, diagnostics = false, quiet = Console.IsOutputRedirected;
        int? oscPort = null;
        double? runSeconds = null;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--diagnostics": diagnostics = true; break;
                    case "--settings": settingsPath = args[++i]; break;
                    case "--no-osc": noOsc = true; break;
                    case "--osc-port": oscPort = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--seconds": runSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--quiet": quiet = true; break;
                    case SteamVrAutoStart.LaunchArgument: bySteamVr = true; quiet = true; break;
                    case "-h" or "--help": Console.WriteLine(Usage); return 0;
                    default: throw new ArgumentException($"Unknown option '{args[i]}'");
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
            return 1;
        }

        AppSettings settings;
        try { settings = AppSettings.LoadOrCreate(settingsPath); }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not read {settingsPath}: {e.Message}");
            return 1;
        }
        if (oscPort is { } port) settings.Osc.Port = port;

        // One instance at a time: an exclusive lock on a file next to the program.
        FileStream? instanceLock;
        try
        {
            instanceLock = new FileStream(Path.Combine(AppContext.BaseDirectory, ".running"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            if (!bySteamVr) Console.Error.WriteLine("VirtualHeartBeatMonitor is already running.");
            return 0;
        }

        using (instanceLock)
        using (var host = new MonitorHost(settings, settingsPath, bySteamVr, noOsc, FramePanels.Factory))
        {
            // Not disposed: ProcessExit (SIGTERM, e.g. SteamVR stopping the app) can fire after Main returns.
            var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
            host.SteamVrClosed += stop.Cancel;
            host.DiagnosticsSaved += path => AppLog.Write($"Diagnostics report saved: {path}");
            host.Start();
            if (diagnostics) host.RequestDiagnostics(waitForSteamVr: true);

            double started = HostClock.Now, nextLog = 0;
            var view = quiet ? null : new ConsoleView();
            try
            {
                while (!stop.IsCancellationRequested && (runSeconds is not { } s || HostClock.Now - started < s))
                {
                    view?.Render(host);
                    if (HostClock.Now >= nextLog)
                    {
                        nextLog = HostClock.Now + 10;
                        AppLog.Write(StatusLine(host));
                    }
                    stop.Token.WaitHandle.WaitOne(200);
                }
            }
            finally
            {
                view?.Restore();
            }
        }
        return 0;
    }

    private static string StatusLine(MonitorHost host)
    {
        if (host.Session is not { } session) return $"Status: {host.Status}";
        var heart = session.LatestSnapshot?.Heart;
        string devices = string.Join(", ", session.GetDevices().Select(d => $"{d.Info.Kind} {d.Source}{(d.UsedForHeartRate ? "" : " (off)")}"));
        return $"Status: {heart?.DisplayText ?? "--"} BPM, {session.StatusText()}; {devices}; OSC {(host.Osc != null ? $"{host.Settings.Osc.Host}:{host.Settings.Osc.Port}" : "off")}";
    }
}
