# ==============================================================================
# DEVDATA_01: Isolated Developer Sandbox State Manager for KeyPulse.
#
# STRICT ZERO CROSS-CONTAMINATION:
# - Lives 100% inside %LOCALAPPDATA%\KeyPulse_DEV (DevData = ...\data, KeepDir = ...\keep).
# - NEVER reads from or writes to the production %APPDATA%\KeyPulse or
#   %LOCALAPPDATA%\Programs\KeyPulse folders.
# - Preserves developer sandbox state (config.json, desktop_organizer.json,
#   screen_brightness.json) across normal dev runs in KeepDir, while allowing
#   'dev fresh' to wipe the sandbox cleanly.
# ==============================================================================
param(
    [Parameter(Mandatory = $true)][string]$DevData,
    [Parameter(Mandatory = $true)][string]$KeepDir
)

$ErrorActionPreference = 'Stop'
$stateFiles = @('config.json', 'desktop_organizer.json', 'screen_brightness.json')

try {
    if (-not (Test-Path -LiteralPath $DevData)) {
        New-Item -ItemType Directory -Path $DevData -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $KeepDir)) {
        New-Item -ItemType Directory -Path $KeepDir -Force | Out-Null
    }

    foreach ($fileName in $stateFiles) {
        $devFile = Join-Path $DevData $fileName
        $keepFile = Join-Path $KeepDir $fileName

        if (Test-Path -LiteralPath $devFile) {
            Copy-Item -LiteralPath $devFile -Destination $keepFile -Force
        } elseif (Test-Path -LiteralPath $keepFile) {
            Copy-Item -LiteralPath $keepFile -Destination $devFile -Force
            Write-Host "[DEV] Restored sandbox $fileName from remembered dev keep folder."
        }
    }
}
catch {
    Write-Host "[DEV] WARNING: dev sandbox sync step skipped: $($_.Exception.Message)"
}
exit 0
