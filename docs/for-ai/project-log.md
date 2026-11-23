# VirtualHeartBeatMonitor: project log (original plan, then dated sections with measurements and decisions)

Goal: estimate the user's heart rate from the motion of SteamVR tracked devices while the user is
**stationary** (sitting, standing idle, lying down), show it, and send it to VRChat over OSC.

**Decisions so far**
- Language: **C# / .NET 10 (LTS)**, shipped as a single exe on GitHub Releases.
- **The HMD is the main sensor**, because every user has one. **Controllers are secondary**: a
  weak extra signal and a motion reference.
- A **chest device** (tracker in a strap, or a controller lying on the chest) is a
  **development and test reference**, not a product requirement. Its beats are clearly visible,
  so it labels the HMD data.
- Heart rate is only reported while the user is still enough. Otherwise the app holds the last
  value and shows low confidence.
- No reference heart-rate monitor yet. Fresh recordings only.

## 1. Approach

Each heartbeat makes the body recoil (**ballistocardiography, BCG**). At the head this is a tiny
periodic nod and bob, mainly in the **1–10 Hz** band. Research on head-worn sensors (e.g.
smart-glasses studies) found the **gyroscope (rotation rate) usually works better than the
accelerometer**. At the chest the signal (**seismocardiography, SCG**) is much stronger and
sharper, in the 5–30 Hz range.

The old attempt failed because a time-domain threshold needs each beat to stand out on its own.
An FFT over about 10 s adds up roughly 10 beats: the periodic heartbeat accumulates while random
motion does not. A signal too weak to see beat-by-beat can still give a clear spectral peak.
Breathing (0.1–0.5 Hz) is a separate peak that can be filtered out. Pitfalls:

1. **Harmonics:** the BCG/SCG waveform isn't sinusoidal, so the 2nd harmonic can be stronger
   than the fundamental, which can lead to reporting 2× HR. Use a harmonic-sum spectrum and
   tracking over time.
2. **Resolution and latency:** a 10 s window gives 6 BPM bins. Use zero-padding, peak
   interpolation and temporal tracking.
3. **Motion:** detect it (on the HMD and controllers), then gate, hold or decay the estimate.

## 2. Data access and sample rate (answered in Phase 0)

The old Unity project sampled poses once per rendered frame, roughly 90 Hz, with frame-time
jitter and per-frame pose prediction. This app reads OpenVR directly from its own thread:

- **Poses** (`GetDeviceToAbsoluteTrackingPose`) can be polled at any rate (e.g. 1 kHz). But
  each call returns the latest pose *extrapolated to now*. Real information only arrives as fast
  as the driver updates the pose, so Phase 0 counts **distinct** updates per device. The update
  rate is expected to be well above 90 Hz but is unverified.
- **Raw IMU** (`IVRIOBuffer`, `/devices/<driver>/<serial>/imu`, `ImuSample_t` with accel and
  gyro and the IMU's own timestamps) gives unfiltered samples at the IMU rate, likely hundreds
  of Hz up to about 1 kHz on Lighthouse devices. If it is available, it's the best source.
- 90 Hz is technically enough for a 1–10 Hz signal. The bigger wins are **exact timestamps,
  no frame-locked jitter, and no prediction or smoothing**.

| Setup | Tracking | To verify |
|---|---|---|
| Bigscreen Beyond 1 + Index controllers + Vive trackers | Lighthouse (IMU + optical) | Raw IMU via IOBuffer; pose update rate; filtering in the 1–10 Hz band |
| Steam Frame (+ controllers) | Inside-out cameras + IMU, streamed to PC | Raw IMU likely unavailable; pose rate and smoothing after streaming |

### Phase 0 results — Beyond setup (2026-11-19, SteamVR 2.19.4)

- **Raw IMU via IOBuffer works** for HMD, Index controllers and Vive tracker
  (`/devices/lighthouse/<serial>/imu`, `elements=0`). Accel in m/s², gyro in rad/s.
  - HMD: 1000 Hz, perfectly regular. Controllers: 250 Hz. Tracker: 250 Hz nominal with wireless
    dropouts (about 14% of samples missing, occasional gaps over 50 ms).
  - The driver ring buffer holds seconds of stale samples on open. The backlog is now discarded
    in `ImuStream.TryOpen`.
- Pose updates: HMD about 700/s, controllers about 309/s. Poses are extrapolated on every poll
  between updates (the matrix changes every poll, velocity only on real updates).
- Posture for that recording: reclined in a chair, tracker on the chest, arms on the armrests,
  head supported. This is a best case for stillness.
- **First seated-still recording (120 s, no reference):** the HMD, the left controller and the
  tracker all agree on about 64 BPM using the **envelope of the 4–20 Hz band** (gyro or accel).
  Per-window estimates follow the same ups and downs across devices. HMD: 80% of 12 s windows
  within 6 BPM of the median; tracker: 91%, with about 4× higher peak prominence. The direct
  low-band spectrum (0.7–8 Hz) does **not** work (locks onto harmonics or sway).
  → Envelope-based spectral estimation is the lead method for Phase 2.
- Steam Frame: not yet tested.

### Phase 2 status (2026-11-19): C# engine in `src/HeartBeat.Core`, scored with `tools/Replay`

The pipeline below is implemented as causal, streaming code: resampler, 4–20 Hz envelope,
12 s harmonic-sum FFT, prominence-weighted fusion, motion gate, and chest breathing. The same
code runs on recordings and live. There are 14 unit tests on synthetic signals.

Seated/reclined recording, scored against the chest tracker (whose beats are clear) as reference:

| Devices fused | Confident | Mean error vs reference | Within 5 BPM |
|---|---|---|---|
| HMD + both controllers | 80% of time | 1.2 BPM | 97% |
| HMD + left controller | 77% | 0.8 BPM | 100% |
| HMD alone | 61% | 1.2 BPM | 98% |
| Both controllers | 73% | 1.3 BPM | 97% |

- Breathing finds both holds (6 s and 9 s) and gives a rate of about 11 breaths/min.
- Resting control: no breathing signal. The heart rate is confident 12% of the time, from
  stretches where the resting HMD alone showed a strong 44–55 BPM peak (cause unknown: desk
  vibration?). In the app, gate on the HMD proximity sensor / user-presence state so a headset
  that isn't worn reports nothing.
- Fixed along the way: remaining stale IMU samples at stream start; gyro bias leaking into the
  motion gate; peaks at the edge of the BPM range.
- Open: Steam Frame untested; the chest tracker is not a real pulse monitor.

**Session set 1 (Beyond, 7 × 2 min, counted wrist pulse before/after, 30 s × 2):**

| Session | Confident | Engine (start → end) | Counted pulse | Chest reference |
|---|---|---|---|---|
| upright-still | 93% | 63 → 65 | 60 → 62 | tracker dropped out |
| reclined-still | 93% | 60 → 59 | 62 → 58 | 0.4 BPM mean error |
| standing-still | 94% | 72 → 75 | 58 → 60 | 0.6 BPM mean error |
| talking | 5% | n/a | 60 → 60 | estimate unusable |
| small-moves | 39% | 61 → 62 | 60 → 64 | no tracker |
| breathing-mix | 38% | 64 → 59 | 60 → 66 | no tracker |
| after-exercise | 86% | 76 → 70 | 96 → 70 | no tracker |

- Still postures work, including upright without head support and standing.
- Talking breaks it. The engine correctly stays unconfident instead of reporting wrong values.
- Standing reads about 12 BPM above the counted pulse, while four devices agree with each other.
  This is likely a counting-posture difference (to confirm).
- After exercise, the heart rate was already about 76 by the first estimate (the window covers
  the first 12 s, after the headset went back on) and it tracks the decline to 70.
- Fixed: a device that stops sending (the tracker battery died) kept reporting its last window.
  Probe now also warns live when a device stops sending, and when no tracker is connected.
- Breathing-mix has no tracker, so breathing could not be checked on it. Re-record it.

**Display rule:** the engine outputs "--" when its confidence is below 0.5
(`HeartRateEstimate.DisplayText` / `DisplayBpm`). A held value stays shown for about 5 s
after motion starts, then switches to "--". From a sweep over the recordings: still sessions
show a value 93–95% of the time, talking 7%, and small movements about 40%. The threshold barely
changes accuracy in still sessions (0.4–0.9 BPM error), but a higher one hides more of the
borderline values during movement.
**Beat events (built 2026-11-19):** `MonitorEngine.Beat` raises a `BeatEvent` per heartbeat:
`Time`, `EmittedAt` (delay about 90 ms), `Measured` (a real peak was found) or predicted from
the FFT rate, `Bpm`, `IntervalSeconds`, `Strength`.
- The beat path uses its own sharper envelope (4–20 Hz rotation + acceleration, about 40 ms
  smoothing, 100 Hz), combined across still devices. A rate-locked detector looks for the next
  peak around the predicted time and fills in a predicted beat if none shows up.
- Gating: no events while the number reads "--". The number now also reads "--" immediately
  when the head moves (it is no longer held), so neither output is sent while moving.
- Results on the Beyond sessions: events keep pace with the heart rate (56–70/min) and 86–100%
  are measured beats. 56–66% land within 120 ms of the chest tracker's own beats. That is a
  lower bound, because the chest reference itself drops samples.
- `BeatDetector.Sample` exposes the beat envelope (for a live waveform), and Replay writes
  `replay_beats.csv` and accepts `--beat Setting=Value` to tune the detector.

**Live app (built 2026-11-19, `src/HeartBeat.App` → `HeartBeatMonitor.exe`):** a console app.
- Waits for SteamVR and opens the raw IMU stream of the headset, controllers and chest trackers.
  It picks up devices that connect later and maps each device clock onto the PC clock.
- Runs the engine on the IMU reader thread and sends OSC to VRChat (`src/HeartBeat.Osc`,
  parameters in README.md).
- Gates on the headset's activity level (proximity), so a headset that isn't worn shows "--".
- Settings in `settings.json`. Smoke-tested live against SteamVR (headset on the desk): devices
  stream, OSC packets arrive, gating works.
- Not yet verified: the Beyond reporting "worn" while worn and still (if not, turn off
  `useProximitySensor`), and a real VRChat session. No window or overlay yet; no SteamVR
  auto-start.

**Icons and diagnostics reports (2026-11-22):**
- Heart icon with a pulse line (`IconArt`, also generates `Assets/app.ico`) for the exe, the
  window, the SteamVR tab thumbnail and the tray. The tray icon is red while a number is shown,
  grey on "--", and its tooltip shows the BPM.
- "Create diagnostics report" (window, tray menu, dashboard tab, or `--diagnostics`): records
  60 s of raw IMU samples (all devices) and pose updates (500 Hz poll, for headsets without
  raw IMU), the engine's view (shown %, per-device peak/prominence/motion), the device list
  with every IMU open attempt and its result, plus app.log, crash.log and settings. Serials are
  masked (prefix + hash), and it's zipped to the Desktop. The recording uses Probe's layout, so
  `Replay <unzipped folder>` works on it directly; tested end to end. This is how support for
  the Steam Frame and other headsets gets built from users' reports.

**Grab to place the wrist panel; chatbox dashes (2026-11-22):**
- The distance, height and tilt sliders are replaced by a "Move wrist panel" toggle. While it's
  on, a second SteamVR Input action set (grip or trigger on both hands, overlay-global priority
  so it takes those buttons from the game) is active. Grabbing within 15 cm of the panel attaches
  it rigidly to the grabbing hand (`WristGrab`). Its pose is re-expressed relative to the wrist
  controller every pass (11 ms), and on release the full 3×4 placement is saved
  (`overlay.placement`, left-hand frame, mirrored on the right). A haptic tick marks grab and
  release. The transform maths has unit tests (no jump on grab; follows the hand).
- Chatbox: while a number is shown it updates every 3 s as before; when the reading drops it
  sends "♥ -- BPM" once (no sooner than 1.5 s after the last message), then stays quiet.
- Not yet tried in the headset.

**BPM graph (2026-11-22):** the live session keeps 10 minutes of history (one point per engine
update; "--" stored as 0). `BpmGraph` draws it with a 0-based scale that tops out at the next 20
above the highest value (at least 100): the last 60 s on the wrist panel (now 320×220), the last
120 s in the dashboard tab and the desktop window. Checked visually with sample data; not yet
seen live, because SteamVR wasn't running.

**Detected-only beats, smoother dashboard pulse (2026-11-20):** "Fill in missed beats" setting
(`beats.sendPredicted`, default on). When off, predicted beats are dropped before OSC, vibration,
the heart animation and the beat counts. The dashboard tab redraws at 30 fps while the heart
pulses (4 fps otherwise), so its animation follows each beat like the wrist panel does.

**Per-device switches and position weighting (2026-11-20):**
- Every device (headset, controllers, all trackers) has a switch in the window's device list and
  in the dashboard tab. Switching takes effect immediately (`MonitorEngine.SetUseForHeartRate`)
  and is remembered per serial (`devices.useDevice`). Other trackers (hips, feet) are now read
  too but start switched off. This replaces `useControllers` / `excludeSerials`.
- Fusion and beat weights are multiplied by body position: chest x3, head x2, hands x1, other
  x0.5. On the recordings, head x2 scores the same as equal weights (0.4–0.9 BPM error, same
  coverage and beat agreement), while x3 started to hurt beat timing on one session. Chest x3
  can't be scored (the chest is the reference) but has by far the clearest peaks.
- Fixed: a leftover early return meant silent IMU streams were only reopened when a new device
  connected, so the "reconnect" half of the frozen-readout fix never ran.

**Panel flicker fix (2026-11-20):** the wrist panel and dashboard tab flickered and then
disappeared in the headset. The dashboard's large raw uploads had been refused and replaced by
PNG files reloaded several times a second, and raw uploads at those rates flicker. Both panels
now upload Direct3D 11 textures (`SetOverlayTexture`) through a small built-in helper with no
extra packages. At start-up a self-check reads a test pattern back from the GPU and falls back to
raw uploads if it doesn't match. The wrist panel redraws only on change (30 fps only during the
beat pulse) and no longer hides when a controller drops out for under 3 s. `app.log` next to the
exe records connects, panel creation, visibility changes and every error, to find the
disappearance if it happens again.

**Dashboard tab, vibration, fixes (built 2026-11-20, after the first headset test):**
- The worn/proximity check is removed. Many people wear the headset pushed up while developing,
  and it still measures fine that way.
- Frozen readout (the wrist panel stuck at 73 BPM): the engine only updated when samples
  arrived, so when the IMU streams stopped, the last number stayed. Updates now run on a clock
  tick, so silence turns into "--" ("no data from devices"). Streams silent for 5 s are
  reopened, and each host loop pass catches errors instead of dying.
- SteamVR dashboard tab "Heart rate": a 960×640 panel drawn with System.Drawing and used with the
  laser pointer (toggles, sliders, segmented choices). It falls back to image files if a raw
  upload is refused. It has every live setting, including wrist panel size, distance, height,
  tilt and reset.
- Vibration on each beat event: SteamVR Input action manifest + default bindings (written next
  to the exe), a 60 ms pulse with strength slider (amplitude) and Both/Left/Right, plus a legacy
  `TriggerHapticPulse` option. Pulses are triggered from the host thread, woken by the beat.
- The desktop window is optional (tray icon when hidden) and stays in sync with dashboard changes.
  Only one instance can run at a time, and unhandled errors go to `crash.log`.
- Smoke-tested in window and console mode against SteamVR; vibration and dashboard input are
  not yet tried in the headset.

**Window, wrist overlay, packaging (built 2026-11-20):**
- `HeartBeatMonitor.exe` is now a WinForms window app (`--console` for the text view). A
  `MonitorHost` thread owns the SteamVR connection: it connects as an overlay app only when
  SteamVR is already running, reconnects after SteamVR restarts, and exits with SteamVR when
  SteamVR launched it.
- Wrist overlay: a 320×140 panel (heart pulsing on beats, BPM or "--", status) uploaded with
  `SetOverlayRaw` at 10 Hz, attached to the left or right controller. The offset and tilt are
  first guesses and need checking in the headset.
- "Start with SteamVR": writes `heartbeatmonitor.vrmanifest` next to the exe, registers it, and
  enables auto-launch (opt-in, off by default).
- Packaging: `tools/publish.ps1` builds one self-contained exe (about 50 MB, openvr_api.dll
  bundled) and a zip; `.github/workflows/release.yml` tests every push and releases on `v*` tags.
- Smoke-tested the packaged exe against SteamVR in window and console mode (devices stream,
  the window and panel render, OSC arrives). Open question: SteamVR reported the Beyond as "not
  worn" while all devices agreed on about 68 BPM with strong peaks, so the proximity check may
  block real readings while sitting still.

**Movement handling (built 2026-11-19, after live testing felt slow and twitchy):**
- Moving moments (0.3 s motion measure + 1 s hangover) are cut out of the FFT window instead of
  discarding it. The window is the longest recent 6–12 s stretch with at most 25% cut out, so
  the number returns about 6–8 s after settling instead of 12–15 s.
- Each device is judged on its own: a moving device sits out and the still ones carry on. The
  user counts as moving (number "--", no beats) only when every heart-rate device is moving.
- Motion threshold raised from 5 to 8 deg/s (sweep over 5/8/12). Results: still sessions show a
  number 96–98% of the time (was 89–93%), small movements 44% (was 29%), median "--" gap 1–8 s
  (was about 10–15 s). Error against the chest reference stays at 0.4–0.9 BPM. Talking stays
  mostly "--" (15%).
- Not built: detecting talking from the headset IMU; predictive beat timing (declined).

## 3. Signal-processing pipeline (initial design, refined in Phase 2)

```
capture (dedicated thread, per device, high-res timestamps)
  → resample to a uniform grid (e.g. 250 Hz)
  → channels: gyro xyz (+ accel xyz) from raw IMU, or from pose angular/linear velocity
  → detrend, band-pass ~0.7–10 Hz (head) — remove breathing and drift
  → axis fusion per window (PCA / SNR-weighted); head pitch is expected to dominate
  → spectrum: 8–12 s Hann window, 0.5–1 s hop, zero-padded, 40–200 BPM
  → harmonic-sum + peak interpolation
  → optional device fusion: SNR-weighted average with controller spectra when they agree
  → HR tracker (Kalman/Viterbi; limits on BPM change per second)
  → confidence (peak prominence, harmonic consistency, motion level)
  → stillness gate: motion energy from HMD + controllers → hold/decay, confidence ↓
```
Variants to compare in Phase 2: envelope-of-band spectrum (as used for SCG), autocorrelation
period estimation, and adaptive noise cancellation using the controllers as a motion reference.
Test **lying down** separately, since the pillow may damp head motion.

**Breathing (optional feature, needs a chest tracker):** take the tilt of the tracker's low-passed
accelerometer (gravity direction, PCA axis), band-pass it to 0.07–0.7 Hz, and derive the breath
phase (inhale/exhale), breaths per minute, depth, and breath holds (chest still for at least 5 s).
This worked clearly in the first recording and tolerates the tracker's wireless sample loss
because the signal is so slow. Send it over OSC too (e.g. `BreathRate`, a `BreathPhase` float
for chest animation, `isBreathHold`). The HMD's tilt did not follow breathing in that recording.
Breathing without a chest tracker (e.g. from beat-to-beat heart-rate changes) is a stretch goal.

**Microphone breathing (to explore later):** breath sounds are broadband noise (about
100–2000 Hz) close to the headset mic. Track that band's loudness, skip stretches with speech,
and look for the 0.1–0.7 Hz rhythm: that gives rate and holds (silence), often inhale/exhale too.
- Problems: quiet nose breathing, talking in VRChat, fans and game audio, noise suppression
  (NVIDIA Broadcast, Krisp, Windows voice processing) removing breath sounds, and which mic the
  setup has. Read the raw device input in WASAPI shared mode (NAudio).
- Privacy: opt-in only, processed on the PC, audio never stored or sent; say so in the README.
- Test: an optional Probe channel that stores only a loudness envelope (about 50 Hz, never
  audio), recorded alongside the chest tracker as reference, during nose breathing, mouth
  breathing and talking.
- If it works: the chest tracker stays the main breathing source, the mic is a fallback, and
  both are combined when present.

**Per-beat events** (avatar heart pulses): try to detect beats in the head signal, constrained to
the FFT rate. If that's unreliable, use a synthetic beat locked to the FFT rate. That is very
likely for the HMD.

## 4. Ground truth (no reference monitor yet)

- **Chest device as reference:** record a chest device together with the HMD and controllers.
  Detect beats in the chest signal, where they're clearly visible, and use them as heart-rate
  labels for the head and controller data.
- **Manual spot checks:** count the wrist pulse for 30 s at the start and end of each recording.
- **Recommended later:** a **Polar H10** or any BLE heart-rate strap. C# can read it natively
  through the WinRT Bluetooth APIs, and it gives beat-accurate intervals for final validation.

Recording protocol per setup (2–3 min each, with the chest reference on): seated still → standing
idle → lying down → talking → small head movements and looking around → after exercise (raised
HR).

Metrics, **per device** (HMD alone, HMD + controllers): MAE (BPM), % within ±5 BPM, **coverage**
(% of still time with a confident estimate), and time to lock after motion stops.

## 5. Architecture (C# / .NET 10)

```
VirtualHeartBeatMonitor.sln
  src/HeartBeat.Core/      DSP pipeline, HR tracker, confidence, stillness gate (no OpenVR dependency)
  src/HeartBeat.OpenVR/    openvr_api.cs (official SDK binding), device enumeration,
                           fixed-rate pose poller, IVRIOBuffer IMU reader
  src/HeartBeat.App/       the exe: settings, recorder, VRChat OSC, UI, SteamVR overlay
  tools/Probe/             Phase 0 console app (data-access check)
  tools/Replay/            runs Core over recordings, labels from the chest device, prints metrics
  tests/HeartBeat.Core.Tests/
```
- **Dependencies (minimal):** OpenVR SDK binding + `openvr_api.dll`, FftSharp (or a small
  built-in FFT), a hand-written UDP OSC sender.
- **Recording format:** timestamped samples per device + metadata JSON, replayable through the
  same `Core` code. Optional Python/Jupyter for exploratory plots only, never shipped.
- **Runs as an OpenVR background/overlay app** next to VRChat, with a `.vrmanifest` for
  auto-start with SteamVR.
- **VRChat OSC:** `127.0.0.1:9000`, `/avatar/parameters/...`. Use the parameter names that
  existing HR avatar prefabs already use (`HR`, `onesHR`/`tensHR`/`hundredsHR`, `HRPercent`,
  `isHRConnected`, `isHRBeat`), plus `isHRConfident` and optional `/chatbox/input`. Rate-limited.
- **UI:** small desktop window (BPM, confidence, stillness indicator, live spectrum). Later, an
  in-VR overlay.
- **Distribution:** GitHub Releases with a framework-dependent single-file exe and a
  self-contained build, both built by GitHub Actions on tag.

## 6. Phases

| Phase | Deliverable | Exit criterion |
|---|---|---|
| **0. Data-access check** | `tools/Probe`: per device reports distinct pose update rate, raw IMU availability and rate, and a live readout of HMD rotation rate. Run on both setups. | Known best signal source per setup |
| **1. Recorder** | Records all devices (raw IMU where available + poses) with metadata and manual pulse notes | Labelled recordings for the protocol, chest reference included |
| **2. Offline algorithm** | `Core` pipeline + `tools/Replay` evaluation, HMD-first | e.g. HMD MAE < 5 BPM when seated still; honest coverage numbers per posture |
| **3. Real-time engine** | Live streaming through the same `Core`; 1–2 Hz output; stillness gate | Live output matches replayed results |
| **4. Outputs** | VRChat OSC + desktop UI; then SteamVR overlay; `isHRBeat`; breathing outputs when a chest tracker is present | HR (and breathing) show on an avatar in VRChat |
| **5. Packaging** | SteamVR auto-start manifest, settings, GitHub Actions releases, README | Download, run, works |
| **6. Stretch** | Breathing without a chest tracker; Steam Frame tuning | n/a |

## 7. Risks

- **Head signal too weak** with fused poses (no raw IMU), especially on Steam Frame. Phase 0 and
  Phase 2 find this out early. Fallback: an optional chest-device mode.
- **Posture differences:** standing has more sway; lying down may damp head motion. Report
  coverage per posture.
- **Expected reliability:** usable only while still. The stillness gate and confidence value
  make that explicit instead of showing wrong numbers.
- **Self-labelling from the chest device** can hide errors. Get a BLE strap before final tuning.

## 2026-11-22: Steam Frame eye-camera PPG experiment (tools/EyePpg)

Idea: the Frame's IR eye-tracking cameras see skin around the eyes; camera PPG may give a pulse
that does not need stillness of the body the way BCG does. Prior work: egoPPG (ICCV 2025, Project
Aria eye cameras, ~7.7 BPM MAE with a neural net) and Eye-PPG (VR headset IR eye cameras).

- No official API for eye images (OpenXR/OpenVR give gaze only). Community FrameEyeCameraFeed
  borrows XRService's DMA-BUFs on the headset (root) and serves MJPEG per eye: 512x400 8-bit,
  ~60 Hz, JPEG quality configurable (default 80). Exposure/gain exist in the buffer but are not
  streamed. Babble Frame Runtime reads the same cameras on-headset.
- `EyePpg record` stores 8x8 block means (ushort, mean x64) per frame plus host arrival time, and
  the SteamVR IMU in Probe's CSV format (same folder, same clock).
- `EyePpg analyze`: 30 Hz resample, blink/gap/IMU-motion masking, 0.7–3.5 Hz band-pass per block;
  per-block "enrichment" at the reference rate vs decoy rates (honest chance check); reference-free
  HR from the most peaked blocks that agree on a rate, 12 s sliding FFT, compared to reference.
- Synthetic check: noise only -> "no pulse"; injected 0.3 % -> 0.7 BPM MAE; 0.05 % -> 0.8 BPM.
- Open: first real recording on the owner's Frame; JPEG quantization vs the ~0.1–1 % IR
  pulse amplitude. (Raw IMU over SteamVR: answered below, no.)
- Decision pending: if it works, same repo with a Frame target sharing HeartBeat.Core.
- Probe check on the Frame (SteamVR 2.19.4, tracking system `cv`, HMD model "Deckard MP",
  controllers "Roy_Left/Right"): **no raw IMU** (`/devices/cv/<serial>/imu` ->
  IOBuffer_PathDoesNotExist for all three). Pose velocities update ~287/s (HMD) and ~195/s
  (controllers). So v0.1.0 cannot measure on the Frame; a pose-based fallback is needed, and the
  eye experiment has no IMU reference or IMU motion mask: use a counted pulse / `--ref`.

### 2026-11-22: first Frame results (TEST 1, seated still 150 s, counted pulse 58 before / 64 after)

Recordings: `recordings/20261122_183622_eyes_test1-still` + `recordings/*_frame-poses-test1`
(an earlier take, `20261122_182738_*`, is spoiled by head movement: the start was not announced).

- **Pose-based HR works on the Frame.** HMD `vAngularVelocity` (~287 updates/s) fed to the
  unchanged engine as the gyro: shown ~100 % of the still time, prominence 8–53, steady 58–66,
  median 62.9 BPM (counted 58 -> 64). This is the path to Frame / inside-out support on PC.
- **Eye cameras: a real but weak pulse.** What worked: average the steady skin blocks into one ROI
  (block mean > 40 and raw relative std < 8 %: 644 / 525 blocks), mask frames whose ROI
  frame-difference is an outlier (median x (1 + 8 x 1.4826); 17–24 % cut), band-pass 0.7–3.5 Hz,
  12 s FFT. The tool's per-block "most peaked blocks" method did not work on real data (gaze
  movement and dark, noisy images: ROI noise 0.13–0.15 % in band, mean brightness ~37/255).
  Against the pose HR: eye 1 power at the reference 4.0x vs decoys 1.4x, spectral peak within
  5 BPM in 62 % of windows (chance ~7 %), MAE 6.3; eye 0 weaker (2.0x vs 1.6x, 20 %). Injected
  pulses on the same data: 0.15 % -> ~7.6x / 88 %, so the real pulse is roughly 0.1 % of skin
  brightness. A recurring ~48–50 BPM peak in both eyes is an artefact of unknown origin.
- Stream was capped at 30 fps with JPEG q95; 60 fps or raw frames on the headset would lower noise.
- Diagnostic scripts used: scratch file-based C# apps (ROI + pose engine); the ROI method still
  needs porting into `EyePpg analyze`.

### 2026-11-22: pose fallback in the app (Frame live test, TEST 2)

- `LiveSession` uses pose angular velocity for devices without raw IMU (details in
  steamvr-and-app.md); `Recording.Load` reads `poses_*.csv` when there is no `imu_*.csv`
  (`RecordedDevice.FromPose`), so Replay and diagnostics zips work for Frame users.
- Replay of TEST 1 poses: HMD alone shown 96 %, median 62.8; with the (resting) controllers fused
  85 % -> pose-based controllers default to off.
- `HeartRateFusion` release rule added (3 confident updates > 12 BPM from the shown value -> jump
  to the raw peak): synthetic pose-like 58 BPM locked on 116 without it. Replay of all 11
  recordings: identical results before/after.
- Live (dev build, `--no-osc --diagnostics`, Frame worn, seated): after settling, shown
  continuously 59–63 BPM, HMD prominence 27–173; capture shown 68 % incl. the start movement.
  A resting Frame controller also tracked 55–62 BPM (prominence 8–15), worth revisiting.
