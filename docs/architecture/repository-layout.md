# Repository Layout

> Read `project_structure.txt` (mandatory rules + hardware guard rails) before this file. Route: [`docs/index.md`](../index.md).

```
.
├── .github/                 # GitHub workflows & CI/CD automation
├── build/                   # Release pipeline, verification sentinels, and dev sandbox scripts
│   ├── BuildPipeline.ps1
│   ├── DevSandbox.ps1
│   ├── sentinels.txt
│   └── VerifySentinels.ps1
├── developer_tools/         # Developer utilities, code analysis tools, logs, and scratch assets
│   ├── build.log            # Mirrored build execution log (isolated from root)
│   ├── build.result.txt     # One-line build verdict
│   ├── scratch/             # Transient icon previews, test images, and scratch artifacts
│   ├── BuildLog.ps1
│   ├── Find_and_Remove_Comments.py
│   ├── Manual_UnInstall.bat
│   ├── Pull_Code_files.py
│   ├── Redundant_Code_Finder.py
│   └── SetConsoleFont.ps1
├── docs/                    # Architecture documentation, changelog, and changes history
│   ├── index.md             # Task router: which architecture file to read for a change
│   ├── architecture/        # One file per subsystem (see index.md)
│   ├── CHANGELOG.md         # Public user-facing release notes (SemVer promoted)
│   └── changes.md           # Continuous technical change ledger for AI agents
├── src/                     # Core application source code and resources
│   └── KeyPulse/
│       ├── Assets/          # Sound effects and UI media resources
│       ├── NativeLibs/      # Native Avalonia dependencies (GZip compressed)
│       ├── App.axaml / .cs
│       ├── MainWindow.axaml / .cs (with .Actions.cs, .Backup.cs, .Settings.cs)
│       ├── DesktopOrganizerWindow.axaml / .cs
│       ├── KeyPulse.csproj  # Main project definition
│       ├── KeyPulse.ico     # Multi-resolution application icon
│       ├── app.manifest     # DPI & OS manifest
│       └── *.cs             # All application services, interop, and data models
├── ssl-certificate/         # Authenticode code-signing scripts and certificates
├── tests/                   # Automated unit and integration test suite
│   └── KeyPulse.Tests/
│       ├── ClipboardThreadHwndLeakTests.cs
│       ├── CoreTests.cs
│       ├── DdcHardwareSafetyTests.cs
│       ├── DdcHandleAllocationRaceTests.cs
│       ├── DdcSessionBusCollisionTests.cs
│       ├── HotkeyManagerGhostRegistrationTests.cs
│       ├── HardwareConsentAndPanelGuardTests.cs
│       ├── DesktopOrganizerStaticPathSandboxLeakTests.cs
│       ├── DesktopOrganizerUiThreadComBlockTests.cs
│       ├── DesktopOrganizerUndoStateTearingTests.cs
│       ├── MainWindowServiceExtractionTests.cs
│       ├── ProgramAudioStreamStarvationTests.cs
│       ├── ScreenBrightnessAsyncVoidCrashTests.cs
│       ├── ScreenBrightnessAtomicPersistenceTests.cs
│       ├── ScreenBrightnessBusLockThreadStarvationTests.cs
│       ├── ScreenBrightnessMutableConfigRaceTests.cs
│       └── ScreenBrightnessUiRenderThrashingTests.cs
├── .gitattributes
├── .gitignore
├── build.cmd                # Official release and build entry point (non-interactive, pushes to GitHub)
├── dev.cmd                  # Developer sandbox entry point (watch, rebuild, trace, sandbox)
├── dev_build.cmd            # Fast local NativeAOT test compiler (no git/tag/remote operations)
├── Directory.Build.props    # Central build configuration properties
├── LICENSE                  # Project license
├── project_structure.txt    # Entry point: mandatory rules + hardware guard rails, routes to docs/index.md
├── README.md                # Project README and quickstart guide
└── version.txt              # SemVer version stamp
```
