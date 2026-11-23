# Workflows

## Build, test, package

```powershell
dotnet build VirtualHeartBeatMonitor.slnx -c Release
dotnet test tests/HeartBeat.Core.Tests -c Release
powershell -ExecutionPolicy Bypass -File tools/publish.ps1   # tests, then artifacts/VirtualHeartBeatMonitor-<ver>-win-x64.zip
```

- `publish.ps1` makes one self-contained exe (about 50 MB; WinForms can't be trimmed). It fails if
  `artifacts/publish/HeartBeatMonitor.exe` is running: close the app first.
- The dev build runs from `src/HeartBeat.App/bin/Release/net10.0-windows/HeartBeatMonitor.exe`.
- `.github/workflows/release.yml` tests every push and attaches the zip to a GitHub release for
  `v*` tags.

## Checking changes live

- Run with a separate settings file and OSC port so nothing reaches VRChat:
  `HeartBeatMonitor.exe --settings <tmp>\s.json --osc-port 9123 --seconds 25`. Then read
  `app.log` next to the exe.
- With `HEARTBEAT_DUMP_DIR=<folder>` set, the app saves `window.png`, `overlay.png` and
  `dashboard.png` after about 15 s: a way to look at the UI without a headset.
- SteamVR must already be running; the app never starts it.

## Recording and replaying

- `tools/Probe`: `Probe check` (device list, IMU availability, update rates), `Probe live`,
  `Probe record --seconds 120 --label name --note "..."` → `recordings/<time>_<label>/`
  (`session.json`, `imu_<index>_<Kind>.csv`, `poses_<index>_<Kind>.csv`). It asks for a
  hand-counted pulse before and after, and warns when a device stops sending.
  `tools/record-sessions.ps1` walks through a standard set of sessions.
- `tools/Replay <folder>`: runs the engine exactly as live and prints:
  - the fused HR, how often a number is shown, and the error against the chest tracker (when its
    prominence is ≥ 10);
  - per-device medians, beat-event stats against the tracker's own beats, and breathing holds.

  Options: `--hr-devices HMD,Controller-L`, `--motion 8`, `--min-window 6`, `--max-masked 0.25`,
  `--head-weight`, `--hand-weight`, `--chest-weight`, `--beat Setting=Value` (any `BeatDetector`
  property), and `--csv` (writes `replay.csv`, `replay_breathing.csv`, `replay_beats.csv`).
- In recordings, the Kind "Tracker" maps to the Chest role. Replay leaves it out of the fused HR
  by default and uses it as the reference.

## Supporting a new headset from a user's diagnostics report

Users click "Create diagnostics report" (or run `--diagnostics`). They sit still for 60 s and get
`VirtualHeartBeatMonitor-diagnostics-<date>.zip` on the Desktop. Serials are masked.

1. Unzip and read `report.txt`:
   - For each device: model, maker, tracking system, controller type.
   - The IMU open attempts with SteamVR's result codes.
   - The IMU rate, gaps and |accel| (≈9.81 means m/s²).
   - Pose updates per second.
   - What the engine made of it: shown %, per-device peak, prominence and motion.
2. If `raw IMU open` and the IMU looks sane, run `tools/Replay <unzipped folder>`. The zip uses
   the Probe layout. Check per-device prominence (> 8 is a usable heartbeat), how often a
   number is shown, and whether the peak matches a plausible resting rate.
3. If the IMU did not open (`IOBuffer_PathDoesNotExist` or similar):
   - Try other paths: the driver may use a different tracking-system name, or none.
   - Otherwise the pose fallback is used automatically (`signal from pose` in the report).
     `tools/Replay` reads `poses_*.csv` when there is no `imu_*.csv` (angular velocity from
     `TrackedDevicePose_t` as the gyro). Check the update rate: about 90 Hz or more is
     needed for the 4–20 Hz band.
4. Look at `app.log` for connection, panel and error history, and at `crash.log` if present.
5. Any device-specific handling belongs in `LiveSession.Classify` / `ImuStream.CandidatePaths`
   (Tracking/App). Keep `HeartBeat.Core` device-agnostic.

## Data and privacy

`recordings/`, `probe-reports/` and `artifacts/` are git-ignored. Recordings and diagnostics
zips contain motion data from which a person's heart rate can be derived; treat them as
personal data.
