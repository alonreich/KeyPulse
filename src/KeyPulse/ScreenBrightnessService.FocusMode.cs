using System;
using System.Collections.Generic;
using System.Linq;

namespace KeyPulse
{
    /// <summary>
    /// Focus Mode: one key dims every screen except the Windows main display, which keeps its level.
    ///
    /// - Pressing the key again restores the dimmed screens to their levels from before; the main
    ///   screen stays where it is now.
    /// - While it is on, any adjustment of the main screen (master slider, master +/- buttons, step
    ///   keys, the main screen's own slider) ends it for every screen LINKED to the main screen
    ///   (the ⇄ button): those screens jump to the main screen's level and then move with it, exactly
    ///   like linked screens always do. Screens that are not linked keep the gap Focus Mode created.
    /// - Presets and per-screen shortcuts set absolute levels, so they simply end Focus Mode.
    ///
    /// Every level change goes through the existing per-screen throttled write path
    /// (QueueThrottledHardwareWrite), so the DDC/CI guard rails in project_structure.txt still apply.
    /// </summary>
    public static partial class ScreenBrightnessService
    {
        public const string DefaultFocusModeHotkey = "Ctrl+Alt+NumPadMultiply";
        public const string DefaultFocusModeName = "Focus Mode";
        public const int MaxFocusModeDimLevel = 30;

        public static bool IsFocusModeActive
        {
            get { lock (ConfigLock) return GetLiveConfigLocked().FocusModeActive; }
        }

        /// <summary>The screen's nickname, or "Screen A/B/C".</summary>
        public static string FormatSlotName(ScreenBrightnessConfig cfg, int slotIndex)
        {
            var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slotIndex);
            return screen != null && !string.IsNullOrWhiteSpace(screen.CustomNickname) ? screen.CustomNickname.Trim() : DefaultSlotName(slotIndex);
        }

        public static string FocusModeDisplayName(ScreenBrightnessConfig cfg)
            => string.IsNullOrWhiteSpace(cfg.FocusModeName) ? DefaultFocusModeName : cfg.FocusModeName.Trim();

        /// <summary>Hotkey / tray entry point. Returns the new state.</summary>
        public static bool ToggleFocusMode()
        {
            if (!IsFeatureActive) return false;
            bool active;
            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                if (cfg.FocusModeActive)
                {
                    ExitFocusModeLocked(cfg, restoreDimmedScreens: true);
                    active = false;
                }
                else
                {
                    active = EnterFocusModeLocked(cfg);
                }
                SaveConfigInternal(cfg);
            }
            NotifyStateChanged();
            return active;
        }

        private static bool EnterFocusModeLocked(ScreenBrightnessConfig cfg)
        {
            var bound = BindMonitorsToSlots(cfg);
            if (bound.Count < 2)
            {
                Program.LogDebug("[FocusMode] Only one screen is connected; nothing to dim.");
                return false;
            }

            int primarySlot = ResolvePrimarySlot(bound.Select(kv => (kv.Key, kv.Value.IsPrimary)));
            int dim = Math.Clamp(cfg.FocusModeDimLevel, 0, MaxFocusModeDimLevel);
            var saved = new List<ScreenCycleEntry>();

            foreach (var slot in bound.Keys.OrderBy(k => k))
            {
                if (slot == primarySlot) continue;
                var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slot);
                if (screen == null) continue;

                saved.Add(new ScreenCycleEntry { SlotIndex = slot, HardwareId = screen.HardwareId, Brightness = screen.LastBrightness, Contrast = screen.LastContrast });

                var (b, c) = FocusDimLevels(screen, dim, cfg.FocusModeLowerContrast);
                SetSlotLevelsLocked(screen, b, c);
            }

            cfg.FocusModeActive = saved.Count > 0;
            cfg.FocusModePrimarySlot = primarySlot;
            cfg.FocusModeSavedLevels = saved;
            Program.LogDebug($"[FocusMode] On: main screen slot {primarySlot} kept, {saved.Count} screen(s) dimmed to {dim}%.");
            return cfg.FocusModeActive;
        }

        private static void ExitFocusModeLocked(ScreenBrightnessConfig cfg, bool restoreDimmedScreens)
        {
            if (restoreDimmedScreens && cfg.FocusModeSavedLevels != null)
            {
                var connected = GetConnectedSlotIndices(cfg);
                foreach (var entry in cfg.FocusModeSavedLevels)
                {
                    if (entry.SlotIndex == cfg.FocusModePrimarySlot || !connected.Contains(entry.SlotIndex)) continue;
                    var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == entry.SlotIndex);
                    if (screen == null) continue;
                    SetSlotLevelsLocked(screen, entry.Brightness, screen.LockRatio ? entry.Brightness : entry.Contrast);
                }
            }

            Program.LogDebug($"[FocusMode] Off (restored={restoreDimmedScreens}).");
            ClearFocusStateLocked(cfg);
        }

        private static void ClearFocusStateLocked(ScreenBrightnessConfig cfg)
        {
            cfg.FocusModeActive = false;
            cfg.FocusModeSavedLevels = new List<ScreenCycleEntry>();
        }

        /// <summary>
        /// Call BEFORE a change that moves the main screen (master drag start, master +/- , step keys,
        /// the main screen's own slider). Linked screens snap to the main screen's current level and
        /// leave Focus Mode, so the change that follows moves them together with no gap.
        /// </summary>
        public static void PrepareForMainScreenAdjustment()
        {
            bool changed;
            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                changed = CollapseLinkedFocusLocked(cfg);
                if (changed) SaveConfigInternal(cfg);
            }
            if (changed) NotifyStateChanged();
        }

        /// <summary>Ends Focus Mode without restoring (a preset or per-screen key just set absolute levels).</summary>
        internal static void EndFocusModeBecauseLevelsWereSet()
        {
            lock (ConfigLock)
            {
                var cfg = GetLiveConfigLocked();
                if (!cfg.FocusModeActive) return;
                ClearFocusStateLocked(cfg);
                Program.LogDebug("[FocusMode] Off: a preset or screen shortcut set new levels.");
            }
        }

        internal static bool CollapseLinkedFocusLocked(ScreenBrightnessConfig cfg)
        {
            if (!cfg.FocusModeActive) return false;

            var primary = cfg.Screens.FirstOrDefault(s => s.SlotIndex == cfg.FocusModePrimarySlot);
            if (primary == null) return false;

            var group = GetLinkedSlotGroup(cfg, cfg.FocusModePrimarySlot);
            if (group.Count <= 1) return false;

            foreach (var slot in group)
            {
                if (slot == cfg.FocusModePrimarySlot) continue;
                var screen = cfg.Screens.FirstOrDefault(s => s.SlotIndex == slot);
                if (screen == null) continue;
                SetSlotLevelsLocked(screen, primary.LastBrightness, screen.LockRatio ? primary.LastBrightness : primary.LastContrast);
            }

            cfg.FocusModeSavedLevels = (cfg.FocusModeSavedLevels ?? new List<ScreenCycleEntry>())
                .Where(e => !group.Contains(e.SlotIndex))
                .ToList();
            if (cfg.FocusModeSavedLevels.Count == 0) cfg.FocusModeActive = false;

            Program.LogDebug($"[FocusMode] Main screen adjusted: {group.Count - 1} linked screen(s) matched it; Focus Mode {(cfg.FocusModeActive ? "still on for unlinked screens" : "off")}.");
            return true;
        }

        /// <summary>The Windows main display's slot; the lowest connected slot when none reports primary.</summary>
        internal static int ResolvePrimarySlot(IEnumerable<(int Slot, bool IsPrimary)> connected)
        {
            var list = connected.ToList();
            if (list.Count == 0) return 0;
            var primary = list.Where(x => x.IsPrimary).Select(x => (int?)x.Slot).FirstOrDefault();
            return primary ?? list.Min(x => x.Slot);
        }

        /// <summary>Target levels for a dimmed screen. Contrast 0 maps to the hardware floor (min + 5%) in DdcWriteSafety.ToRaw.</summary>
        internal static (int Brightness, int Contrast) FocusDimLevels(ScreenHardwareSetting screen, int dim, bool lowerContrast)
        {
            int b = Math.Clamp(dim, 0, MaxFocusModeDimLevel);
            int c = screen.LockRatio ? b : (lowerContrast ? 0 : Math.Clamp(screen.LastContrast, 0, 100));
            return (b, c);
        }

        private static void SetSlotLevelsLocked(ScreenHardwareSetting screen, int brightness, int contrast)
        {
            screen.LastBrightness = Math.Clamp(brightness, 0, 100);
            screen.LastContrast = Math.Clamp(contrast, 0, 100);
            screen.VirtualBrightness = screen.LastBrightness;
            screen.VirtualContrast = screen.LastContrast;
            QueueThrottledHardwareWrite(screen.SlotIndex, screen.LastBrightness, screen.LastContrast);
        }
    }
}
