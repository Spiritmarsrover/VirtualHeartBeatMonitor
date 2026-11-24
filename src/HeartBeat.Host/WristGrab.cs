using HeartBeat.Tracking;
using Valve.VR;

namespace HeartBeat.App;

/// <summary>
/// Grab-to-move for the wrist panel. In move mode, holding grip or trigger on the other hand
/// while it is near the panel picks the panel up; it then follows that hand rigidly (keeping the
/// offset it had when grabbed) and stays attached to the wrist controller. Letting go fixes the
/// new placement.
/// </summary>
public sealed class WristGrab
{
    public enum Result { None, Grabbed, Moving, Released }

    /// <summary>How close (metres) the grabbing controller must be to the panel's centre.</summary>
    public float ReachMeters { get; set; } = 0.15f;

    private HmdMatrix34_t _handToPanel;
    private ETrackedControllerRole _grabHand;
    private bool _wasHeld;

    public bool Holding { get; private set; }

    /// <param name="moveMode">False ends an ongoing grab (the placement is kept).</param>
    public Result Update(WristOverlay wrist, CVRSystem system, TrackedDevicePose_t[] poses, ControllerHaptics input, bool moveMode)
    {
        uint wristDevice = wrist.AttachedDevice;
        var otherHand = wrist.Hand == OverlayHand.Left ? ETrackedControllerRole.RightHand : ETrackedControllerRole.LeftHand;
        uint handDevice = system.GetTrackedDeviceIndexForControllerRole(Holding ? _grabHand : otherHand);
        if (wristDevice >= poses.Length || handDevice >= poses.Length
            || !poses[wristDevice].bPoseIsValid || !poses[handDevice].bPoseIsValid)
        {
            return Holding && !moveMode ? Release(input) : Result.None;
        }

        var wristPose = poses[wristDevice].mDeviceToAbsoluteTracking;
        var handPose = poses[handDevice].mDeviceToAbsoluteTracking;
        var panelPose = Rigid.Multiply(wristPose, wrist.PlacementForHand());
        bool held = moveMode && input.GrabHeld(Holding ? _grabHand : otherHand);
        bool pressed = held && !_wasHeld;
        _wasHeld = held;

        if (!Holding)
        {
            if (pressed && Rigid.Distance(handPose, panelPose) <= ReachMeters)
            {
                Holding = true;
                _grabHand = otherHand;
                _handToPanel = Rigid.Multiply(Rigid.Inverse(handPose), panelPose);
                Tick(input, _grabHand);
                return Result.Grabbed;
            }
            return Result.None;
        }

        if (!held) return Release(input);

        // Follow the hand, expressed relative to the wrist so the panel stays attached to it.
        var newPanel = Rigid.Multiply(handPose, _handToPanel);
        wrist.SetPlacementForHand(Rigid.Multiply(Rigid.Inverse(wristPose), newPanel));
        return Result.Moving;
    }

    private Result Release(ControllerHaptics input)
    {
        Holding = false;
        _wasHeld = false;
        Tick(input, _grabHand);
        return Result.Released;
    }

    private static void Tick(ControllerHaptics input, ETrackedControllerRole hand) =>
        input.Pulse(0.5, hand == ETrackedControllerRole.LeftHand ? HapticHands.Left : HapticHands.Right, legacy: false);
}
