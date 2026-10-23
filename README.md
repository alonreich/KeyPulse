# KeyPulse 🔑

**KeyPulse** is a lightning-fast, ultra-lightweight Windows hotkey manager designed for power users. Engineered natively in C# 13 and .NET 9 with Avalonia UI and published via NativeAOT, it offers instant cold starts, microscopic memory footprints, and clean Win32 hotkey integration.

## Features

- ⚡ **NativeAOT Compiled**: A single, self-contained binary. No .NET runtime installation required.
- 🎯 **Safe Win32 Hotkey Integration**: Utilizes Windows' native `RegisterHotKey` API with zero low-level keyboard hooks, eliminating false-positive antivirus warnings.
- 🎯 **Dynamic Multi-Key Capture**: Record zero (manual-only), one, or multiple shortcut combinations per action, plus one-click System Tray Quick Actions (`⚡`).
- 🛠️ **5 Core Shortcut Actions**:
  1. **Open Folder**: Instantly launch Windows Explorer to any path.
  2. **Launch Program**: Start any executable, script, document (e.g., HTML, PDF, text files), or directory dynamically via Windows Shell.
  3. **Browse Web**: Open web URLs (`http://`, `https://`) or local web documents (`file:///`) in your configured browser (Default, Chrome, Edge, Brave, Firefox).
  4. **Type Text**: Simulates hardware-level scan-code typing for predefined text (works in games, RDP, and VMs).
  5. **Insert Text (Paste)**: Injects large chunks of text instantly while preserving and restoring HGLOBAL clipboard formats.
- ☀️ **Hardware-Safe Screen Brightness & Contrast Control**: Dedicated multi-screen control panel with per-preset customizable shortcuts, forward and backward cycle shortcuts (e.g. `Ctrl+Alt+Num+` / `Ctrl+Alt+Num-`), out-of-the-box dual-key step adjustment (`Ctrl+Alt+=, Ctrl+Alt+NumPadAdd` and `Ctrl+Alt+-, Ctrl+Alt+NumPadSubtract`), interactive **Inter-Display Link/Sync Connectors (`⇄ SYNC`)** allowing pairwise and clustered display synchronization between adjacent monitors, tactile minimalist `−` and `+` square master stepper buttons, in-memory 60 FPS linked **All Displays (Synced) Master Slider** with real-time vertical slider tracking and **Boundary Clamping Memory** (maintains custom differential offsets across 2, 3, or more displays; clamps safely at 0% or 100% to protect physical hardware and controllers, unclamping in exact reverse order upon return), dynamic baseline re-anchoring on individual display adjustments, pointer-capture drag session tracking preventing mid-drag slider resets, resizable and movable **Brightness/Contrast Slider** overlay with window geometry persistence, user-friendly custom display nicknames displayed everywhere (slider card headers, direct hotkey dropdowns, preset editor tables, and on-screen identify banners), configurable **Instant Screen Level Hotkeys** (e.g. `Ctrl+Alt+1` -> Samsung 4K to 70%), collision-safe Avalonia input event hotkey recording, interactive step delta selector (1%, 2%, 5%, 10%), per-screen enable checkboxes for granular display control (e.g. brighten Display A to 60% while leaving Display B untouched), active-display-only dynamic layout (automatically hides disconnected screens and resizes overlay), dual vertical sliders, locked or independent Brightness/Contrast ratios, hardware DDC/CI (`dxva2.dll`) with low-level VCP opcode (`0x10` Luminance, `0x12` Contrast) fallback + blind write support for HDMI/DP displays, internal laptop backlight (`\\.\LCD`), and stable Hardware ID binding.
  - 🛡️ **Hardware Protection Protocol**: Rigorous safety guard rails protect your monitor's microcontrollers and GPU I2C bus: only standard brightness (VCP 0x10) and contrast (VCP 0x12) are ever written, every value is read first and skipped if the monitor already has it (sparing its settings memory), nothing is written to a monitor whose range could not be read, held keys and slider drags are written only once they settle (500ms, at most every 1.5s while still moving), a refusing monitor is never auto-retried, 50ms spacing between all DDC/CI commands, a 5% contrast floor so the picture never goes fully black, sequential multi-display writes (eliminating bus contention), serialized background multi-screen polling, decoupled contrast error resilience, strict raw value clamping to hardware limits, and decoupled zero-thrash disk I/O during slider movement.
- 🖥️ **Desktop Organizer**: Save, rename, and restore multi-monitor desktop icon layouts with atomic LKG persistence, overwrite confirmation prompts, non-overlapping responsive profile cards, explicit `Save`/`Cancel` inline renaming, customizable placement for unsaved new icons (primary screen center, untouched in place, right-aligned, or secondary monitor center), and optional periodic auto-snapshots with custom day intervals (`1–30 days`, retains latest 5 while protecting manual layouts forever).
- 🔄 **Automated Boot-Time Update Checks & In-App Updates**: Nonblocking startup check against GitHub releases with configurable release channels (**Stable** and **Beta**), quiet tray notifications on silent boot, formatted "What's New" release notes viewer, in-flight download cancellation with progress indicators, a dedicated 15-minute download client for slow connections, and cryptographic SHA-256 and PE header validation prior to execution.
- 🛡️ **Update Safety Canary & Automatic Crash Rollback**: Preserves `KeyPulse.exe.bak` during in-place updates, sets a post-update health canary (`.post_update_canary`) with a 15-second startup stabilization window, and automatically prompts for 1-click rollback if a newly updated binary encounters a crash loop during startup.
- 🔒 **Last Known Good (LKG) Redundancy & Corruption Guard**: Automatically mirrors verified settings to `config.lkg.json`. Deserialization errors automatically recover from LKG and set Read-Only session mode to prevent blank defaults from overwriting user shortcuts.
- ⚙️ **Consolidated 4-Tab Settings Dialog**: Modern categorized preferences dialog featuring 4 top-level application groupings (**General**, **Appearance**, **Displays & Icons**, **Backup & Updates**), single-instance enforcement with modal dialog ownership, instant live search filtering across all settings, 1-click admin privilege toggle ("Run as Admin", "Revert to User") with startup notice direct "Restart As Administrator" relaunch, interactive `👁` password reveal toggle in backup/restore passphrase dialogs, typing delay inline range validation (`1–200 ms`), preferred web browser selector (System Default, Chrome, Edge, Firefox, Brave) with real-time detection, conditional update channel dimming, direct Desktop Organizer automation toggles, contextual per-tab and global "Restore Defaults", persistent window size/position/state memory, and an isolated red Danger Zone.
- 🛡️ **Conflict Probing & Guided Resolution**: Actively probes and validates your custom hotkeys against existing OS/App shortcuts to prevent collisions, automatically scrolling to and selecting conflicting shortcuts for instant editing.
- 💾 **Encrypted Backup & State Persistence**: Remembers your window layouts, sizes, shortcuts, desktop layouts, and brightness presets across reboots, with optional AES-256-GCM encrypted backup/restore, direct "Show in File Explorer" export revelation, and multi-store rollback copies protecting `config.json`, `desktop_organizer.json`, and `screen_brightness.json`.
- ♿ **WCAG 2.2 AA Accessible & Polished UI**: 24×24px accessible help badge touch and keyboard targets across all windows, WCAG AA compliant Light and Dark mode contrast ratios (`AppAccentBrush` `#2563eb`, `AppSuccessBrush` `#15803d`), sequential keyboard tab ordering across all controls, accessible screen-reader names (`AutomationProperties.Name`), unsaved draft safety confirmation guards, synchronized horizontal table header scrolling, Enter-key desktop layout creation, accessible Title Case button styling, and non-color `⚠️ Unencrypted` badge indicators.
- 🚀 **Single-File Self-Installing Architecture**: Installs seamlessly into `%LOCALAPPDATA%` from a single standalone executable without secondary launcher processes or scripts, sets up Start Menu shortcuts via unmanaged COM VTables, registers safely into `appwiz.cpl` (Programs & Features), and operates silently from the System Tray. Starts as standard user by default with an optional 1-click UAC-free Task Scheduler logon elevation toggle in Settings. Upgrades feature non-intrusive desktop shortcut handling and fast runtime library verification.
- 🧪 **Automated Quality Gates & Test Suite**: Pre-release CI/CD test gates (`tests/KeyPulse.Tests`) blocking broken builds, NativeAOT platform linker and trimming gates (`-p:PublishAot=true`), detached SHA-256 checksum manifests (`SHA256SUMS.txt`), and Authenticode code-signing integration.

## Installation

1. Download the latest official release: [📥 **Download KeyPulse.exe (Latest Release)**](https://github.com/alonreich/KeyPulse/releases/latest/download/KeyPulse.exe)
2. Run the downloaded `KeyPulse.exe`. 
3. The built-in setup engine will seamlessly move the executable to its local app data directory, establish all necessary Registry keys, and launch the application into your System Tray.

## Uninstallation

KeyPulse fully registers itself within Windows. To remove it cleanly:
- Open **Add or Remove Programs** (or `appwiz.cpl`).
- Search for **KeyPulse**.
- Click **Uninstall**. The built-in uninstaller will purge the application and its registry footprint completely.

## Usage

- Click or double-click the tray icon to toggle the main shortcut manager window, or right-click the tray icon to access quick actions, Screen Brightness, Desktop Organizer, or **Settings**. Navigating to Screen Brightness, Desktop Organizer, or Settings from the tray automatically dismisses and hides the main shortcut key bind window.
- Click on the **Press keys...** text box and physically press the combination you want (e.g., `Ctrl+Alt+A`).
- Select your Action, provide the Target (Path/URL/Text), and click **Add**.
- Close the window to minimize KeyPulse to the System Tray.

## System Requirements

- **OS**: Windows 10 / Windows 11 (x64)
- **Dependencies**: None. (Self-Contained NativeAOT)

## Hardware Safety & Disclaimer

KeyPulse includes optional Screen Brightness & Contrast features that interface directly with physical monitor scaler microcontrollers over the GPU I2C bus via VESA DDC/CI (`dxva2.dll`) and laptop backlight drivers (`\\.\LCD`). 

- **Disabled by Default**: Hardware control remains completely dormant until you explicitly review and accept the hardware safety disclaimer. When disabled, KeyPulse performs zero display queries, sends zero DDC/CI commands, and runs no background display processes.
- **Monitor Memory Wear**: Many monitors save every brightness/contrast change to a small internal memory (NVRAM/EEPROM) with a limited number of write cycles. Very frequent changes over a long time may wear it. KeyPulse minimizes writes (read-before-write, settle-based writing, no automatic retries), but frequent adjustments are at your own discretion.
- **Warranty & Liability**: KeyPulse is free software provided "AS IS", without warranty of any kind. Under no circumstances shall the authors or contributors be liable for any direct or indirect hardware wear, monitor lockups, or damages. You may disable this feature entirely at any time in Settings.

## License

This project is licensed under the [MIT License](LICENSE).
