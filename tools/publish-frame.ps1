<#
  Builds the Steam Frame package: one self-contained linux-arm64 HeartBeatMonitor (no .NET needed on
  the headset; it uses SteamVR's own libopenvr_api.so) plus licences and install notes, as a .tar.gz
  in artifacts/.
      powershell -ExecutionPolicy Bypass -File tools/publish-frame.ps1 [-Version 0.3.0]
#>
param([string] $Version = "")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ($Version -eq "") {
    $Version = ([xml](Get-Content src/HeartBeat.Native/HeartBeat.Native.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

$out = "artifacts/frame/VirtualHeartBeatMonitor"
if (Test-Path "artifacts/frame") { Remove-Item -Recurse -Force "artifacts/frame" }
dotnet publish src/HeartBeat.Native/HeartBeat.Native.csproj -c Release -r linux-arm64 -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }
Remove-Item "$out/*.pdb", "$out/openvr_api.dll" -ErrorAction SilentlyContinue

Copy-Item LICENSE -Destination $out
@"
VirtualHeartBeatMonitor $Version for the Steam Frame (runs on the headset, for standalone use;
for PC VR use the Windows version).

Install (Desktop Mode > Konsole, or over SSH), with the .tar.gz in ~/Downloads:

    cd ~
    tar -xzf ~/Downloads/VirtualHeartBeatMonitor-$Version-steamframe-arm64.tar.gz
    chmod +x ~/VirtualHeartBeatMonitor/HeartBeatMonitor
    ~/VirtualHeartBeatMonitor/HeartBeatMonitor

With SteamVR running, the "Heart rate" tab appears in the SteamVR dashboard and the panel on your
left wrist. Turn on "Start with SteamVR" there so it starts by itself from then on.
Settings and app.log are kept next to the program. Diagnostics reports go to ~/Desktop.

Uses Noto Sans glyphs (SIL Open Font License, see NotoSans-OFL.txt).
"@ | Set-Content -Encoding utf8 "$out/INSTALL.txt"

$tar = "artifacts/VirtualHeartBeatMonitor-$Version-steamframe-arm64.tar.gz"
if (Test-Path $tar) { Remove-Item $tar }
tar -czf $tar -C artifacts/frame VirtualHeartBeatMonitor
if ($LASTEXITCODE -ne 0) { throw "tar failed" }
Write-Host "Package: $tar"
