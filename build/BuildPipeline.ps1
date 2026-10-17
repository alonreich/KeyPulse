# ==============================================================================
# KeyPulse Structured Build & Release Pipeline (Inspired by FvsBuild)
# Exit codes: 0 = OK, 1 = BUILD FAILED / REFUSED, 2 = PUBLISH INCOMPLETE
# ==============================================================================
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RawArgs
)

$ErrorActionPreference = 'Continue'
$env:DOTNET_CLI_FORCE_UTF8_ENCODING = 'false'
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$env:VSCONSOLEOUTPUT = '1'

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..') -ErrorAction Stop).Path
Set-Location -LiteralPath $repoRoot -ErrorAction Stop

$noPublish = $false
$devMode = $false
$beta = $false
$assumeYes = $false
$showHelp = $false

foreach ($arg in $RawArgs) {
    if ([string]::IsNullOrWhiteSpace($arg)) { continue }
    switch -Regex ($arg) {
        '^(?i)--no-publish$' { $noPublish = $true }
        '^(?i)--dev$'        { $devMode = $true }
        '^(?i)--beta$'       { $beta = $true }
        '^(?i)--yes$'        { $assumeYes = $true }
        '^(?i)(--help|-h)$'  { $showHelp = $true }
    }
}

if ($showHelp) {
    Write-Host "Usage: build.cmd [--no-publish] [--dev] [--beta] [--yes]"
    Write-Host "  (default)     official STABLE release: release gate, build, publish to GitHub as latest"
    Write-Host "  --beta        official BETA release: published as a pre-release"
    Write-Host "  --yes         skip the 'type PUBLISH' confirmation"
    Write-Host "  --no-publish  build only; GitHub and the repository's version files are not touched"
    Write-Host "  --dev         local dev build (dev_build.cmd): no GitHub, .\\compiled is not wiped first"
    exit 0
}

# Step 0: Pre-build Sentinel Verification (SYS-VERIFYTOOL)
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'build\VerifySentinels.ps1') -RepoRoot $repoRoot
if ($LASTEXITCODE -ne 0) {
    exit 1
}

$buildVersion = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmm', [System.Globalization.CultureInfo]::InvariantCulture)
$tag = "v$buildVersion"
$publish = (-not $noPublish) -and (-not $devMode)
$flavor = if ($devMode) { 'Local Dev (NativeAOT win-x64)' } else { 'NativeAOT win-x64' }

Write-Host "Build version: $buildVersion  (tag $tag)"

function Invoke-GitCapture {
    param([string[]]$GitArgs)
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $out = & git -c core.safecrlf=false @GitArgs 2>&1
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $prevEap
    }
    $lines = @()
    foreach ($item in $out) {
        $s = if ($item -is [System.Management.Automation.ErrorRecord]) {
            if ($null -ne $item.Exception -and $null -ne $item.Exception.Message) { $item.Exception.Message.TrimEnd() } else { $item.ToString().TrimEnd() }
        } else {
            $item.ToString().TrimEnd()
        }
        if ($s.Length -gt 0) { $lines += $s }
    }
    return [pscustomobject]@{
        Success = ($code -eq 0)
        Lines   = $lines
        Code    = $code
    }
}

function Undo-ReleasePlan {
    param($Plan)
    if ($null -eq $Plan) { return }
    $tagExists = Invoke-GitCapture -GitArgs @('rev-parse', '-q', '--verify', "refs/tags/$($Plan.Tag)")
    if ($tagExists.Success) {
        [void](Invoke-GitCapture -GitArgs @('tag', '-d', $Plan.Tag))
    }
    if ($Plan.Committed -and (-not [string]::IsNullOrWhiteSpace($Plan.BaseSha))) {
        $res = Invoke-GitCapture -GitArgs @('reset', '--mixed', '--quiet', $Plan.BaseSha)
        if ($res.Success) {
            $shortSha = $Plan.BaseSha.Substring(0, [Math]::Min(12, $Plan.BaseSha.Length))
            Write-Host "[RELEASE] Undone: commit rolled back to $shortSha (working tree changes preserved) and local tag $($Plan.Tag) removed."
        } else {
            Write-Host "WARNING: [RELEASE] Could not reset commit to $($Plan.BaseSha); check git status and git tag."
        }
    }
}

function Sync-VersionFiles {
    param([string]$Version)
    Write-Host "[VERSION] Synchronizing version '$Version' across project files..."
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)

    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'version.txt'), "$Version`r`n", $utf8NoBom)
    Write-Host "[VERSION] Updated version.txt"

    $dirProps = @"
<Project>
  <PropertyGroup>
    <Version Condition="'`$(Version)' == ''">$Version</Version>
    <AssemblyVersion>`$(Version)</AssemblyVersion>
    <FileVersion>`$(Version)</FileVersion>
    <InformationalVersion>`$(Version)</InformationalVersion>
    <ProductVersion>`$(Version)</ProductVersion>
  </PropertyGroup>
</Project>
"@
    [System.IO.File]::WriteAllText((Join-Path $repoRoot 'Directory.Build.props'), $dirProps, $utf8NoBom)
    Write-Host "[VERSION] Updated Directory.Build.props"

    $csprojPath = Join-Path $repoRoot 'KeyPulse.csproj'
    if (Test-Path -LiteralPath $csprojPath) {
        $csproj = [System.IO.File]::ReadAllText($csprojPath)
        $csproj = [regex]::Replace($csproj, '<Version[^>]*>.*?</Version>', "<Version Condition=`"'`$(Version)' == ''`">$Version</Version>")
        [System.IO.File]::WriteAllText($csprojPath, $csproj, $utf8NoBom)
        Write-Host "[VERSION] Updated KeyPulse.csproj"
    }

    $manifestPath = Join-Path $repoRoot 'app.manifest'
    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = [System.IO.File]::ReadAllText($manifestPath)
        $manifest = [regex]::Replace($manifest, '(<assemblyIdentity\s+version=")[^"]*(")', "`${1}$Version`${2}")
        [System.IO.File]::WriteAllText($manifestPath, $manifest, $utf8NoBom)
        Write-Host "[VERSION] Updated app.manifest"
    }
}

function Start-ReleasePreflight {
    param([string]$Version, [string]$Tag, [bool]$IsBeta, [bool]$AssumeYesFlag)
    Write-Host "[RELEASE] Checking that this release can be published..."
    $problems = [System.Collections.Generic.List[string]]::new()

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Write-Host "ERROR: [RELEASE] git is not on PATH."
        return $null
    }

    $status = Invoke-GitCapture -GitArgs @('status', '--porcelain', '--untracked-files=normal')
    if (-not $status.Success) {
        $problems.Add("'git status' failed - is this a git checkout?")
    } elseif ($status.Lines.Count -gt 0) {
        Write-Host "[RELEASE] Working tree has $($status.Lines.Count) modified/untracked file(s); they will be staged and committed automatically."
    }

    $headRes = Invoke-GitCapture -GitArgs @('symbolic-ref', '--short', 'HEAD')
    $branch = if ($headRes.Success -and $headRes.Lines.Count -eq 1) { $headRes.Lines[0] } else { '' }
    if ($branch.Length -eq 0) {
        $problems.Add("HEAD is detached. Check out a branch first.")
    }

    $originHeadRes = Invoke-GitCapture -GitArgs @('symbolic-ref', '--short', 'refs/remotes/origin/HEAD')
    $defaultBranch = if ($originHeadRes.Success -and $originHeadRes.Lines.Count -eq 1) {
        $originHeadRes.Lines[0] -replace '^origin/', ''
    } else {
        'main'
    }

    if ((-not $IsBeta) -and $branch.Length -gt 0 -and ($branch -ne $defaultBranch) -and ($branch -ne 'master')) {
        $problems.Add("A stable release is cut from '$defaultBranch' only; this is '$branch'. (Use --beta for a test build from another branch.)")
    }

    $baseSha = ''
    if ($branch.Length -gt 0) {
        $localRes = Invoke-GitCapture -GitArgs @('rev-parse', 'HEAD')
        if ($localRes.Success -and $localRes.Lines.Count -eq 1) {
            $baseSha = $localRes.Lines[0]
        }
        $fetchRes = Invoke-GitCapture -GitArgs @('fetch', '--quiet', 'origin', $branch)
        if (-not $fetchRes.Success) {
            $problems.Add("'git fetch origin $branch' failed, so remote state cannot be verified.")
        } else {
            $remoteRes = Invoke-GitCapture -GitArgs @('rev-parse', "origin/$branch")
            if ($remoteRes.Success -and $remoteRes.Lines.Count -eq 1) {
                $ancestorRes = Invoke-GitCapture -GitArgs @('merge-base', '--is-ancestor', "origin/$branch", 'HEAD')
                if (-not $ancestorRes.Success) {
                    $problems.Add("'origin/$branch' has commits not in local '$branch'. Pull/rebase first before releasing.")
                }
            }
        }
    }

    $localTagRes = Invoke-GitCapture -GitArgs @('rev-parse', '-q', '--verify', "refs/tags/$Tag")
    if ($localTagRes.Success) {
        $problems.Add("The tag $Tag already exists locally. Wait a minute and run again.")
    }

    $remoteTagRes = Invoke-GitCapture -GitArgs @('ls-remote', '--tags', 'origin', "refs/tags/$Tag")
    if ($remoteTagRes.Success -and $remoteTagRes.Lines.Count -gt 0) {
        $problems.Add("The tag $Tag already exists on GitHub. Wait a minute and run again.")
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        $problems.Add("The GitHub CLI (gh) is not installed or not on PATH.")
    } else {
        & gh auth status > $null 2>&1
        if ($LASTEXITCODE -ne 0) {
            $problems.Add("The GitHub CLI (gh) is not signed in ('gh auth login').")
        }
    }

    if ($problems.Count -gt 0) {
        Write-Host "ERROR: [RELEASE] The release was NOT started. Nothing was built, committed, tagged or published:"
        foreach ($p in $problems) {
            Write-Host "  - $p"
        }
        return $null
    }

    Write-Host "###########################################################"
    if ($IsBeta) {
        Write-Host "ABOUT TO PUBLISH BETA $Tag (pre-release)"
    } else {
        Write-Host "ABOUT TO PUBLISH $Tag TO GITHUB RELEASES"
    }
    $shortSha = $baseSha.Substring(0, [Math]::Min(12, $baseSha.Length))
    Write-Host "From '$branch' at $shortSha."
    Write-Host "###########################################################"

    return [pscustomobject]@{
        Version   = $Version
        Tag       = $Tag
        Beta      = $IsBeta
        Branch    = $branch
        BaseSha   = $baseSha
        Committed = $false
    }
}

function Commit-ReleaseVersion {
    param($Plan)
    Write-Host "[RELEASE] Staging all working tree changes and synchronized version files..."
    $addRes = Invoke-GitCapture -GitArgs @('add', '-A')
    if (-not $addRes.Success) {
        Write-Host "ERROR: [RELEASE] Could not stage working tree files for release commit."
        Undo-ReleasePlan -Plan $Plan
        return $false
    }

    $stagedCheck = Invoke-GitCapture -GitArgs @('diff', '--cached', '--quiet')
    if (-not $stagedCheck.Success) {
        $commitRes = Invoke-GitCapture -GitArgs @('commit', '--quiet', '-m', "release: $($Plan.Tag)", '-m', 'Automated release commit and version sync by KeyPulse build pipeline (RELGATE_01).')
        if (-not $commitRes.Success) {
            Write-Host "ERROR: [RELEASE] Could not create release commit."
            Undo-ReleasePlan -Plan $Plan
            return $false
        }
        $Plan.Committed = $true
    }

    $title = if ($Plan.Beta) { "KeyPulse $($Plan.Tag) (Beta)" } else { "KeyPulse $($Plan.Tag)" }
    $tagRes = Invoke-GitCapture -GitArgs @('tag', '-a', $Plan.Tag, '-m', $title)
    if (-not $tagRes.Success) {
        Write-Host "ERROR: [RELEASE] Could not create annotated release tag $($Plan.Tag)."
        Undo-ReleasePlan -Plan $Plan
        return $false
    }
    Write-Host "[RELEASE] Committed working tree + version files and tagged $($Plan.Tag)."
    return $true
}

# BUILDKILL_01: Stop ONLY processes whose executable lives inside this repository.
# Never kill the installed KeyPulse running from %LOCALAPPDATA%\Programs\KeyPulse.
function Stop-RepoProcesses {
    $normRoot = ([System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar).ToLowerInvariant()
    foreach ($proc in [System.Diagnostics.Process]::GetProcesses()) {
        try {
            if ($proc.Id -eq $PID) { continue }
            $exe = $null
            try { $exe = $proc.MainModule.FileName } catch { continue }
            if ([string]::IsNullOrWhiteSpace($exe)) { continue }
            if ($exe.ToLowerInvariant().StartsWith($normRoot)) {
                Write-Host "  -> Stopping $([System.IO.Path]::GetFileName($exe)) (PID $($proc.Id)): runs from this repository."
                try {
                    $proc.Kill()
                    [void]$proc.WaitForExit(5000)
                } catch { }
            }
        }
        finally {
            $proc.Dispose()
        }
    }
    & dotnet build-server shutdown > $null 2>&1
}

function Initialize-CompiledDirectory {
    param([bool]$IsDevMode)
    $outDir = Join-Path $repoRoot 'compiled'
    if (-not (Test-Path -LiteralPath $outDir)) {
        New-Item -ItemType Directory -Path $outDir -Force | Out-Null
    }
    foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($outDir)) {
        $name = [System.IO.Path]::GetFileName($entry)
        if ($name -notmatch '^(?i)KeyPulse\.exe$') {
            try { Remove-Item -LiteralPath $entry -Recurse -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
    if (-not $IsDevMode) {
        $exe = Join-Path $outDir 'KeyPulse.exe'
        if (Test-Path -LiteralPath $exe) {
            try { Remove-Item -LiteralPath $exe -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

function Clear-BuildArtifacts {
    foreach ($dir in @('bin', 'obj')) {
        $target = Join-Path $repoRoot $dir
        if (Test-Path -LiteralPath $target) {
            try { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
    & dotnet clean "KeyPulse.csproj" -c Release -r win-x64 --nologo -v q -nodeReuse:false > $null 2>&1
}

function Initialize-MsvcToolchain {
    if (Get-Command link.exe -ErrorAction SilentlyContinue) {
        Write-Host "Native AOT toolchain detected."
        return $true
    }

    $vswhere = $null
    if (Get-Command vswhere.exe -ErrorAction SilentlyContinue) {
        $vswhere = 'vswhere.exe'
    } else {
        $defaultVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $defaultVsWhere) {
            $vswhere = $defaultVsWhere
        }
    }

    if (-not $vswhere) {
        Write-Host "ERROR: Native AOT platform linker (link.exe) not found in PATH."
        Write-Host "ERROR: Open a Developer Command Prompt or install Visual Studio C++ build tools."
        return $false
    }

    $installPaths = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null
    foreach ($inst in $installPaths) {
        if ([string]::IsNullOrWhiteSpace($inst)) { continue }
        $vsDevCmd = Join-Path $inst.Trim() 'Common7\Tools\VsDevCmd.bat'
        if (Test-Path -LiteralPath $vsDevCmd) {
            $envLines = & cmd.exe /d /c "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set"
            foreach ($line in $envLines) {
                $eq = $line.IndexOf('=')
                if ($eq -gt 0) {
                    $k = $line.Substring(0, $eq)
                    $v = $line.Substring($eq + 1)
                    [System.Environment]::SetEnvironmentVariable($k, $v, [System.EnvironmentVariableTarget]::Process)
                }
            }
            if (Get-Command link.exe -ErrorAction SilentlyContinue) {
                Write-Host "Native AOT toolchain detected."
                return $true
            }
        }
    }

    Write-Host "ERROR: Native AOT platform linker (link.exe) not found."
    Write-Host "ERROR: Open a Developer Command Prompt or install Visual Studio C++ build tools."
    return $false
}

# ILCCRASH_01: NativeAOT publish with automatic single-threaded retry if .NET 9 ILC scanner hits internal race.
function Invoke-NativeAotPublish {
    param([string]$Version, [string]$FinalDir, [bool]$SingleThreaded)
    $pubArgs = @(
        'publish', 'KeyPulse.csproj',
        '-c', 'Release',
        '-r', 'win-x64',
        '-p:TreatWarningsAsErrors=true',
        '-p:PublishAot=true',
        '-p:SelfContained=true',
        "-p:Version=$Version",
        "-p:AssemblyVersion=$Version",
        "-p:FileVersion=$Version",
        "-p:InformationalVersion=$Version",
        '-nodeReuse:false',
        '-p:UseSharedCompilation=false',
        '-o', $FinalDir,
        '-v', 'm'
    )
    if ($SingleThreaded) {
        $pubArgs += '-p:IlcSingleThreaded=true'
    }

    $ilcCrashed = $false
    $markers = @(
        'ILCompiler.NativeAotFatalErrorException',
        'ILCompiler.CodeGenerationFailedException',
        'error IL1013'
    )

    & dotnet @pubArgs 2>&1 | ForEach-Object {
        $line = $_.ToString()
        Write-Host $line
        foreach ($m in $markers) {
            if ($line.IndexOf($m, [System.StringComparison]::Ordinal) -ge 0) {
                $ilcCrashed = $true
            }
        }
    }
    $code = $LASTEXITCODE
    return [pscustomobject]@{
        Success    = ($code -eq 0)
        IlcCrashed = $ilcCrashed
    }
}

function Invoke-NativeAotBuildLadder {
    param([string]$Version)
    $stagingDir = Join-Path $repoRoot 'obj\StandaloneTemp\Staging'
    $finalDir = Join-Path $repoRoot 'obj\StandaloneTemp\NativeAot_final'
    $outputDir = Join-Path $repoRoot 'compiled'
    $outputExe = Join-Path $outputDir 'KeyPulse.exe'

    Write-Host "[NativeAOT] 1. Purging old temp folders..."
    if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $finalDir) { Remove-Item -LiteralPath $finalDir -Recurse -Force -ErrorAction SilentlyContinue }

    Write-Host "[NativeAOT] 2. Publishing standalone NativeAOT binary..."
    $res = Invoke-NativeAotPublish -Version $Version -FinalDir $finalDir -SingleThreaded $false
    if (-not $res.Success) {
        if ($res.IlcCrashed) {
            Write-Host "WARNING: [NativeAOT] ILC crashed internally (known .NET 9 parallel scanner race, dotnet/runtime#108743)."
            Write-Host "WARNING: [NativeAOT] Retrying publish once with single-threaded ILC scanner (-p:IlcSingleThreaded=true)..."
            $res = Invoke-NativeAotPublish -Version $Version -FinalDir $finalDir -SingleThreaded $true
            if (-not $res.Success) { return $false }
        } else {
            return $false
        }
    }

    Write-Host "[NativeAOT] 3. Moving final EXE to compiled folder..."
    $builtExe = Join-Path $finalDir 'KeyPulse.exe'
    if (-not (Test-Path -LiteralPath $builtExe)) {
        Write-Host "ERROR: Expected NativeAOT EXE was not produced in $finalDir"
        return $false
    }

    if (-not (Test-Path -LiteralPath $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }
    Move-Item -LiteralPath $builtExe -Destination $outputExe -Force

    Write-Host "[NativeAOT] 4. Cleaning up temporary artifacts..."
    if (Test-Path -LiteralPath $stagingDir) { Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $finalDir) { Remove-Item -LiteralPath $finalDir -Recurse -Force -ErrorAction SilentlyContinue }

    return $true
}

function Test-CompiledOutput {
    $outputDir = Join-Path $repoRoot 'compiled'
    $outputExe = Join-Path $outputDir 'KeyPulse.exe'
    $invalid = -not (Test-Path -LiteralPath $outputExe)

    if (Test-Path -LiteralPath $outputDir) {
        foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($outputDir)) {
            $name = [System.IO.Path]::GetFileName($entry)
            if ($name -notmatch '^(?i)KeyPulse\.exe$') {
                Write-Host "ERROR: Removing disallowed artifact from compiled: $name"
                try { Remove-Item -LiteralPath $entry -Recurse -Force -ErrorAction SilentlyContinue } catch { }
                $invalid = $true
            }
        }
    }

    if ($invalid) {
        Write-Host "ERROR: .\compiled must contain only KeyPulse.exe."
        return $false
    }

    Write-Host "Verified .\compiled contains exactly KeyPulse.exe."
    return $true
}

# RELTAG_01 / RETAIN_01: Draft-first GitHub release publisher with SHA-256 verification and 5-release retention keeping git tags.
function Publish-GitHubRelease {
    param($Plan, [string]$ExePath)
    $stableReleasesKept = 5
    $betaReleasesKept = 3

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Host "[PUBLISH] STOPPED: GitHub CLI (gh) is not installed or not on PATH."
        return $false
    }
    Write-Host "[PUBLISH] 1/7 GitHub CLI found.                                    [OK]"

    & gh auth status > $null 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[PUBLISH] STOPPED: gh is installed but not signed in."
        return $false
    }
    Write-Host "[PUBLISH] 2/7 GitHub sign-in valid.                                [OK]"

    $repo = (& gh repo view --json nameWithOwner --jq .nameWithOwner 2>$null | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($repo)) {
        Write-Host "[PUBLISH] STOPPED: could not resolve the GitHub repository for this folder."
        return $false
    }
    $repo = $repo.Trim()
    Write-Host "[PUBLISH] 3/7 Target repository: $repo            [OK]"

    if (-not (Test-Path -LiteralPath $ExePath)) {
        Write-Host "[PUBLISH] STOPPED: built executable missing at $ExePath."
        return $false
    }
    $localHash = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "[PUBLISH] 4/7 Built exe fingerprint ($localHash) + tag $($Plan.Tag) ready. [OK]"

    $title = if ($Plan.Beta) { "KeyPulse $($Plan.Tag) (Beta)" } else { "KeyPulse $($Plan.Tag)" }
    $notes = "Automated NativeAOT release published by build.cmd on $($Plan.Tag). SHA256: $localHash"

    $createArgs = @('release', 'create', $Plan.Tag, $ExePath, '--repo', $repo, '--title', $title, '--notes', $notes, '--draft', '--verify-tag')
    if ($Plan.Beta) { $createArgs += '--prerelease' }

    & gh @createArgs > $null 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[PUBLISH] STOPPED: draft upload for $($Plan.Tag) failed. Existing latest release is untouched."
        return $false
    }
    Write-Host "[PUBLISH] 5/7 Draft release $($Plan.Tag) uploaded and tag verified.    [OK]"

    $remoteDigest = (& gh release view $Plan.Tag --repo $repo --json assets --jq ".assets[0].digest" 2>$null | Select-Object -First 1)
    $remoteHash = if ($remoteDigest) { ($remoteDigest.Trim() -replace '^(?i)sha256:', '').ToLowerInvariant() } else { '' }
    if ($remoteHash -ne $localHash) {
        Write-Host "[PUBLISH] STOPPED: uploaded asset hash ($remoteHash) does NOT match built exe ($localHash). Draft kept unpublished."
        return $false
    }
    Write-Host "[PUBLISH] 6/7 Verified remote asset SHA-256 matches built exe.     [OK]"

    $editArgs = @('release', 'edit', $Plan.Tag, '--repo', $repo, '--draft=false')
    if ($Plan.Beta) { $editArgs += '--prerelease' } else { $editArgs += '--latest' }
    & gh @editArgs > $null 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[PUBLISH] STOPPED: verified draft $($Plan.Tag) could not be promoted to live."
        return $false
    }

    # RETAIN_01: Prune releases beyond retention window (5 stable, 3 beta) while keeping git tags intact.
    try {
        $listJson = (& gh release list --repo $repo --limit 100 --json tagName,isDraft,isPrerelease 2>$null) -join "`n"
        if (-not [string]::IsNullOrWhiteSpace($listJson)) {
            $releases = ConvertFrom-Json $listJson
            $valid = @()
            foreach ($r in $releases) {
                if ($r.isDraft) { continue }
                $cleanVer = ($r.tagName -replace '^[vV]', '')
                $parsedVer = $null
                if ([Version]::TryParse($cleanVer, [ref]$parsedVer)) {
                    $valid += [pscustomobject]@{
                        Tag        = $r.tagName
                        Beta       = [bool]$r.isPrerelease
                        VersionObj = $parsedVer
                    }
                }
            }
            $ordered = $valid | Sort-Object -Property VersionObj -Descending
            $stableList = @($ordered | Where-Object { -not $_.Beta })
            $betaList = @($ordered | Where-Object { $_.Beta })
            $newestStable = if ($stableList.Count -gt 0) { $stableList[0].VersionObj } else { $null }

            $doomed = [System.Collections.Generic.List[string]]::new()
            if ($stableList.Count -gt $stableReleasesKept) {
                foreach ($old in ($stableList | Select-Object -Skip $stableReleasesKept)) {
                    $doomed.Add($old.Tag)
                }
            }
            for ($i = 0; $i -lt $betaList.Count; $i++) {
                $b = $betaList[$i]
                if ($i -ge $betaReleasesKept -or ($null -ne $newestStable -and $b.VersionObj -le $newestStable)) {
                    $doomed.Add($b.Tag)
                }
            }

            foreach ($oldTag in $doomed) {
                Write-Host "[PUBLISH] Pruning older release $oldTag (git tag is preserved)."
                & gh release delete $oldTag --repo $repo --yes > $null 2>&1
            }
        }
    } catch {
        Write-Host "WARNING: [PUBLISH] Release retention pruning encountered an error: $($_.Exception.Message)"
    }

    Write-Host "[PUBLISH] 7/7 Release $($Plan.Tag) promoted and retention policy enforced. [OK]"
    Write-Host ""
    Write-Host "###########################################################"
    Write-Host "SUCCESS: release $($Plan.Tag) is live and verified."
    Write-Host "Download: https://github.com/$repo/releases/latest/download/KeyPulse.exe"
    Write-Host "###########################################################"
    return $true
}

# ==============================================================================
# MAIN PIPELINE EXECUTION
# ==============================================================================
$plan = $null
if ($publish) {
    $plan = Start-ReleasePreflight -Version $buildVersion -Tag $tag -IsBeta $beta -AssumeYesFlag $assumeYes
    if ($null -eq $plan) {
        exit 1
    }
    Sync-VersionFiles -Version $plan.Version
} else {
    Write-Host "[VERSION] Not an official release: repository version files are left unchanged."
}

try {
    Write-Host "###########################################################"
    if ($devMode) {
        Write-Host "PREPARING LOCAL BUILD ENVIRONMENT..."
    } else {
        Write-Host "PURGING PREVIOUS BUILD ARTIFACTS..."
    }
    Write-Host "###########################################################"
    Stop-RepoProcesses
    Initialize-CompiledDirectory -IsDevMode $devMode
    Clear-BuildArtifacts

    Write-Host ""
    Write-Host "###########################################################"
    Write-Host "CHECKING NATIVE AOT TOOLCHAIN..."
    Write-Host "###########################################################"
    if (-not (Initialize-MsvcToolchain)) {
        Undo-ReleasePlan -Plan $plan
        exit 1
    }

    Write-Host ""
    Write-Host "###########################################################"
    Write-Host "BUILDING KeyPulse: $flavor"
    Write-Host "###########################################################"
    if (-not (Invoke-NativeAotBuildLadder -Version $buildVersion)) {
        Undo-ReleasePlan -Plan $plan
        exit 1
    }

    if (-not (Test-CompiledOutput)) {
        Undo-ReleasePlan -Plan $plan
        exit 1
    }

    Write-Host ""
    Write-Host "###########################################################"
    if ($devMode) {
        Write-Host "SUCCESS: Local dev build completed successfully."
    } else {
        Write-Host "SUCCESS: Build completed successfully."
    }
    Write-Host ""
    Write-Host "Native EXE: .\compiled\KeyPulse.exe"
    Write-Host "Log file:   .\build.log  (first line: OK / WARN / FAIL)"
    Write-Host "###########################################################"

    if ($null -eq $plan) {
        if (-not $devMode) {
            Write-Host ""
            Write-Host "[PUBLISH] Skipped on request (--no-publish). GitHub was not touched."
        }
        exit 0
    }

    Write-Host ""
    Write-Host "###########################################################"
    if ($plan.Beta) {
        Write-Host "PUBLISHING BETA TO GITHUB..."
    } else {
        Write-Host "PUBLISHING RELEASE TO GITHUB..."
    }
    Write-Host "###########################################################"

    if (-not (Commit-ReleaseVersion -Plan $plan)) {
        exit 1
    }

    $pushRes = Invoke-GitCapture -GitArgs @('push', '--atomic', '--quiet', 'origin', "HEAD:refs/heads/$($plan.Branch)", "refs/tags/$($plan.Tag)")
    if (-not $pushRes.Success) {
        Write-Host "ERROR: [RELEASE] The release commit and tag $($plan.Tag) could not be pushed."
        Undo-ReleasePlan -Plan $plan
        Write-Host "WARNING: BUILD OK - but nothing was published: release commit and tag were rolled back locally."
        exit 2
    }
    Write-Host "[RELEASE] Pushed release commit and tag $($plan.Tag) atomically."

    $compiledExe = Join-Path $repoRoot 'compiled\KeyPulse.exe'
    if (Publish-GitHubRelease -Plan $plan -ExePath $compiledExe) {
        exit 0
    }

    Write-Host "WARNING: BUILD OK - commit and tag $($plan.Tag) are on GitHub, but release asset promotion did not complete."
    exit 2
}
catch {
    Write-Host "ERROR: Unhandled build pipeline failure: $($_.Exception.Message)"
    Undo-ReleasePlan -Plan $plan
    exit 1
}
