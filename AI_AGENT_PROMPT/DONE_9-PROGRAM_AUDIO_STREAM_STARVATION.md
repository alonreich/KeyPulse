CONTEXT: Read .\project_structure.txt.
# TASK SPECIFICATION: 9 - PROGRAM_AUDIO_STREAM_STARVATION

## 1. TARGET CONTEXT & SCOPE
- File Path: `src\KeyPulse\Program.cs`
- Target Range: Lines 1898 to 1925
- Defect Classification: Resource Starvation

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `Program.PlaySound` handles sound playback by calling `asm.GetManifestResourceStream` and queueing a work item to the CLR `ThreadPool` with `ThreadPool.QueueUserWorkItem`. Inside the work item, it wraps the stream in a `SoundPlayer` and calls `player.PlaySync()`. Because `PlaySync()` blocks the worker thread for the entire playback duration of the WAV file (several hundred milliseconds), high-frequency hotkey triggers (e.g. holding down brightness step hotkeys or rapid multi-action executions) queue dozens of blocking tasks to the global ThreadPool. This exhausts available worker threads, delaying timers, async I/O completion callbacks, and background processing across the entire application. In addition, loading fresh assembly resource streams on every sound execution generates redundant allocations.
- Target Pattern: In-Memory Audio Buffer Pre-Caching & Non-Blocking Asynchronous Playback via Win32 `PlaySound(byte[], IntPtr.Zero, SND_ASYNC | SND_MEMORY)` or cached reusable players with debounce throttling.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - Preserve `SoundEnabled` toggle check (`if (!SoundEnabled) return;`).
  - Preserve assembly manifest resource loading convention (`KeyPulse.Assets.{soundName}.wav`).
  - Preserve failure resilience (silent recovery when audio hardware is unavailable or muted).
- Public Interface Parity: Maintain exact public method signature:
  - `public static void PlaySound(string soundName)`

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate `PlaySound` within lines 1898 to 1925 of `src\KeyPulse\Program.cs`.
2. Implement an in-memory cache (`ConcurrentDictionary<string, byte[]>`) that loads and holds raw WAV byte arrays from the assembly once upon initial request.
3. Replace the blocking `ThreadPool.QueueUserWorkItem` + `player.PlaySync()` model. Utilize native Win32 `PlaySound` with flags `SND_ASYNC | SND_MEMORY` (P/Invoke to `winmm.dll`), which executes non-blocking asynchronous audio playback in the Windows multimedia subsystem without occupying a CLR thread pool thread.
4. Add a lightweight rate-limiting timestamp per sound to throttle redundant triggers within a 50ms window.
5. Retain logging on missing embedded sound assets.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler or linter warnings under NativeAOT compilation.
- Behavioral Validation: All audio triggers (hotkey actions, brightness changes) produce clear audible sound without clipping or early termination. Rapid successive triggers do not throw or crash.
- Performance Check: ThreadPool thread count remains stable during 100 rapid hotkey presses; zero worker thread blocking observed.
Please update .\project_structure.txt when done, then please run .\dev_build.cmd to make sure all is now still succesfully compiling Native AOT and no issues/warning/errors.
Then, when finally done with all, add test file testing this scenario carefully to .\tests\ folder and then when done writing the relvant thorough test files which covers this scenario then locate this current prompt which we are working on (the same prompt we are performing this session) and only rename this ai agent prompt from the .\AI_AGENT_PROMPT\ folder for only the done ai agent prompt alone to "DONE_<THE OLD AI AGNENT PROMPT NAME STILL HERE>"
