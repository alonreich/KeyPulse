CONTEXT: Read .\project_structure.txt.
# TASK SPECIFICATION: 8 - DESKTOP_ORGANIZER_UNDO_STATE_TEARING

## 1. TARGET CONTEXT & SCOPE
- File Path: `src\KeyPulse\DesktopOrganizerService.cs`
- Target Range: Lines 190, 843 to 847, 866, 1089 to 1105
- Defect Classification: Race Condition

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `_lastUndoSnapshot` is maintained as a bare static field without synchronization primitives. When a display topology change occurs, `HandleDisplayChangeDebounced()` triggers layout restoration on a background timer thread via `RestoreLayout(...)`. Concurrently, user interactions on the UI thread (`DesktopOrganizerWindow`) can trigger layout restore or undo operations via `UndoLastRestore(...)`. Because `_lastUndoSnapshot` is read, assigned (`_lastUndoSnapshot = preRestoreSnapshot`), and cleared (`_lastUndoSnapshot = null`) concurrently across threads without locking or atomics, state tearing occurs: `UndoLastRestore` may read a partially updated snapshot, or an automated display change can overwrite a user's pre-restore snapshot mid-flight, destroying undo capabilities or applying corrupted icon coordinates.
- Target Pattern: Thread-Safe State Encapsulation via synchronized critical sections (`lock (SyncLock)`) or atomic reference exchange (`Interlocked.Exchange`).

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - Preserve `HasUndo` boolean status check.
  - Preserve the rule that an undo operation itself must not overwrite the undo buffer (`if (!isUndoOperation)`).
  - Preserve `UnsavedIconBehavior.LeaveInPlace` during undo restorations.
- Public Interface Parity: Maintain exact public signature:
  - `public static bool HasUndo => ...`
  - `public static bool UndoLastRestore(out string report, CancellationToken cancellationToken = default)`

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate `_lastUndoSnapshot`, `HasUndo`, and lines 843-847, 866, and 1089-1105 in `DesktopOrganizerService.cs`.
2. Encapsulate all reads, mutations, and resets of `_lastUndoSnapshot` inside synchronized blocks using the existing `SyncLock` object, or manage `_lastUndoSnapshot` via atomic CAS (`Interlocked.CompareExchange`).
3. In `UndoLastRestore`, atomically take and clear the snapshot reference before invoking `RestoreLayout`, ensuring that concurrent background restorations cannot tear or overwrite the active undo state.
4. Ensure `HasUndo` checks the snapshot reference safely without torn reads.

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler or linter warnings under NativeAOT compilation.
- Behavioral Validation: Unit tests pass (`tests/KeyPulse.Tests`). Rapid concurrent calls to `RestoreLayout` and `UndoLastRestore` across multiple threads preserve consistent undo state without throwing null reference exceptions or restoring corrupted icon maps.
- Performance Check: Zero lock contention overhead on desktop organizer workflows.
Please update .\project_structure.txt when done, then please run .\dev_build.cmd to make sure all is now still succesfully compiling Native AOT and no issues/warning/errors.
Then, when finally done with all, add test file testing this scenario carefully to .\tests\ folder and then when done writing the relvant thorough test files which covers this scenario then locate this current prompt which we are working on (the same prompt we are performing this session) and only rename this ai agent prompt from the .\AI_AGENT_PROMPT\ folder for only the done ai agent prompt alone to "DONE_<THE OLD AI AGNENT PROMPT NAME STILL HERE>"
