# Signal processing (`src/HeartBeat.Core`)

All numbers below are current defaults. "Measured" results come from the owner's Beyond
recordings, scored with `tools/Replay` against a chest tracker and hand-counted pulses (details
in project-log.md).

## Input conditioning

- `UniformResampler`: irregular device-clock samples → a global 200 Hz grid (t = k/200, so all
  devices line up). The native rate is detected from the first 32 samples, with a 4th-order
  anti-alias low-pass at 0.4 × min(rates). Gaps up to 0.25 s are bridged linearly; longer gaps
  restart the grid.
- Raw IMU contains a constant gyro bias, so anything measuring motion high-passes first.

## Heart rate (the number)

1. `VibrationEnvelope`: gyro x/y/z band-passed 4–20 Hz (4th order), vector magnitude, band-passed
   0.5–5 Hz, decimated to 50 Hz. One bump per beat.
2. `MotionGate`: gyro band-passed 0.1–4 Hz, 0.3 s running RMS (deg/s). Above
   `MotionThresholdDps` = 8 (or within a 1 s hangover) the device counts as moving, and its
   envelope samples are masked.
3. `SpectralHeartRate`: picks the longest recent window of 6–12 s in which at most 25% of samples
   are masked. Masked samples are replaced with the window mean. Then Hann window, zero-padded
   FFT (×8), harmonic sum f + 0.6·2f + 0.3·3f on a 40–180 BPM grid (0.5 BPM steps), normalised
   by the median. "Prominence" is peak ÷ median (noise is about 3–5).
4. `HeartRateFusion`: weighted average of the devices' normalised spectra. Weight =
   (prominence − 2) × position factor (chest 3, head 2, hand 1, other 0.5). Then:
   - a soft continuity prior around the last confident value (σ 12 BPM, floor 0.35);
   - rejecting peaks within 2 BPM of the grid edges;
   - confidence = clamp((prominence − 5) / (10 − 5));
   - EMA smoothing (α 0.35).
5. Display rule: show a number only if confidence ≥ 0.5 and the estimate is not held. Held means
   no usable spectrum this update (all devices moving, warming up, no clear peak). Otherwise "--".
6. A device sits out of fusion while it is moving, not receiving (no samples for 1 s), or switched
   off by the user. The user counts as moving only when every heart-rate device is moving.

Measured: 0.4–0.9 BPM mean difference from the chest reference while still. Head weight 2 scores
the same as equal weights; 3 hurt beat timing on one session.

## Beat events

1. `BeatEnvelope` (per device, always computed): gyro and accel band-passed 4–20 Hz, each
   normalised by its running variance, magnitude, 8 Hz low-pass (about 40 ms), ÷ running mean,
   100 Hz. Sits near 1 between beats.
2. `MonitorEngine.ProcessBeatFrames`: combines devices sample by sample on the shared 100 Hz grid,
   weighted like fusion. It waits up to 0.15 s for slower devices. The index uses
   `Math.Floor(t·rate + 1e-6)`; banker's rounding once caused every other slot to go missing.
3. `BeatDetector`, rate-locked:
   - Predicts the next beat at last + 60/BPM.
   - Looks in a window of ±25% of the period, ranking peaks by height × proximity weight
     (Gaussian σ 0.12 period, floor 0.8).
   - Decides at the predicted time, or earlier for a peak ≥ 2.5× background.
   - If nothing clear (> 1.15× background) shows up by 0.12 s after the prediction, it emits a
     predicted beat (`Measured = false`).
   - Lock-on takes the strongest peak over one full period, which must be ≥ 1.3× background.
   - Disabled (and unlocked) whenever the number shows "--".
4. The app setting `beats.sendPredicted` (default true) drops predicted beats before OSC,
   vibration and animation.

Measured: events at 56–70/min (matching BPM), 86–100% measured, a delay of about 90 ms from the
envelope peak, 56–66% within 120 ms of the chest tracker's own beats (a lower bound, because the
reference drops samples).

## Breathing (chest tracker only)

`BreathingEstimator`:

1. Accel low-passed at 1 Hz and decimated to 10 Hz; the gravity direction is a unit vector.
2. An EMA mean and covariance (τ 20 s) give the principal tilt axis (power iteration). Tilt is in
   degrees.
3. Breathing signal: tilt band-passed 0.07–0.7 Hz. Breaths are trough-to-peak turning points with
   hysteresis of 0.3 × the median recent depth. Rate = 60 / median trough interval over 30 s.
4. Hold: measured on the low-passed tilt without the high-pass, because the high-pass keeps
   drifting after the chest stops and hides holds. Movement over 1 s, smoothed with τ 0.5 s, below
   0.5 × its 60 s median counts as still; still for at least 5 s is a hold.
5. Valid only if the band-passed tilt std ≥ 0.3° (a resting tracker gives about 0.03°).

Measured: both deliberate holds found (6 s and 9 s), rates matched the visible breathing.

## Known limits / ideas not built

- Talking puts energy in the same band, so it shows "--". Detecting speech from the headset IMU
  (85–250 Hz at 1 kHz) is an unbuilt idea.
- Microphone-based breathing: idea only (see project-log.md).
- No pose-based fallback for devices without raw IMU (needed for headsets that don't expose
  IVRIOBuffer IMU).
