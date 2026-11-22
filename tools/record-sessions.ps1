<#
  Guided recording sessions for Phase 2 evaluation.
  Run from the repository root with SteamVR running:
      powershell -ExecutionPolicy Bypass -File tools/record-sessions.ps1 -Headset beyond
  Pick sessions with -Only, e.g.  -Only upright-still,standing-still
  Each session asks for a counted wrist pulse before and after (count 30 s, double it).
#>
param(
    [ValidateSet("beyond", "frame")] [string] $Headset = "beyond",
    [string[]] $Only = @(),
    [int] $Seconds = 120
)

$probe = "tools/Probe/bin/Release/net10.0/Probe.exe"
if (-not (Test-Path $probe)) {
    dotnet build VirtualHeartBeatMonitor.slnx -c Release | Out-Null
}

$sessions = @(
    @{ Label = "upright-still";   Text = "Sit upright WITHOUT head support. Hands resting on your lap holding the controllers. Breathe normally, stay still." },
    @{ Label = "reclined-still";  Text = "Lean back with your head supported, arms on the armrests (same as the first recording). Breathe normally." },
    @{ Label = "standing-still";  Text = "Stand relaxed, arms hanging at your sides holding the controllers. Stay as still as is comfortable." },
    @{ Label = "talking";         Text = "Sit upright and talk continuously, as you would in VRChat (read something aloud, or describe your room)." },
    @{ Label = "small-moves";     Text = "Sit upright. Every 10-15 s look around or gesture briefly, then settle again." },
    @{ Label = "breathing-mix";   Text = "Reclined. ~30 s normal, ~30 s slow and deep, one ~10 s breath hold, then ~30 s fast breathing. Note the order if you change it." },
    @{ Label = "after-exercise";  Text = "FIRST do ~1 min of squats or jumping jacks (HMD off is fine), put everything back on, then sit reclined and still while your pulse comes down." }
)
if ($Only.Count -gt 0) { $sessions = $sessions | Where-Object { $Only -contains $_.Label } }

Write-Host ""
Write-Host "Before starting: chest tracker strapped to the middle of your chest (it is the reference)." -ForegroundColor Cyan
Write-Host "Headset: $Headset. Sessions: $($sessions.Count) x $Seconds s." -ForegroundColor Cyan

foreach ($s in $sessions) {
    Write-Host ""
    Write-Host "=== $($s.Label) ===" -ForegroundColor Yellow
    Write-Host $s.Text
    $answer = Read-Host "Press Enter when ready (s = skip, q = quit)"
    if ($answer -eq "q") { break }
    if ($answer -eq "s") { continue }
    & $probe record --seconds $Seconds --label "$Headset-$($s.Label)" --note "$Headset; $($s.Text)"
}
Write-Host ""
Write-Host "Done. Recordings are in the recordings folder." -ForegroundColor Cyan
