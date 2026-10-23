using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KeyPulse
{
    /// <summary>One display target column inside a preset card, as the card renders it.</summary>
    internal readonly record struct ProfileTargetView(
        int SlotIndex,
        string SlotTitle,
        bool LockRatio,
        bool Enabled,
        int Brightness,
        int Contrast);

    /// <summary>
    /// Everything a preset card in <see cref="ScreenBrightnessWindow"/> displays, computed without touching any UI control.
    /// </summary>
    internal sealed class ProfileRowView
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IncludeInCycle { get; init; }
        public bool ShowInTrayQuickActions { get; init; }
        public string Hotkey { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public int CycleStepIndex { get; init; }
        public int TotalCycleSteps { get; init; }
        public string BadgeText { get; init; } = string.Empty;
        public bool CanDelete { get; init; }
        public IReadOnlyList<ProfileTargetView> Targets { get; init; } = Array.Empty<ProfileTargetView>();

        /// <summary>The parts of a card that decide which controls exist (slot columns, titles, contrast row).</summary>
        public string LayoutKey
        {
            get
            {
                var sb = new StringBuilder(Id);
                foreach (var t in Targets)
                {
                    sb.Append('|').Append(t.SlotIndex).Append(':').Append(t.SlotTitle).Append(':').Append(t.LockRatio ? 'L' : 'U');
                }
                return sb.ToString();
            }
        }

        public bool ContentEquals(ProfileRowView other) =>
            Id == other.Id &&
            Name == other.Name &&
            IncludeInCycle == other.IncludeInCycle &&
            ShowInTrayQuickActions == other.ShowInTrayQuickActions &&
            Hotkey == other.Hotkey &&
            IsActive == other.IsActive &&
            CycleStepIndex == other.CycleStepIndex &&
            TotalCycleSteps == other.TotalCycleSteps &&
            BadgeText == other.BadgeText &&
            CanDelete == other.CanDelete &&
            Targets.SequenceEqual(other.Targets);
    }

    internal enum ListReconcileAction
    {
        /// <summary>Nothing on screen differs from the config; leave every control alone.</summary>
        None,
        /// <summary>Same cards in the same order; update values, badges and highlights on the existing controls.</summary>
        UpdateInPlace,
        /// <summary>Cards were added, removed, reordered or need different controls; clear and rebuild the list.</summary>
        Rebuild,
        /// <summary>A rebuild is needed but the user is typing or dragging inside the list; rebuild once they stop.</summary>
        DeferRebuild
    }

    /// <summary>
    /// Pure decision logic for incremental reconciliation of the Screen Brightness preset and instant-hotkey lists,
    /// so rapid <see cref="ScreenBrightnessService.StateChanged"/> bursts (held step / cycle hotkeys) never tear down
    /// the visual tree or steal keyboard focus.
    /// </summary>
    internal static class ScreenBrightnessListReconciler
    {
        /// <summary>Preset display order: Screen A target brightness, then average target brightness.</summary>
        public static List<ScreenBrightnessProfile> SortProfiles(IEnumerable<ScreenBrightnessProfile> profiles) =>
            profiles
                .OrderBy(p => p.Targets.FirstOrDefault(t => t.SlotIndex == 0)?.Brightness ?? 50)
                .ThenBy(p => p.Targets.Average(t => t.Brightness))
                .ToList();

        public static string FormatCycleBadge(bool includeInCycle, int cycleStepIndex, int totalCycleSteps) =>
            includeInCycle
                ? (cycleStepIndex == 1 ? $"CYCLE STEP 1/{totalCycleSteps} (LOWEST • FIRST)" : $"CYCLE STEP {cycleStepIndex}/{totalCycleSteps}")
                : "NOT IN CYCLE";

        public static string GetSlotTitle(ScreenBrightnessConfig cfg, int slotIndex)
        {
            string slotTitle = ScreenBrightnessService.DefaultSlotName(slotIndex);
            var screenCfg = cfg.Screens?.FirstOrDefault(s => s.SlotIndex == slotIndex);
            if (screenCfg != null && !string.IsNullOrWhiteSpace(screenCfg.CustomNickname) &&
                !string.Equals(screenCfg.CustomNickname, slotTitle, StringComparison.OrdinalIgnoreCase))
            {
                slotTitle = screenCfg.CustomNickname;
            }
            return slotTitle;
        }

        /// <summary>
        /// Builds the card views in display order. <paramref name="orderedCycleIds"/> is the shortcut cycle order
        /// (<see cref="ScreenBrightnessService.GetOrderedCycleProfiles"/>); an empty <paramref name="connectedSlots"/>
        /// falls back to Screen A, as the card always shows at least one column.
        /// </summary>
        public static List<ProfileRowView> BuildProfileRows(
            ScreenBrightnessConfig cfg,
            IReadOnlyList<string> orderedCycleIds,
            IReadOnlyList<int> connectedSlots)
        {
            var slots = connectedSlots.Count > 0 ? connectedSlots.ToList() : new List<int> { 0 };
            var sorted = SortProfiles(cfg.Profiles);
            var rows = new List<ProfileRowView>(sorted.Count);
            bool canDelete = cfg.Profiles.Count > 1;

            foreach (var profile in sorted)
            {
                int cycleRank = 0;
                if (profile.IncludeInCycle)
                {
                    cycleRank = 1;
                    for (int i = 0; i < orderedCycleIds.Count; i++, cycleRank++)
                    {
                        if (orderedCycleIds[i] == profile.Id) break;
                    }
                    if (cycleRank > orderedCycleIds.Count) cycleRank = 0;
                }

                var targets = new List<ProfileTargetView>(slots.Count);
                foreach (int slotIdx in slots)
                {
                    var screenCfg = cfg.Screens?.FirstOrDefault(s => s.SlotIndex == slotIdx);
                    var target = profile.Targets.FirstOrDefault(t => t.SlotIndex == slotIdx);
                    targets.Add(new ProfileTargetView(
                        slotIdx,
                        GetSlotTitle(cfg, slotIdx),
                        screenCfg?.LockRatio ?? false,
                        target?.Enabled ?? true,
                        target?.Brightness ?? 50,
                        target?.Contrast ?? 50));
                }

                rows.Add(new ProfileRowView
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    IncludeInCycle = profile.IncludeInCycle,
                    ShowInTrayQuickActions = profile.ShowInTrayQuickActions,
                    Hotkey = profile.Hotkey ?? string.Empty,
                    IsActive = profile.Id == cfg.LastCycleProfileId,
                    CycleStepIndex = cycleRank,
                    TotalCycleSteps = orderedCycleIds.Count,
                    BadgeText = FormatCycleBadge(profile.IncludeInCycle, cycleRank, orderedCycleIds.Count),
                    CanDelete = canDelete,
                    Targets = targets
                });
            }
            return rows;
        }

        public static bool LayoutMatches(IReadOnlyList<ProfileRowView>? current, IReadOnlyList<ProfileRowView> next)
        {
            if (current == null || current.Count != next.Count) return false;
            for (int i = 0; i < next.Count; i++)
            {
                if (current[i].LayoutKey != next[i].LayoutKey) return false;
            }
            return true;
        }

        /// <summary>
        /// Decides how to bring the on-screen preset list (<paramref name="current"/>, null = never rendered) to <paramref name="next"/>.
        /// </summary>
        public static ListReconcileAction DecideProfiles(
            IReadOnlyList<ProfileRowView>? current,
            IReadOnlyList<ProfileRowView> next,
            bool userInteracting)
        {
            if (!LayoutMatches(current, next))
            {
                return userInteracting && current != null ? ListReconcileAction.DeferRebuild : ListReconcileAction.Rebuild;
            }
            for (int i = 0; i < next.Count; i++)
            {
                if (!current![i].ContentEquals(next[i])) return ListReconcileAction.UpdateInPlace;
            }
            return ListReconcileAction.None;
        }

        /// <summary>Everything an instant-hotkey card displays; equal signatures mean the list needs no rebuild.</summary>
        public static string ComputeDirectHotkeysSignature(ScreenBrightnessConfig cfg)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ScreenBrightnessService.MaxScreens; i++)
            {
                sb.Append(GetSlotTitle(cfg, i)).Append('\u001F');
            }
            if (cfg.DirectHotkeys != null)
            {
                foreach (var dh in cfg.DirectHotkeys)
                {
                    sb.Append('\u001E')
                      .Append(dh.Id).Append('\u001F')
                      .Append(dh.Hotkey).Append('\u001F')
                      .Append(dh.TargetSlotIndex).Append('\u001F')
                      .Append(dh.Brightness).Append('\u001F')
                      .Append(dh.Contrast).Append('\u001F')
                      .Append(dh.LockRatio ? '1' : '0');
                }
            }
            return sb.ToString();
        }

        public static ListReconcileAction DecideDirectHotkeys(string? currentSignature, string nextSignature, bool userInteracting)
        {
            if (currentSignature == null) return ListReconcileAction.Rebuild;
            if (string.Equals(currentSignature, nextSignature, StringComparison.Ordinal)) return ListReconcileAction.None;
            return userInteracting ? ListReconcileAction.DeferRebuild : ListReconcileAction.Rebuild;
        }
    }
}
