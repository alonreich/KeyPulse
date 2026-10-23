CONTEXT: Read .\project_structure.txt.
# TASK SPECIFICATION: 10 - MAINWINDOW_MONOLITHIC_GOD_CLASS

## 1. TARGET CONTEXT & SCOPE
- File Path: `src\KeyPulse\MainWindow.axaml.cs` (and partial files `MainWindow.Actions.cs`, `MainWindow.Backup.cs`, `MainWindow.Settings.cs`)
- Target Range: Lines 1 to 3792 (`MainWindow.axaml.cs`), 6,580 LOC total across partials
- Defect Classification: Modernization Bottleneck

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `MainWindow` functions as an architectural god class encompassing 6,580 lines of code across 4 partial files. It tightly couples Avalonia UI presentation logic with direct Win32 API invocations, Task Scheduler XML generation, UAC elevation management, Windows Registry scanning for multiple browser variants (Chrome, Edge, Firefox, Brave), AES-GCM encrypted backup archive packing, and low-level file system persistence. This tight coupling violates the Single Responsibility Principle, creates high regression risks when modifying UI elements, impedes automated unit testing of core business logic, and hinders NativeAOT linker optimization and trimming.
- Target Pattern: Modular Service Extraction / Clean Separation of Concerns. Extract non-UI responsibilities into dedicated domain services (`StartupElevationService`, `BrowserDetectionService`, `BackupManager`, and `HotkeyCoordinator`) with clear interfaces.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - Preserve exact Task Scheduler XML schema, logon trigger configurations, and highest-privilege runlevel flags for UAC-free startup.
  - Preserve specific Registry paths and executable probing sequences for Default, Chrome, Edge, Brave, and Firefox browsers.
  - Preserve full encryption, compression, and format compatibility for KeyPulse configuration backups (`.kpbackup`).
  - Preserve window state persistence (position, size, minimize-to-tray, and multi-monitor bounds checking).
- Public Interface Parity: Maintain exact public view API surface and Avalonia XAML control event handlers.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate the disparate subsystems embedded within `MainWindow.axaml.cs` and its partial classes.
2. Extract the Task Scheduler elevation and startup logic into a dedicated domain service (e.g. `StartupElevationService`).
3. Extract browser Registry discovery logic into a standalone `BrowserDetectionService`.
4. Decouple backup creation, AES decryption/encryption, and archive handling into `BackupService` enhancements, leaving `MainWindow.Backup.cs` strictly responsible for file picker dialogs and UI feedback.
5. Retain exact public event signatures and wire the extracted services into `MainWindow`, ensuring clean separation between presentation and system infrastructure.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler or linter warnings under NativeAOT compilation (`dotnet build`).
- Behavioral Validation: All unit tests in `tests/KeyPulse.Tests` pass. Startup elevation toggle, browser shortcuts, backup/restore workflows, and hotkey bindings function with 100% behavioral parity.
- Performance Check: Reduced UI initialization latency, improved code readability, and isolated unit-testable service boundaries.
Please update .\project_structure.txt when done, then please run .\dev_build.cmd to make sure all is now still succesfully compiling Native AOT and no issues/warning/errors.
Then, when finally done with all, add test file testing this scenario carefully to .\tests\ folder and then when done writing the relvant thorough test files which covers this scenario then locate this current prompt which we are working on (the same prompt we are performing this session) and only rename this ai agent prompt from the .\AI_AGENT_PROMPT\ folder for only the done ai agent prompt alone to "DONE_<THE OLD AI AGNENT PROMPT NAME STILL HERE>"
