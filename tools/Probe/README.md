# Probe (Phase 0)

Checks what tracking data SteamVR exposes on a given setup, and records raw data for analysis.
SteamVR must already be running; Probe connects as a background app and never starts it.

```powershell
dotnet build VirtualHeartBeatMonitor.slnx -c Release
$probe = "tools/Probe/bin/Release/net10.0/Probe.exe"
```

1. **Check** (run once per setup, devices on and still): writes `probe-reports/probe_*.txt`
   ```powershell
   & $probe check
   & $probe check --hz 2000
   ```
2. **Live** readout (Ctrl+C to stop): `& $probe live`
3. **Record** a session, e.g. seated still with HMD + controllers (+ chest tracker as reference):
   ```powershell
   & $probe record --seconds 120 --label seated-still --note "beyond, tracker on chest"
   ```
   Output: `recordings/<time>_<label>/` with `poses_*.csv`, `imu_*.csv` (if raw IMU available)
   and `session.json`.
