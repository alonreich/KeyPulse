@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%" >nul || exit /b 1

rem Official release & build entrypoint:
rem Usage: build.cmd [--beta] [--no-publish] [--dev]
rem Exit codes: 0 = OK, 1 = build failed/refused, 2 = release not updated
rem RELGATE_01: an official release proves the checkout is clean, on the default branch and
rem pushed, stamps version files in a release commit + tag, and publishes to GitHub.
rem For local NativeAOT builds without touching GitHub, use .\dev_build.cmd (or build.cmd --no-publish).
powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%developer_tools\BuildLog.ps1" -ScriptPath ".\build\BuildPipeline.ps1" %*
set "RC=%ERRORLEVEL%"

if "%RC%"=="1" (
  echo.
  echo ###########################################################
  echo  BUILD FAILED - nothing was published.
  echo  Scroll up for the first ERROR line, or read .\build.log
  echo ###########################################################
)

popd >nul
exit /b %RC%
