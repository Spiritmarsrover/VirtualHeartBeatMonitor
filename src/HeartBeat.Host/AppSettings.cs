using System.Text.Json;
using System.Text.Json.Serialization;
using HeartBeat.Osc;
using HeartBeat.Tracking;

namespace HeartBeat.App;

public sealed class OscSettings : VRChatOutputSettings
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9000;
}

public sealed class DeviceSettings
{
    /// <summary>Serials of trackers worn on the chest (heart rate + breathing). Trackers whose SteamVR role is "chest" are included automatically.</summary>
    public List<string> ChestTrackerSerials { get; set; } = new();
    /// <summary>
    /// Per-device choice (by serial) to use it for the heart rate and beats. Devices not listed use
    /// the default: headset, controllers and chest trackers on, other trackers off.
    /// </summary>
    public Dictionary<string, bool> UseDevice { get; set; } = new();
}

public sealed class DetectionSettings
{
    /// <summary>Confidence (0..1) needed to show a number; below it the output is "--" and no beats are sent.</summary>
    public double DisplayThreshold { get; set; } = 0.5;
    /// <summary>Rotation (deg/s) above which a device counts as moving.</summary>
    public double MotionThresholdDps { get; set; } = 8;
}

public sealed class OverlaySettings
{
    public static readonly OverlaySettings Defaults = new();

    /// <summary>Which wrist shows the in-VR panel: Off, Left or Right.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OverlayHand Hand { get; set; } = OverlayHand.Left;
    public float WidthMeters { get; set; } = 0.09f;

    /// <summary>
    /// Where the panel sits relative to the left controller: a 3×4 transform (row-major, metres),
    /// set by grabbing the panel with the other hand. Mirrored on the right hand. Null = default.
    /// </summary>
    public List<float>? Placement { get; set; }

    public static readonly Valve.VR.HmdMatrix34_t DefaultPlacement = Rigid.FromTiltAndOffset(-45f, 0f, 0.02f, 0.12f);

    public Valve.VR.HmdMatrix34_t PlacementMatrix() =>
        Placement is { Count: 12 } p ? Rigid.FromArray(p) : DefaultPlacement;

    public void ResetPosition()
    {
        WidthMeters = Defaults.WidthMeters;
        Placement = null;
    }
}

public sealed class HapticsSettings
{
    /// <summary>Vibrate the controllers gently on every heartbeat.</summary>
    public bool Enabled { get; set; } = false;
    /// <summary>Vibration strength, 0..1.</summary>
    public double Strength { get; set; } = 0.3;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public HapticHands Hands { get; set; } = HapticHands.Both;
    /// <summary>Use the old SteamVR vibration call (for controllers SteamVR Input has no binding for).</summary>
    public bool UseLegacyApi { get; set; } = false;
}

public sealed class BeatSettings
{
    /// <summary>
    /// Also send beats predicted from the heart rate when no clear beat was detected (keeps a steady
    /// rhythm). Off: only detected beats trigger OSC, vibration and the heart animation.
    /// </summary>
    public bool SendPredicted { get; set; } = true;
}

public sealed class HudSettings
{
    /// <summary>
    /// A small heart with the BPM in the lower left of the view, coloured by what the measurement is
    /// doing. Drifts slowly and pulses so it does not burn into OLED headset screens.
    /// </summary>
    public bool Enabled { get; set; } = false;
}

public sealed class GeneralSettings
{
    /// <summary>Show the desktop window. When off, the app runs from the SteamVR dashboard and the tray icon.</summary>
    public bool ShowDesktopWindow { get; set; } = true;
    /// <summary>When SteamVR starts the app, start with the window minimised.</summary>
    public bool StartMinimizedWithSteamVr { get; set; } = true;
}

public sealed class AppSettings
{
    public OscSettings Osc { get; set; } = new();
    public DeviceSettings Devices { get; set; } = new();
    public DetectionSettings Detection { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public HapticsSettings Haptics { get; set; } = new();
    public BeatSettings Beats { get; set; } = new();
    public HudSettings Hud { get; set; } = new();
    public GeneralSettings General { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Loads settings, writing a file with the defaults if none exists yet.</summary>
    public static AppSettings LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var defaults = new AppSettings();
            defaults.Save(path);
            return defaults;
        }
        var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new AppSettings();
        loaded.Save(path); // adds settings that are new in this version, drops removed ones
        return loaded;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
}
