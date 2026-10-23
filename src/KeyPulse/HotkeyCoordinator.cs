using System;
using System.Collections.Generic;
using System.Linq;

namespace KeyPulse
{
    /// <summary>
    /// TASK 10: the Win32 side of ApplyHotkeys, moved out of MainWindow. Validation and the status
    /// wording stay in the window; this class only reconciles one row's live registrations with
    /// its validated combinations. It never calls HotkeyManager.Clear() (ISSUE_5): unchanged combos
    /// keep their registration, removed combos are released one by one, new combos are registered.
    /// </summary>
    internal static class HotkeyCoordinator
    {
        internal readonly record struct ReconcileResult(int SuccessCount, int ConflictCount, string? FirstConflictCombo);

        /// <summary>Test seam: replaces HotkeyManager.Register. Return (false, 0) to simulate a conflict.</summary>
        internal static Func<string, Action, (bool Ok, int Id)>? RegisterOverrideForTesting;

        /// <summary>Test seam: replaces HotkeyManager.Unregister.</summary>
        internal static Action<int>? UnregisterOverrideForTesting;

        private static bool Register(string combo, Action callback, out int id)
        {
            var overrideRegister = RegisterOverrideForTesting;
            if (overrideRegister != null)
            {
                var (ok, newId) = overrideRegister(combo, callback);
                id = newId;
                return ok;
            }
            return HotkeyManager.Register(combo, callback, out id);
        }

        private static void Unregister(int id)
        {
            var overrideUnregister = UnregisterOverrideForTesting;
            if (overrideUnregister != null) overrideUnregister(id);
            else HotkeyManager.Unregister(id);
        }

        /// <param name="validCombos">Combinations that passed format and duplicate checks, as (as typed, normalized).</param>
        internal static ReconcileResult Reconcile(HotkeyEntry entry, IReadOnlyList<(string Original, string Normalized)> validCombos, Action callback)
        {
            var currentNormSet = new HashSet<string>(validCombos.Select(v => v.Normalized), StringComparer.OrdinalIgnoreCase);
            var toRemove = entry.RegisteredHotkeyIds.Keys.Where(k => !currentNormSet.Contains(k)).ToList();
            foreach (var k in toRemove)
            {
                if (entry.RegisteredHotkeyIds.TryGetValue(k, out var regId) && regId != 0)
                {
                    Unregister(regId);
                }
                entry.RegisteredHotkeyIds.Remove(k);
                entry.ComboStatuses.Remove(k);
                entry.ComboHints.Remove(k);
            }

            var successCount = 0;
            var conflictCount = 0;
            string? firstConflictCombo = null;

            foreach (var (orig, norm) in validCombos)
            {
                if (entry.RegisteredHotkeyIds.TryGetValue(norm, out var existingId) && existingId != 0)
                {
                    successCount++;
                    entry.ComboStatuses[norm] = "Active";
                    continue;
                }

                if (Register(orig, callback, out var newId))
                {
                    entry.RegisteredHotkeyIds[norm] = newId;
                    entry.ComboStatuses[norm] = "Active";
                    entry.ComboHints.Remove(norm);
                    successCount++;
                }
                else
                {
                    entry.ComboStatuses[norm] = "Conflict";
                    conflictCount++;
                    firstConflictCombo ??= orig;
                }
            }

            entry.RegisteredHotkeyId = entry.RegisteredHotkeyIds.Values.FirstOrDefault();
            entry.RegisteredCombo = entry.RegisteredHotkeyIds.Keys.FirstOrDefault() ?? string.Empty;

            return new ReconcileResult(successCount, conflictCount, firstConflictCombo);
        }

        /// <summary>Hands one row's Win32 hotkeys back, leaving every other row registered.</summary>
        internal static void Release(HotkeyEntry entry)
        {
            if (entry.RegisteredHotkeyIds.Count > 0)
            {
                foreach (var id in entry.RegisteredHotkeyIds.Values)
                {
                    if (id != 0) Unregister(id);
                }
                entry.RegisteredHotkeyIds.Clear();
            }

            if (entry.RegisteredHotkeyId != 0)
            {
                Unregister(entry.RegisteredHotkeyId);
                entry.RegisteredHotkeyId = 0;
            }

            entry.RegisteredCombo = string.Empty;
            entry.ComboStatuses.Clear();
            entry.ComboHints.Clear();
        }

        internal static void ReleaseAll(IEnumerable<HotkeyEntry> entries)
        {
            foreach (var entry in entries) Release(entry);
        }
    }
}
