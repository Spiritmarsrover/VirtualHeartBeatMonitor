using System.Runtime.InteropServices;
using System.Text.Json;
using Valve.VR;

namespace HeartBeat.Tracking;

public enum HapticHands { Both, Left, Right }

/// <summary>
/// Controller input and output through SteamVR Input: short vibrations (one per heartbeat) and a
/// "grab" button (grip or trigger) used to move the wrist panel. The action manifest and default
/// bindings for common controllers are written next to the exe. The legacy TriggerHapticPulse
/// call is available as a fallback for controllers SteamVR Input has no binding for.
/// All calls must come from one thread (the host loop).
/// </summary>
public sealed class ControllerHaptics
{
    private const string ActionSet = "/actions/heartbeat";
    private const string Action = "/actions/heartbeat/out/pulse";
    private const string GrabSet = "/actions/heartbeatgrab";
    private const string GrabAction = "/actions/heartbeatgrab/in/grab";
    private static readonly string[] ControllerTypes =
        ["knuckles", "vive_controller", "oculus_touch", "holographic_controller", "hpmotioncontroller", "vive_cosmos_controller"];

    private readonly CVRSystem _system;
    private readonly CVRInput? _input;
    private readonly ulong _actionSet, _action, _grabSet, _grabAction, _left, _right;
    private readonly VRActiveActionSet_t[] _normal = new VRActiveActionSet_t[1];
    private readonly VRActiveActionSet_t[] _withGrab = new VRActiveActionSet_t[2];
    private static readonly uint ActiveSetSize = (uint)Marshal.SizeOf<VRActiveActionSet_t>();
    private static readonly uint DigitalSize = (uint)Marshal.SizeOf<InputDigitalActionData_t>();

    public bool InputReady { get; }
    public string? Error { get; private set; }

    /// <summary>
    /// While true, the grab button is read, taking grip and trigger away from the game (only while
    /// the user is moving the wrist panel).
    /// </summary>
    public bool GrabActive { get; set; }

    public ControllerHaptics(VrRuntime vr, string folder)
    {
        _system = vr.System;
        _input = Valve.VR.OpenVR.Input;
        if (_input == null) { Error = "SteamVR Input not available"; return; }
        // Development switch: run without an action manifest (no vibration action, no grab).
        if (Environment.GetEnvironmentVariable("HEARTBEAT_NO_INPUT") == "1") { Error = "SteamVR Input switched off (HEARTBEAT_NO_INPUT)"; return; }
        try
        {
            string manifest = WriteManifest(folder);
            var err = _input.SetActionManifestPath(manifest);
            if (err != EVRInputError.None) { Error = $"Action manifest: {err}"; return; }
            ulong set = 0, action = 0, grabSet = 0, grab = 0, left = 0, right = 0;
            _input.GetActionSetHandle(ActionSet, ref set);
            _input.GetActionHandle(Action, ref action);
            _input.GetActionSetHandle(GrabSet, ref grabSet);
            _input.GetActionHandle(GrabAction, ref grab);
            _input.GetInputSourceHandle("/user/hand/left", ref left);
            _input.GetInputSourceHandle("/user/hand/right", ref right);
            (_actionSet, _action, _grabSet, _grabAction, _left, _right) = (set, action, grabSet, grab, left, right);
            _normal[0].ulActionSet = set;
            _withGrab[0].ulActionSet = set;
            _withGrab[1].ulActionSet = grabSet;
            _withGrab[1].nPriority = Valve.VR.OpenVR.k_nActionSetOverlayGlobalPriorityMin;
            InputReady = set != 0 && action != 0;
            if (!InputReady) Error = "Vibration action not found";
        }
        catch (IOException e)
        {
            Error = $"Could not write action manifest: {e.Message}";
        }
    }

    /// <summary>Refreshes action state. Call regularly (each host loop pass).</summary>
    public void Update()
    {
        if (!InputReady) return;
        bool grab = GrabActive && _grabSet != 0;
        _input!.UpdateActionState(grab ? _withGrab : _normal, ActiveSetSize);
    }

    /// <summary>True while the grab button (grip or trigger) is held on that hand.</summary>
    public bool GrabHeld(ETrackedControllerRole hand)
    {
        if (!InputReady || !GrabActive || _grabAction == 0) return false;
        var data = new InputDigitalActionData_t();
        var err = _input!.GetDigitalActionData(_grabAction, ref data, DigitalSize, hand == ETrackedControllerRole.LeftHand ? _left : _right);
        return err == EVRInputError.None && data.bActive && data.bState;
    }

    /// <param name="strength">0..1. Mapped to amplitude (SteamVR Input) or pulse length (legacy).</param>
    public void Pulse(double strength, HapticHands hands, bool legacy)
    {
        strength = Math.Clamp(strength, 0, 1);
        if (strength <= 0) return;
        foreach (var (role, source) in Targets(hands))
        {
            if (!legacy && InputReady)
            {
                var err = _input!.TriggerHapticVibrationAction(_action, 0f, 0.06f, 55f, (float)strength, source);
                if (err != EVRInputError.None) Error = $"Vibration: {err}";
            }
            else
            {
                uint device = _system.GetTrackedDeviceIndexForControllerRole(role);
                if (device != Valve.VR.OpenVR.k_unTrackedDeviceIndexInvalid)
                    _system.TriggerHapticPulse(device, 0, (ushort)(300 + 3200 * strength));
            }
        }
    }

    private IEnumerable<(ETrackedControllerRole role, ulong source)> Targets(HapticHands hands)
    {
        if (hands != HapticHands.Right) yield return (ETrackedControllerRole.LeftHand, _left);
        if (hands != HapticHands.Left) yield return (ETrackedControllerRole.RightHand, _right);
    }

    private static string WriteManifest(string folder)
    {
        var json = new JsonSerializerOptions { WriteIndented = true };
        object GrabSource(string hand, string input) => new Dictionary<string, object>
        {
            ["path"] = $"/user/hand/{hand}/input/{input}",
            ["mode"] = "button",
            ["inputs"] = new Dictionary<string, object> { ["click"] = new Dictionary<string, string> { ["output"] = GrabAction } },
        };

        foreach (var type in ControllerTypes)
        {
            var binding = new Dictionary<string, object>
            {
                ["controller_type"] = type,
                ["name"] = "VirtualHeartBeatMonitor",
                ["description"] = "Heartbeat vibration, grab to move the wrist panel",
                ["bindings"] = new Dictionary<string, object>
                {
                    [ActionSet] = new Dictionary<string, object>
                    {
                        ["haptics"] = new[]
                        {
                            new Dictionary<string, string> { ["output"] = Action, ["path"] = "/user/hand/left/output/haptic" },
                            new Dictionary<string, string> { ["output"] = Action, ["path"] = "/user/hand/right/output/haptic" },
                        },
                        ["sources"] = Array.Empty<object>(),
                    },
                    [GrabSet] = new Dictionary<string, object>
                    {
                        ["sources"] = new[]
                        {
                            GrabSource("left", "grip"), GrabSource("right", "grip"),
                            GrabSource("left", "trigger"), GrabSource("right", "trigger"),
                        },
                    },
                },
            };
            File.WriteAllText(Path.Combine(folder, $"heartbeat_bindings_{type}.json"), JsonSerializer.Serialize(binding, json));
        }

        var manifest = new Dictionary<string, object>
        {
            ["default_bindings"] = ControllerTypes.Select(t => new Dictionary<string, string>
            {
                ["controller_type"] = t,
                ["binding_url"] = $"heartbeat_bindings_{t}.json",
            }).ToArray(),
            ["actions"] = new[]
            {
                new Dictionary<string, string> { ["name"] = Action, ["type"] = "vibration" },
                new Dictionary<string, string> { ["name"] = GrabAction, ["type"] = "boolean" },
            },
            ["action_sets"] = new[]
            {
                new Dictionary<string, string> { ["name"] = ActionSet, ["usage"] = "leftright" },
                new Dictionary<string, string> { ["name"] = GrabSet, ["usage"] = "leftright" },
            },
            ["localization"] = new[]
            {
                new Dictionary<string, string>
                {
                    ["language_tag"] = "en_US",
                    [ActionSet] = "Heartbeat", [Action] = "Heartbeat pulse",
                    [GrabSet] = "Move wrist panel", [GrabAction] = "Grab wrist panel",
                },
            },
        };
        string path = Path.Combine(folder, "heartbeat_actions.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, json));
        return path;
    }
}
