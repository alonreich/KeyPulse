# File System Paths & Legacy Cleanup

> Read `project_structure.txt` (mandatory rules + hardware guard rails) before this file. Route: [`docs/index.md`](../index.md).

> [!IMPORTANT]
> **AI AGENT DIRECTIVE:** DO NOT delete or overwrite this section. If you change a path used by this application, you MUST move the old path to the "Legacy / Deprecated Paths" list and add the new path to the "Active Paths" list. The application installer and uninstaller (`SetupWindow.cs`) MUST always take full responsibility for explicitly targeting and permanently deleting all legacy paths to maintain a pristine user environment.

## Active Paths (Currently in Use)
- **Installation Directory:** `%LOCALAPPDATA%\Programs\KeyPulse`
- **Application Binary:** `%LOCALAPPDATA%\Programs\KeyPulse\KeyPulse.exe`
- **Application Backup Binary:** `%LOCALAPPDATA%\Programs\KeyPulse\KeyPulse.exe.bak` (previous working executable preserved during in-place update)
- **Post-Update Canary:** `%LOCALAPPDATA%\Programs\KeyPulse\.post_update_canary` (ephemeral post-update launch verification marker for crash-loop rollback)
- **Native Runtime Libraries:** `%LOCALAPPDATA%\Programs\KeyPulse\runtime` (verified by SHA-256 against embedded resources unless `native.fingerprint` matches the running build. Removed with the install directory on uninstall.)
- **Native Library Build Fingerprint:** `%LOCALAPPDATA%\Programs\KeyPulse\runtime\native.fingerprint` (app version + embedded library names/lengths; gates the startup fast path; deleted with the runtime folder)
- **Library Verification Stamps:** `%LOCALAPPDATA%\Programs\KeyPulse\runtime\*.dll.stamp` (metadata record of verified hash; deleted with the runtime folder)
- **Configuration & State:** `%APPDATA%\KeyPulse\config.json`
- **Configuration LKG Backup:** `%APPDATA%\KeyPulse\config.lkg.json` (last known good copy, refreshed only by a successful save; `Load()` never writes it)
- **Configuration History Backups:** `%APPDATA%\KeyPulse\config.backup-<utc yyyyMMdd-HHmmss>.json` (at most one per 30 min; newest 5 plus one per day for 14 days kept) and `%APPDATA%\KeyPulse\config.safety-shortcuts.json` (copy taken before a save that would leave zero shortcuts); removed with the whole folder on uninstall
- **Desktop Organizer Configuration:** `%APPDATA%\KeyPulse\desktop_organizer.json` (persisted multi-monitor desktop icon snapshot profiles)
- **Desktop Organizer LKG Backup:** `%APPDATA%\KeyPulse\desktop_organizer.lkg.json` (last known good copy verified on each successful save/load)
- **Screen Brightness Configuration:** `%APPDATA%\KeyPulse\screen_brightness.json` (persisted multi-monitor hardware bindings, lock states, and brightness/contrast presets)
- **Screen Brightness LKG Backup:** `%APPDATA%\KeyPulse\screen_brightness.lkg.json` (last known good copy verified on each successful save/load)
- **Diagnostic Logs:** `%APPDATA%\KeyPulse\logs\keypulse_debug.txt` and `keypulse_crash.txt` (plus `.old` rollovers). Deliberately inside `%APPDATA%\KeyPulse` so the uninstaller's existing purge of that folder takes them with it.
- **Restore Rollback Copies:** `%APPDATA%\KeyPulse\config.before-restore-*.json`, `%APPDATA%\KeyPulse\desktop_organizer.before-restore-*.json`, and `%APPDATA%\KeyPulse\screen_brightness.before-restore-*.json` (five most recent kept; removed with the whole folder on uninstall)
- **Code Signing Assets:** `ssl-certificate/KeyPulse_CodeSigning.pfx`, `ssl-certificate/KeyPulse_CodeSigning.cer`, `ssl-certificate/Create-SelfSignedCert.ps1`, `ssl-certificate/Install-Certificate.ps1` (self-signed Authenticode certificate whose thumbprint the updater pins; tamper detection only, no SmartScreen reputation). The signing private key used by `build.cmd` lives in the build machine's `Cert:\CurrentUser\My` store.
- **Dedicated Uninstaller Staging:** `%LOCALAPPDATA%\KeyPulse_Uninstall\KeyPulse.exe` (isolated staging directory maintaining PE `OriginalFilename` metadata)
- **Dedicated Update Staging:** `%LOCALAPPDATA%\KeyPulse\updates\` (or `%LOCALAPPDATA%\KeyPulse_DEV\updates\` in dev mode) for staging downloaded updater packages prior to hash verification and execution
- **Quarantined Config Backups:** `%APPDATA%\KeyPulse\config.damaged-*.json`, `%APPDATA%\KeyPulse\desktop_organizer.damaged-*.json`, and `%APPDATA%\KeyPulse\screen_brightness.damaged-*.json` (created only when the live configs cannot be parsed; removed with the whole `%APPDATA%\KeyPulse` folder on uninstall)
- **Pre-Wipe Safety Copies:** `%APPDATA%\KeyPulse.before-wipe-*.json`, `%APPDATA%\KeyPulse.config.lkg.before-wipe-*.json`, `%APPDATA%\KeyPulse.desktop_organizer.before-wipe-*.json`, and `%APPDATA%\KeyPulse.screen_brightness.before-wipe-*.json` - dated copies written BESIDE the config folder before a "Wipe Settings" install destroys it (three most recent kept). They live outside the folder on purpose so the wipe cannot take them; the UNINSTALLER deletes them for a pristine exit.
- **Explorer "Add to KeyPulse" Hand-off:** Low-integrity Named Pipe `\\.\pipe\KeyPulse_IPC_Session_<SessionId>` (primary) with exact-read framing, with queued multi-line append fallback to `%APPDATA%\KeyPulse\staged-add.txt`, claimed for reading as `staged-add.txt.claimed-<guid>` (deleted once read); removed with the whole folder on uninstall.
- **Explorer Context Menu (registry, HKCU):** `Software\Classes\*\shell\KeyPulse.Add`, `Software\Classes\Directory\shell\KeyPulse.Add`, `Software\Classes\Directory\Background\shell\KeyPulse.Add` - "Add to KeyPulse" on files, folders and folder background (classic menu on Win10, "Show more options" on Win11). Registered by the installer, removed by the uninstaller.
- **Named shutdown event:** `KeyPulse_ExitApp` (session shared event, same low-label SDDL as the mutex) - the installer's graceful "please exit now" channel, because closing the main window only hides it.
- **Developer Sandbox Root (`DEVDATA_01`, Dev Mode Only):** `%LOCALAPPDATA%\KeyPulse_DEV` (`data\` for live sandboxed `config.json`, `desktop_organizer.json`, and `screen_brightness.json`; `keep\` for clean-baseline state restoration across `dev.cmd` runs; `runtime\` for isolated dev native libraries; `%TMP%\KeyPulse_DEV` or `.devlogs\` for dev logs; and `\\.\pipe\KeyPulse_IPC_DEV_Session_<SessionId>` for dev IPC).
- **Build Logging:** `developer_tools\build.log` and `developer_tools\build.result.txt` (isolated from repository root)
- **Developer Scratch:** `developer_tools\scratch\` (isolated directory for transient icon previews, mockups, and test artifacts)

## Legacy / Deprecated Paths (Must be cleaned up by Installer/Uninstaller)
- `C:\Program Files\KeyPulse` (Abandoned global install directory)
- `C:\Program Files (x86)\KeyPulse` (Abandoned global install directory)
- `.\build.log` (Legacy build log path at repository root, relocated to `developer_tools\build.log`)
- `%TEMP%\KeyPulse_NativeLibs` (Legacy temporary native library extraction folder, superseded by the `runtime` subfolder)
- `%TEMP%\KeyPulse_Runtime` (Deprecated insecure %TEMP% native library fallback extraction folder, eliminated to prevent DLL hijacking)
- `%LOCALAPPDATA%\KeyPulse` (Legacy log folder holding `keypulse_debug.txt` / `keypulse_crash.txt`. No uninstaller ever removed it, so a record of every shortcut the user pressed survived uninstalling. Both the installer and the uninstaller now delete it.)
- `%LOCALAPPDATA%\Programs\KeyPulse\boot.vbs` (Deprecated VBScript launch helper; replaced by direct executable invocation with `--hidden` argument)
- `%TEMP%\KeyPulse*.exe` (Legacy temporary execution binaries)
- `%TEMP%\keypulse_*.txt` (Legacy temporary installer log files)
