# Builds windows\build\Rewind.exe for this PC. Needs the .NET 10 SDK.
#   .\build.ps1             build
#   .\build.ps1 -SelfTest   build, then run the capture → encode → replay → MP4 self-test
param([switch]$SelfTest)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$rid = if ($arch -eq [System.Runtime.InteropServices.Architecture]::Arm64) { 'win-arm64' } else { 'win-x64' }

dotnet publish Rewind.csproj -c Release -r $rid --self-contained false -o build -p:DebugType=embedded -nologo
if ($LASTEXITCODE) { exit $LASTEXITCODE }
Write-Host "built build\Rewind.exe ($rid)"

if ($SelfTest) {
    & .\build\Rewind.exe --selftest (Join-Path $env:TEMP 'rewind-selftest') | Out-Host
    exit $LASTEXITCODE
}
