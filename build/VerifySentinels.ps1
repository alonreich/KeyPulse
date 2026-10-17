# ==============================================================================
# SYS-VERIFYTOOL: Pre-build fix sentinel verifier for KeyPulse.
# Parses build/sentinels.txt, rejects malformed lines, and verifies every tag
# exists in its target file. Exit 0 = all present, Exit 1 = halt build.
# ==============================================================================
param(
    [string]$RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$sentinelRelPath = 'build/sentinels.txt'
$sentinelFullPath = Join-Path $RepoRoot ($sentinelRelPath -replace '/', [System.IO.Path]::DirectorySeparatorChar)

if (-not (Test-Path -LiteralPath $sentinelFullPath)) {
    [Console]::Error.WriteLine("FIX SENTINEL CHECK FAILED: could not find $sentinelRelPath at '$sentinelFullPath'.")
    exit 1
}

$lines = [System.IO.File]::ReadAllLines($sentinelFullPath)
$malformed = [System.Collections.Generic.List[string]]::new()
$sentinels = [System.Collections.Generic.List[pscustomobject]]::new()
$seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

$lineNumber = 0
foreach ($raw in $lines) {
    $lineNumber++
    $line = $raw.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }

    $eq = $line.IndexOf('=')
    if ($eq -le 0 -or $eq -eq ($line.Length - 1)) {
        $malformed.Add("line ${lineNumber}: $line")
        continue
    }

    $tag = $line.Substring(0, $eq).Trim()
    $relPath = $line.Substring($eq + 1).Trim()

    if ($tag.Length -eq 0 -or $relPath.Length -eq 0 -or $tag -match '\s') {
        $malformed.Add("line ${lineNumber}: $line")
        continue
    }

    if ($relPath.Contains('\')) {
        $malformed.Add("line ${lineNumber}: use forward slashes in paths - $line")
        continue
    }

    $key = "$tag`0$relPath"
    if (-not $seen.Add($key)) { continue }

    $sentinels.Add([pscustomobject]@{
        Tag        = $tag
        RelPath    = $relPath
        LineNumber = $lineNumber
    })
}

$cache = @{}
$failures = [System.Collections.Generic.List[pscustomobject]]::new()

foreach ($s in $sentinels) {
    if (-not $cache.ContainsKey($s.RelPath)) {
        $targetFull = Join-Path $RepoRoot ($s.RelPath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (Test-Path -LiteralPath $targetFull) {
            $cache[$s.RelPath] = [System.IO.File]::ReadAllText($targetFull)
        } else {
            $cache[$s.RelPath] = $null
        }
    }

    $content = $cache[$s.RelPath]
    if ($null -eq $content) {
        $failures.Add([pscustomobject]@{
            Sentinel = $s
            Reason   = 'file does not exist'
        })
        continue
    }

    if ($content.IndexOf($s.Tag, [System.StringComparison]::Ordinal) -lt 0) {
        $failures.Add([pscustomobject]@{
            Sentinel = $s
            Reason   = 'tag not found in file'
        })
    }
}

if ($malformed.Count -eq 0 -and $failures.Count -eq 0) {
    Write-Host "Fix sentinels: $($sentinels.Count) checked, all present."
    exit 0
}

[Console]::Error.WriteLine("FIX SENTINEL CHECK FAILED.")
[Console]::Error.WriteLine("")

if ($malformed.Count -gt 0) {
    [Console]::Error.WriteLine("  $sentinelRelPath has $($malformed.Count) malformed line(s):")
    foreach ($m in $malformed) {
        [Console]::Error.WriteLine("    $m")
    }
    [Console]::Error.WriteLine("")
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("  $($failures.Count) sentinel(s) did not resolve:")
    foreach ($f in $failures) {
        $padTag = $f.Sentinel.Tag.PadRight(28)
        [Console]::Error.WriteLine("    $padTag $($f.Sentinel.RelPath)   [$($f.Reason)]  (${sentinelRelPath}:$($f.Sentinel.LineNumber))")
    }
    [Console]::Error.WriteLine("")
    [Console]::Error.WriteLine("  Either the fix was REVERTED - restore it - or replace the line with '# RETIRED <TAG> - <why>'.")
}

exit 1
