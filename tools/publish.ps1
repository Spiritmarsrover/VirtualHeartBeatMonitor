<#
  Builds the release package: one self-contained HeartBeatMonitor.exe (no .NET install needed)
  plus README, zipped into artifacts/.
      powershell -ExecutionPolicy Bypass -File tools/publish.ps1
#>
param([string] $Version = "")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if ($Version -eq "") {
    $Version = ([xml](Get-Content src/HeartBeat.App/HeartBeat.App.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}

dotnet test tests/HeartBeat.Core.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

$out = "artifacts/publish"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet publish src/HeartBeat.App/HeartBeat.App.csproj -c Release -p:PublishSingleFile=true -p:DebugType=none -p:Version=$Version -o $out
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Copy-Item README.md, LICENSE -Destination $out
Copy-Item lib/openvr/LICENSE -Destination "$out/OpenVR-LICENSE.txt"

$zip = "artifacts/VirtualHeartBeatMonitor-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out/*" -DestinationPath $zip
Write-Host "Package: $zip"
