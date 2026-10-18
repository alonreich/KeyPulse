# Changelog

All notable changes to KeyPulse are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **AI Agent Directive:** Every agent or developer modifying code in this repository MUST maintain this file. Record meaningful technical changes and plain-English user impact under `## [Unreleased]`.

## [Unreleased]

## [v2026.10.18.2011] - 2026-10-18

### Added
- **Dark Mode Settings Visibility**: Redesigned Settings tabs in Dark Mode with crisp high-contrast outlines, distinct background tiles, and vibrant top accent indicators so every tab is instantly visible and easy to navigate.
- **Fluent Shortcut Search**: The main window search bar is now neatly aligned to the left with an intuitive "Search:" label, ergonomic width, and a one-click "✕" clear button right inside the box.
- **Compact Editor Layout**: Reorganized shortcut inputs onto a single horizontal line, keeping Key Combinations, Action Type, and Action Name together while expanding the text box up to 800px with smooth 3-line text wrapping.
- **Modern Scroll Overflow Shadows**: Upgraded shortcut list scroll indication with clean Windows 11-style elevation drop shadows, completely replacing old vertical arrow indicators.
- **Never-Collapsing Scrollbar**: Locked the main window scrollbar in a visible state so it remains steady and never auto-hides or jumps while scrolling.
- **Update Restart Control**: Replaced sudden automatic restarts after update downloads with clear "Restart & Install Now" and "Install Later" choices.

### Fixed
- **Zero Data Loss & Privilege Retention**: Upgrades and updates now strictly preserve all custom shortcuts, quick typing snippets, obfuscation settings, and elevated Administrator permissions.
- **Update File Lock Immunity**: Eliminated update crashes caused by Windows file locks on running executables and runtime native components during in-app updates.
- **Cryptographic Security Hardening**: Downloaded updates strictly verify published SHA-256 digests and Authenticode signatures, rejecting untrusted or corrupted binaries.
### Changed
- **Repository Optimization & Dead Code Removal**: Purged ~18.8 MB of unused uncompressed native DLL libraries, eliminated obsolete Win32 interop declarations and ListView constants, removed unreferenced properties/helpers, and pruned stale test runner directories.

## [v2026.10.18.1809] - 2026-10-18

### Added
- **NativeAOT COM VTable Shortcut Creation**: Implemented native unmanaged COM VTable function pointers (`delegate* unmanaged[Stdcall]`) in `SetupWindow.cs` for `IShellLinkW` and `IPersistFile`, backed by an autonomous PowerShell `WScript.Shell` fallback, eliminating NativeAOT `PlatformNotSupportedException` during in-app updates and installations.
- **Git Line Ending Normalization**: Added `.gitattributes` defining text attributes and end-of-line rules, eliminating Git LF/CRLF conversion warnings across environments.

### Fixed
- **Unit Test Environment Sandboxing & Automatic Detection**: Sandboxed `KeyPulse.Tests` execution and added automatic test-runner environment detection in `ConfigStore.ResolveConfigDirectory()`, guaranteeing test runs during build quality gates cannot access or overwrite live user configuration files in `%APPDATA%\KeyPulse`.
- **Pre-Overwrite Configuration Safety Backups**: Added automatic timestamped safety backups (`config.backup-<timestamp>.json`), retention pruning (keeping the 10 newest backups), and preservation of dedicated recovery copies (`config.safety-shortcuts.json`) whenever saving over existing shortcuts.
- **LKG Empty-Overwrite Protection**: Hardened `ConfigStore.UpdateLkgCopy` to reject overwriting last-known-good copies with empty shortcut configurations when valid shortcuts were previously recorded.
- **Local Code Signing Password Resolution**: Added fallback password resolution in `build/BuildPipeline.ps1` for local `KeyPulse_CodeSigning.pfx`, ensuring local builds and releases sign cleanly without warnings.
- **Non-Interactive Release Builds**: Streamlined `build.cmd` and `build/BuildPipeline.ps1` to execute release preflight and publishing directly without interactive `PUBLISH` confirmation prompts.

## [v2026.10.18.1724] - 2026-10-18

### Added
- **Active Directory-Style Property Sheet Tabs**: Overhauled Settings & Preferences with 8 dedicated property sheets (General, Hotkeys, Typing, Theme, Displays, Desktop Icons, Updates, Backup) featuring seamless top-mounted tab ears physically connected to the content page container.
- **Full-Bleed Zero-Padding Systray Icon**: Redesigned multi-resolution application icon (16x16 through 256x256) with zero padding to maximize visual clarity and size in the Windows system tray.
- **Resumable HTTP Range Downloads & Network Retries**: Update downloader automatically resumes interrupted transfers using HTTP Range requests and retries transient socket failures with exponential backoff.
- **Authenticode Signature & Mandatory Digest Verification**: Downloaded updates strictly verify Windows Authenticode code signatures and release SHA-256 digests prior to launching.
- **Non-Blocking Post-Update Crash Rollback**: Canary recovery replaces crashing binaries out-of-process, eliminating Windows in-use file lock crashes.
- **Elevated Installer Resilience**: In-app upgrade installer detects surviving elevated instances and prompts to elevate rather than failing on locked binaries.

### Fixed
- **SemVer Prerelease Comparison**: Fixed version comparison so official stable releases correctly upgrade existing beta installations.
- **Forward Schema Preservation**: Config saves preserve newer schema version numbers rather than downgrading to version 1.
- **Forward-Compatible Backup Restore**: Backups containing newer unrecognized action types skip unsupported actions with warnings rather than aborting as corrupt.
- **Atomic Release Publication Rollback**: Release pipeline pushes tags and commits only after remote asset verification, preventing remote branch desynchronization on failed publications.
- **Strict Release Staging**: Build pipeline stages only version-controlled release assets instead of indiscriminate `git add -A`.
- **Persistent Background Update Notifications**: Updates detected while running minimized to tray are retained and presented when opening the main window.

## [v2026.10.18.1349] - 2026-10-18

### Added
- **Search Clear Buttons ("✕") & Escape Key Clear**: Added prominent clear buttons to both `MainWindow` and `SettingsDialog` search inputs that appear dynamically while typing. Pressing `Escape` or clicking "✕" immediately resets the search query and returns focus.
- **Search Auto-Reset on Tray Minimize/Reopen**: Closing or hiding `MainWindow` to the system tray and reopening it now automatically clears any active search text and restores the full shortcut list.
- **Settings Information Architecture Overhaul**: Reorganized preferences into 4 clear top-tier categories (`General`, `Shortcuts & Typing`, `Appearance & Displays`, `Maintenance & About`):
  - **Tab 0 (General)**: Co-located Windows Startup and Administrator Privileges with a live session status badge and clear explanation of elevated shortcut injection.
  - **Tab 1 (Shortcuts & Typing)**: Replaced raw delay controls with 3 one-click speed presets (Fast 1ms, Normal 6ms, Compatible 18ms) plus custom millisecond stepper; replaced binary Chrome checkbox with segmented button selector and live Chrome installation detection.
  - **Tab 2 (Appearance & Displays)**: Replaced theme ComboBox with 3-way segmented buttons (Match Windows, Light, Dark); maintained direct buttons for Screen Brightness Manager and Desktop Icon Organizer.
  - **Tab 3 (Maintenance & About)**: Grouped Software Updates (with manual check button and Stable/Beta channel switch), Backup & Import Settings, and an isolated Danger Zone for Factory Reset.

### Fixed
- **Settings Danger Zone Isolation**: Removed non-destructive Administrator elevation controls from Danger Zone, reserving the red card exclusively for Factory Reset.
- **Restore Defaults Completeness**: Fixed `Restore Defaults` (both tab-level and global factory reset) to properly reset `AutoCheckForUpdates` (true), `UpdateChannel` ("Stable"), and `SkippedUpdateVersion`.
- **Settings Tab Switching Filter Collision**: Switching tabs in Settings now automatically clears active search filters to display the selected tab cleanly.
- **Shortcut Editor Target Layout Collision**: Fixed overlapping text inside the target input area caused by insufficient `RowDefinitions` in `MainWindow.axaml`, which previously caused the action hint text and error blocks to collapse directly onto the target input box.

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
