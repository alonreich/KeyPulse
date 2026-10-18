# ==============================================================================
# Install KeyPulse Code Signing Certificate to Local Trusted Store
# ==============================================================================
param(
    [switch]$LocalMachine
)

$ErrorActionPreference = 'Stop'
$certDir = $PSScriptRoot
$cerPath = Join-Path $certDir "KeyPulse_CodeSigning.cer"

if (-not (Test-Path -LiteralPath $cerPath)) {
    Write-Host "ERROR: Certificate file not found at $cerPath"
    exit 1
}

$cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cerPath)

$storeScope = if ($LocalMachine) { "LocalMachine" } else { "CurrentUser" }

# Import to Root
$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", $storeScope)
$rootStore.Open("ReadWrite")
$rootStore.Add($cert)
$rootStore.Close()
Write-Host "[CERT] Added certificate to $storeScope\Root store."

# Import to TrustedPublisher
$pubStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPublisher", $storeScope)
$pubStore.Open("ReadWrite")
$pubStore.Add($cert)
$pubStore.Close()
Write-Host "[CERT] Added certificate to $storeScope\TrustedPublisher store."

Write-Host "[CERT] Certificate installation complete."
