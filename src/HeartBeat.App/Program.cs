using System.Globalization;
using System.Runtime.InteropServices;
using HeartBeat.Tracking;

namespace HeartBeat.App;

internal static class Program
{
    private const string Usage = """
        HeartBeatMonitor: heart rate (and breathing with a chest tracker) from SteamVR devices,
        sent to VRChat over OSC, with a desktop window and an in-VR wrist panel.

        Usage: HeartBeatMonitor [--console] [--settings path] [--no-osc] [--osc-port N] [--seconds N]

          --console    text view in a console instead of the window
          --settings   settings file (default: settings.json next to the exe; created on first run)
          --no-osc     do not send OSC
          --osc-port   send OSC to this port instead of the one in the settings
          --seconds    exit after N seconds (testing)
          --steamvr    set by SteamVR when it starts the app: exit together with SteamVR
          --diagnostics  record a diagnostics report (1 minute, sit still) once SteamVR is connected
        """;

    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();

    private static void LogCrash(object? error)
    {
        AppLog.Write($"Crash: {error}");
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject);
        AppLog.Start();
        Application.ThreadException += (_, e) => LogCrash(e.Exception);
        string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        bool console = false, noOsc = false, bySteamVr = false, diagnostics = false;
        int? oscPort = null;
        double? runSeconds = null;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--console": console = true; break;
                    case "--diagnostics": diagnostics = true; break;
                    case "--settings": settingsPath = args[++i]; break;
                    case "--no-osc": noOsc = true; break;
                    case "--osc-port": oscPort = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--seconds": runSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case SteamVrAutoStart.LaunchArgument: bySteamVr = true; break;
                    case "-h" or "--help": ShowConsole(); Console.WriteLine(Usage); return 0;
                    default: throw new ArgumentException($"Unknown option '{args[i]}'");
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            ShowConsole();
            Console.Error.WriteLine($"{e.Message}\n\n{Usage}");
            return 1;
        }

        AppSettings settings;
        try
        {
            settings = AppSettings.LoadOrCreate(settingsPath);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            string message = $"Could not read {settingsPath}:\n{e.Message}";
            if (console) { ShowConsole(); Console.Error.WriteLine(message); }
            else MessageBox.Show(message, "VirtualHeartBeatMonitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        if (oscPort is { } port) settings.Osc.Port = port;

        // One instance at a time (two would fight over the panels and send duplicate OSC).
        using var single = new Mutex(true, "VirtualHeartBeatMonitor.SingleInstance", out bool first);
        if (!first)
        {
            if (bySteamVr) return 0;
            const string busy = "VirtualHeartBeatMonitor is already running (see the tray icon or the SteamVR dashboard).";
            if (console) { ShowConsole(); Console.Error.WriteLine(busy); }
            else MessageBox.Show(busy, "VirtualHeartBeatMonitor", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        using var host = new MonitorHost(settings, settingsPath, bySteamVr, noOsc);
        host.Start();
        if (diagnostics) host.RequestDiagnostics(waitForSteamVr: true);
        if (console) host.DiagnosticsSaved += path => Console.WriteLine($"Diagnostics report saved: {path}");
        return console ? RunConsole(host, runSeconds) : RunWindow(host, runSeconds);
    }

    private static int RunWindow(MonitorHost host, double? runSeconds)
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(host, runSeconds));
        return 0;
    }

    private static int RunConsole(MonitorHost host, double? runSeconds)
    {
        ShowConsole();
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        host.SteamVrClosed += stop.Cancel;
        double started = HostClock.Now;
        var view = new ConsoleView();
        try
        {
            while (!stop.IsCancellationRequested && (runSeconds is not { } s || HostClock.Now - started < s))
            {
                view.Render(host);
                Thread.Sleep(200);
            }
        }
        finally
        {
            view.Restore();
        }
        return 0;
    }

    /// <summary>The exe is a window app; attach to the parent console (or open one) for text output.</summary>
    private static void ShowConsole()
    {
        if (!Console.IsOutputRedirected && !AttachConsole(-1)) AllocConsole();
    }
}
