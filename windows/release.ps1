# Builds a self-contained, single-file Rewind.exe for x64 and ARM64 (no .NET install needed to run it),
# zips each into dist\, and builds an installer for each if Inno Setup is installed
# (winget install JRSoftware.InnoSetup).  Usage: .\release.ps1 1.3.0
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
          "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { Write-Warning 'Inno Setup not found, so no installers will be built (winget install JRSoftware.InnoSetup).' }

New-Item -ItemType Directory -Force dist | Out-Null
foreach ($arch in 'x64', 'arm64') {
    $out = Join-Path 'dist' "win-$arch"
    dotnet publish Rewind.csproj -c Release -r "win-$arch" --self-contained true -o $out -nologo `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:Version=$Version
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    $zip = Join-Path 'dist' "Rewind-$Version-Windows-$arch.zip"
    Compress-Archive -Path (Join-Path $out 'Rewind.exe'), '..\LICENSE' -DestinationPath $zip -Force
    Write-Host "wrote $zip"

    if ($iscc) {
        & $iscc /Q "/DAppVersion=$Version" "/DArch=$arch" "/DBinDir=$((Resolve-Path $out).Path)" "/O$((Resolve-Path dist).Path)" installer\Rewind.iss
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
        Write-Host "wrote dist\Rewind-$Version-Windows-$arch-Setup.exe"
    }
}
