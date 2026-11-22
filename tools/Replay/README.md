# Replay (Phase 2)

Runs the `HeartBeat.Core` pipeline over a Probe recording, exactly as it would run live, and
scores it. The chest tracker (if present) serves as the heart-rate reference and the breathing
source; by default it is not fused into the heart rate.

```powershell
dotnet build VirtualHeartBeatMonitor.slnx -c Release
$replay = "tools/Replay/bin/Release/net10.0/Replay.exe"
& $replay recordings/<folder>                          # timeline + summary
& $replay recordings/<folder> --hr-devices HMD         # headset only
& $replay recordings/<folder> --csv                    # writes replay.csv and replay_breathing.csv
```

Unit tests: `dotnet test tests/HeartBeat.Core.Tests`
