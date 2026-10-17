@echo off
setlocal enabledelayedexpansion

echo ============================================================================
echo                   KeyPulse - Complete System Wiper
echo ============================================================================
echo.
echo [1/6] Terminating any running instances of KeyPulse...
taskkill /f /im "KeyPulse.exe" /t >nul 2>&1
if !errorlevel! equ 0 (
    echo       - Successfully terminated KeyPulse.exe.
    timeout /t 2 /nobreak >nul
) else (
    echo       - No running instances found.
)

echo.
echo [2/6] Removing Scheduled Tasks and Registry Entries...
schtasks /Delete /TN "KeyPulse_AdminTask" /F >nul 2>&1
if !errorlevel! equ 0 (
    echo       - Removed Scheduled Task: KeyPulse_AdminTask
) else (
    echo       - Scheduled Task KeyPulse_AdminTask not found or requires elevation.
)

reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\KeyPulse" /f >nul 2>&1
if !errorlevel! equ 0 (
    echo       - Removed Uninstall registry key (HKCU).
) else (
    echo       - Uninstall registry key not found.
)

reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "KeyPulse" /f >nul 2>&1
if !errorlevel! equ 0 (
    echo       - Removed Auto-Start registry entry (HKCU).
) else (
    echo       - Auto-Start registry entry not found.
)

echo.
echo [3/6] Removing Application Shortcuts...
set "START_MENU_DIR=%APPDATA%\Microsoft\Windows\Start Menu\Programs"
set "DESKTOP_DIR=%USERPROFILE%\Desktop"

if exist "%START_MENU_DIR%\KeyPulse.lnk" (
    del /f /q "%START_MENU_DIR%\KeyPulse.lnk"
    echo       - Deleted Start Menu shortcut.
) else (
    echo       - Start Menu shortcut not found.
)

if exist "%DESKTOP_DIR%\KeyPulse.lnk" (
    del /f /q "%DESKTOP_DIR%\KeyPulse.lnk"
    echo       - Deleted Desktop shortcut.
) else (
    echo       - Desktop shortcut not found.
)

echo.
echo [4/6] Deleting Installed Binaries and Native Runtime Cache...
set "INSTALL_DIR=%LOCALAPPDATA%\Programs\KeyPulse"
if exist "%INSTALL_DIR%" (
    rmdir /s /q "%INSTALL_DIR%"
    echo       - Wiped directory: %INSTALL_DIR%
) else (
    echo       - Directory not found: %INSTALL_DIR%
)

set "RUNTIME_FALLBACK=%TEMP%\KeyPulse_Runtime"
if exist "%RUNTIME_FALLBACK%" (
    rmdir /s /q "%RUNTIME_FALLBACK%"
    echo       - Wiped directory: %RUNTIME_FALLBACK%
)

echo.
echo [5/6] Deleting AppData Configuration, Brain, and Logs...
set "ROAMING_DIR=%APPDATA%\KeyPulse"
if exist "%ROAMING_DIR%" (
    rmdir /s /q "%ROAMING_DIR%"
    echo       - Wiped directory: %ROAMING_DIR%
) else (
    echo       - Directory not found: %ROAMING_DIR%
)

set "TEMP_LOG_DIR=%TEMP%\KeyPulse"
if exist "%TEMP_LOG_DIR%" (
    rmdir /s /q "%TEMP_LOG_DIR%"
    echo       - Wiped directory: %TEMP_LOG_DIR%
) else (
    echo       - Directory not found: %TEMP_LOG_DIR%
)

echo.
echo [6/6] Deleting Developer Sandbox Data (Optional)...
set "DEV_HOME_DIR=%LOCALAPPDATA%\KeyPulse_DEV"
if exist "%DEV_HOME_DIR%" (
    rmdir /s /q "%DEV_HOME_DIR%"
    echo       - Wiped Dev Sandbox: %DEV_HOME_DIR%
) else (
    echo       - Dev Sandbox not found: %DEV_HOME_DIR%
)

echo.
echo ============================================================================
echo      KeyPulse has been completely wiped from this system!
echo      Your machine is now in a 100%% clean state for fresh testing.
echo ============================================================================
echo.
pause
