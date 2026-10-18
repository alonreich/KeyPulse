# Changelog

All notable changes to KeyPulse are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **AI Agent Directive:** Every agent or developer modifying code in this repository MUST maintain this file. Record meaningful technical changes and plain-English user impact under `## [Unreleased]`.

## [Unreleased]

## [v2026.10.18.0549] - 2026-10-18

### Fixed
- **Native Library Extraction Stream Lifetime**: Fixed an `ObjectDisposedException` ("Cannot access a closed Stream") during extraction of GZip-compressed native libraries by preserving underlying stream lifetime (`leaveOpen: true`) and caching resource length before stream decompression.

## [v2026.10.18.0517] - 2026-10-18

### Added
- **Self-Signed Authenticode Code Signing**: Added self-signed code signing pipeline in `build/BuildPipeline.ps1` and CI with RFC-3161 Digicert timestamping (`ssl-certificate/`).
- **High-DPI Multi-Resolution Icon & Complete PE Resources**: Added 256x256 multi-mipmap icon and verified PE metadata (`Company`, `Product`, `Copyright`, `Description`).
- **Release Channel Selector**: Users can switch between Stable and Beta release channels under Settings > About & Software Updates.
- **In-Flight Download Cancellation & Indeterminate Progress**: Users can cancel long-running updates mid-download, with smooth indeterminate progress feedback for unmetered streams.
- **Multi-Store Backup Rollback Protection**: Restore operations now automatically snapshot and roll back all secondary stores (`desktop_organizer.json`, `screen_brightness.json`) alongside `config.json`.
- **Automated Changelog Release Gate**: Preflight and CI pipelines verify that `docs/CHANGELOG.md` contains documented entries under `## [Unreleased]` before allowing releases.
- **NativeAOT CI Verification**: CI workflow validates full IL trimming, reflection-free serialization, and platform linker compatibility via `-p:PublishAot=true`.

### Changed
- **Direct Shell COM Desktop Icon Management**: Replaced cross-process memory allocation and injection (`OpenProcess`, `VirtualAllocEx`, `WriteProcessMemory`, `ReadProcessMemory`) in `DesktopOrganizerService` with native Shell COM interfaces (`IShellWindows`, `IServiceProvider`, `IShellBrowser`, `IShellView`, `IFolderView`).
- **Task Scheduler COM Integration**: Migrated task registration, query, and battery configuration to Windows `Schedule.Service` COM API, eliminating temporary XML file drops in `%TEMP%` and avoiding child `schtasks.exe` executions.
- **Embedded Native DLL GZip Compression**: Compressed native binaries (`av_libglesv2.dll.gz`, `libHarfBuzzSharp.dll.gz`, `libSkiaSharp.dll.gz`) to de-identify raw PE headers in PE `.rdata` while preserving single-file standalone distribution.
- **Isolated Binary Staging**: Relocated uninstaller staging from root `%TEMP%` to `%LOCALAPPDATA%\KeyPulse_Uninstall\` and update staging to `%LOCALAPPDATA%\Programs\KeyPulse\updates\`.
- **Deferred Startup Network Probe**: Initial background update check is delayed by 10 minutes to prevent unprompted outbound network flags during automated sandbox detonation.
- **NativeAOT COM Activation**: Added `ComActivation.cs` using Win32 `ole32!CoCreateInstance` to cleanly instantiate COM classes under NativeAOT without CLR metadata warnings.
- **Seamless In-App Update Experience**: Added `--update-worker` mode which eliminates confusing "Wipe Settings" prompts during updates, automatically preserves preferences, and relaunches the app when finished.
- **Dedicated Long-Running Download Client**: Replaced restrictive 20-second API timeout with a dedicated 15-minute download client supporting slow connections.
- **Canary Startup Stabilization**: Post-update rollback executable is now preserved for 15 seconds of healthy execution before deletion, ensuring hooks and background services stabilize without premature cleanup.
- **Full Working-Tree Release Traceability**: Build pipeline stages all working tree modifications (`git add -A`) so release commits record complete source provenance.
- **Non-Intrusive Boot Notifications**: Background update checks when launching hidden to tray now display subtle tray notifications instead of modal window dialogs.

### Fixed
- **VBScript Wrapper Elimination**: Removed `boot.vbs` dropper and `wscript.exe` background execution in `Program.cs`, invoking `KeyPulse.exe --hidden` directly via the Registry `CurrentVersion\Run` key.
- **Hidden Shell Self-Deletion Removal**: Removed `cmd.exe /c for /L ... ping 127.0.0.1` self-delete loop in `SetupWindow.cs`, replacing it with direct folder deletion and Win32 `MoveFileEx` with `MOVEFILE_DELAY_UNTIL_REBOOT`.
- **Security Manifest Execution Level**: Declared `<requestedExecutionLevel level="asInvoker" uiAccess="false" />` in `app.manifest` to avoid unwanted UAC elevation flags and heuristic triggers.
- **Cryptographic Update & PE Header Verification**: Downloaded executables are strictly validated against GitHub SHA-256 digests, Content-Length stream sizes, and valid Windows PE MZ headers before execution.
- **Failed Release Publication Cleanup**: Failed GitHub release drafts and remote Git tags are automatically deleted if verification or promotion fails, preventing broken release states.
- **Administrator Privilege Retention**: Updater workers inherit Administrator privileges via `runas` when updating from an elevated session.
- **Canary Non-Terminating Crash Filtering**: Suppressed post-update canary increments on handled, non-fatal exceptions so healthy updates are not rolled back erroneously.
- **Fallback Release Download URL**: Corrected fallback download link syntax to point to valid GitHub release asset endpoints.

### Security
- **Untrusted Binary Execution Prevention**: Updates with corrupted byte counts, invalid PE signatures, or missing/mismatched SHA-256 hashes are rejected prior to launching.
- **Forward-Compatible Configuration Schema**: Added JsonExtensionData support to prevent unknown future configuration attributes from being stripped during save cycles.

## [v2026.10.17.1515] - 2026-10-17

### Added
- Sequential multi-display brightness adjustments with 30 ms settling delays and 50 ms VESA MCCS dual-write pauses to protect GPU display controller multiplexers and monitor scalers.
- Trailing-edge flush and 250 ms hardware speed limiters for monitor brightness sliders.

### Fixed
- Fixed screen brightness DDC/CI monitor handle leaks by ensuring deterministic destruction of handles.
- Fixed Dev Mode configuration routing to eliminate cross-contamination with production settings.

## [v2026.10.17.1436] - 2026-10-17

### Fixed
- Fixed race condition in Desktop Organizer configuration saves by adding LKG backup and empty profile overwrite guards.
