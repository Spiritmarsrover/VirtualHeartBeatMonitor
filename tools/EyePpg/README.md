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
curl -fsSL https://github.com/Curtis-VL/FrameEyeCameraFeed/releases/latest/download/install.sh | sudo ARGS="--quality 95 --fps 30" bash
```

`--quality 95` keeps more of the faint brightness changes than the default 80; `--fps 30` halves
the Wi-Fi load (30 fps is plenty for a pulse). Check it at `http://<headset-ip>:8090` — the
pictures only appear while the headset is worn.

## 2. On the PC

```powershell
dotnet build tools/EyePpg -c Release
$eye = "tools/EyePpg/bin/Release/net10.0-windows/EyePpg.exe"
& $eye record --url http://<headset-ip>:8090 --seconds 120 --label still
& $eye analyze recordings/<folder>
```

- `record` saves `eye_0.bin`, `eye_1.bin` (8x8 block means, ~70 MB per eye for 2 min), a few
  snapshot JPEGs and, if SteamVR is running, the headset/controller IMU in Probe's format. It asks
  for a counted pulse before and after (count 30 s at the wrist or neck, x2).
- `analyze` writes `eyeppg_report.txt`, `eyeppg_timeline.csv` and plots:
  - `eyeppg_map_eye<n>.png` — which parts of the picture carry the reference heart rate;
  - `eyeppg_spectrum.png`, `eyeppg_timeline.png`, `eyeppg_waveform.png`.
- `analyze --inject 70` adds a fake 0.3 % pulse to the recording: checks the analysis can find
  a pulse that is known to be there, on your real camera noise. `--inject 70:0.001` = 0.1 %.

## How to read the result

The reference rate comes from the headset IMU (same engine as the app), else `--ref`, else the
counted pulse. "Enrichment" = how much of a block's power sits at the reference rate (and 2x)
compared to an even spread; 1x is noise. The same is measured at decoy rates near it. A real pulse
shows as a patch of skin blocks well above the decoys; the verdict line summarises that.
The heart rate timeline is computed without the reference, then compared to it.

Synthetic check (no pulse / 0.3 % / 0.05 % injected): "no pulse", 0.7 BPM, 0.8 BPM mean error.
