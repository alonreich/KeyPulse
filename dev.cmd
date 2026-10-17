@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"

set "PROJECT=KeyPulse.csproj"
set "CONFIG=Debug"
set "RUNTIME=win-x64"
set "DOTNET_WATCH_SUPPRESS_EMOJIS=1"
set "REPO_ROOT=%CD%"
set "DEV_FULL="

REM ======================================================================
REM DEVDATA_01: Strict Sandbox Isolation.
REM All dev settings, brain, desktop layouts, brightness presets, runtime
REM DLLs, and logs live in isolated dev folders - NEVER in %APPDATA%\KeyPulse
REM or %LOCALAPPDATA%\Programs\KeyPulse.
REM ======================================================================
set "KEYPULSE_DEV_MODE=1"
set "KEYPULSE_DEV_HOME=%LOCALAPPDATA%\KeyPulse_DEV"
set "KEYPULSE_DEV_DATA_DIR=%KEYPULSE_DEV_HOME%\data"

REM DEVUPDATE_01: In dev, UpdateChecker inspects .\compiled\KeyPulse.exe
REM built by .\dev_build.cmd instead of querying GitHub.
set "KEYPULSE_DEV_UPDATE_SOURCE=%REPO_ROOT%"

REM ----------------------------------------------------------------------
REM DEV LOG DIRECTORY: All dev-mode logs go EXCLUSIVELY to
REM %TMP%\KeyPulse_DEV\ (or .devlogs\ in trace mode).
REM ----------------------------------------------------------------------
set "KEYPULSE_DEV_LOG_DIR=%TMP%\KeyPulse_DEV"
if not exist "%KEYPULSE_DEV_LOG_DIR%" mkdir "%KEYPULSE_DEV_LOG_DIR%"

REM ======================================================================
REM STALE-STATE PURGE & SENTINEL CHECK. Runs before EVERY mode.
REM ======================================================================
call :KILL_STALE
call :WIPE_DEV_CONFIG
call :DEV_SANDBOX
call :VERIFY_PATCHES
if errorlevel 1 exit /b 1

if "%~1"=="" goto WATCH
if /I "%~1"=="run" goto RUN
if /I "%~1"=="build" goto BUILD
if /I "%~1"=="restore" goto RESTORE
if /I "%~1"=="clean" goto CLEAN
if /I "%~1"=="fresh" goto FRESH
if /I "%~1"=="rebuild" goto REBUILD
if /I "%~1"=="trace" goto TRACE

echo Unknown option: %1
echo.
echo Usage:
echo   dev          Hot reload mode. App stays open, UI updates on save.
echo                Incremental: only what changed is rebuilt - DEVFAST_01.
echo   dev rebuild  Like 'dev', but deletes bin/obj first - full rebuild.
echo   dev run      Single Debug launch in isolated dev sandbox.
echo   dev build    Build only, no run.
echo   dev fresh    Like 'dev', but ALSO wipes the sandboxed config/state
echo                (%KEYPULSE_DEV_HOME%) so the app boots as if freshly installed.
echo   dev restore  Restore NuGet packages after project/package changes.
echo   dev clean    Clean Debug output.
echo   dev trace    Like 'dev', but writes the log to .devlogs\ INSIDE the repo so
echo                it can be read and shared for diagnostics.
goto :EOF

:TRACE
set "KEYPULSE_DEV_LOG_DIR=%REPO_ROOT%\.devlogs"
if not exist "%KEYPULSE_DEV_LOG_DIR%" mkdir "%KEYPULSE_DEV_LOG_DIR%"
del /q "%KEYPULSE_DEV_LOG_DIR%\*.txt" 2>nul
del /q "%KEYPULSE_DEV_LOG_DIR%\*.log" 2>nul
echo [DEV] TRACE MODE: log goes to %KEYPULSE_DEV_LOG_DIR%
goto WATCH

:FRESH
call :WIPE_DEV_DATA
call :DEV_SANDBOX
set "DEV_FULL=1"
goto WATCH

:REBUILD
set "DEV_FULL=1"
goto WATCH

:WATCH
if "%DEV_FULL%"=="1" goto WATCH_FULL
echo [DEV] Incremental build - only changed files are rebuilt. Use 'dev rebuild' for a full wipe.
echo [DEV] Sandbox data: %KEYPULSE_DEV_DATA_DIR%
echo [DEV] Sandbox logs: %KEYPULSE_DEV_LOG_DIR%
goto WATCH_START
:WATCH_FULL
call :NUKE_BUILD
:WATCH_START

echo Starting HOT RELOAD watch mode (Isolated Dev Sandbox)...
echo   NOTE: hot reload cannot apply structural edits (new fields, new methods,
echo         changed signatures). If a change does not appear, STOP and re-run dev.cmd.
echo Press Ctrl+C to stop.
echo.
dotnet watch run --project "%PROJECT%" -c %CONFIG% -r %RUNTIME% -p:PublishAot=false -- run-ui
goto :EOF

:RUN
echo Running Debug single launch in isolated dev sandbox (full clean)...
echo [DEV] Wiping build caches (bin/obj) for a guaranteed-fresh build...
dotnet build-server shutdown >nul 2>nul
if exist "%REPO_ROOT%\bin" rd /s /q "%REPO_ROOT%\bin" 2>nul
if exist "%REPO_ROOT%\obj" rd /s /q "%REPO_ROOT%\obj" 2>nul
dotnet run --project "%PROJECT%" -c %CONFIG% -r %RUNTIME% -p:PublishAot=false -- run-ui
goto :EOF

:BUILD
echo Building Debug no run (full clean)...
call :NUKE_BUILD
dotnet build "%PROJECT%" -c %CONFIG% -r %RUNTIME% -p:PublishAot=false -consoleLoggerParameters:Summary
goto :EOF

:RESTORE
echo Restoring Debug dependencies...
dotnet restore "%PROJECT%" -r %RUNTIME%
goto :EOF

:CLEAN
echo Cleaning Debug output...
call :NUKE_BUILD
dotnet clean "%PROJECT%" -c %CONFIG% -r %RUNTIME% -consoleLoggerParameters:Summary
goto :EOF

:VERIFY_PATCHES
powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%\build\VerifySentinels.ps1" -RepoRoot "%REPO_ROOT%"
if errorlevel 1 exit /b 1
goto :EOF

REM ======================================================================
REM Subroutine: KILL EVERY STALE PROCESS THIS REPO OWNS (BUILDKILL_01).
REM ONLY processes whose executable lives under this repository are stopped.
REM The installed KeyPulse in %LOCALAPPDATA%\Programs\KeyPulse is NEVER touched.
REM ======================================================================
:KILL_STALE
echo [DEV] Purging stale processes owned by this repo...

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$root = '%REPO_ROOT%';" ^
  "Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |" ^
  "  Where-Object { $_.ExecutablePath -and $_.ExecutablePath.ToLower().StartsWith($root.ToLower()) } |" ^
  "  ForEach-Object {" ^
  "    Write-Host ('[DEV]   killing ' + $_.Name + ' (PID ' + $_.ProcessId + ')');" ^
  "    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }" 2>nul

dotnet build-server shutdown >nul 2>nul
taskkill /F /IM VBCSCompiler.exe >nul 2>nul
taskkill /F /IM MSBuild.exe >nul 2>nul

goto :EOF

:WIPE_DEV_CONFIG
if exist "%KEYPULSE_DEV_LOG_DIR%" (
    echo [DEV] Clearing previous dev logs in %KEYPULSE_DEV_LOG_DIR%...
    del /q "%KEYPULSE_DEV_LOG_DIR%\*.txt" 2>nul
    del /q "%KEYPULSE_DEV_LOG_DIR%\*.log" 2>nul
    del /q "%KEYPULSE_DEV_LOG_DIR%\*.old" 2>nul
)
goto :EOF

:DEV_SANDBOX
powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%\build\DevSandbox.ps1" -DevData "%KEYPULSE_DEV_DATA_DIR%" -KeepDir "%KEYPULSE_DEV_HOME%\keep"
goto :EOF

:WIPE_DEV_DATA
if exist "%KEYPULSE_DEV_DATA_DIR%" (
    echo [DEV] Wiping sandboxed data %KEYPULSE_DEV_DATA_DIR% for clean-slate boot...
    rd /s /q "%KEYPULSE_DEV_DATA_DIR%" 2>nul
)
if exist "%KEYPULSE_DEV_HOME%\keep" (
    echo [DEV] Wiping remembered sandbox backup %KEYPULSE_DEV_HOME%\keep...
    rd /s /q "%KEYPULSE_DEV_HOME%\keep" 2>nul
)
goto :EOF

:NUKE_BUILD
echo [DEV] Wiping build caches (bin/obj) for a guaranteed-fresh build...
dotnet build-server shutdown >nul 2>nul
ping -n 2 127.0.0.1 >nul 2>nul
if exist "%REPO_ROOT%\bin" rd /s /q "%REPO_ROOT%\bin" 2>nul
if exist "%REPO_ROOT%\obj" rd /s /q "%REPO_ROOT%\obj" 2>nul
goto :EOF
