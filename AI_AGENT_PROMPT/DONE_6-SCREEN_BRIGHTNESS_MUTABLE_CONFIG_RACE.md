CONTEXT: Read .\project_structure.txt.
# TASK SPECIFICATION: 6 - SCREEN_BRIGHTNESS_MUTABLE_CONFIG_RACE

## 1. TARGET CONTEXT & SCOPE
- File Path: `src\KeyPulse\ScreenBrightnessService.cs`
- Target Range: Lines 53, 181 to 204, 371 to 424
- Defect Classification: Race Condition

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `LoadConfig()` caches a singleton reference in `private static ScreenBrightnessConfig? _cachedConfig` and returns this shared mutable reference directly to all callers across multiple threads. Inside `NormalizeConfig()` (called by `SaveConfig`, `CycleBrightnessPreset`, and profile switches), collections like `cfg.Screens` and `cfg.Profiles` are directly modified (e.g. `cfg.Screens.Add(...)`, `cfg.Screens = cfg.Screens.OrderBy(...).ToList()`). Callers on UI threads (such as `ScreenBrightnessWindow` or overlay renderers) iterating over `cfg.Screens` concurrently experience `InvalidOperationException: Collection was modified; enumeration operation may not execute`. Additionally, background DDC polling threads mutating `LastBrightness` on shared instances produce torn reads and state corruption.
- Target Pattern: Immutable Configuration Snapshots / Thread-Safe Defensive Cloning & Copy-On-Write semantics.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - Preserve `NormalizeConfig()` normalization rules (default slot names, clamping brightness/contrast to [0, 100], `LockRatio` coupling).
  - Preserve `MaxScreens` slot constraints and slot index ordering.
  - Preserve fallback reading from `BackupConfigFilePath` when `ConfigFilePath` is corrupt or missing.
- Public Interface Parity: Maintain exact public method signature:
  - `public static ScreenBrightnessConfig LoadConfig()`
  - `public static bool SaveConfig(ScreenBrightnessConfig config)`

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate `_cachedConfig`, `LoadConfig()` (lines 181-204), and `NormalizeConfig()` (lines 371-424).
2. Implement deep cloning or immutable snapshot generation in `LoadConfig()`. When `_cachedConfig` is returned, return either a cloned snapshot or an immutable representation so callers cannot mutate the shared cache.
3. In `SaveConfig()`, clone the incoming configuration prior to normalizing and writing to disk, ensuring thread-safe copy-on-write semantics.
4. Replace mutable collection references during normalization with thread-safe atomic swaps (`Interlocked.Exchange`) or synchronize read access to `Screens` and `Profiles`.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler or linter warnings under NativeAOT compilation.
- Behavioral Validation: Unit tests pass (`tests/KeyPulse.Tests`). Rapid concurrent calls to `LoadConfig()`, `SaveConfig()`, and UI iterations of `Screens` and `Profiles` execute without throwing `InvalidOperationException`.
- Performance Check: High-frequency hotkey stepping incurs negligible cloning overhead while eliminating collection mutation races.
Please update .\project_structure.txt when done, then please run .\dev_build.cmd to make sure all is now still succesfully compiling Native AOT and no issues/warning/errors.
Then, when finally done with all, add test file testing this scenario carefully to .\tests\ folder and then when done writing the relvant thorough test files which covers this scenario then locate this current prompt which we are working on (the same prompt we are performing this session) and only rename this ai agent prompt from the .\AI_AGENT_PROMPT\ folder for only the done ai agent prompt alone to "DONE_<THE OLD AI AGNENT PROMPT NAME STILL HERE>"
