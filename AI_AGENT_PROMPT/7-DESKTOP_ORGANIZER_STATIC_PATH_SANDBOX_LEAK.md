CONTEXT: Read .\project_structure.txt.
# TASK SPECIFICATION: 7 - DESKTOP_ORGANIZER_STATIC_PATH_SANDBOX_LEAK

## 1. TARGET CONTEXT & SCOPE
- File Path: `src\KeyPulse\DesktopOrganizerService.cs`
- Target Range: Lines 187 to 188
- Defect Classification: Modernization Bottleneck

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `DesktopOrganizerService.ConfigPath` and `LkgConfigPath` are declared as `public static readonly string = Path.Combine(ConfigStore.ConfigDirectory, ...)`. In C# and the .NET runtime, static readonly fields are evaluated once at class initialization time. If `ConfigStore.ConfigDirectory` is updated dynamically (e.g. during test fixtures, automated CI test runs, portable USB mode, or sandboxed test harnesses), `DesktopOrganizerService` retains the stale initial `%APPDATA%` path. This breaches isolation by persisting test layout data into the host user's actual roaming/local AppData profile and breaks multi-tenant sandbox environments.
- Target Pattern: Dynamic Property Binding (Expression-Bodied Property Getters).

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - Preserve file names (`desktop_organizer.json` and `desktop_organizer.lkg.json`).
  - Preserve compatibility with `ConfigStore.ConfigDirectory`.
  - Preserve Last-Known-Good backup restoration routines.
- Public Interface Parity: Maintain exact public member names and types:
  - `public static string ConfigPath => Path.Combine(ConfigStore.ConfigDirectory, "desktop_organizer.json");`
  - `public static string LkgConfigPath => Path.Combine(ConfigStore.ConfigDirectory, "desktop_organizer.lkg.json");`

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate lines 187-188 of `DesktopOrganizerService.cs`.
2. Convert `public static readonly string ConfigPath` and `public static readonly string LkgConfigPath` into read-only expression-bodied properties (`=>`).
3. Audit any internal callers to ensure paths are evaluated dynamically on each configuration load or save invocation.
4. Verify that altering `ConfigStore.ConfigDirectory` immediately redirects `ConfigPath` and `LkgConfigPath` to the updated directory.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler or linter warnings under NativeAOT compilation.
- Behavioral Validation: Unit tests configuring custom temporary test directories confirm that `DesktopOrganizerService` reads and writes strictly within the redirected sandbox directory without touching `%APPDATA%`.
- Performance Check: Zero measurable performance impact from string path combination on file operations.
Please update .\project_structure.txt when done, then please run .\dev_build.cmd to make sure all is now still succesfully compiling Native AOT and no issues/warning/errors.
Then, when finally done with all, add test file testing this scenario carefully to .\tests\ folder and then when done writing the relvant thorough test files which covers this scenario then locate this current prompt which we are working on (the same prompt we are performing this session) and only rename this ai agent prompt from the .\AI_AGENT_PROMPT\ folder for only the done ai agent prompt alone to "DONE_<THE OLD AI AGNENT PROMPT NAME STILL HERE>"
