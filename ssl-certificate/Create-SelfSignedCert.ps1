# ==============================================================================
# Generate Self-Signed Authenticode Code Signing Certificate for KeyPulse
# ==============================================================================
param(
    [string]$CertPassword = $(if ($env:KEYPULSE_CERT_PASSWORD) { $env:KEYPULSE_CERT_PASSWORD } else { [System.Guid]::NewGuid().ToString('N') }),
    [int]$ValidYears = 5
)

$ErrorActionPreference = 'Stop'
$certDir = $PSScriptRoot
if (-not (Test-Path -LiteralPath $certDir)) {
    New-Item -ItemType Directory -Path $certDir -Force | Out-Null
}

$pfxPath = Join-Path $certDir "KeyPulse_CodeSigning.pfx"
$cerPath = Join-Path $certDir "KeyPulse_CodeSigning.cer"

Write-Host "[CERT] Generating self-signed Authenticode Code Signing Certificate..."
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=KeyPulse, O=KeyPulse Development, OU=Release Signing" `
    -KeyUsage DigitalSignature `
    -KeySpec Signature `
    -KeyLength 2048 `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($ValidYears) `
    -CertStoreLocation "Cert:\CurrentUser\My"

$securePassword = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText

Write-Host "[CERT] Exporting PFX to $pfxPath..."
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null

Write-Host "[CERT] Exporting public CER to $cerPath..."
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null

Write-Host "[CERT] Certificate Thumbprint: $($cert.Thumbprint)"
Write-Host "[CERT] Certificate generation completed successfully."
