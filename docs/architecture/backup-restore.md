# Backup & Restore

> Read `project_structure.txt` (mandatory rules + hardware guard rails) before this file. Route: [`docs/index.md`](../index.md).

## Backup & Restore (`BackupService.cs`)
Everything that can refuse, warn or roll back lives in `BackupService` so the UI cannot skip a check.
The rule it exists to enforce: **a file replaces the user's shortcuts only after it has proved what it
is, proved it is undamaged, and been read end to end.** Nothing is touched before all three hold.

- **File format v2**: a readable `BackupEnvelope` (FileType, FormatVersion, CreatedUtc,
  CreatedByVersion, Encryption, KDF parameters, `PayloadSha256`) wrapping a base64 `BackupPayload`.
  The envelope stays readable on purpose so the file can identify itself, be dated, and be recognised
  as encrypted BEFORE anyone is asked for a passphrase.
- **Encryption at rest (optional, offered on every export)**: AES-256-GCM, key from
  PBKDF2-SHA256 with a random 16-byte salt and 300,000 iterations, 12-byte nonce, 16-byte tag,
  AAD `"KeyPulseBackup.v2"`. Both the derivation and the write run on a background thread. There is
  no recovery path for a forgotten passphrase and the UI says so.
- **Integrity**: `PayloadSha256` is checked BEFORE the payload is parsed, so a bit-flip or a
  truncation can never reach the shortcut list. `Write` also reads the finished file back and
  re-decrypts it - a backup that cannot be read again is not a backup.
- **Atomic write**: through `AtomicFileWriter.WriteAtomic` with fallback protection.
- **What travels**: every shortcut (including `DisplayName`, `KeyCombinations`, `IsTargetObfuscated`, `AddToQuickActionMenu`),
  the complete Desktop Organizer configuration and profiles (`BackupPayload.DesktopOrganizer`),
  the complete Screen Brightness configuration, hardware bindings, lock states, and presets (`BackupPayload.ScreenBrightness`),
  the browser/sound/theme/typing-speed/sort preferences, `HasSeenTrayHint`, and the full window
  layout - main, Settings and Setup geometry plus all four column widths.
- **What deliberately does NOT travel**: `AppConfig.LaunchOnBoot`. Whether KeyPulse starts with
  Windows is a property of the machine (a registry value or a scheduled task), and silently turning
  it on because a backup from another PC had it on would be an unpleasant surprise. Restore says so.
- **Targets are exported in plain form inside the payload**, on purpose: on disk they are DPAPI-bound
  to this Windows account (see [persistence-interop.md](persistence-interop.md), Target protection), so a protected copy could never be restored anywhere else -
  which is the one case backups exist for. Protection for the FILE is the passphrase.
- **Geometry is validated against the CURRENT machine** (`ApplyRestoredWindowLayout`): sizes are
  clamped to the working area, a position is used only when it lands on a monitor that is actually
  attached, and `Minimized` is never restored. A position from a three-monitor desk would otherwise
  open the window somewhere the user cannot reach, which looks exactly like the app failing to start.
- **Corruption vs. machine differences**: `ValidatePayload` refuses the WHOLE file for structural
  damage (an action code outside `ActionType`, a null row, a failed checksum) and imports-and-flags
  anything that is merely different about this PC (keys already taken, a missing folder, a
  combination this build cannot parse). Rejecting a file for the second kind is what made restore
  useless on a new PC.
- **Backwards compatibility**: every backup taken by an earlier build is a raw copy of `config.json`.
  `Inspect` recognises those, decrypts their legacy `ENC:` targets, and upgrades them. The guard that
  keeps this from reopening the old data-loss hole is a `JsonDocument` shape check: a `"Hotkeys"`
  array must be PRESENT IN THE FILE. A deserializer's default empty list is never good enough.
- **Rollback**: `ConfigStore.SaveAllRollbackCopies("before-restore")` writes timestamped rollback copies of all active stores (`config.json`, `desktop_organizer.json`, and `screen_brightness.json`) before a restore replaces them. Any exception during import restores all snapshots; if the rollback itself fails, the message reports the backup paths to recover by hand.

## Directives

- **NEVER accept a restore file lacking the `KeyPulseBackup` header** (or, for a legacy file, a real `"Hotkeys"` array present in the json), and never restore without confirming first and writing a rollback copy.
- **NEVER parse a backup payload before its checksum has been verified.**
