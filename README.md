# KeyPulse 🔑

**KeyPulse** is a lightning-fast, ultra-lightweight Windows hotkey manager designed for power users. Engineered natively in C# 13 and .NET 9 with Avalonia UI and published via NativeAOT, it offers instant cold starts, microscopic memory footprints, and clean Win32 hotkey integration.

## Features

- ⚡ **NativeAOT Compiled**: A single, self-contained binary. No .NET runtime installation required.
- 🎯 **Safe Win32 Hotkeys**: Uses Windows' native `RegisterHotKey` API with no low-level keyboard hook, so KeyPulse never sees keystrokes meant for other apps.
- 🎯 **Flexible Shortcuts**: Zero (run manually), one, or several key combinations per action. Any action can also be shown in the tray menu (`⚡` badge) for one-click use.
- 🛠️ **5 Shortcut Actions**:
  1. **Open Folder**: Open any path in Windows Explorer.
  2. **Launch Program**: Start any executable, script, document or folder through the Windows Shell.
  3. **Browse Web**: Open `http://`, `https://` or `file:///` addresses in your chosen browser (Windows default, Chrome, Edge, Brave, Firefox).
  4. **Type Text**: Types text with hardware scan codes (works in games, RDP and VMs), using the keyboard layout of the window you type into.
  5. **Insert Text (Paste)**: Pastes large text instantly and puts your clipboard back afterwards.
- ☀️ **Monitor Brightness & Contrast (optional, off by default)**:
  - **Brightness slider** next to the clock: one slider per screen plus an **All screens** slider that keeps the gap between screens. Link neighbouring screens with **⇄ SYNC** so they move together.
  - **Brightness settings** window: every brightness shortcut in **one list** - Brighter / Darker (default `Ctrl+Alt+=` / `Ctrl+Alt+-` and `Ctrl+Alt+Num +` / `Ctrl+Alt+Num -`), next / previous preset, a key per preset, screen shortcuts (e.g. `Ctrl+Alt+1` -> Screen A to 70%) and Focus Mode. Up to 3 presets can be pinned to the tray menu.
  - 🎯 **Focus Mode** (`Ctrl+Alt+Num *`): dims every screen except your main one; press again to bring them back. Linked screens follow your main screen again as soon as you change its brightness. Dim level (0-30%), name and key are adjustable.
  - Works with external monitors over DDC/CI (`dxva2.dll`) and with laptop panels (`\\.\LCD`), with custom screen names and stable hardware binding.
  - 🛡️ **Hardware Protection**: only brightness (VCP 0x10) and contrast (VCP 0x12) are ever written; every value is read first and skipped if the monitor already has it; nothing is written to a monitor whose range could not be read; held keys and slider drags are written only once they settle (500 ms, at most every 1.5 s while still moving); a refusing monitor is never auto-retried; 50 ms between DDC/CI commands; multi-screen writes are strictly one at a time; contrast never goes below a 5% floor; recovers by itself after monitor sleep or display changes.
- 🖥️ **Desktop Organizer**: Save, rename and restore desktop icon layouts across all your displays. Each layout remembers the **desktop icon size** and puts it back before moving icons; if Ctrl + mouse wheel changed your icon size, the Organizer shows it with a **Reset to normal** button. Optional automatic restore when displays change, automatic backups every 1-30 days (latest 5 kept, manual layouts never deleted), a choice of where new icons go, and one-step undo.
- 🔄 **Updates**: Background update checks against GitHub releases (**Stable** and **Beta** channels), "What's New" notes, resumable downloads with cancel. A download is installed only if it is signed by the KeyPulse release certificate (checked by Windows and pinned by thumbprint) or matches its published SHA-256 checksum.
- 🛡️ **Update Safety Canary & Rollback**: Keeps `KeyPulse.exe.bak` during updates and offers a one-click rollback if a new version keeps crashing at startup.
- 🔒 **Settings Never Silently Lost**: Atomic saves done in the background, a last-known-good copy (`config.lkg.json`), dated history backups (recent ones plus one per day for two weeks), and read-only protection when a settings file is damaged.
- ⚙️ **Settings Dialog (5 tabs)**: **General**, **Appearance**, **Brightness**, **Desktop icons**, **Backup & Updates**, with live search, a **Run as admin** switch, typing speed, browser choice, theme, and one **Reset…** button that lets you pick exactly what to reset (shortcuts and saved layouts are never reset).
- 🛡️ **Conflict Probing**: Checks your shortcuts against keys already taken by Windows or other apps and points you to the conflicting row.
- 💾 **Backup & Restore**: Optional AES-256-GCM encrypted backups of shortcuts, preferences, desktop layouts and brightness presets, with rollback copies taken before every restore.
- 📂 **"Add to KeyPulse" in Explorer**: Right-click a file or folder to create a shortcut for it.
- ♿ **Accessible UI**: Light and Dark themes with WCAG AA contrast, keyboard navigation and screen-reader names throughout.
- 🚀 **Self-Installing Single File**: Installs into `%LOCALAPPDATA%`, registers in Programs & Features, adds Start Menu shortcuts, and runs from the tray. Starts as a normal user; an optional UAC-free admin logon task lets shortcuts reach apps that run as administrator.
- 🧪 **Release Gates**: The full test suite, regression sentinels and a warnings-as-errors NativeAOT build must pass before a release; releases are code-signed and ship with `SHA256SUMS.txt`.

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

- Click or double-click the tray icon to toggle the main shortcut window, or right-click it for the Brightness slider, your pinned presets, Focus Mode, Desktop Organizer, your tray-menu shortcuts, and **Settings**.
- Click the **Press keys here** box and press the combination you want (e.g., `Ctrl+Alt+A`).
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
