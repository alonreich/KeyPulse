# KeyPulse 🔑

**KeyPulse** is a lightning-fast, ultra-lightweight Windows hotkey manager designed for power users. Engineered natively in C# 13 and .NET 9 with Avalonia UI and published via NativeAOT, it offers instant cold starts, microscopic memory footprints, and global hardware-level keyboard hooks.

## Features

- ⚡ **NativeAOT Compiled**: A single, self-contained binary. No .NET runtime installation required.
- 🪝 **Global Hardware Hooks**: Intercepts keyboard events at the lowest OS level using Win32 API.
- 🎯 **Dynamic Key Capture**: Automatically records complex shortcut combinations (e.g., `Ctrl+Alt+Shift+X`).
- 🛠️ **5 Core Actions**:
  1. **Open Folder**: Instantly launch Windows Explorer to any path.
  2. **Launch Program**: Start any executable or script dynamically.
  3. **Browse Chrome**: Open URLs directly in Google Chrome.
  4. **Type Text**: Simulates hardware-level fast-typing for predefined text.
  5. **Insert Text (Paste)**: Injects large chunks of text instantly using a temporary clipboard payload.
- 🛡️ **Conflict Probing**: Actively probes and validates your custom hotkeys against existing OS/App shortcuts to prevent collisions.
- 💾 **State Persistence**: Remembers your window layouts, sizes, and specific configurations across reboots.
- 🚀 **Self-Installing Architecture**: Installs seamlessly into `%LOCALAPPDATA%`, sets up Start Menu shortcuts, registers safely into `appwiz.cpl` (Programs & Features), and operates silently from the System Tray.

## Installation

1. Download the latest `KeyPulse.exe` from the Releases page.
2. Run `KeyPulse.exe`. 
3. The built-in setup engine will seamlessly move the executable to its local app data directory, establish all necessary Registry keys, and launch the application into your System Tray.

## Uninstallation

KeyPulse fully registers itself within Windows. To remove it cleanly:
- Open **Add or Remove Programs** (or `appwiz.cpl`).
- Search for **KeyPulse**.
- Click **Uninstall**. The built-in uninstaller will purge the application and its registry footprint completely.

## Usage

- Double-click the tray icon or right-click and select **Settings** to access the UI.
- Click on the **Press keys...** text box and physically press the combination you want (e.g., `Ctrl+Alt+A`).
- Select your Action, provide the Target (Path/URL/Text), and click **Add**.
- Close the window to minimize KeyPulse to the System Tray.

## System Requirements

- **OS**: Windows 10 / Windows 11 (x64)
- **Dependencies**: None. (Self-Contained NativeAOT)
