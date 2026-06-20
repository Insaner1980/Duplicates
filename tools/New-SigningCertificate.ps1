param(
    [string]$Subject = "CN=Finnvek",
    [string]$OutputPath = ".\certs\Duplicates.cer"
)

$ErrorActionPreference = "Stop"

$directory = Split-Path -Parent $OutputPath
if ($directory) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyUsage DigitalSignature `
    -FriendlyName "Duplicates local MSIX signing"

Export-Certificate -Cert $certificate -FilePath $OutputPath | Out-Null

Write-Host "Certificate exported to $OutputPath"
Write-Host "Trust it with:"
Write-Host "Import-Certificate -FilePath `"$OutputPath`" -CertStoreLocation Cert:\CurrentUser\TrustedPeople"
