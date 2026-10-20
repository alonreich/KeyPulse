# Technical Changes Log

All internal technical, architectural, and low-level engineering changes to KeyPulse are continuously tracked in this document by autonomous AI agents and core release engineers.

> **AI Agent & Developer Directive**:
> Whenever an agent or engineer makes technical changes to this repository, record every technical alteration, internal mechanism, refactoring, security validation, and API change under `## [Unreleased]`.
> During official release packaging, consumer-facing release notes must be curated and promoted to `docs/CHANGELOG.md` in clean, user-friendly language.

## [Unreleased]

### Display Hardware Control Lockup & Autonomous DDC/CI Bus Recovery (`ScreenBrightnessService.cs`, `HotKeyManager.cs`)
- **Root Cause Analysis & Throttle State Machine Deadlock Fix**:
  - Investigated issue where KeyPulse monitor brightness control suddenly ceased responding after monitor sleep or idle timeouts, only recovering if Twinkle Tray was launched as Administrator.
  - Root Cause 1: In `ExecutePendingWriteForSlot`, `throttle.LastWrittenBrightness = targetB` and `throttle.LastWrittenContrast = targetC` were unconditionally updated regardless of whether `WriteSingleScreenHardwareWithRetry` succeeded. When a monitor entered DPMS sleep or had an I2C bus timeout, the write failed but the throttle recorded the target as written. Subsequent slider adjustments or hotkey presses back to that value were discarded as "targets unchanged", permanently deadlocking hardware writes.
  - Root Cause 2: KeyPulse lacked OS-level display change and power broadcast listeners. Twinkle Tray listened to `WM_DISPLAYCHANGE` and `WM_POWERBROADCAST`, issuing privileged VESA VCP wake commands (`0xDF`, `0xD6`, `0x10`) that reset the GPU display controller's I2C multiplexer. Running Twinkle Tray elevated reset the GPU driver's I2C bus, which indirectly allowed KeyPulse to communicate again.
  - Fix: In `ExecutePendingWriteForSlot`, guarded throttle updates so `LastWrittenBrightness` and `LastWrittenContrast` only update when `written == true`.
  - Added `WakeDdcBus(IntPtr hPhys)` issuing non-destructive VESA VCP `0xDF` and `0x10` handshake queries to wake dormant display scalers and synchronize I2C clocks.
  - Added physical monitor handle cache invalidation and settling delay prior to retry in `WriteSingleScreenHardwareWithRetry`.
  - Converted `HotKeyManager` message window from `HWND_MESSAGE` (`-3`) to a top-level hidden unowned window (`IntPtr.Zero`), enabling reception of broadcast `WM_DISPLAYCHANGE` (`0x007E`) and `WM_POWERBROADCAST` (`0x0218`) messages.
  - Dispatched `ScreenBrightnessService.OnDisplayChangedOrWoken()` and `DesktopOrganizerService.HandleDisplayChangeDebounced()` on broadcast events, restoring autonomous bus recovery without external dependencies.

### Master Slider Ceiling Breakthrough Virtual Level Batch Updates (`ScreenBrightnessService.cs`, `ScreenBrightnessOverlayWindow.cs`, `ScreenBrightnessWindow.cs`)
- Added `SetScreenLevelsBatch` overload taking 5-tuples `(SlotIndex, Brightness, Contrast, VirtualBrightness, VirtualContrast)` in `ScreenBrightnessService`.
- Updated `ApplyMasterSliderMovement` in `ScreenBrightnessOverlayWindow` and `ScreenBrightnessWindow` to maintain uncompressed linear differential offsets (`vb`, `vc`) during ceiling and floor breakthrough.
- Removed destructive `ReanchorAllScreens()` call from master slider `PointerReleasedEvent`, preserving relative differential ratios seamlessly when dragging back down from 100%.

### Preset Target Sliders In-Memory Update & Pointer Release Persistence (`ScreenBrightnessWindow.cs`)
- Removed synchronous `ScreenBrightnessService.SaveConfig(cfg)` from `bSlider.PropertyChanged` and `cSlider.PropertyChanged` in preset editor rows.
- Bound persistence to `PointerReleasedEvent` and `PointerCaptureLostEvent`, updating in-memory during drag and writing to disk only upon user release.

### Dynamic Quick Presets Bar in Overlay (`ScreenBrightnessOverlayWindow.cs`)
- Promoted local `presetsPanel` to private field `_presetsPanel`.
- Wired `PopulateQuickPresetsBar(_presetsPanel)` inside `OnServiceStateChanged()`, dynamically synchronizing quick preset buttons, active highlight states, and labels upon profile switches or configuration modifications.

### Screen Direct Hotkey Default Baseline Standardization (`Models.cs`)
- Updated `ScreenDirectHotkey.Brightness` and `Contrast` initial factory defaults from 65% to 50% to align with application-wide 50% balanced baseline.

### Desktop Shell COM PIDL Leak Prevention on Cancellation (`DesktopOrganizerService.cs`)
- Wrapped `RestoreLayout` loop and unsaved item positioning in `try ... finally` blocks to guarantee `CoTaskMemFree` is called for every unmanaged `item.Pidl` pointer in `unsavedItems`, preventing shell memory leaks when layout restoration is cancelled.

### Master Slider Drag Boundary Physics & Breakthrough Interpolation (`ScreenBrightnessOverlayWindow.cs`, `ScreenBrightnessWindow.cs`, `ScreenBrightnessService.cs`)
- **Two-Stage Drag Boundary Physics**:
  - Implemented `_brickWallUp` and `_brickWallDown` calculations based on drag baseline screens.
  - Phase 1 stops master slider motion firmly at the brick wall once the highest screen reaches 100% or lowest reaches 0%, preserving relative differentials.
  - Phase 2 (`_breakthroughUp` / `_breakthroughDown`) activates on the second pull/gesture past the wall, interpolating remaining displays from their brick-wall value to 100% (or 0%) smoothly.
  - Fixed premature drag session cancellation in `PointerCaptureLostEvent`.
  - Added `SetScreenLevelsBatch` in `ScreenBrightnessService` for atomic multi-screen level updates during master drag.

### Inter-Display Sync Averaging & 1:1 Lockstep Tracking (`ScreenBrightnessService.cs`, `ScreenBrightnessOverlayWindow.cs`, `ScreenBrightnessWindow.cs`)
- **Immediate Averaging on Link (`ScreenBrightnessService.SetScreenLinkedToNext`)**:
  - When `isLinked` becomes true, calculates average brightness and contrast across all screens in the linked group and assigns both immediately.
  - Queues throttled hardware writes and notifies UI state immediately.
- **1:1 Live Lockstep (`ScreenBrightnessService.RequestIndividualOrLinkedChange`)**:
  - When dragging an individual slider in a linked pair, sets linked companion displays directly to match the dragged level 1:1.
- **Preserved `IsLinkedToNext` across Hardware Reads**:
  - Added missing `IsLinkedToNext = slotCfg.IsLinkedToNext` assignment in `ReadSingleScreenHardware` and fallback `results[i]`, preventing async hardware reads from clearing linked state.
- **Win32 Monitor Enumeration Caching**:
  - Added 250ms debounce cache to `GetConnectedSlotIndices`, eliminating high-frequency `EnumDisplayMonitors` calls during slider drag ticks.
- **Synchronous UI Dispatch on Current Thread**:
  - In `OnServiceStateChanged`, executes update synchronously if `Dispatcher.UIThread.CheckAccess()` is true, ensuring zero-latency lockstep slider animation.

### Default Preset Values Update (`ScreenBrightnessService.cs`, `Models.cs`, `ScreenBrightnessWindow.cs`)
- Updated factory shipping defaults:
  - Screen default levels: 50% across Screen A, B, C.
  - Night / Low preset: 25% brightness & contrast.
  - Balanced preset: 50% brightness & contrast.
  - Daylight / Competitive preset: 75% brightness & contrast.
  - Updated restore defaults dialog text to reflect 25%, 50%, 75%.

### Inter-Display Link/Sync Connectors & Real-Time Slider Propagation (Models.cs, ScreenBrightnessService.cs, ScreenBrightnessOverlayWindow.cs, ScreenBrightnessWindow.cs)
- **Pairwise Display Sync Data Model (Models.cs, ScreenBrightnessService.cs)**:
  - Added IsLinkedToNext (bool, default alse) to ScreenHardwareSetting and ScreenLiveState.
  - Added GetLinkedSlotGroup(ScreenBrightnessConfig cfg, int slotIndex) resolving transitive connected display groups across adjacent slots.
  - Added SetScreenLinkedToNext(int slotIndex, bool isLinked) persisting pairwise links and re-anchoring baseline offsets.
  - Implemented RequestIndividualOrLinkedChange(slotIndex, brightness, contrast, isBrightnessChanged, isContrastChanged): when a slider on an individual screen changes, propagates delta movements symmetrically across all linked displays in its sync group while respecting individual monitor LockRatio configurations.
- **Inter-Display Link Connector UI (ScreenBrightnessOverlayWindow.cs, ScreenBrightnessWindow.cs)**:
  - Inserted high-contrast ⇄ SYNC toggle buttons between adjacent display cards within _containersGrid (columns *, Auto, *, Auto, *).
  - Active linked state features deep sky blue #0369A1 background, electric cyan #38BDF8 border, bold white glyphs, and descriptive tooltips. Unlinked state features subtle #1E293B background and #94A3B8 foreground.
  - Narrowed display container card padding from 12, 10 to 10, 8 for sleek multi-monitor layout balance.
- **Master Slider Disk Thrashing & Boundary Stutter Elimination**:
  - Removed synchronous SaveConfig(cfg) on every drag tick in RequestMasterLevelChange, enforcing strictly in-memory updates. Disk writes are deferred to the existing background trailing-edge throttle in ExecutePendingWriteForSlot.
  - Added preserveVirtual: true in RequestScreenLevelChangeInternal so boundary clamp writes do not destroy VirtualBrightness or VirtualContrast offset memory.
  - Cached container grid configuration (_lastRenderedScreenConfig) to prevent destructive visual tree tearing (_containersGrid.Children.Clear()) on every slider tick.
- **Live Vertical Sliders Visual Tracking Fix**:
  - Removed unconditional if (view.UserTouched) return; block in ScreenContainerBuilder.SyncFromConfigState.
  - Added IsDraggingBrightness and IsDraggingContrast tracking to ScreenContainerView, ensuring vertical companion sliders glide live with master or companion slider adjustments without fighting local cursor drags.

## [v2026.10.20.0939] - 2026-10-20

### Multi-Monitor Virtual Clamping Memory & Drag Session State (`Models.cs`, `ScreenBrightnessService.cs`, `ScreenBrightnessOverlayWindow.cs`, `ScreenBrightnessWindow.cs`)
- **Virtual Clamping Offsets (`Models.cs`, `ScreenBrightnessService.cs`)**:
  - Added `VirtualBrightness` and `VirtualContrast` (default 65, clamp range `[-100, 200]`) to `ScreenHardwareSetting`.
  - In `RequestMasterLevelChange`, calculated `newVirtualB = Math.Clamp(screen.VirtualBrightness + delta, -100, 200)` and derived physical level `newPhysicalB = Math.Clamp(newVirtualB, 0, 100)`. Hardware writes clamp to valid [0, 100] DDC/CI / backlight ranges, while differential offsets are remembered even when multiple screens hit 0% or 100%.
  - Added `ReanchorAllScreens()` called on individual slider changes to synchronize `VirtualBrightness = LastBrightness` and `VirtualContrast = LastContrast`, establishing the new differential ratio without offset drift.
  - Added `_isUserDraggingMaster` drag session tracking attached via pointer capture events (`PointerPressed`, `PointerReleased`, `PointerCaptureLost`) to prevent background state synchronization from interrupting or resetting the slider thumb mid-drag.
  - Master slider baseline in both main window and overlay window now tracks the rounded average of all connected screens.

### UI Simplification & Custom Display Nickname Propagation
- **Master Stepper Controls**:
  - Replaced `−5%` / `+5%` labels with minimalist `−` / `+` bold 16pt glyphs styled with 32x30 square footprint, centered alignment, and hand cursor.
- **Consistent Display Nickname Display**:
  - In `ScreenContainerBuilder.CreateScreenContainer`, `ApplyLiveStateToContainer`, and `SyncFromConfigState`: when a custom nickname is present, `SlotBadge` is hidden (`IsVisible = !hasCustomNick`) and title displays the nickname directly.
  - In `ScreenBrightnessWindow` direct shortcut and preset editor rows: display `sCfg.CustomNickname` directly instead of prepending default slot names.
  - In `ScreenBrightnessService.ShowIdentifyBanners`: prominent identification banner shows custom nickname directly.
- **Overlay Window Renaming**:
  - Renamed "Quick Sliders (Overlay)" to "Brightness/Contrast Slider" across `App.axaml.cs` tray menu, `ScreenBrightnessWindow.cs`, and `ScreenBrightnessOverlayWindow.cs`.

### Non-Intrusive Desktop Shortcut on Update (`SetupWindow.cs`)
- In `InstallWorkerAsync` Step 5, added probe checking `(_isUpdate || isUpgrade) && desktop != null && !File.Exists(desktop)`. When detected, desktop shortcut creation is skipped (`desktop = null; shouldCreateDesktop = false`), avoiding unwanted shortcut re-creation on clean desktops. Updated `CreateShortcutsAsync` with `desktopShortcutRequested` parameter to avoid false-positive warning logs.

### Update Installer Elevation & Admin Task Preservation Resilience
- **Scheduled Task Fast-Path & Non-Elevated Protection (`Program.cs`)**:
  - In `Program.CreateOrUpdateAdminTask`, added a non-destructive fast-path querying `TryGetAdminTaskViaCom` and `RunSchtasks /Query /XML`. If `KeyPulse_AdminTask` is already registered, targets `Program.ExePath`, and possesses `<RunLevel>HighestAvailable</RunLevel>`, it verifies XML integrity without invoking destructive `RegisterTask` or `schtasks /Create /XML /F` commands that fail with `ERROR: Access is denied` under unelevated tokens.
  - Enabled non-elevated task state transitions using `schtasks /Change /TN KeyPulse_AdminTask /ENABLE` and `/DISABLE`.
  - Added elevated fallback helper invoking `ExePath --setup-admin-task --for-user "{user}"` with `Verb = "runas"` if an unelevated installer must create a fresh task.
- **Update Worker Elevation Trigger (`MainWindow.axaml.cs`)**:
  - In `ExecuteRestart()`, evaluated `shouldElevate = Program.IsElevated || Program.IsAdminTaskInstalled() || Program.GetAdminTaskState() == Program.AdminTaskState.Enabled`.
  - Configured updater child process `ProcessStartInfo` to launch with `Verb = "runas"` and append `--was-elevated` whenever administrative privileges or startup tasks are active.
- **Setup Window Progress Feedback (`SetupWindow.cs`)**:
  - Refined Step 6 logging to "Preserved Administrator privileges (logon task configured with battery restrictions disabled)."

### Settings & Preferences Information Architecture & Wiring (Audit Issues #1–#17)
- **Settings Architecture & Tab Sprawl Consolidation (#9)**: Consolidated settings from 8 tabs into 4 top-tier application categories: General (category 0: Startup, Sound, Typing delay, Web Browser, Admin mode), Appearance (category 1: Light/Dark/System theme), Displays & Icons (category 2: Screen Brightness & Desktop Organizer), and Backup & Updates (category 3: Updates, Backup/Import, Danger Zone).
- **Desktop Organizer Automation Settings (#10)**: Surfaced AutoRestoreOnDisplayChange and AutoPeriodicSnapshotEnabled checkboxes directly on the Displays & Icons preferences card in MainWindow.axaml.cs, synchronized with DesktopOrganizerService.
- **Conditional Update Channel Dimming (#11)**: Implemented RefreshUpdateChannelInteractivity() to dynamically disable and dim channelCombo (55% opacity) when AutoCheckForUpdates is disabled.
- **Typing Delay Inline Validation (#12)**: Added inline speedErrorText block validating 1–200 ms range inputs, auto-reverting to current config on invalid entries.
- **Admin Privilege Microcopy & Light Contrast (#5, #15)**: Simplified privilege elevation buttons to concise native text ("Run as Admin", "Revert to User") with WCAG AA compliant theme-aware styling.
- **Expanded Preferred Web Browser Selection (#3, #16)**: Added PreferredBrowser to AppConfig and BackupPayload, with support for "System", "Chrome", "Edge", "Firefox", and "Brave", complete with real-time detection via TryFindBrowser and automatic fallback.
- **Backup State Persistence & Settings Window Memory (#1, #8)**: Added SettingsWindowState (Normal, Maximized) and serialized PreferredBrowser, AutoCheckForUpdates, UpdateChannel, and SkippedUpdateVersion into BackupPayload and ApplyBackupPayload.
- **Password Mask/Reveal Toggle (#4)**: Added interactive 👁 toggle buttons to password fields in AskBackupPassphraseAsync and AskUnlockPassphraseAsync.
- **Modal Window Ownership & Settings Single-Instance (#6, #7)**: Added _activeSettingsWindow guard to prevent duplicate Settings instances and established modal parenting (ShowDialogOrWindowAsync(..., owner: w)) for all child dialogs.
- **Non-Topmost Dialog Focus (#17)**: Removed forced topmost flags in ConfirmSettingsActionAsync to preserve clean system Z-ordering.

### Screen Brightness & Tray Lifecycle (#50–#54)
- **Systray Navigation Auto-Hide (#50)**: In MainWindow.axaml.cs, added `this.Hide()` when opening ScreenBrightnessWindow, DesktopOrganizerWindow, or Settings from the system tray menu.
- **Differential Synced Master Brightness Slider (#51)**: Renamed master bar to ⚡ ALL DISPLAYS (SYNCED): with a SYNCED MASTER badge. Moving individual screen sliders recalibrates differential baselines (UpdateMasterBaselineFromScreens), preserving custom per-monitor offsets during master slider or hotkey adjustments.
- **Movable, Resizable & Memory-Enabled Overlay (#52)**: Enabled CanResize = true and BeginMoveDrag on ScreenBrightnessOverlayWindow. Persisted OverlayWindowX, OverlayWindowY, OverlayWindowWidth, and OverlayWindowHeight in ScreenBrightnessConfig. Fixed slider clipping by setting slider MinHeight = 110, card ClipToBounds = true, and wrapping status text.
- **Master Slider Visual Distinction (#53)**: Applied linear gradient styling (#162032 to #1e1b4b), vibrant border gradient (#38BDF8 to #818CF8), and SYNCED MASTER badge to master control bars.
- **Custom Shortcut Keys Table Alignment (#54)**: Standardized zero margins and padding in ListBox.ShortcutList in App.axaml, aligned headers in MainWindow.axaml, and synchronized horizontal scroll offsets in HotkeyList_ScrollChanged.

### Process Elevation & Admin Mode Notice
- **One-Click Admin Relaunch from Startup Notice**: Replaced static error popup in VerifyAdminModeIsRealAsync() with a dedicated dialog containing an immediate "Restart As Administrator" action button and RelaunchAsAdministrator(adminNoticeWin), enabling seamless elevation via UAC with clean state persistence and mutex handoff.

### Hardware Display Control & DDC/CI Interop
- **DDC/CI Concurrency Hardening & Direct Write Throttling (`ScreenBrightnessService.cs`)**:
  - In `TryWriteDdcMonitor`, eliminated redundant pre-write I2C read queries that previously triggered I2C bus lockups and slider freezes when run concurrently with other monitor utilities (e.g., Twinkle Tray).
  - Implemented a 3-attempt hardware write retry loop with a 35ms sleep between attempts (`Thread.Sleep(35)`) for both brightness (`SetMonitorBrightness` / VCP `0x10`) and contrast (`SetMonitorContrast` / VCP `0x12`).
  - Standardized inter-command pause between brightness and contrast writes to 40ms (`Thread.Sleep(40)`).
  - Added a 50ms pause before re-querying monitor handles via `BindMonitorsToSlots(cfg)` upon write failures.
  - In `ReadAllScreensLiveAsync`, replaced parallel polling with serialized sequential execution with a 35ms delay between physical displays (`await Task.Delay(35, ct)`), eliminating GPU I2C multiplexer bus contention.
  - Removed manual "Read Hardware" and "Cycle Next Step" buttons in `ScreenBrightnessWindow.cs` in favor of automated safe timing background polling.
- **Multi-Monitor Differential Lock with Boundary Compression**: Added `public bool LockMonitorDifferential { get; set; } = true;` to `ScreenBrightnessConfig` in `Models.cs`. In `ScreenBrightnessService.TriggerBrightnessStep`, delta adjustments clamp each display independently to `[0, 100]`. This preserves relative offsets during steps while naturally compressing when individual screens hit 0% or 100%. When disabled, `TriggerBrightnessStep` resolves cursor location via `GetCursorPos` and `MonitorFromPoint` to step only the screen with mouse cursor focus.
- **GPU I2C Bus Multiplexer Collision Guard**: Implemented `private static readonly object HardwareWriteLock = new();` in `ScreenBrightnessService.WriteSingleScreenHardwareWithRetry` with an enforced 30ms inter-command settling delay (`Thread.Sleep(30)`). This serializes physical DDC/CI I2C bus access across distinct monitor ports, preventing GPU packet drops and enabling simultaneous multi-monitor adjustments.
- **Linked "All Displays" Master Control**: Added `ScreenBrightnessService.RequestMasterLevelChange(int deltaBrightness)` and linked master slider controls in `ScreenBrightnessOverlayWindow` and `ScreenBrightnessWindow`, automatically adjusting all active displays in tandem.
- **Instant Screen Level Hotkeys System**: Added `ScreenDirectHotkey` model with `Id`, `Name`, `Hotkey`, `TargetSlotIndex` (-1 for all, 0..2 for specific displays), `Brightness`, `Contrast`, and `LockRatio`. Added NativeAOT serialization entries in `AppConfigJsonContext`. Implemented `ScreenBrightnessService.TriggerDirectHotkey(ScreenDirectHotkey)` and global hotkey registration supporting comma-separated key chords.
- **Tray Overlay Lifecycle & Auto-Close Fix**: Added `_openedUtc` timestamp and a 600ms deactivation grace window (`if ((DateTime.UtcNow - _openedUtc).TotalMilliseconds < 600) return;`) in `ScreenBrightnessOverlayWindow.OnOverlayDeactivated`, preventing Windows taskbar menu dismissal from immediately closing the overlay upon opening.
- **Virtual Key Aliasing for Minus and Hyphen**: Added mappings for `"minus"`, `"-"`, and `"oemminus"` to `0xBD` (`VK_OEM_MINUS`) in `HotkeyManager.KeyNameAliases`, enabling multi-key step bindings like `"Ctrl+Alt+=, Ctrl+Alt+NumPadAdd"` and `"Ctrl+Alt+-, Ctrl+Alt+NumPadSubtract"`.
- **Granular Multi-Display Preset Targeting**: Added `public bool Enabled { get; set; } = true;` to `ScreenCycleEntry` in `Models.cs`. In `ScreenBrightnessService.ApplyProfileToMemory` and `ApplyProfileToHardwareDeltaOnly`, slots with `target == null || !target.Enabled` are skipped, allowing presets to target a single monitor (e.g. Display A only = 60%) while leaving other displays completely untouched. Removed single-screen fallback logic that previously copied Slot 0 levels to Slot 1.
- **Direct Brightness Step Hotkeys (±5%)**: Added `StepUpHotkey`, `StepDownHotkey`, and `StepDelta` (default 5%) to `ScreenBrightnessConfig` in `Models.cs`. In `ScreenBrightnessService.TriggerBrightnessStep`, added global delta adjustment across all connected displays via `RequestScreenLevelChange`. Registered hotkeys globally in `RegisterBrightnessHotkeys()`. Added corresponding interactive hotkey recorders in `ScreenBrightnessWindow.CreateCycleHotkeysPanel`.
- **Low-Level Hook Hotkey Recording & Conflict Resolution**: In `ScreenBrightnessWindow.AttachHotkeyRecorder`, activated `HotkeyManager.EnableCaptureHook()` and raw key event listeners while a hotkey box has focus. This prevents Windows from swallowing combinations like `Ctrl+Alt+NumPadSubtract` as active OS hotkeys before they can be assigned. Added `ClearConflicting` logic across all cycle and step hotkey boxes to prevent duplicate bindings.
- **Hardware Write Throttle Loop Prevention**: In `ScreenBrightnessService.ExecutePendingWriteForSlot`, updated `throttle.LastWrittenBrightness = targetB` and `throttle.LastWrittenContrast = targetC` unconditionally upon write attempt completion. Previously, failed hardware writes left `LastWritten` unupdated, creating an infinite 250ms runaway retry loop against monitors that reject queries.
- **Bidirectional Preset Cycling**: Updated `ScreenBrightnessService.TriggerShortcutCycleStep(bool reverse = false)` to support forward and reverse cycling across ordered presets, capped to a maximum of 10 presets per sequence. Redundant no-op probe steps continue to be filtered against active displays.
- **Dedicated Global Screen Brightness Hotkey Service**: Added `CycleUpHotkey` and `CycleDownHotkey` to `ScreenBrightnessConfig` and `Hotkey` to `ScreenBrightnessProfile`. Added `ScreenBrightnessService.RegisterBrightnessHotkeys()` and `UnregisterAllBrightnessHotkeys()` to register these shortcuts globally via `HotkeyManager.Register`, decoupling brightness hotkeys from the main shortcut list. Wired lifecycle hooks into `MainWindow.RegisterAllHotkeys` and `MainWindow.OnClosed`.
- **DDC/CI Blind Write Fallback & Diagnostic Logging**: In `ScreenBrightnessService.TryWriteDdcMonitor`, if both `GetMonitorBrightness` and low-level VCP opcode `0x10` queries fail (common over certain HDMI/DisplayPort GPU driver implementations), brightness bounds now default to 0..100 and execute blind writes using both `SetMonitorBrightness` and `SetVCPFeature(hPhysical, 0x10, rawVal)`. Added `Marshal.GetLastWin32Error()` logging on write failures.
- **Hardware DDC/CI VCP Opcode Fallback Interop**: Added P/Invoke imports `SetVCPFeature(IntPtr, byte, uint)` and `GetVCPFeatureAndVCPFeatureReply(IntPtr, byte, out uint, out uint, out uint)` from `dxva2.dll` in `ScreenBrightnessService.cs`. In `TryWriteDdcMonitor` and `TryReadDdcMonitor`, added automatic fallbacks to VCP opcode `0x10` (Brightness/Luminance) and opcode `0x12` (Contrast) when standard high-level DXVA2 calls (`GetMonitorBrightness`/`SetMonitorBrightness`) fail.
- **Hardware DDC Contrast Error Decoupling**: Updated `TryWriteDdcMonitor` so failure to write contrast on monitors lacking VCP `0x12` support or with locked contrast ratios does not invalidate a successful brightness write (`wroteB`), preventing retry loops and dropped brightness commands.
- **Diagnostic Tracing Instrumentation**: Added granular `Program.LogDebug` diagnostic logging across `RequestScreenLevelChange`, `QueueThrottledHardwareWrite`, `ExecutePendingWriteForSlot`, `WriteSingleScreenHardwareWithRetry`, and `TryWriteDdcMonitor`.
- **Live User Slider Interaction Guard**: Added `if (view.UserTouched) return;` check in `ScreenContainerBuilder.SyncFromConfigState` in `ScreenBrightnessOverlayWindow.cs`, ensuring background configuration changes never overwrite slider coordinates while a user is actively dragging or wheel-scrolling.

### Shell Interop & Target Resolution
- **Shell Document & Folder Execution**: In `MainWindow.TryResolveLaunchTarget` and `TrySplitLaunchCommand`, added checks for `Directory.Exists` and non-executable `File.Exists` (e.g. `.html`, `.pdf`, `.docx`), allowing dynamic shell launching via `ProcessStartInfo` with `UseShellExecute = true`.
- **Local File Scheme Normalization**: Updated `MainWindow.TryNormalizeUrlTarget` to accept and normalize `file://` URIs and `.html`/`.htm` file paths.
- **Unsaved Shortcut Auto-Commit**: Implemented dirty-state tracking and auto-commit in `MainWindow.HotkeyList_SelectionChanged` to preserve uncommitted edits when navigating away from a row.
- **Conflict Navigation**: Enhanced duplicate key detection in `MainWindow.Add_Click` to automatically scroll to and select conflicting shortcuts.

### UI / UX & WCAG 2.2 AA Accessibility
- **Comprehensive UI/UX Audit Implementations (ISSUE_01 through ISSUE_14)**:
  - **ISSUE_01 (Accessible Help Badges)**: Implemented 24×24px minimum touch and keyboard focus targets on help badges with explicit `Focusable="True"` and descriptive `AutomationProperties.Name` across `MainWindow.axaml`, `ScreenBrightnessWindow.cs`, `DesktopOrganizerWindow.axaml`, and `SetupWindow.cs`.
  - **ISSUE_02 (Light Theme Contrast Tokens)**: Updated Light theme contrast tokens to meet WCAG 2.2 AA (>= 4.5:1 text, >= 3:1 UI): `AppAccentBrush` (`#2563eb`), `AppSuccessBrush` (`#15803d`), and `AppSuccessSoftBrush` (`#166534`) in `App.axaml`.
  - **ISSUE_03 (Ordered Tab Navigation)**: Implemented sequential `TabIndex` ordering from 0 to 18 (`KeyCombo`: 0, `AddKeyButton`: 1, `ActionCombo`: 2, `ActionNameText`: 3, `TargetText`: 4, `ObfuscateCheck`: 5, `QuickActionCheck`: 6, `ExpandTargetBtn`: 7, `BrowseBtn`: 8, `AddButton`: 9, `CancelEditButton`: 10, `SearchBox`: 11, Table Headers: 12..15, Bottom Actions: 16..18) in `MainWindow.axaml`.
  - **ISSUE_04 (Accessible Screen-Reader Names)**: Assigned explicit `AutomationProperties.Name` to table column headers, settings tabs, and the tray overlay close button.
  - **ISSUE_05 (Unsaved Draft Confirmation Guard)**: Added dirty-draft detection in `MainWindow.HotkeyList_SelectionChanged` prompting users before switching selections away from an active uncommitted new shortcut draft.
  - **ISSUE_06 (Synchronized Header Horizontal Scrolling)**: Synchronized `ShortcutHeaderGrid.Margin` with `ScrollViewer.Offset.X` in `MainWindow.axaml.cs` to prevent table column headers from drifting out of alignment with row cells during horizontal scroll.
  - **ISSUE_07 (Theme Resource Token Resolution)**: Replaced undefined `AppBgBrush` resource reference with `AppSurfaceBrush` in `ScreenBrightnessWindow.cs`.
  - **ISSUE_08 (Enter Key Desktop Layout Saving)**: Added `KeyDown` listener for `Key.Enter` on `NewProfileNameBox` in `DesktopOrganizerWindow.axaml.cs` to trigger saving without mouse interaction.
  - **ISSUE_09 (Manual Update Check Status)**: Added visible "Checking for updates..." status text when clicking "Check for Updates" in Settings.
  - **ISSUE_10 (Modal Escape Key Dismissal)**: Wired `Key.Escape` handler to dismiss the wipe confirmation dialog in `SetupWindow.cs`.
  - **ISSUE_11 (Context-Aware Setting Reset)**: Dynamically hid the non-applicable "Restore Tab Defaults" button on the Backup settings tab.
  - **ISSUE_12 (Title/Sentence Case Buttons)**: Standardized all-caps button text ("RUN", "REMOVE", "DUPLICATE") to Title/Sentence case ("Run", "Remove", "Duplicate") in `MainWindow.axaml`.
  - **ISSUE_13 (Vertical Slider Clearance)**: Added vertical margins in `ScreenBrightnessOverlayWindow.cs` (`Margin="0,0,0,8"` on percentage text, `Margin="0,6,0,8"` on sliders, `Margin="0,6,0,0"` on labels) preventing slider thumbs and text labels from overlapping numeric readouts.
  - **ISSUE_14 (Anchored Action Name Field)**: Replaced unanchored floating layout of `ActionNameText` with `HorizontalAlignment="Left"` directly beneath the "Action Name" header label in `MainWindow.axaml`.

### Security & Cryptographic Integrity
- **DPAPI Target Protection Flag Scope**: In `Models.cs` (`TargetProtectionFailed`), changed condition to `CryptoHelper.ProtectionUnavailable && IsTargetObfuscated`. In `MainWindow.axaml.cs`, restricted the startup security notice to only show when `CryptoHelper.ProtectionUnavailable && unprotected > 0` for obfuscated shortcuts. Non-secret shortcuts (e.g. standard URLs and paths) are no longer flagged as DPAPI encryption failures.
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

### Codebase Optimization & Dead Code Pruning
- **Uncompressed Native Library Cleanup**: Deleted unreferenced uncompressed native DLLs `av_libglesv2.dll` (~5.4 MB), `libHarfBuzzSharp.dll` (~1.8 MB), and `libSkiaSharp.dll` (~11.6 MB) in `src/KeyPulse/NativeLibs/`, freeing ~18.8 MB of repository bloat left behind by embedded GZip compression.
- **Legacy Interop & Dead Code Removal in DesktopOrganizerService**: Removed unused P/Invokes `SendMessageTimeout` and `GetWindowThreadProcessId`, unused helper `SafeSendMessageTimeout`, unused ListView constants (`LVM_FIRST`, `LVM_GETITEMCOUNT`, `LVM_GETITEMPOSITION`, `LVM_SETITEMPOSITION32`, `LVM_GETITEMTEXTW`, `LVM_GETITEMSPACING`), unused memory constants (`PROCESS_VM_OPERATION`, `PROCESS_VM_READ`, `PROCESS_VM_WRITE`, `MEM_COMMIT`, `MEM_RESERVE`, `MEM_RELEASE`, `PAGE_READWRITE`), unused constant `SMTO_ABORTIFHUNG`, and unreferenced property `LastUndoProfileName`.
- **Dead Method Removal in Program.cs**: Removed unreferenced native library hashing helper `FileHashMatches`.
- **Dead Property Removal in Models.cs**: Removed unreferenced `[JsonIgnore]` property `SummaryText` from `DesktopLayoutProfile`.
- **Orphaned Test Directory Cleanup**: Removed stale MSTest output directory tree in `tests/KeyPulse.Tests/TestResults`.

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
