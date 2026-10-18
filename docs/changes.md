# Technical Changes Log

All internal technical, architectural, and low-level engineering changes to KeyPulse are continuously tracked in this document by autonomous AI agents and core release engineers.

> **AI Agent & Developer Directive**:
> Whenever an agent or engineer makes technical changes to this repository, record every technical alteration, internal mechanism, refactoring, security validation, and API change under `## [Unreleased]`.
> During official release packaging, consumer-facing release notes must be curated and promoted to `docs/CHANGELOG.md` in clean, user-friendly language.

## [Unreleased]

### Security & Cryptographic Integrity
- **Mandatory Cryptographic Verification in Update Checker**: Updated `UpdateChecker.DownloadUpdateAsync` to strictly reject downloaded binaries unless either (1) the computed SHA-256 matches an expected digest, or (2) the binary contains a valid Authenticode signature with Subject matching "KeyPulse". Suppressed silent acceptance on `CryptographicException` to prevent unauthenticated code execution.
- **PE Header Pre-Validation in Stamp Cache**: Updated `Program.ExtractNativeLibs` to inspect existing unpacked DLLs for size > 64 bytes and valid PE `MZ` header (`0x4D, 0x5A`) before trusting sidecar `.stamp` hashes, preventing zero-byte or corrupted DLL bypasses.
- **Automated CI/CD Code Signing**: Enhanced `build/BuildPipeline.ps1` with Certificate Store thumbprint lookup (`$env:KEYPULSE_CERT_THUMBPRINT`) alongside environment variable password resolution (`$env:KEYPULSE_CERT_PASSWORD`), eliminating manual password entry during builds.

### Process Synchronization & Update Reliability
- **Parent Process Handshake in Update Worker**: Added `--parent-pid <PID>` CLI parameter parsing in `Program.cs`. In `--update-worker` mode, the child process waits up to 6,000ms for the parent process to cleanly terminate before acquiring setup mutexes and extracting native libraries, preventing `IOException` file locks on `%LOCALAPPDATA%\Programs\KeyPulse\runtime\*.dll`.
- **Atomic NTFS Executable Replacement**: In `SetupWindow.cs`, modified binary replacement to rename the active `KeyPulse.exe` to `KeyPulse.exe.old` and register Win32 `MoveFileEx(..., MOVEFILE_DELAY_UNTIL_REBOOT)` prior to moving `stagedExe` to `Program.ExePath`. In the recovery catch block, moved failed targets aside before restoring `backupExe`.
- **Update Binary Cleanup**: Added automated cleanup in `SetupWindow.cs` targeting `%LOCALAPPDATA%\KeyPulse\updates\KeyPulse_Update_*.exe` (and dev equivalents) during installation.
- **Downgrade Warning Enforcement**: Updated `SetupWindow.cs` so that downgrade warnings and user confirmation prompts are never bypassed in update worker mode (`!_isUpdate || isDowngrade`).
- **Elevated Privilege State Preservation**: Added `--was-elevated` parameter propagation from `MainWindow.axaml.cs` to `--update-worker`. In `SetupWindow.cs`, ensures `_preserveAdminPrivilege` defaults to true when `--was-elevated` or `Program.IsElevated` is detected, guaranteeing elevated sessions never downgrade to unprivileged standard user mode after upgrades.

### Network & Update UX
- **User-Controlled Update Restart**: In `MainWindow.axaml.cs`, replaced automatic 800ms exit with "Restart & Install Now" and "Install Later" buttons, eliminating surprise restarts after update verification.
- **GitHub API Rate Limit Handling**: In `UpdateChecker.CheckAsync`, added explicit inspection of HTTP 403 status codes and `X-RateLimit-Reset` response headers, calculating remaining reset duration and guiding the user with actionable instructions.

### UI / UX Enhancements
- **Dark Mode Settings Tab Contrast & Resilient AppBrush**: Fixed `AppBrush` in `MainWindow.axaml.cs` to resolve theme-dictionary resources through `app.TryGetResource(..., app.ActualThemeVariant)` with a deterministic solid-brush fallback dictionary for Dark/Light mode, eliminating transparent styling bugs on inactive tabs and panels. Inactive tabs now render on `AppPanelBrush` (`#182235`) with `AppBorderBrush` (`#334155`) borders and high-contrast text, while active tabs display a 3px `AppAccentBrush` (`#60a5fa`) top border connecting seamlessly with the content page container.
- **Interactive Floating Scroll Indicators & Theme Elevation Glow**: Added `ScrollMoreDownPill` (`[ ▼ X more shortcuts below ▼ ]`) and `ScrollMoreUpPill` (`[ ▲ Back to top ▲ ]`) in `MainWindow.axaml` and `MainWindow.axaml.cs` for one-click page down/top scrolling when shortcut items exceed viewport height. Replaced invisible black edge fades with `#401d4ed8` theme elevation glow and added explicit vertical scrollbar styling (`Width="10"`, `AllowAutoHide="False"`).
- **Main Shortcut Search Box Ergonomics**: Aligned the search bar to the left with a dedicated "Search:" label, constrained width between 250px and 350px, and added an integrated "✕" clear button on the right inside the input field.
- **Shortcut Editor Single-Row Layout**: Reorganized shortcut editor row in `MainWindow.axaml` to place Key Combinations (200-300px), Action Combo (200-300px), Action Name (500-600px), and Add/Cancel buttons all on a single line. Reconfigured target text box to 800px wide with text wrapping up to 3 lines (`MaxHeight="76"`).
- **Persistent Vertical Scrollbar**: Set `ScrollViewer.VerticalScrollBarVisibility="Visible"` and `ScrollViewer.AllowAutoHide="False"` on `HotkeyList` to ensure the scrollbar never auto-collapses.

### Repository Architecture & Developer Tools
- **Build Logger & Scratch Relocation**: Relocated `build.log` and `build.result.txt` generation from repository root to `developer_tools/build.log` in `developer_tools/BuildLog.ps1`, `build.cmd`, and `dev_build.cmd`. Isolated temporary preview icon artifacts into `developer_tools/scratch/` to maintain a pristine repository root directory.

### Release Pipeline & Lifecycle
- **Beta Tag SemVer Normalization**: Updated `build/BuildPipeline.ps1` to append `-beta` to `$tag` when `--beta` is specified, ensuring SemVer prerelease precedence correctly prioritizes stable releases.
- **Beta Retention Pruning Fix**: Fixed beta pruning in `build/BuildPipeline.ps1` to prune strictly by FIFO count (`$i -ge $betaReleasesKept`), preventing premature deletion of beta releases when newer stable releases exist.

## [v2026.10.18.1809] - 2026-10-18
- Implemented native unmanaged COM VTable function pointers in `SetupWindow.cs` for `IShellLinkW` and `IPersistFile`.
- Added `.gitattributes` for LF/CRLF normalization.
- Sandboxed `KeyPulse.Tests` execution against live `%APPDATA%\KeyPulse` directories.
- Added pre-overwrite configuration safety backups (`config.backup-<timestamp>.json`) and retention pruning.

## [v2026.10.18.1724] - 2026-10-18
- Redesigned multi-resolution application icon (16x16 through 256x256).
- Implemented resumable HTTP Range downloads and network retry policies in `UpdateChecker.cs`.
- Integrated Authenticode signature verification with RFC-3161 Digicert timestamping.
- Added non-blocking post-update canary crash rollback mechanism in `Program.cs`.

## [v2026.10.18.1349] - 2026-10-18
- Added Search Clear Buttons ("✕") and Escape key clear handling in `MainWindow` and `SettingsDialog`.
- Reorganized Settings into 4 categories with dedicated Property Sheet cards.
- Fixed shortcut editor target layout collision in `MainWindow.axaml`.

## [v2026.10.18.0549] - 2026-10-18
- Preserved underlying GZip stream lifetime during native library extraction.

## [v2026.10.18.0517] - 2026-10-18
- Migrated Task Scheduler integration to Windows `Schedule.Service` COM API.
- Implemented GZip compression for embedded native DLLs.
- Created `ComActivation.cs` for NativeAOT COM activation.
