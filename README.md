# KeyPulse 🔑

**KeyPulse** is a lightning-fast, ultra-lightweight Windows hotkey manager designed for power users. Engineered natively in C# 13 and .NET 9 with Avalonia UI and published via NativeAOT, it offers instant cold starts, microscopic memory footprints, and global hardware-level keyboard hooks.

## Features

- ⚡ **NativeAOT Compiled**: A single, self-contained binary. No .NET runtime installation required.
- 🪝 **Global Hardware Hooks**: Intercepts keyboard events at the lowest OS level using Win32 API.
- 🎯 **Dynamic Multi-Key Capture**: Record zero (manual-only), one, or multiple shortcut combinations per action, plus one-click System Tray Quick Actions (`⚡`).
- 🛠️ **6 Core Actions**:
  1. **Open Folder**: Instantly launch Windows Explorer to any path.
  2. **Launch Program**: Start any executable or script dynamically.
  3. **Browse Web**: Open URLs in your configured browser (Default, Chrome, Edge, Brave, Firefox).
  4. **Type Text**: Simulates hardware-level scan-code typing for predefined text (works in games, RDP, and VMs).
  5. **Insert Text (Paste)**: Injects large chunks of text instantly while preserving and restoring HGLOBAL clipboard formats.
  6. **Cycle Screen Brightness**: Cycles physical monitor Brightness & Contrast presets in-game with zero OSD popups and zero focus stealing.
- ☀️ **Hardware-Safe Screen Brightness & Contrast Control**: Active-display-only dynamic layout (automatically hides disconnected screens and resizes overlay to 420px, 540px, or 680px), dual vertical sliders, locked or independent Brightness/Contrast ratios, hardware DDC/CI (`dxva2.dll`) + internal laptop backlight (`\\.\LCD`), stable Hardware ID binding, inline display renaming with explicit `Save`/`Cancel` actions, and intelligent shortcut cycle edge-case handling.
  - 🛡️ **Hardware Protection Protocol**: Rigorous safety guard rails protect your monitor's microcontrollers and GPU I2C bus: sequential multi-display writes (eliminating bus contention), 30ms I2C multiplexer settling delays, 50ms VESA MCCS dual-write pauses, 250ms hardware write speed limiter with trailing-edge final flush, delta-only write filtering, strict raw value clamping to hardware limits, and decoupled zero-thrash disk I/O during slider movement.
- 🖥️ **Desktop Organizer**: Save, rename, and restore multi-monitor desktop icon layouts with atomic LKG persistence, non-overlapping responsive profile cards, explicit `Save`/`Cancel` inline renaming, customizable placement for unsaved new icons (primary screen center, untouched in place, right-aligned, or secondary monitor center), and optional periodic auto-snapshots with custom day intervals (`1–30 days`, retains latest 5 while protecting manual layouts forever).
- 🔄 **Automated Boot-Time Update Checks & In-App Updates**: Nonblocking startup check against GitHub releases with configurable release channels (**Stable** and **Beta**), quiet tray notifications on silent boot, formatted "What's New" release notes viewer, in-flight download cancellation with progress indicators, a dedicated 15-minute download client for slow connections, and cryptographic SHA-256 and PE header validation prior to execution.
- 🛡️ **Update Safety Canary & Automatic Crash Rollback**: Preserves `KeyPulse.exe.bak` during in-place updates, sets a post-update health canary (`.post_update_canary`) with a 15-second startup stabilization window, and automatically prompts for 1-click rollback if a newly updated binary encounters a crash loop during startup.
- 🔒 **Last Known Good (LKG) Redundancy & Corruption Guard**: Automatically mirrors verified settings to `config.lkg.json`. Deserialization errors automatically recover from LKG and set Read-Only session mode to prevent blank defaults from overwriting user shortcuts.
- ⚙️ **Consolidated 4-Tab Settings Dialog**: Modern categorized preferences dialog featuring 4 top-level application groupings (**General**, **Typing & Speed**, **Window & Display**, **Advanced & Safety**), instant live search filtering across all settings, customizable character delay stepper (`1–200 ms`), per-tab and global "Restore Defaults", persistent window size/position and active tab memory, and an isolated red Danger Zone.
- 🛡️ **Conflict Probing**: Actively probes and validates your custom hotkeys against existing OS/App shortcuts to prevent collisions.
- 💾 **Encrypted Backup & State Persistence**: Remembers your window layouts, sizes, shortcuts, desktop layouts, and brightness presets across reboots, with optional AES-256-GCM encrypted backup/restore and multi-store rollback copies protecting `config.json`, `desktop_organizer.json`, and `screen_brightness.json`.
- 🚀 **Self-Installing Architecture**: Installs seamlessly into `%LOCALAPPDATA%`, sets up Start Menu shortcuts, registers safely into `appwiz.cpl` (Programs & Features), and operates silently from the System Tray. Upgrades preserve Administrator privileges seamlessly and use headless `--update-worker` auto-restarts without prompt interruptions.
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

- Click or double-click the tray icon to toggle the main shortcut manager window, or right-click the tray icon to access quick actions, Screen Brightness, Desktop Organizer, or **Settings**.
- Click on the **Press keys...** text box and physically press the combination you want (e.g., `Ctrl+Alt+A`).
- Select your Action, provide the Target (Path/URL/Text), and click **Add**.
- Close the window to minimize KeyPulse to the System Tray.

## System Requirements

- **OS**: Windows 10 / Windows 11 (x64)
- **Dependencies**: None. (Self-Contained NativeAOT)
