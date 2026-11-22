# VirtualHeartBeatMonitor

Shows your heart rate in VR, measured from the motion sensors in your headset and controllers.
No heart-rate strap needed. Sends it to VRChat over OSC.

## What it does

- Measures your heart rate while you sit or stand still (shows "--" while you move or talk).
- Sends the BPM and every heartbeat to VRChat avatar parameters (`HR`, `isHRBeat`, …).
- Shows it on your wrist and in a "Heart rate" tab in the SteamVR dashboard.
- Optional: vibrates your controllers on each beat, measures breathing with a chest tracker.

## Status

Early. Tested on Bigscreen Beyond + Index controllers + Vive trackers. Other headsets are
untested. If it doesn't work on yours, please send a diagnostics report (below).

## Install

1. Download the latest zip from Releases and unzip it anywhere.
2. Start SteamVR, then run `HeartBeatMonitor.exe`.
3. In VRChat, turn on OSC.
4. Sit still for about 10 seconds.

## Diagnostics report

Click **Create diagnostics report** (window, tray menu, or the SteamVR dashboard tab) and sit
still for one minute. A zip appears on your Desktop: send it to me. It contains your device
list, one minute of motion data and the app's logs. Serial numbers are masked.

## For developers and AI assistants

Everything technical is in [docs/for-ai](docs/for-ai/README.md).

## License

MIT, see [LICENSE](LICENSE). Uses the OpenVR SDK (BSD-3-Clause, Valve Corporation), included in `lib/openvr`.
