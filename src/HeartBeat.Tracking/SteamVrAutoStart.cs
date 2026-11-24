using System.Text.Json;
using Valve.VR;

namespace HeartBeat.Tracking;

/// <summary>
/// Registers the app with SteamVR (an app manifest next to the exe) so it can start
/// automatically with SteamVR. Needs a connection made as an overlay app.
/// </summary>
public static class SteamVrAutoStart
{
    public const string AppKey = "virtualheartbeatmonitor.app";
    public const string LaunchArgument = "--steamvr";

    public static string ManifestPath => Path.Combine(AppContext.BaseDirectory, "heartbeatmonitor.vrmanifest");

    public static bool IsEnabled(VrRuntime vr) =>
        vr.Applications is { } apps && apps.IsApplicationInstalled(AppKey) && apps.GetApplicationAutoLaunch(AppKey);

    /// <summary>
    /// Registers the app with SteamVR and identifies this process as it, on every start. Otherwise
    /// SteamVR knows it only as "system.generated.heartbeatmonitor"; on the Steam Frame such an
    /// unregistered app's dashboard tab got no laser-pointer input. Returns an error message or null.
    /// </summary>
    public static string? Register(VrRuntime vr, string exePath)
    {
        var apps = vr.Applications;
        if (apps == null) return "SteamVR applications interface not available";
        WriteManifest(exePath);
        var err = apps.AddApplicationManifest(ManifestPath, false);
        if (err != EVRApplicationError.None) return $"Could not register with SteamVR: {err}";
        err = apps.IdentifyApplication((uint)Environment.ProcessId, AppKey);
        return err == EVRApplicationError.None ? null : $"Could not identify the app to SteamVR: {err}";
    }

    /// <summary>Turns auto-start on or off. Returns an error message, or null on success.</summary>
    public static string? Set(VrRuntime vr, bool enabled, string exePath)
    {
        var apps = vr.Applications;
        if (apps == null) return "SteamVR applications interface not available";

        if (!enabled)
        {
            if (apps.IsApplicationInstalled(AppKey)) apps.SetApplicationAutoLaunch(AppKey, false);
            return null;
        }

        if (Register(vr, exePath) is { } error) return error;
        var err = apps.SetApplicationAutoLaunch(AppKey, true);
        return err == EVRApplicationError.None ? null : $"Could not enable auto-start: {err}";
    }

    /// <summary>The manifest key SteamVR reads on this platform (the Steam Frame uses binary_path_linux_arm).</summary>
    private static string BinaryPathKey =>
        OperatingSystem.IsWindows() ? "binary_path_windows"
        : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture is System.Runtime.InteropServices.Architecture.Arm64 ? "binary_path_linux_arm"
        : "binary_path_linux";

    private static void WriteManifest(string exePath)
    {
        var manifest = new
        {
            source = "builtin",
            applications = new[]
            {
                new Dictionary<string, object>
                {
                    ["app_key"] = AppKey,
                    ["launch_type"] = "binary",
                    [BinaryPathKey] = exePath,
                    ["arguments"] = LaunchArgument,
                    ["is_dashboard_overlay"] = true,
                    ["strings"] = new Dictionary<string, object>
                    {
                        ["en_us"] = new { name = "VirtualHeartBeatMonitor", description = "Heart rate from your headset and controllers, sent to VRChat." },
                    },
                },
            },
        };
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}
