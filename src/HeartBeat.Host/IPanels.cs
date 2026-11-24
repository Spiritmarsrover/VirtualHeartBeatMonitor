using HeartBeat.Tracking;

namespace HeartBeat.App;

/// <summary>
/// The in-VR panels (wrist panel, SteamVR dashboard tab): drawing and texture upload are platform
/// code (Windows: System.Drawing + Direct3D 11; Steam Frame: built-in drawer + raw upload).
/// One instance per SteamVR session, used only on the host thread.
/// </summary>
public interface IPanels : IDisposable
{
    /// <summary>Called on every pass of the host loop (~30 ms; ~11 ms while the wrist panel is being moved).</summary>
    void Update(LiveSession session);
}

/// <summary>Creates the panels for a new SteamVR session (null: no panels).</summary>
public delegate IPanels? PanelsFactory(MonitorHost host, VrRuntime vr, ControllerHaptics haptics);
