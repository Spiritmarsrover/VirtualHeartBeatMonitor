# EyePpg (experiment)

Can the Steam Frame's eye-tracking cameras see your pulse? Skin brightness under infrared light
changes very slightly with each heartbeat (camera PPG). This tool records both eye cameras and
checks whether that change is visible.

Valve has no API for the eye-camera images. This uses the community tool
[FrameEyeCameraFeed](https://github.com/Curtis-VL/FrameEyeCameraFeed), which runs on the headset
and serves each eye as an MJPEG stream (512x400 greyscale, ~60 fps). It is unofficial, runs as
root on the headset, and a SteamOS update can break it.

## 1. On the headset (once)

Set a user password in the headset's developer settings, then in Konsole or over SSH:

```bash
curl -fsSL https://github.com/Curtis-VL/FrameEyeCameraFeed/releases/latest/download/install.sh | sudo ARGS="--quality 95" bash
```

`--quality 95` keeps more of the faint brightness changes than the default 80 (default 60 fps;
`--fps 30` halves the Wi-Fi load). Check it at `http://<headset-ip>:8090` — the
pictures only appear while the headset is worn.

## 2. On the PC

```powershell
dotnet build tools/EyePpg -c Release
$eye = "tools/EyePpg/bin/Release/net10.0-windows/EyePpg.exe"
& $eye record --url http://<headset-ip>:8090 --seconds 120 --label still
& $eye analyze recordings/<folder>
```

- `record` saves `eye_0.bin`, `eye_1.bin` (8x8 block means, ~70 MB per eye for 2 min), a few
  snapshot JPEGs and, if SteamVR is running, the headset and controller motion: raw IMU where
  available, otherwise poses (Steam Frame), in Probe's format and on the same clock as the eyes.
  It asks for a counted pulse before and after (count 30 s at the wrist or neck, x2).
- `analyze` writes `eyeppg_report.txt`, `eyeppg_timeline.csv` and plots:
  - `eyeppg_map_eye<n>.png`: power at the reference heart rate per 8x8 block, skin region outlined;
  - `eyeppg_spectrum.png`, `eyeppg_timeline.png`, `eyeppg_waveform.png`.
- `analyze --poses <Probe folder>` takes the reference from a separate Probe recording (older
  recordings), aligned by start time.
- `analyze --inject 70` adds a fake 0.3 % pulse: checks that the analysis finds a pulse known to
  be there, on your real camera noise. `--inject 70:0.0015` = 0.15 %. `--ref 80` with a wrong
  rate is the opposite check (should find nothing).

## Method

1. Skin region per eye: 8x8 blocks that are lit (mean > 40) and steady (brightness varies < 8 %
   over the recording): eyelids and cheek, not the moving eye. Averaged into one signal at 30 Hz.
2. Cut out: jumps in that signal (blinks, squints), gaps of 100 ms or more, head movement.
3. Band-pass 0.7–3.5 Hz; 12 s FFT once per second. Each eye's own peak is the heart rate (found
   without the reference).
4. Scoring against the reference (headset motion through the app's engine, else `--ref`, else
   the counted pulse): power at the reference rate vs at decoy rates (1x = noise), and how often
   the eye's peak is within 5 BPM (chance ~7 %). Verdict: "PULSE FOUND" at >= 2x the decoys and
   >= 50 %; "weak pulse" at >= 1.4x or >= 25 %.

## Results so far (2026-11-22, 30 fps, JPEG 95)

TEST 1, seated still: eye 1 pulse found (4.7x vs decoys 1.7x, within 5 BPM 51 %); eye 0 weak;
skin-region noise ~0.16 %, real pulse about 0.1 % of skin brightness. Injected 0.15 %: found in
both eyes; wrong reference: nothing.
