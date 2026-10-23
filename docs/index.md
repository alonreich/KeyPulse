# KeyPulse Documentation Index

Read [`project_structure.txt`](../project_structure.txt) first (mandatory rules and hardware guard rails). Then open ONLY the files your change touches.

## Route by task

| You are changing... | Read |
|---|---|
| Startup args, Dev Mode, single-instance mutex, native DLL extraction, start-with-Windows, admin task / elevation, installer, uninstaller, updates, canary rollback | [architecture/boot-install-update.md](architecture/boot-install-update.md) |
| Main window, shortcut table, editor, search/sort, Settings dialog, `ApplyHotkeys` | [architecture/main-window.md](architecture/main-window.md) |
| Global hotkey registration, capture mode, `RunOnThread`, typing / paste / clipboard injection | [architecture/hotkeys-input.md](architecture/hotkeys-input.md) |
| Backup export / restore, encryption, payload validation, rollback | [architecture/backup-restore.md](architecture/backup-restore.md) |
| Desktop icon layouts, auto-snapshots, Shell COM / STA worker, undo | [architecture/desktop-organizer.md](architecture/desktop-organizer.md) |
| Brightness / contrast, DDC/CI, laptop backlight, presets, overlay slider, brightness hotkeys | [architecture/screen-brightness.md](architecture/screen-brightness.md) |
| `config.json` / `ConfigStore`, `AtomicFileWriter`, DPAPI target protection, named-pipe IPC, COM activation, JSON source generation | [architecture/persistence-interop.md](architecture/persistence-interop.md) |
| `dev.cmd`, `dev_build.cmd`, `build.cmd`, release pipeline, sentinels, CI, `developer_tools\` | [architecture/dev-workflow-release.md](architecture/dev-workflow-release.md) |
| Any file, folder, registry key, pipe or event path the app creates or deletes | [architecture/paths.md](architecture/paths.md) |
| Adding / moving / deleting repository files | [architecture/repository-layout.md](architecture/repository-layout.md) |

A change that crosses subsystems (for example: a new persisted setting shown in Settings and carried in backups) reads each file it touches.

## Route by source file

| File | Read |
|---|---|
| `Program.cs`, `App.axaml(.cs)`, `SetupWindow.cs`, `UpdateChecker.cs` | boot-install-update.md |
| `MainWindow.axaml(.cs)`, `MainWindow.Actions.cs`, `MainWindow.Settings.cs`, `MainWindow.Backup.cs` | main-window.md |
| `HotKeyManager.cs`, `InputSimulator.cs` | hotkeys-input.md |
| `BackupService.cs` | backup-restore.md |
| `DesktopOrganizerService.cs`, `DesktopOrganizerWindow.axaml(.cs)` | desktop-organizer.md |
| `ScreenBrightnessService.cs`, `ScreenBrightnessWindow.cs`, `ScreenBrightnessOverlayWindow.cs`, `ScreenBrightnessListReconciler.cs`, `DdcWriteSafety.cs` | screen-brightness.md |
| `Models.cs`, `ConfigStore.cs`, `CryptoHelper.cs`, `AtomicFileWriter.cs`, `IpcService.cs`, `ComActivation.cs` | persistence-interop.md |
| `build\*`, `*.cmd`, `developer_tools\*`, `.github\workflows\*` | dev-workflow-release.md |

## Record your change

- Technical changes: `## [Unreleased]` in [changes.md](changes.md).
- User-facing impact: `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md).
- If you change a behaviour described in an architecture file, update that file in the same change. Keep each fact in exactly one file.
