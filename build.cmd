@echo off
set "DOTNET_CLI_FORCE_UTF8_ENCODING=false"
set "DOTNET_CLI_UI_LANGUAGE=en-US"
set "VSCONSOLEOUTPUT=1"
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1

if "%~1"=="--internal-log" goto :run_logged
if exist build.log del /f /q build.log
powershell -NoProfile -Command "& { & '%~f0' --internal-log %* 2>&1 | Tee-Object -FilePath build.log; exit $LASTEXITCODE }"
set "RC=%ERRORLEVEL%"

if "%RC%"=="1" (
  echo.
  echo ###########################################################
  echo  BUILD FAILED - nothing was published.
  echo  Scroll up for the first ERROR line, or read .\build.log
  echo ###########################################################
  pause
)
exit /b %RC%

:run_logged
shift
setlocal enabledelayedexpansion
cd /d "."

set "PROJECT_FILE=KeyPulse.csproj"
set "PROJECT_EXE=KeyPulse.exe"
set "OUTPUT_EXE=KeyPulse.exe"
set "OUTPUT_DIR=.\compiled"
set "FINAL_DIR=.\obj\StandaloneTemp\NativeAot_final"
set "PUBLISH_BASE_ARGS=-p:TreatWarningsAsErrors=false"
set "PUBLISH_AOT_ARGS=-p:PublishAot=true"
set "DOTNET_LOG_ARGS=-consoleLoggerParameters:ErrorsOnly"

echo ###########################################################
echo PURGING PREVIOUS BUILD ARTIFACTS...
echo ###########################################################
call :TERMINATE_PROCESSES
if exist "%OUTPUT_DIR%" rd /s /q "%OUTPUT_DIR%"
mkdir "%OUTPUT_DIR%"
call :CLEAN_ALL

echo.
echo ###########################################################
echo CHECKING NATIVE AOT TOOLCHAIN...
echo ###########################################################
call :DETECT_NATIVE_AOT
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo BUILDING KeyPulse: NativeAOT win-x64
echo ###########################################################
call :BUILD_NATIVE
if errorlevel 1 exit /b 1

call :VALIDATE_COMPILED_OUTPUT
if errorlevel 1 exit /b 1

echo.
echo ###########################################################
echo SUCCESS: Build completed successfully.
echo.
echo Native EXE: %OUTPUT_DIR%\%OUTPUT_EXE%
echo Log file:  .\build.log
echo ###########################################################

if "!DO_PUBLISH!"=="0" (
  echo.
  echo [PUBLISH] Skipped on request ^(--no-publish^). GitHub was not touched.
  exit /b 0
)

echo.
echo ###########################################################
echo PUBLISHING RELEASE TO GITHUB...
echo ###########################################################
call :PUBLISH_RELEASE
if errorlevel 1 exit /b 2

exit /b 0

:PUBLISH_RELEASE
where gh >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: GitHub CLI ^(gh^) is not installed or not on PATH.
  exit /b 1
)
echo [PUBLISH] 1/7 GitHub CLI found.                                    [OK]

gh auth status >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: gh is installed but not signed in.
  exit /b 1
)
echo [PUBLISH] 2/7 GitHub sign-in valid.                                [OK]

set "REPO="
for /f "usebackq delims=" %%R in (`gh repo view --json nameWithOwner --jq .nameWithOwner 2^>nul`) do set "REPO=%%R"
if not defined REPO (
  echo [PUBLISH] STOPPED: could not work out the GitHub repository for this folder.
  exit /b 1
)
echo [PUBLISH] 3/7 Target repository: !REPO!            [OK]

set "LOCALHASH="
for /f "skip=1 delims=" %%H in ('certutil -hashfile "%OUTPUT_DIR%\%OUTPUT_EXE%" SHA256') do (
  if not defined LOCALHASH set "LOCALHASH=%%H"
)
set "LOCALHASH=!LOCALHASH: =!"
if not defined LOCALHASH (
  echo [PUBLISH] STOPPED: could not fingerprint the freshly built exe.
  exit /b 1
)
for /f "usebackq delims=" %%D in (`powershell -NoProfile -Command "Get-Date -Format yyyy.MM.dd"`) do set "TAG=v%%D"
echo [PUBLISH] 4/7 Built exe fingerprint + tag !TAG! ready.        [OK]

set "REMOVED=0"
for /f "usebackq delims=" %%T in (`gh release list --repo !REPO! --json tagName --jq ".[].tagName" 2^>nul`) do (
  echo [PUBLISH]     removing previous release %%T
  gh release delete %%T --repo !REPO! --cleanup-tag --yes >nul 2>&1
  set /a REMOVED+=1
)
echo [PUBLISH] 5/7 Previous releases removed: !REMOVED!                    [OK]

gh release create !TAG! "%OUTPUT_DIR%\%OUTPUT_EXE%" --repo !REPO! --title "KeyPulse !TAG!" --notes "Automated NativeAOT release published by build.cmd on !TAG!. SHA256 !LOCALHASH!" --latest >nul 2>&1
if errorlevel 1 (
  echo [PUBLISH] STOPPED: creating release !TAG! failed.
  exit /b 1
)
echo [PUBLISH] 6/7 Release !TAG! created and asset uploaded.        [OK]

set "REMOTEHASH="
for /f "usebackq delims=" %%V in (`gh release view !TAG! --repo !REPO! --json assets --jq ".assets[0].digest" 2^>nul`) do set "REMOTEHASH=%%V"
set "REMOTEHASH=!REMOTEHASH:sha256:=!"
if /I not "!REMOTEHASH!"=="!LOCALHASH!" (
  echo [PUBLISH] STOPPED: the uploaded asset does NOT match the file that was just built.
  exit /b 1
)
echo [PUBLISH] 7/7 Published asset hash matches the built exe.          [OK]

echo.
echo ###########################################################
echo SUCCESS: release !TAG! is live and is the only release.
echo Download: https://github.com/!REPO!/releases/latest/download/%OUTPUT_EXE%
echo ###########################################################
exit /b 0

:BUILD_NATIVE
set "STAGING_DIR=.\obj\StandaloneTemp\Staging"
set "FINAL_DIR=.\obj\StandaloneTemp\NativeAot_final"

echo [NativeAOT] 1. Purging old temp folders...
if exist "%STAGING_DIR%" rd /s /q "%STAGING_DIR%"
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"

echo [NativeAOT] 2. Publishing standalone installer...
dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 %PUBLISH_BASE_ARGS% %PUBLISH_AOT_ARGS% -o "%FINAL_DIR%" %DOTNET_LOG_ARGS%
if errorlevel 1 exit /b 1

echo [NativeAOT] 3. Moving final EXE to compiled folder...
if not exist "%FINAL_DIR%\%PROJECT_EXE%" (
  echo ERROR: Expected NativeAOT EXE was not produced in %FINAL_DIR%
  exit /b 1
)

move /y "%FINAL_DIR%\%PROJECT_EXE%" "%OUTPUT_DIR%\%OUTPUT_EXE%"
if errorlevel 1 exit /b 1

echo [NativeAOT] 4. Cleaning up temporary artifacts...
if exist "%STAGING_DIR%" rd /s /q "%STAGING_DIR%"
if exist "%FINAL_DIR%" rd /s /q "%FINAL_DIR%"

exit /b 0

:VALIDATE_COMPILED_OUTPUT
set "INVALID=0"
if not exist "%OUTPUT_DIR%\%OUTPUT_EXE%" set "INVALID=1"
for %%F in ("%OUTPUT_DIR%\*") do (
  if /I not "%%~nxF"=="%OUTPUT_EXE%" set "INVALID=1"
)
if "!INVALID!"=="1" (
  echo ERROR: %OUTPUT_DIR% must contain only %OUTPUT_EXE%.
  echo Actual:
  dir /b "%OUTPUT_DIR%" 2>nul
  exit /b 1
)
echo Verified %OUTPUT_DIR% contains exactly %OUTPUT_EXE%.
exit /b 0

:DETECT_NATIVE_AOT
where link.exe >nul 2>&1
if not errorlevel 1 goto DETECT_NATIVE_AOT_OK

set "VSWHERE="
where vswhere.exe >nul 2>&1
if not errorlevel 1 set "VSWHERE=vswhere.exe"
if not defined VSWHERE if exist "%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"

if not defined VSWHERE (
  echo ERROR: Native AOT platform linker ^(link.exe^) not found in PATH.
  echo ERROR: Open a Developer Command Prompt or install Visual Studio C++ build tools.
  exit /b 1
)

for /f "usebackq delims=" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2^>nul`) do (
  if exist "%%I\Common7\Tools\VsDevCmd.bat" (
    set "VSCMD_SKIP_SENDTELEMETRY=1"
    call "%%I\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul
    where link.exe >nul 2>&1
    if not errorlevel 1 goto DETECT_NATIVE_AOT_OK
  )
)

echo ERROR: Native AOT platform linker ^(link.exe^) not found.
echo ERROR: Open a Developer Command Prompt or install Visual Studio C++ build tools.
exit /b 1

:DETECT_NATIVE_AOT_OK
echo Native AOT toolchain detected.
exit /b 0

:TERMINATE_PROCESSES
taskkill /F /IM KeyPulse.exe /T 2>nul
dotnet build-server shutdown 2>nul
exit /b 0

:CLEAN_ALL
if exist "bin" rd /s /q "bin" 2>nul
if exist "obj" rd /s /q "obj" 2>nul
dotnet clean KeyPulse.csproj -c Release -r win-x64 --nologo -v q >nul 2>&1
exit /b 0
