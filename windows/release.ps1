# Builds a self-contained, single-file Rewind.exe for x64 and ARM64 (no .NET install needed to run it),
# zips each into dist\, and builds an installer for each if Inno Setup is installed
# (winget install JRSoftware.InnoSetup).  Usage: .\release.ps1 2.3.0
# Rewind.exe and the installers are signed with the "Rewind Release" code-signing certificate from your
# certificate store (make one with scripts\new-signing-cert.ps1). In-app updates only install an installer
# signed with the same certificate as the copy that's running. -Unsigned skips signing.
param([Parameter(Mandatory = $true)][string]$Version, [string]$Certificate = 'Rewind Release', [switch]$Unsigned)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$cert = $null
if (-not $Unsigned) {
    $cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Thumbprint -eq $Certificate -or $_.Subject -eq "CN=$Certificate" } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { throw "Code-signing certificate `"$Certificate`" not found. Make one with scripts\new-signing-cert.ps1, or pass -Unsigned." }
}
function Sign([string]$File) {
    if (-not $cert) { return }
    $null = Set-AuthenticodeSignature -FilePath $File -Certificate $cert -HashAlgorithm SHA256
    # A self-signed certificate isn't trusted by Windows, so the status isn't "Valid"; the signer is what counts.
    if ((Get-AuthenticodeSignature -FilePath $File).SignerCertificate.Thumbprint -ne $cert.Thumbprint) { throw "Signing $File failed." }
    Write-Host "signed $File"
}

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
    Sign (Join-Path $out 'Rewind.exe')
    $zip = Join-Path 'dist' "Rewind-$Version-Windows-$arch.zip"
    Compress-Archive -Path (Join-Path $out 'Rewind.exe'), '..\LICENSE' -DestinationPath $zip -Force
    Write-Host "wrote $zip"

    if ($iscc) {
        & $iscc /Q "/DAppVersion=$Version" "/DArch=$arch" "/DBinDir=$((Resolve-Path $out).Path)" "/O$((Resolve-Path dist).Path)" installer\Rewind.iss
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
        Sign "dist\Rewind-$Version-Windows-$arch-Setup.exe"
        Write-Host "wrote dist\Rewind-$Version-Windows-$arch-Setup.exe"
    }
}
