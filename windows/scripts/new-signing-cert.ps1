# Makes the "Rewind Release" code-signing certificate that release.ps1 signs Rewind.exe and the installers with.
# Run it once, on the PC you build releases on. Keep a backup of the certificate (with its private key): copies of
# Rewind signed with it only accept updates signed with it, so a new certificate means users reinstall by hand once.
#   Back up:  Export-PfxCertificate -Cert Cert:\CurrentUser\My\<thumbprint> -FilePath RewindRelease.pfx -Password (Read-Host -AsSecureString)
param([string]$Name = 'Rewind Release')
$ErrorActionPreference = 'Stop'

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Where-Object { $_.Subject -eq "CN=$Name" }
if ($existing) { Write-Host "Already have `"$Name`": $($existing[0].Thumbprint)"; exit 0 }

$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=$Name" -CertStoreLocation Cert:\CurrentUser\My `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(20)
Write-Host "Made `"$Name`": $($cert.Thumbprint). Back it up (see the top of this script)."
