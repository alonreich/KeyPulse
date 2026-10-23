CONTEXT: Read .\project_structure.txt.
# TASK SPECIFICATION: 7 - SCREEN_BRIGHTNESS_BUSLOCK_THREAD_STARVATION

## 1. TARGET CONTEXT & SCOPE
- File Path: `src\KeyPulse\ScreenBrightnessService.cs`
- Target Range: Lines 1063 to 1156, 2022 to 2084, 2098 to 2212
- Defect Classification: Resource Starvation

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `ExecuteWorkerForSlot` runs on a CLR ThreadPool worker thread (`_ = Task.Run(...)`). Each of the 4 supported monitor slots maintains its own independent worker loop. Inside `TryWriteDdcMonitor`, the code executes synchronous blocking calls: `BusLock.Wait()` on `SemaphoreSlim` and `Thread.Sleep(VesaInterCommandDelayMs)` (40ms) repeatedly across retry attempts and contrast steps (up to 240ms per monitor). When a master brightness level change occurs (`RequestMasterLevelChange`), up to 4 worker threads simultaneously block the global ThreadPool waiting synchronously for `BusLock`. This starves ThreadPool worker threads and delays other asynchronous application tasks, I/O callbacks, and timers across KeyPulse.
- Target Pattern: Fully Asynchronous I2C Bus Arbitration. Replace synchronous `BusLock.Wait()` with `await BusLock.WaitAsync(ct)` and replace blocking `Thread.Sleep(VesaInterCommandDelayMs)` with non-blocking `await Task.Delay(VesaInterCommandDelayMs, ct)` inside asynchronous hardware write pipelines.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - Preserve the VESA 40ms inter-command delay (`VesaInterCommandDelayMs`) to protect monitor microcontroller hardware.
  - Preserve `ThrottleIntervalMs` hardware rate limiting (250ms).
  - Preserve circuit breaker logic (trips after 3 consecutive failures to avoid bus thrashing).
  - Preserve single-access hardware bus exclusivity (`BusLock`).
- Public Interface Parity: Maintain public API surface of `ScreenBrightnessService`.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate `ExecuteWorkerForSlot`, `WriteSingleScreenHardwareWithRetry`, and `TryWriteDdcMonitor` in `ScreenBrightnessService.cs`.
2. Convert `WriteSingleScreenHardwareWithRetry` and `TryWriteDdcMonitor` to async methods: `WriteSingleScreenHardwareWithRetryAsync` and `TryWriteDdcMonitorAsync`.
3. In `TryWriteDdcMonitorAsync`, replace `BusLock.Wait()` with `await BusLock.WaitAsync().ConfigureAwait(false)`.
4. In the `finally` block and retry backoffs, replace `Thread.Sleep(VesaInterCommandDelayMs)` with `await Task.Delay(VesaInterCommandDelayMs).ConfigureAwait(false)`.
5. Ensure `BusLock.Release()` is deterministically called in a `finally` block regardless of cancellation or exceptions.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler or linter warnings under NativeAOT compilation (`dotnet build`).
- Behavioral Validation: Multi-monitor brightness changes execute smoothly without hardware I2C bus lockups. All unit tests pass (`tests/KeyPulse.Tests`).
- Performance Check: Zero ThreadPool worker thread blocking during concurrent multi-monitor brightness adjustments.
Please update .\project_structure.txt when done, then please run .\dev_build.cmd to make sure all is now still succesfully compiling Native AOT and no issues/warning/errors.
Then, when finally done with all, add test file testing this scenario carefully to .\tests\ folder and then when done writing the relvant thorough test files which covers this scenario then locate this current prompt which we are working on (the same prompt we are performing this session) and only rename this ai agent prompt from the .\AI_AGENT_PROMPT\ folder for only the done ai agent prompt alone to "DONE_<THE OLD AI AGNENT PROMPT NAME STILL HERE>"
