CONTEXT: Read .\project_structure.txt.
# TASK SPECIFICATION: 6 - SCREEN_BRIGHTNESS_UI_RENDER_THRASHING

## 1. TARGET CONTEXT & SCOPE
- File Path: `src\KeyPulse\ScreenBrightnessWindow.cs`
- Target Range: Lines 1859 to 1900, 2604 to 2649
- Defect Classification: Modernization Bottleneck

## 2. PROBLEM ANALYSIS & ROOT CAUSE
- Flaw Mechanism: `OnServiceStateChanged()` calls `RenderProfilesList()` and `RenderDirectHotkeysList()` on every `StateChanged` notification. When a user presses or holds brightness adjustment hotkeys (`TriggerBrightnessStep` or `TriggerShortcutCycleStep`), `NotifyStateChanged(immediate: true)` fires repeatedly. `RenderProfilesList()` executes `_profilesStack.Children.Clear()`, destroying and reallocating all UI controls, cards, input textboxes, badges, and sliders across all configured presets. This causes severe UI thread thrashing (dozens of visual tree tears and rebuilds per second) and destroys keyboard focus if the user has an inline preset rename box (`nameBox`) or hotkey recorder focused.
- Target Pattern: Incremental UI Reconciliation / In-Place Visual Element Updating. Update existing visual control properties (values, badges, highlights) in-place without tearing down the entire children collection of `_profilesStack`. Only rebuild the list when presets are added, deleted, or reordered.

## 3. GUARDED LOGIC & INVARIANT PRESERVATION
- CRITICAL: Do NOT delete, bypass, or discard existing validation rules, legacy fallbacks, or edge-case handling present in this block.
- Explicitly Guarded Behavior:
  - Preserve preset sorting order based on average target brightness.
  - Preserve active preset visual styling (`AppSelectedBrush`, `AppAccentBrush`, active badge indicator).
  - Preserve cycle step badge formatting (`CYCLE STEP X/Y`).
  - Preserve tooltip descriptions and inline preset renaming validation (max 32 characters, unique name enforcement).
- Public Interface Parity: Maintain internal method signatures and event wiring.

## 4. STEP-BY-STEP REFACTORING INSTRUCTIONS
1. Isolate `RenderProfilesList()` (lines 1859-1900) and `OnServiceStateChanged()` (lines 2604-2649) in `ScreenBrightnessWindow.cs`.
2. Introduce dirty checking or granular update logic: check if the collection of profiles has structurally changed (count, IDs, order).
3. If profile IDs and order match the existing rows, iterate the existing cards in `_profilesStack.Children` and update only the dynamic elements (active indicator border, cycle step badge, and slider values) without calling `Children.Clear()`.
4. Only perform a full clear and rebuild if profiles have been added, deleted, or renamed.
5. In `OnServiceStateChanged()`, do not reconstruct UI controls while the user is actively typing in an input field (`nameBox.IsFocused` or `FocusManager`).

## 5. DEFINITION OF DONE & VERIFICATION
- Compilation: Zero compiler or linter warnings under NativeAOT compilation.
- Behavioral Validation: All presets and hotkeys render accurately and reflect active profile state. Rapidly pressing brightness step hotkeys updates UI indicators smoothly with zero visual stutter or lost focus in active text inputs.
- Performance Check: Memory allocations and GC pressure drop significantly during rapid hotkey adjustments.
Please update .\project_structure.txt when done, then please run .\dev_build.cmd to make sure all is now still succesfully compiling Native AOT and no issues/warning/errors.
Then, when finally done with all, add test file testing this scenario carefully to .\tests\ folder and then when done writing the relvant thorough test files which covers this scenario then locate this current prompt which we are working on (the same prompt we are performing this session) and only rename this ai agent prompt from the .\AI_AGENT_PROMPT\ folder for only the done ai agent prompt alone to "DONE_<THE OLD AI AGNENT PROMPT NAME STILL HERE>"
