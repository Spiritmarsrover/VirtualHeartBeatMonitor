# SteamVR integration and the app

## Connecting

`VrRuntime.ConnectOverlayIfRunning()` first inits as `VRApplication_Background`, which fails
fast if SteamVR isn't running, so the app never starts SteamVR itself. It then re-inits as
`VRApplication_Overlay`, which overlays, input and app manifests need. `MonitorHost` retries every
2 s while waiting and reconnects after SteamVR restarts. With `--steamvr` (set by the auto-start
manifest) the app exits when SteamVR quits.

## Raw IMU

- Opened with `IVRIOBuffer.Open("/devices/<trackingSystem>/<serial>/imu", Read, sizeof(ImuSample_t), 0)`
  (`ImuStream.TryOpen`; tries the tracking-system name as-is and lowercased).
- Lighthouse: works for the HMD (1000 Hz), Index controllers (250 Hz) and Vive trackers (250 Hz;
  wireless dropouts of up to 30% when worn on the body).
- The ring buffer can hold stale samples from before opening, even from a previous session.
  Handled by `DiscardBacklog`, dropping a prefix that ends in a jump of more than 1 s, and the
  recording loader dropping the same.
- Device clocks are mapped to the PC clock per device (`LiveSession.OnImu`).
- Streams silent for 5 s are reopened. The engine updates on a clock tick, so silence turns into
  "--" rather than a frozen value.

## Pose fallback (no raw IMU: Steam Frame and other inside-out headsets)

- A device whose IMU path cannot be opened uses the pose instead (`LiveSession`, `UsesPose`):
  a `PosePoller` at 1 kHz (raw uncalibrated universe), keeping only polls where the velocity
  changed (= new driver update), and feeds `vAngularVelocity` (rad/s) to the engine as the gyro,
  with zero acceleration and host-clock time. Samples are queued and pushed on the reader thread
  (`OnTick`), so the engine stays single-threaded. Breathing is off for pose devices.
- Steam Frame (SteamVR 2.19.4, tracking system `cv`): HMD ~287 updates/s, controllers
  ~195–208/s. Head pose works well while still (see project-log 2026-11-22).
- Pose-based controllers start switched off (resting controllers only added noise in TEST 1);
  the user can switch them on. Raw-IMU controllers keep the old default.
- Diagnostics poll poses at 1 kHz too and report `signal from raw IMU / pose` per device.

## Overlays

- **Wrist panel** (`WristOverlay`): attached to a controller with
  `SetOverlayTransformTrackedDeviceRelative`. Placement is a 3×4 transform stored for the left
  hand (`overlay.placement`) and mirrored across the controller's YZ plane for the right. It
  hides only after the controller has been missing for 3 s. Redraws on content change, at 30 fps
  during the beat pulse.
- **Dashboard tab** (`DashboardPanel`): `CreateDashboardOverlay`, mouse input
  (`SetOverlayInputMethod(Mouse)`, mouse scale = pixel size; OpenVR's y is flipped). Redraws at
  4 fps while visible, 30 fps during the beat pulse, and immediately after input. Overlay keys can
  remain "in use" for a moment after a previous process exits, so creation is retried every 5 s.
- **Image upload** (`PanelPresenter`): Direct3D 11 BGRA texture + `SetOverlayTexture`.
  `D3D11Textures` calls D3D11 through COM vtable slots (no NuGet package; adding one was blocked
  in this environment). At start-up it writes a pattern to a texture and reads it back through a
  staging copy; if that fails, it falls back to `SetOverlayRaw`, and the dashboard can fall back
  to PNG files. Raw uploads at high rates flickered in the headset, which is why textures are
  used.

## SteamVR Input (`ControllerHaptics`)

Writes `heartbeat_actions.json` and `heartbeat_bindings_<type>.json` next to the exe at start-up,
for knuckles, vive_controller, oculus_touch, holographic_controller, hpmotioncontroller and
vive_cosmos_controller.

- `/actions/heartbeat/out/pulse` (vibration): 60 ms, 55 Hz, amplitude = strength, per hand.
  `haptics.useLegacyApi` uses `TriggerHapticPulse` instead.
- `/actions/heartbeatgrab/in/grab` (boolean, grip or trigger click): its action set is activated
  only in "move wrist panel" mode, with priority `k_nActionSetOverlayGlobalPriorityMin` so it
  takes those buttons from the game. `WristGrab` picks the panel up when the other hand holds
  grab within 15 cm of the panel.

## Auto-start

`SteamVrAutoStart` writes `heartbeatmonitor.vrmanifest` (app key `virtualheartbeatmonitor.app`,
`is_dashboard_overlay`, arguments `--steamvr`) next to the exe, registers it, and sets auto-launch.
It's opt-in from the UI.

## OSC to VRChat (`VRChatOutput`, default 127.0.0.1:9000)

Avatar parameters under `/avatar/parameters/`:

| Parameter | Type | Notes |
|---|---|---|
| `HR`, `onesHR`, `tensHR`, `hundredsHR` | int | Names follow existing heart-rate avatar prefabs. Not updated while "--" (keep the last value). |
| `HRPercent`, `FullHRPercent` | float | (HR − hrMin)/(hrMax − hrMin), and ×2 − 1. |
| `isHRConnected`, `isHRActive` | bool | Active = a number is shown. |
| `isHRBeat`, `isHRBeatMeasured` | bool | 100 ms pulse per beat event. |
| `BreathRate`, `BreathPhase`, `isBreathHold` | float, float, bool | Chest tracker only; phase at 10 Hz. |

Values are re-sent every 5 s even if unchanged. The chatbox (`/chatbox/input`, optional) shows
"♥ 64 BPM" every 3 s while a number is shown, and "♥ -- BPM" once when it drops (≥ 1.5 s after
the previous message).

## Settings (`settings.json` next to the exe; saved immediately)

`osc.*` (enabled, host, port, chatbox, hrMin/hrMax, beatPulseMs), `devices.useDevice` (serial →
bool; default on for head, hands and chest, off for other trackers), `devices.chestTrackerSerials`
(also auto-detects SteamVR role "chest"), `detection.displayThreshold` (0.5),
`detection.motionThresholdDps` (8), `overlay.hand`, `overlay.widthMeters`, `overlay.placement`,
`haptics.*`, `beats.sendPredicted`, `general.showDesktopWindow`,
`general.startMinimizedWithSteamVr`. Settings that are removed are dropped on the next save.

## Command line

`HeartBeatMonitor.exe [--console] [--settings path] [--no-osc] [--osc-port N] [--seconds N] [--diagnostics] [--steamvr]`

Single instance (named mutex). Development environment variables: `HEARTBEAT_DUMP_DIR` saves PNGs
of the window, wrist panel and dashboard tab after about 15 s; `HEARTBEAT_DIAGNOSTICS_DIR`
redirects diagnostics zips and suppresses opening Explorer.
