# Builds a self-contained, single-file Rewind.exe for x64 and ARM64 (no .NET install needed to run it)
# and zips each into dist\.  Usage: .\release.ps1 1.0.0
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

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
}
