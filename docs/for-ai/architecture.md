# Architecture

## Projects (`VirtualHeartBeatMonitor.slnx`)

| Project | Target | Role |
|---|---|---|
| `src/HeartBeat.Core` | net10.0 | All signal processing. No OpenVR, no UI. Used live and by Replay. |
| `src/HeartBeat.Tracking` | net10.0 | OpenVR access: connection, device list, raw IMU streams, pose polling, overlays (wrist, dashboard), SteamVR Input (haptics + grab), auto-start manifest, rigid-transform maths. Includes Valve's `lib/openvr/openvr_api.cs` binding. |
| `src/HeartBeat.Osc` | net10.0 | OSC encoder and the VRChat parameter mapping (`VRChatOutput`). |
| `src/HeartBeat.App` | net10.0-windows, WinForms | `HeartBeatMonitor.exe`: live session, host thread, window, tray, panels' drawing, Direct3D 11 texture upload, diagnostics reports, settings, logs. |
| `tools/Probe` | console | Phase-0 tool: checks what data devices expose, records sessions to CSV. |
| `tools/Replay` | console | Runs `HeartBeat.Core` over a recording and scores it. |
| `tests/HeartBeat.Core.Tests` | xunit | Synthetic-signal tests for DSP, engine, beats, OSC, rigid maths. |

`lib/openvr/` holds the official OpenVR SDK v2.15.6 C# binding and `openvr_api.dll` (BSD-3).

## Live data flow

```
SteamVR ─ IVRIOBuffer "/devices/<driver>/<serial>/imu" (raw accel m/s², gyro rad/s, device clock)
   │  ImuStream (per device) ← ImuReader thread polls every ~2 ms
   ▼
LiveSession.OnImu: map device clock → PC clock (min(host − sample), slow upward leak)
   │  MonitorEngine.Push(deviceId, ImuSample)      [reader thread]
   │  LiveSession.OnTick → MonitorEngine.TryUpdate(now) once per second → MonitorSnapshot
   ▼
MonitorEngine (Core)
   per device: UniformResampler (200 Hz) → MotionGate, VibrationEnvelope → SpectralHeartRate,
               BeatEnvelope (100 Hz), BreathingEstimator (chest only)
   across devices: HeartRateFusion (number), BeatDetector (beat events)
   ▼ events: SnapshotReady (1 Hz), Beat, BreathingUpdated (~10 Hz)
MonitorHost thread (App): OSC (VRChatOutput), haptics, wrist panel, dashboard tab, grab-to-move,
                          diagnostics capture, reconnects
MainForm (UI thread): reads host/session state on a 50 ms timer; tray icon
```

## Threads

- **ImuReader thread**: reads IMU streams, calls `MonitorEngine.Push` and `TryUpdate`. The engine
  is touched only on this thread. `LiveSession.RefreshDevices` stops the reader before adding
  devices.
- **MonitorHost thread**: owns the `VrRuntime` (connects as an overlay app only if SteamVR is
  running), polls events, drives overlays/OSC/haptics/input. Woken immediately by beat events
  (`AutoResetEvent`), otherwise every 30 ms (11 ms while the wrist panel is being moved). Each
  loop pass catches its own exceptions and logs them.
- **UI thread**: WinForms window and tray. Communicates through `MonitorHost` methods
  (`SettingsChanged`, `RequestAutoStart`, `SetDeviceUsed`, `SetMoveWristPanel`,
  `RequestDiagnostics`) and a `SettingsVersion` counter to resync controls.
- **PosePoller thread**: only during a diagnostics capture (500 Hz pose polling).

## Key classes

| Class | What it does |
|---|---|
| `MonitorEngine` | The pipeline. Device roles and weights, stillness, fusion, beats, staleness. |
| `HeartRateEstimate` | The output number: `DisplayText` is "62" or "--". |
| `BeatEvent` | `Time`, `EmittedAt`, `Measured`, `Bpm`, `IntervalSeconds`, `Strength`. |
| `LiveSession` | Devices, IMU streams, clock mapping, BPM history (10 min, "--" stored as 0), per-device on/off. |
| `MonitorHost` | Lifecycle and everything SteamVR-side outside the engine. |
| `WristOverlay` + `OverlayRenderer` | Wrist panel (320×220 px): heart, BPM, status, 60 s graph. |
| `DashboardPanel` + `DashboardUi` | SteamVR dashboard tab (960×640 px) with laser-pointer widgets. |
| `PanelPresenter` + `D3D11Textures` | Upload panel bitmaps as D3D11 textures (raw-upload fallback). |
| `ControllerHaptics` | SteamVR Input: vibration action + grab action (grip/trigger). |
| `WristGrab` + `Rigid` | Grab-to-place the wrist panel; 3×4 transform maths. |
| `DiagnosticsCapture` / `DiagnosticsReport` | One-minute capture and zip report (see workflows.md). |
| `AppSettings` | `settings.json` next to the exe. |
| `AppLog` | `app.log` / `app.previous.log` next to the exe; `crash.log` for unhandled exceptions. |
