# Changelog

All notable changes to KeyPulse are documented in this file.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

> **AI Agent Directive:** Every agent or developer modifying code in this repository MUST maintain this file. Record meaningful technical changes and plain-English user impact under `## [Unreleased]`.

## [Unreleased]

## [v2026.10.20.0603] - 2026-10-20

### Added
- **One-Click Admin Relaunch from Startup Notice**: Added an immediate "Restart As Administrator" primary action button to the Administrator mode startup notice dialog, allowing users who launched KeyPulse manually via desktop shortcuts to elevate directly without opening Settings.
- **Synced All-Displays Master Slider & Gap Preservation**: Renamed master brightness bar to "ALL DISPLAYS (SYNCED)" with a dedicated "SYNCED MASTER" badge and vibrant gradient border/background. Moving individual monitor sliders now recalibrates and preserves relative differential offsets when subsequently adjusting the master synced slider or step hotkeys.
- **Movable, Resizable & Geometry-Remembering Brightness Overlay**: Enabled window resizing and title bar dragging (BeginMoveDrag) on the Quick Sliders overlay (ScreenBrightnessOverlayWindow), persisting custom window bounds and coordinates in ScreenBrightnessConfig.
- **Direct Desktop Organizer Automation in Settings**: Surfaced "Automatically restore icon positions on display or resolution change" and "Automatically create periodic backup snapshots" checkboxes directly on the Displays & Icons preferences card.
- **Step Delta Selector**: Added an interactive step size selector (1%, 2%, 5%, 10%) bound directly to cfg.StepDelta in the Screen Brightness manager.
- **Expanded Preferred Web Browser Selection**: Added browser selector dropdown supporting System Default (Recommended), Google Chrome, Microsoft Edge, Mozilla Firefox, and Brave Browser with real-time detection and fallback notices.
- **Password Show/Hide Eye Toggle**: Added an 👁 reveal/mask toggle button beside password fields in backup encryption and unlock dialogs.
- **Settings Window Single-Instance & Modal Parenting**: Guarded settings from duplicate instances and established strict modal window ownership across backup and passphrase dialogs to prevent orphaned child windows.

### Changed
- **4-Tab Categorized Settings Information Architecture**: Consolidated 8 scattered tabs into 4 top-tier application groupings: **General**, **Appearance**, **Displays & Icons**, and **Backup & Updates**, complete with full search filtering and contextual tab defaults restoration.
- **Conditional Update Channel Interactivity**: Automatically dim and disable the Update Channel dropdown when "Automatically check for updates on startup" is unchecked.
- **Admin Privilege Microcopy & Light Theme Contrast**: Replaced technical elevation labels with ultra-short, clear microcopy ("Run as Admin", "Revert to User") and enhanced badge background/border contrast for WCAG AA compliance in Light mode.

### Fixed
- **Systray Navigation Auto-Hide (#50)**: Clicking *Screen Brightness*, *Desktop Organizer*, or *Settings* from the system tray menu now immediately hides the main shortcuts window instead of leaving it stranded in the background.
- **Quick Sliders Overlay Numeric Truncation & Slider Collisions (#52)**: Fixed clipping and overlapping labels/sliders in the overlay window by constraining slider heights to 110px, enabling card clipping, and wrapping status text.
- **Visual Distinction for Synced Master Slider (#53)**: Applied distinctive gradient styling and badges to the master synced slider so users immediately distinguish it from individual monitor controls.
- **Shortcut Table Column Vertical Alignment (#54)**: Synchronized list item grid layout and horizontal scroll compensation to eliminate column header separator distortion.
- **Backup Setting Persistence & State Memory (#1, #8)**: Ensured PreferredBrowser, AutoCheckForUpdates, UpdateChannel, SkippedUpdateVersion, and SettingsWindowState are fully serialized in backup exports and remembered across app sessions.
- **Typing Delay Inline Validation (#12)**: Added inline validation messages alerting users when typing delay inputs fall outside the valid 1–200 ms range.
- **Non-Topmost Dialog Focus (#17)**: Removed forced topmost flags on confirmation dialogs to prevent broken system Z-ordering.

## [v2026.10.19.2052] - 2026-10-19

### Added
- **Comprehensive WCAG 2.2 AA Accessibility Polish**: Standardized minimum 24×24px accessible touch and keyboard focus targets on help badges across all windows with explicit `AutomationProperties.Name`, updated Light theme contrast tokens (`AppAccentBrush` `#2563eb`, `AppSuccessBrush` `#15803d`, `AppSuccessSoftBrush` `#166534`) to exceed the 4.5:1 text and 3:1 UI threshold, implemented ordered keyboard `TabIndex` traversal (0 to 18) throughout editor and action controls, added screen-reader naming to table headers and settings tabs, enabled Enter key layout creation in Desktop Organizer, and wired Escape key dismissal to the Setup wipe confirmation modal.
- **DDC/CI Concurrency Resilience & Twinkle Tray Coexistence**: Implemented a 3-attempt hardware write retry loop with 35ms settling sleep, a 40ms inter-command pause between brightness and contrast writes, direct DDC/CI writes eliminating redundant pre-write I2C reads, and serialized background multi-screen polling in `ReadAllScreensLiveAsync` to prevent GPU I2C multiplexer collisions when running alongside third-party monitor utilities.
- **Multi-Monitor Differential Lock with Boundary Compression**: Added a relative display brightness differential toggle (`Lock relative display brightness differential`, enabled by default) ensuring connected monitors step up/down in tandem while preserving their relative brightness offset (e.g. Display A stays 15% brighter than Display B). When the lower display reaches 0%, further step-downs smoothly compress the differential until all displays reach 0% (and symmetrically at 100% ceiling).
- **Linked "All Displays" Master Slider**: Added a master linked slider to both the Quick Sliders (Tray Overlay) popup and the main Screen Brightness window, featuring instant `-5%` and `+5%` stepper buttons to adjust all displays together while preserving their relative differential.
- **Instant Screen Level Hotkeys**: Added dedicated direct hotkeys in the Screen Brightness window, enabling users to bind custom shortcuts that immediately set a specific display (or all displays) to designated brightness and contrast percentages (e.g. `Ctrl+Alt+1` -> Screen A to 70%).
- **Top Number Row & Numpad Multi-Key Step Defaults**: Default out-of-the-box brightness step shortcuts now trigger from both the top number row and the numpad (`Ctrl+Alt+=, Ctrl+Alt+NumPadAdd` for Step Up; `Ctrl+Alt+-, Ctrl+Alt+NumPadSubtract` for Step Down).
- **Dedicated Granular Screen Brightness Shortcuts**: Migrated brightness shortcut configuration into the "Screen Brightness & Contrast Control" window, allowing users to assign dedicated hotkeys directly to individual presets (e.g., `Ctrl+Alt+NumPadDivide`), configure global forward/backward cycle shortcuts (e.g., `Ctrl+Alt+NumPadAdd` and `Ctrl+Alt+NumPadSubtract`), and configure direct `Step Up (+5%)` / `Step Down (-5%)` brightness adjustment shortcuts.
- **Per-Screen Independent Preset Toggles**: Presets now support individual screen checkboxes (e.g., adjust Display A to 60% while leaving Display B untouched), enabling granular multi-monitor setups without forcing uniform brightness levels across all screens.
- **Unconditional DDC/CI Blind Write Fallback**: When external displays or GPU drivers reject MCCS/VCP read queries over HDMI/DisplayPort, KeyPulse now automatically falls back to blind writes with default 0..100 ranges using both high-level DXVA2 and low-level VCP opcodes, logging Win32 error codes for deep diagnostics.
- **Low-Level DDC/CI Hardware Fallback**: Added low-level VCP opcode communication (`0x10` Luminance, `0x12` Contrast via Win32 `SetVCPFeature` and `GetVCPFeatureAndVCPFeatureReply`) to support monitors where high-level MCCS DXVA2 calls fail, restoring responsive screen brightness adjustments across a wider variety of external displays.
- **Document & Folder Launching**: Expanded the "Launch Program" action to open non-executable documents (e.g. `.html`, `.pdf`, `.txt`) and folders via Windows Shell execution, and updated file browse pickers to include HTML documents.
- **Local File Web URLs (`file:///`)**: Added support for `file:///` URLs and local `.html` file paths under the "Browse Web" action.
- **Accessible Table Keyboard Sorting**: Made shortcut table column headers focusable via keyboard (`Tab`), supporting Space/Enter to toggle sort order and Left/Right Arrow keys to navigate between columns.
- **Unencrypted Target Badge**: Added an explicit `⚠️ Unencrypted` badge in the shortcut list when Windows DPAPI account encryption is unavailable, ensuring security status is communicated accessibly beyond color alone.
- **One-Click Backup Revelation in File Explorer**: Exporting a backup now displays the full destination path and provides an immediate "Show in File Explorer" button.
- **Duplicate Shortcut Guided Resolution**: When attempting to add a shortcut that is already registered, KeyPulse now automatically navigates and scrolls directly to the existing shortcut in the list so you can edit or replace it immediately.
- **Desktop Layout Overwrite Confirmation**: Added a confirmation prompt before overwriting existing saved desktop icon profiles in the Desktop Organizer.

### Changed
- **Streamlined Screen Brightness Controls**: Removed redundant manual "Read Hardware" and "Cycle Next Step" buttons in favor of automated safe timing background polling and dedicated global preset cycling hotkeys.
- **Action Button Casing Standardization**: Converted all-caps labels ("RUN", "REMOVE", "DUPLICATE") to accessible Title/Sentence case ("Run", "Remove", "Duplicate").
- **Anchored Action Name Alignment**: Anchored the Action Name input directly beneath the header label to eliminate floating layout misalignment.
- **Context-Aware Settings Reset**: Automatically hid the non-applicable "Restore Tab Defaults" button on the Backup settings tab.
- **Independent Per-Monitor Adjustment**: Individual screen sliders can be adjusted independently at any time, immediately establishing a new differential offset for subsequent linked adjustments and global steps.
- **Mouse Focus Fallback in Unlocked Mode**: When the relative differential lock is unchecked, global brightness step shortcuts adjust only the display currently under the mouse cursor.
- **Streamlined Shortcut Action Types**: Removed "Cycle screen brightness" from the main Keys tab action dropdown in favor of the dedicated hotkey controls in the Screen Brightness window.
- **Clean Preset Creation**: Removed the redundant "Save Current Sliders as Preset" button in favor of a clean "+ Add New Preset" workflow.

### Fixed
- **Overlay Slider Text Clipping & Overlap**: Added dedicated vertical clearance margins (`Margin="0,0,0,8"` on percentage text, `Margin="0,6,0,8"` on sliders, `Margin="0,6,0,0"` on labels) in the tray Quick Sliders overlay, preventing slider thumbs and text labels from clipping into numeric readouts.
- **Shortcut Table Header Horizontal Scroll Alignment**: Synchronized the table header margin with `ScrollViewer.Offset.X` so header columns track row columns accurately during horizontal scrolling.
- **Unsaved Shortcut Draft Protection**: Added an explicit confirmation guard when clicking away to another shortcut row while an uncommitted new shortcut draft is in progress, preventing accidental data loss.
- **Theme Resource Token Resolution**: Replaced undefined `AppBgBrush` resource reference with `AppSurfaceBrush` in `ScreenBrightnessWindow`.
- **Manual Update Check Status Feedback**: Added visible "Checking for updates..." status indicators when clicking "Check for Updates" in Settings.
- **System Tray Quick Sliders Auto-Close on Tray Dismissal**: Fixed an issue where clicking "Quick Sliders (Overlay)" from the system tray menu caused the overlay to immediately close due to Windows deactivation dispatching within 20ms of the context menu dismissing. Implemented a 600ms grace window and cursor-anchored position clamping.
- **GPU I2C Bus Collisions Across Multiple Displays**: Added a global hardware lock (`HardwareWriteLock`) with a mandatory 30ms settling delay between DDC/CI monitor commands. This eliminates GPU I2C multiplexer collisions that previously dropped commands and caused multi-monitor brightness adjustments to only apply to a single display.
- **Minus and Hyphen Key Parsing in Shortcut Recorders**: Added virtual key mappings for `-`, `minus`, and `oemminus` (`0xBD`) in `HotkeyManager`, resolving failures where `Ctrl+Alt+-` could not be recorded or bound.

- **False DPAPI Security Alert on Startup**: Fixed an issue where non-secret shortcuts (e.g. plain URLs, script paths) were erroneously flagged as DPAPI encryption failures, triggering an alarming "KeyPulse security notice" on startup or update even though DPAPI was working properly.
- **Screen Brightness Hotkey Recording & Conflict Resolution**: Fixed hotkey input recording in the Screen Brightness dialog by engaging the low-level keyboard hook on focus, preventing Windows from intercepting combinations like `Ctrl+Alt+NumPadSubtract` as active actions during assignment. Reassigning a combination now automatically clears conflicting duplicates across cycle and step actions.
- **Long URL Layout Overflow**: Converted editor card layout to a constrained star column and enabled wrapping (`TextWrapping.Wrap`) across all target types, preventing long URLs and script commands from expanding past the window edge and hiding editor action buttons.
- **Hardware Write Throttle Loop Prevention**: Fixed an issue where unsuccessful hardware writes stalled state timestamps, causing an infinite 250ms retry loop against unresponsive monitor hardware.
- **Editor Target TextBox Stutter & Ballooning**: Standardized `TargetText` minimum height to a compact 34px across all action types, eliminating sudden 92px height jumps and visual stutter when selecting text typing or paste actions.
- **Shortcut Table Selection Highlight Overflow**: Constrained `ListBox.ShortcutList ListBoxItem` horizontal alignment to `Left`, preventing the blue selection highlight bar from bleeding beyond the table columns into blank space.
- **Table Grid Border Polish**: Added an explicit right-hand border to the Actions column in both the header and row templates, ensuring the shortcut table terminates cleanly without bleeding into blank space.
- **Decoupled Contrast & Brightness Writes**: Hardware brightness adjustments no longer fail or roll back if a connected monitor does not support or rejects contrast adjustments.
- **Active Slider Protection During Events**: Prevented background configuration sync events from resetting active slider positions while a user is dragging or adjusting brightness sliders.
- **Unsaved Shortcut Edit Retention**: Switching between rows in the shortcut list now auto-commits in-progress field modifications instead of silently discarding unsaved edits.
- **Dialog Escape Key Dismissal**: Enabled `Escape` key dismissal across all dynamic application dialogs.
- **Microcopy & Security Terminology Consistency**: Standardized copy across dialogs and notifications to reference "Hide secret text" instead of legacy "Blur box" phrasing, and sanitized raw exception messages in updater and backup dialogs.

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
- **Automated Changelog Release Gate**: Preflight and CI pipelines verify that `docs/CHANGELOG.md` contains documented entries under `## [Unreleased]

## [v2026.10.20.0603] - 2026-10-20

### Added
- **One-Click Admin Relaunch from Startup Notice**: Added an immediate "Restart As Administrator" primary action button to the Administrator mode startup notice dialog, allowing users who launched KeyPulse manually via desktop shortcuts to elevate directly without opening Settings.
- **Synced All-Displays Master Slider & Gap Preservation**: Renamed master brightness bar to "ALL DISPLAYS (SYNCED)" with a dedicated "SYNCED MASTER" badge and vibrant gradient border/background. Moving individual monitor sliders now recalibrates and preserves relative differential offsets when subsequently adjusting the master synced slider or step hotkeys.
- **Movable, Resizable & Geometry-Remembering Brightness Overlay**: Enabled window resizing and title bar dragging (BeginMoveDrag) on the Quick Sliders overlay (ScreenBrightnessOverlayWindow), persisting custom window bounds and coordinates in ScreenBrightnessConfig.
- **Direct Desktop Organizer Automation in Settings**: Surfaced "Automatically restore icon positions on display or resolution change" and "Automatically create periodic backup snapshots" checkboxes directly on the Displays & Icons preferences card.
- **Step Delta Selector**: Added an interactive step size selector (1%, 2%, 5%, 10%) bound directly to cfg.StepDelta in the Screen Brightness manager.
- **Expanded Preferred Web Browser Selection**: Added browser selector dropdown supporting System Default (Recommended), Google Chrome, Microsoft Edge, Mozilla Firefox, and Brave Browser with real-time detection and fallback notices.
- **Password Show/Hide Eye Toggle**: Added an 👁 reveal/mask toggle button beside password fields in backup encryption and unlock dialogs.
- **Settings Window Single-Instance & Modal Parenting**: Guarded settings from duplicate instances and established strict modal window ownership across backup and passphrase dialogs to prevent orphaned child windows.

### Changed
- **4-Tab Categorized Settings Information Architecture**: Consolidated 8 scattered tabs into 4 top-tier application groupings: **General**, **Appearance**, **Displays & Icons**, and **Backup & Updates**, complete with full search filtering and contextual tab defaults restoration.
- **Conditional Update Channel Interactivity**: Automatically dim and disable the Update Channel dropdown when "Automatically check for updates on startup" is unchecked.
- **Admin Privilege Microcopy & Light Theme Contrast**: Replaced technical elevation labels with ultra-short, clear microcopy ("Run as Admin", "Revert to User") and enhanced badge background/border contrast for WCAG AA compliance in Light mode.

### Fixed
- **Systray Navigation Auto-Hide (#50)**: Clicking *Screen Brightness*, *Desktop Organizer*, or *Settings* from the system tray menu now immediately hides the main shortcuts window instead of leaving it stranded in the background.
- **Quick Sliders Overlay Numeric Truncation & Slider Collisions (#52)**: Fixed clipping and overlapping labels/sliders in the overlay window by constraining slider heights to 110px, enabling card clipping, and wrapping status text.
- **Visual Distinction for Synced Master Slider (#53)**: Applied distinctive gradient styling and badges to the master synced slider so users immediately distinguish it from individual monitor controls.
- **Shortcut Table Column Vertical Alignment (#54)**: Synchronized list item grid layout and horizontal scroll compensation to eliminate column header separator distortion.
- **Backup Setting Persistence & State Memory (#1, #8)**: Ensured PreferredBrowser, AutoCheckForUpdates, UpdateChannel, SkippedUpdateVersion, and SettingsWindowState are fully serialized in backup exports and remembered across app sessions.
- **Typing Delay Inline Validation (#12)**: Added inline validation messages alerting users when typing delay inputs fall outside the valid 1–200 ms range.
- **Non-Topmost Dialog Focus (#17)**: Removed forced topmost flags on confirmation dialogs to prevent broken system Z-ordering.

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
