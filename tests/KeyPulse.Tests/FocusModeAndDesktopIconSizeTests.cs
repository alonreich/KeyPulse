using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    /// <summary>Focus Mode (F1-F7), desktop icon size (D1-D4), the shortcuts table (U4) and the NativeAOT desktop COM path.</summary>
    [TestClass]
    [DoNotParallelize]
    public class FocusModeAndDesktopIconSizeTests
    {
        private string? _sandbox;
        private readonly List<(int Slot, int B, int C)> _writes = new();
        private List<(IntPtr HMonitor, string HardwareId, bool IsPrimary)> _monitors = new();

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_focus_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
            ConfigStore.ConfigDirectory = _sandbox;
            _writes.Clear();
            _monitors = new List<(IntPtr, string, bool)>
            {
                (new IntPtr(0x1001), "MON-MAIN", true),
                (new IntPtr(0x1002), "MON-SIDE", false)
            };
            ScreenBrightnessService.ForceActiveForTesting = true;
            ScreenBrightnessService.ConnectedMonitorsOverrideForTesting = () => _monitors;
            ScreenBrightnessService.HardwareWriteOverrideForTesting = (slot, b, c, wb, wc) =>
            {
                lock (_writes) _writes.Add((slot, b, c));
                return true;
            };
            ScreenBrightnessService.BrightnessHotkeyRegisterOverrideForTesting = (_, _) => 1;
            ScreenBrightnessService.BrightnessHotkeyUnregisterOverrideForTesting = _ => { };
            ScreenBrightnessService.ResetToDefaults();
            ScreenBrightnessService.ResetWriteSchedulerForTesting();
            ScreenBrightnessService.InvalidateMonitorCache();
            SetLevels(70, 60);
        }

        [TestCleanup]
        public void Cleanup()
        {
            ScreenBrightnessService.UnregisterAllBrightnessHotkeys();
            ScreenBrightnessService.ConnectedMonitorsOverrideForTesting = null;
            ScreenBrightnessService.HardwareWriteOverrideForTesting = null;
            ScreenBrightnessService.BrightnessHotkeyRegisterOverrideForTesting = null;
            ScreenBrightnessService.BrightnessHotkeyUnregisterOverrideForTesting = null;
            ScreenBrightnessService.ForceActiveForTesting = null;
            ScreenBrightnessService.ResetWriteSchedulerForTesting();
            ScreenBrightnessService.InvalidateMonitorCache();
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_sandbox) && Directory.Exists(_sandbox))
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }

        private static void SetLevels(int main, int side)
        {
            ScreenBrightnessService.SetScreenLevelsBatch(new List<(int, int, int)> { (0, main, main), (1, side, side) });
        }

        private static (int B, int C) Level(int slot)
        {
            var s = ScreenBrightnessService.LoadConfig().Screens.First(x => x.SlotIndex == slot);
            return (s.LastBrightness, s.LastContrast);
        }

        private static void Link(bool linked)
        {
            ScreenBrightnessService.SetScreenLinkedToNext(0, linked);
            SetLevels(70, 60);
            if (linked) SetLevels(70, 70); // linking averages; give both a known level
        }

        // ---- Focus Mode ------------------------------------------------------------------------

        [TestMethod]
        public void FocusMode_On_DimsOtherScreens_KeepsMainScreen()
        {
            Assert.IsTrue(ScreenBrightnessService.ToggleFocusMode());
            Assert.AreEqual(70, Level(0).B, "Main screen keeps its level.");
            Assert.AreEqual(0, Level(1).B, "Other screen goes to 0%.");
            Assert.IsTrue(ScreenBrightnessService.IsFocusModeActive);
        }

        [TestMethod]
        public void FocusMode_SecondPress_RestoresOthers_MainStaysWhereItIsNow()
        {
            ScreenBrightnessService.ToggleFocusMode();
            ScreenBrightnessService.RequestIndividualOrLinkedChange(0, 85, 85, isBrightnessChanged: true, isContrastChanged: false);

            Assert.IsFalse(ScreenBrightnessService.ToggleFocusMode());
            Assert.AreEqual(85, Level(0).B, "Main screen is not reset.");
            Assert.AreEqual(60, Level(1).B, "Other screen gets its pre-focus level back.");
            Assert.IsFalse(ScreenBrightnessService.IsFocusModeActive);
        }

        [TestMethod]
        public void FocusMode_LinkedScreens_MasterStepCancelsTheGap()
        {
            Link(true);
            ScreenBrightnessService.ToggleFocusMode();
            Assert.AreEqual(0, Level(1).B);

            ScreenBrightnessService.RequestMasterLevelChange(5);

            Assert.AreEqual(75, Level(0).B);
            Assert.AreEqual(75, Level(1).B, "Linked screen jumps to the main screen and moves with it.");
            Assert.IsFalse(ScreenBrightnessService.IsFocusModeActive);
        }

        [TestMethod]
        public void FocusMode_LinkedScreens_MasterDragStartCancelsTheGap()
        {
            Link(true);
            ScreenBrightnessService.ToggleFocusMode();

            ScreenBrightnessService.PrepareForMainScreenAdjustment();

            Assert.AreEqual(70, Level(1).B);
            Assert.IsFalse(ScreenBrightnessService.IsFocusModeActive);
        }

        [TestMethod]
        public void FocusMode_LinkedScreens_MainScreenSliderCancelsTheGap()
        {
            Link(true);
            ScreenBrightnessService.ToggleFocusMode();

            ScreenBrightnessService.RequestIndividualOrLinkedChange(0, 50, 50, isBrightnessChanged: true, isContrastChanged: false);

            Assert.AreEqual(50, Level(0).B);
            Assert.AreEqual(50, Level(1).B);
            Assert.IsFalse(ScreenBrightnessService.IsFocusModeActive);
        }

        [TestMethod]
        public void FocusMode_UnlinkedScreens_KeepTheNewGap()
        {
            Link(false);
            ScreenBrightnessService.ToggleFocusMode();

            ScreenBrightnessService.RequestMasterLevelChange(5);

            Assert.AreEqual(75, Level(0).B);
            Assert.AreEqual(5, Level(1).B, "Unlinked screen keeps the gap and moves by the same step.");
            Assert.IsTrue(ScreenBrightnessService.IsFocusModeActive, "Focus Mode stays on until the key is pressed again.");
        }

        [TestMethod]
        public void FocusMode_PresetEndsIt()
        {
            ScreenBrightnessService.ToggleFocusMode();
            var preset = ScreenBrightnessService.LoadConfig().Profiles.First();
            ScreenBrightnessService.ApplyProfileAsync(preset);
            Assert.IsFalse(ScreenBrightnessService.IsFocusModeActive);
        }

        [TestMethod]
        public void FocusMode_SingleScreen_DoesNothing()
        {
            _monitors = new List<(IntPtr, string, bool)> { (new IntPtr(0x1001), "MON-MAIN", true) };
            ScreenBrightnessService.InvalidateMonitorCache();
            Assert.IsFalse(ScreenBrightnessService.ToggleFocusMode());
            Assert.IsFalse(ScreenBrightnessService.IsFocusModeActive);
        }

        [TestMethod]
        public void FocusMode_KeepsTheWindowsMainDisplay_NotSlotA()
        {
            Assert.AreEqual(1, ScreenBrightnessService.ResolvePrimarySlot(new[] { (0, false), (1, true) }));
            Assert.AreEqual(0, ScreenBrightnessService.ResolvePrimarySlot(new[] { (2, false), (0, false) }), "No primary reported: lowest slot.");
        }

        [TestMethod]
        public void FocusMode_DimLevelAndContrastOption()
        {
            var screen = new ScreenHardwareSetting { LockRatio = false, LastContrast = 80 };
            Assert.AreEqual((20, 80), ScreenBrightnessService.FocusDimLevels(screen, 20, lowerContrast: false));
            Assert.AreEqual((20, 0), ScreenBrightnessService.FocusDimLevels(screen, 20, lowerContrast: true));
            Assert.AreEqual((30, 80), ScreenBrightnessService.FocusDimLevels(screen, 90, lowerContrast: false), "Dim level is capped at 30%.");
            screen.LockRatio = true;
            Assert.AreEqual((10, 10), ScreenBrightnessService.FocusDimLevels(screen, 10, lowerContrast: false));
        }

        [TestMethod]
        public void FocusMode_DefaultKey_IsCtrlAltNumPadMultiply_AndParses()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual("Ctrl+Alt+NumPadMultiply", cfg.FocusModeHotkey);
            Assert.IsTrue(HotkeyManager.TryParseCombo(cfg.FocusModeHotkey, out var mods, out var vk));
            Assert.AreEqual(HotkeyManager.ModCtrl | HotkeyManager.ModAlt, mods);
            Assert.AreEqual(0x6Au, vk);
            Assert.AreEqual("Ctrl+Alt+Num *", HotkeyManager.DescribeComboList(cfg.FocusModeHotkey));
        }

        [TestMethod]
        public void FocusMode_DefaultsApplyToConfigsSavedBeforeIt()
        {
            var old = JsonSerializer.Deserialize("{\"Enabled\":true}", AppConfigJsonContext.Default.ScreenBrightnessConfig)!;
            Assert.AreEqual(ScreenBrightnessService.DefaultFocusModeHotkey, old.FocusModeHotkey);
            Assert.AreEqual(ScreenBrightnessService.DefaultFocusModeName, old.FocusModeName);
            Assert.IsFalse(old.FocusModeActive);
        }

        [TestMethod]
        public void FocusMode_StateSurvivesConfigSnapshots()
        {
            ScreenBrightnessService.ToggleFocusMode();
            var snap = ScreenBrightnessService.LoadConfig();
            Assert.IsTrue(snap.FocusModeActive);
            Assert.AreEqual(1, snap.FocusModeSavedLevels.Count);
            Assert.AreEqual(60, snap.FocusModeSavedLevels[0].Brightness);
        }

        // ---- Shortcuts table (U4) --------------------------------------------------------------

        [TestMethod]
        public void ShortcutsTable_ListsEveryKind_AndAssigningTakesTheKeyFromOtherRows()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.DirectHotkeys.Add(new ScreenDirectHotkey { Name = "Side", TargetSlotIndex = 1, Brightness = 40, Hotkey = "Ctrl+Alt+F1" });
            var rows = BrightnessShortcuts.BuildRows(cfg);

            CollectionAssert.IsSubsetOf(new[] { BrightnessShortcuts.IdStepUp, BrightnessShortcuts.IdStepDown, BrightnessShortcuts.IdFocus, BrightnessShortcuts.IdCycleUp, BrightnessShortcuts.IdCycleDown },
                rows.Select(r => r.Id).ToList());
            Assert.AreEqual(cfg.Profiles.Count, rows.Count(r => r.Id.StartsWith(BrightnessShortcuts.PresetPrefix)));
            Assert.AreEqual(1, rows.Count(r => r.Id.StartsWith(BrightnessShortcuts.ScreenPrefix)));

            BrightnessShortcuts.ClearDuplicate(cfg, "Ctrl+Alt+F1", BrightnessShortcuts.IdFocus);
            Assert.IsTrue(BrightnessShortcuts.SetKeys(cfg, BrightnessShortcuts.IdFocus, "Ctrl+Alt+F1"));
            Assert.AreEqual("Ctrl+Alt+F1", cfg.FocusModeHotkey);
            Assert.AreEqual(string.Empty, cfg.DirectHotkeys[0].Hotkey, "The screen shortcut lost the key it gave away.");
        }

        // ---- Desktop icon size (D1-D4) ---------------------------------------------------------

        [TestMethod]
        public void IconSize_Descriptions()
        {
            Assert.AreEqual("Medium (normal)", DesktopOrganizerService.DescribeIconSize(48));
            Assert.IsNull(DesktopOrganizerService.DescribeIconSizeDifference(48));

            var small = DesktopOrganizerService.DescribeIconSizeDifference(43)!;
            StringAssert.Contains(small, "43 px");
            StringAssert.Contains(small, "slightly smaller");

            var large = DesktopOrganizerService.DescribeIconSizeDifference(96)!;
            StringAssert.Contains(large, "Large (96 px)");
            StringAssert.Contains(large, "larger");
        }

        [TestMethod]
        public void IconSize_IsStoredWithTheLayout_AndOldLayoutsReadAsUnknown()
        {
            var profile = new DesktopLayoutProfile { Name = "x", DesktopIconSize = 43, DesktopViewMode = 1 };
            var json = JsonSerializer.Serialize(profile, AppConfigJsonContext.Default.DesktopLayoutProfile);
            var back = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.DesktopLayoutProfile)!;
            Assert.AreEqual(43, back.DesktopIconSize);

            var old = JsonSerializer.Deserialize("{\"Name\":\"old\"}", AppConfigJsonContext.Default.DesktopLayoutProfile)!;
            Assert.AreEqual(0, old.DesktopIconSize, "Layouts saved before this change never touch the icon size on restore.");
        }

        [TestMethod]
        public void DisplayDifference_IsReportedOnlyWhenDisplaysChanged()
        {
            var profile = new DesktopLayoutProfile
            {
                Monitors = new List<DesktopMonitorSnapshot>
                {
                    new() { Index = 1, Width = 2560, Height = 1440, IsPrimary = true },
                    new() { Index = 2, Width = 1920, Height = 1080 }
                }
            };
            var same = new List<(int, int, int, bool)> { (1, 2560, 1440, true), (2, 1920, 1080, false) };
            Assert.IsNull(DesktopOrganizerService.DescribeDisplayDifference(profile, same));

            var fewer = new List<(int, int, int, bool)> { (1, 2560, 1440, true) };
            StringAssert.Contains(DesktopOrganizerService.DescribeDisplayDifference(profile, fewer)!, "2 display(s)");

            var resized = new List<(int, int, int, bool)> { (1, 1920, 1080, true), (2, 1920, 1080, false) };
            StringAssert.Contains(DesktopOrganizerService.DescribeDisplayDifference(profile, resized)!, "resolution changed");
        }

        // ---- NativeAOT guard: no built-in COM interop anywhere ---------------------------------

        [TestMethod]
        public void App_UsesNoBuiltInComInterop()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "KeyPulse"))) dir = dir.Parent;
            Assert.IsNotNull(dir);
            foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "src", "KeyPulse"), "*.cs"))
            {
                var src = File.ReadAllText(file);
                foreach (var banned in new[] { "[ComImport", "GetUniqueObjectForIUnknown", "GetObjectForIUnknown(", "ComActivation.CreateInstance<" })
                {
                    Assert.IsFalse(src.Contains(banned), $"{Path.GetFileName(file)} uses {banned}: built-in COM interop fails under NativeAOT; use vtable calls.");
                }
            }
        }

        [TestMethod]
        public void TaskScheduler_MissingTask_IsReportedAsNotFound()
        {
            var name = "KeyPulse_Test_DoesNotExist_" + Guid.NewGuid().ToString("N");
            Assert.IsFalse(TaskSchedulerCom.TryGetTask(name, out var xml, out _, out var notFound, out var error));
            Assert.IsTrue(notFound, "Connect + GetTask must run and map the HRESULT to 'not found': " + error);
            Assert.IsNull(xml);
            Assert.IsTrue(TaskSchedulerCom.TryDeleteTask(name, out var delError), "Deleting a task that does not exist counts as deleted: " + delError);
        }

        [TestMethod]
        public void TaskScheduler_NotFoundCodes()
        {
            Assert.IsTrue(TaskSchedulerCom.IsNotFound(unchecked((int)0x80070002)));
            Assert.IsTrue(TaskSchedulerCom.IsNotFound(unchecked((int)0x80041318)));
            Assert.IsFalse(TaskSchedulerCom.IsNotFound(unchecked((int)0x80070005)), "Access denied is not 'missing'.");
        }

        // ---- NativeAOT guard for the desktop shell path ----------------------------------------

        [TestMethod]
        public void DesktopOrganizer_UsesNoBuiltInComInterop()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "KeyPulse"))) dir = dir.Parent;
            Assert.IsNotNull(dir);
            foreach (var file in Directory.GetFiles(Path.Combine(dir!.FullName, "src", "KeyPulse"), "DesktopOrganizerService*.cs"))
            {
                var src = File.ReadAllText(file);
                Assert.IsFalse(src.Contains("[ComImport"), $"{Path.GetFileName(file)}: [ComImport] interfaces fail under NativeAOT.");
                Assert.IsFalse(src.Contains("GetUniqueObjectForIUnknown"), $"{Path.GetFileName(file)}: RCW creation fails under NativeAOT.");
                Assert.IsFalse(src.Contains("ComActivation.CreateInstance<"), $"{Path.GetFileName(file)}: typed COM activation fails under NativeAOT.");
            }
        }
    }
}
