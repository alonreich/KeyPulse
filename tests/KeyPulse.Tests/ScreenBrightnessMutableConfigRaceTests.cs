using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ScreenBrightnessMutableConfigRaceTests
    {
        private string? _sandbox;

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_sb_race_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
            ConfigStore.ConfigDirectory = _sandbox;
            ScreenBrightnessService.ResetToDefaults();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_sandbox) && Directory.Exists(_sandbox))
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }

        [TestMethod]
        public void LoadConfig_ReturnsDistinctInstancesEachCall()
        {
            var a = ScreenBrightnessService.LoadConfig();
            var b = ScreenBrightnessService.LoadConfig();

            Assert.AreNotSame(a, b);
            Assert.AreNotSame(a.Screens, b.Screens);
            Assert.AreNotSame(a.Profiles, b.Profiles);
            Assert.AreNotSame(a.DirectHotkeys, b.DirectHotkeys);
            Assert.AreNotSame(a.Screens[0], b.Screens[0]);
            Assert.AreNotSame(a.Profiles[0], b.Profiles[0]);
            Assert.AreNotSame(a.Profiles[0].Targets, b.Profiles[0].Targets);
            Assert.AreNotSame(a.Profiles[0].Targets[0], b.Profiles[0].Targets[0]);
        }

        [TestMethod]
        public void LoadConfig_MutatingSnapshotDoesNotLeakIntoSharedCache()
        {
            var snap = ScreenBrightnessService.LoadConfig();
            snap.Screens[0].CustomNickname = "Mutated";
            snap.Screens.Clear();
            snap.Profiles.Clear();
            snap.DirectHotkeys.Add(new ScreenDirectHotkey { Hotkey = "Ctrl+Alt+9" });
            snap.StepDelta = 42;

            var fresh = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, fresh.Screens.Count);
            Assert.AreEqual("Screen A", fresh.Screens[0].CustomNickname);
            Assert.AreEqual(3, fresh.Profiles.Count);
            Assert.AreEqual(0, fresh.DirectHotkeys.Count);
            Assert.AreEqual(5, fresh.StepDelta);
        }

        [TestMethod]
        public void SaveConfig_ClonesIncomingConfig_LaterCallerMutationDoesNotLeak()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Screens[1].CustomNickname = "Desk Left";
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(cfg));

            cfg.Screens[1].CustomNickname = "Hacked";
            cfg.Screens.Clear();

            var fresh = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, fresh.Screens.Count);
            Assert.AreEqual("Desk Left", fresh.Screens[1].CustomNickname);
        }

        [TestMethod]
        public void SaveConfig_DoesNotNormalizeCallerObjectInPlace_ButNormalizesPersistedCopy()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Screens.RemoveAll(s => s.SlotIndex == 2);
            var screensRef = cfg.Screens;

            Assert.IsTrue(ScreenBrightnessService.SaveConfig(cfg));

            Assert.AreSame(screensRef, cfg.Screens, "Caller's collection reference must not be swapped under it.");
            Assert.AreEqual(2, cfg.Screens.Count, "Caller's collection must not be mutated by SaveConfig.");

            var fresh = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, fresh.Screens.Count, "MaxScreens slots must be restored.");
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, fresh.Screens.Select(s => s.SlotIndex).ToArray());
            Assert.AreEqual("Screen C", fresh.Screens[2].CustomNickname);
        }

        [TestMethod]
        public void NormalizeRules_ClampAndLockRatioCoupling_StillApplied()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Screens[0].LockRatio = true;
            cfg.Screens[0].LastBrightness = 150;
            cfg.Screens[0].LastContrast = 10;
            cfg.Screens[1].LockRatio = false;
            cfg.Screens[1].LastBrightness = -20;
            cfg.Screens[1].LastContrast = 500;
            cfg.Screens[2].CustomNickname = "   ";
            cfg.Profiles[0].Name = "";
            cfg.Profiles[0].Targets[0].Brightness = 999;
            cfg.Profiles[0].Targets[0].Contrast = -5;
            cfg.StepUpHotkey = "";
            cfg.DirectHotkeys.Add(new ScreenDirectHotkey { Id = "", Brightness = 300, Contrast = -1 });

            Assert.IsTrue(ScreenBrightnessService.SaveConfig(cfg));
            var fresh = ScreenBrightnessService.LoadConfig();

            Assert.AreEqual(100, fresh.Screens[0].LastBrightness);
            Assert.AreEqual(100, fresh.Screens[0].LastContrast, "LockRatio couples contrast to brightness.");
            Assert.AreEqual(0, fresh.Screens[1].LastBrightness);
            Assert.AreEqual(100, fresh.Screens[1].LastContrast);
            Assert.AreEqual("Screen C", fresh.Screens[2].CustomNickname);
            Assert.AreEqual("Brightness Preset", fresh.Profiles[0].Name);
            Assert.AreEqual(100, fresh.Profiles[0].Targets[0].Brightness);
            Assert.AreEqual(0, fresh.Profiles[0].Targets[0].Contrast);
            Assert.AreEqual("Ctrl+Alt+=, Ctrl+Alt+NumPadAdd", fresh.StepUpHotkey);
            Assert.AreEqual(1, fresh.DirectHotkeys.Count);
            Assert.IsFalse(string.IsNullOrWhiteSpace(fresh.DirectHotkeys[0].Id));
            Assert.AreEqual(100, fresh.DirectHotkeys[0].Brightness);
            Assert.AreEqual(0, fresh.DirectHotkeys[0].Contrast);
        }

        [TestMethod]
        public void Normalize_ExtraSlotsAreTrimmedToMaxScreens_AndOrderedBySlotIndex()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Screens.Add(new ScreenHardwareSetting { SlotIndex = 7 });
            cfg.Screens.Reverse();
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(cfg));

            var fresh = ScreenBrightnessService.LoadConfig();
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, fresh.Screens.Select(s => s.SlotIndex).ToArray());
            foreach (var p in fresh.Profiles)
            {
                CollectionAssert.AreEqual(new[] { 0, 1, 2 }, p.Targets.Select(t => t.SlotIndex).ToArray());
            }
        }

        [TestMethod]
        public void SaveConfig_StaleSnapshot_DoesNotRollBackRuntimeLevelsChangedByOthers()
        {
            var stale = ScreenBrightnessService.LoadConfig();
            var other = ScreenBrightnessService.LoadConfig();

            other.Screens[0].LockRatio = false;
            other.Screens[0].LastBrightness = 80;
            other.Screens[0].LastContrast = 66;
            other.Screens[0].HardwareId = "HW-LIVE";
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(other));

            stale.Screens[1].CustomNickname = "Edited Elsewhere";
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(stale));

            var fresh = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual(80, fresh.Screens[0].LastBrightness);
            Assert.AreEqual(66, fresh.Screens[0].LastContrast);
            Assert.AreEqual("HW-LIVE", fresh.Screens[0].HardwareId);
            Assert.AreEqual("Edited Elsewhere", fresh.Screens[1].CustomNickname);
        }

        [TestMethod]
        public void SaveConfig_ExplicitCallerChangeToRuntimeField_Wins()
        {
            var snap = ScreenBrightnessService.LoadConfig();
            snap.Screens[0].LockRatio = false;
            snap.Screens[0].LastBrightness = 33;
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(snap));

            Assert.AreEqual(33, ScreenBrightnessService.LoadConfig().Screens[0].LastBrightness);
        }

        [TestMethod]
        public void ResetToDefaults_ReturnsSnapshot_NotSharedCache()
        {
            var defaults = ScreenBrightnessService.ResetToDefaults();
            defaults.Screens.Clear();
            defaults.Profiles.Clear();

            var fresh = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, fresh.Screens.Count);
            Assert.AreEqual(3, fresh.Profiles.Count);
        }

        [TestMethod]
        public void LoadConfig_FallsBackToLkgWhenPrimaryCorrupt()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Screens[0].CustomNickname = "Persisted";
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(cfg));

            File.WriteAllText(ScreenBrightnessService.ConfigFilePath, "{ not json !!");
            ScreenBrightnessService.InvalidateConfigCache();

            var recovered = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual("Persisted", recovered.Screens[0].CustomNickname);
            Assert.AreEqual(ScreenBrightnessService.MaxScreens, recovered.Screens.Count);
        }

        [TestMethod]
        public void CloneConfig_CopiesEveryScalarAndCollectionElement()
        {
            var src = ScreenBrightnessService.CreateDefaultConfig();
            src.SchemaVersion = 9;
            src.Enabled = true;
            src.DisclaimerAccepted = true;
            src.GlobalLockBrightnessContrast = true;
            src.LastCycleProfileId = "abc";
            src.CycleUpHotkey = "Ctrl+1";
            src.CycleDownHotkey = "Ctrl+2";
            src.StepDelta = 11;
            src.LockMonitorDifferential = false;
            src.OverlayWindowX = 12.5;
            src.OverlayWindowY = 13.5;
            src.OverlayWindowWidth = 801;
            src.OverlayWindowHeight = 602;
            src.DirectHotkeys.Add(new ScreenDirectHotkey { Id = "d1", Name = "n", Hotkey = "Ctrl+3", TargetSlotIndex = -1, Brightness = 7, Contrast = 8, LockRatio = false });
            src.Screens[2].IsLinkedToNext = true;
            src.Screens[2].HardwareId = "hw";
            src.Screens[2].ModelName = "mdl";

            var clone = ScreenBrightnessService.CloneConfig(src);

            Assert.AreEqual(9, clone.SchemaVersion);
            Assert.IsTrue(clone.Enabled && clone.DisclaimerAccepted && clone.GlobalLockBrightnessContrast);
            Assert.AreEqual("abc", clone.LastCycleProfileId);
            Assert.AreEqual("Ctrl+1", clone.CycleUpHotkey);
            Assert.AreEqual("Ctrl+2", clone.CycleDownHotkey);
            Assert.AreEqual(src.StepUpHotkey, clone.StepUpHotkey);
            Assert.AreEqual(src.StepDownHotkey, clone.StepDownHotkey);
            Assert.AreEqual(11, clone.StepDelta);
            Assert.IsFalse(clone.LockMonitorDifferential);
            Assert.AreEqual(12.5, clone.OverlayWindowX);
            Assert.AreEqual(13.5, clone.OverlayWindowY);
            Assert.AreEqual(801, clone.OverlayWindowWidth);
            Assert.AreEqual(602, clone.OverlayWindowHeight);
            Assert.IsTrue(clone.Screens[2].IsLinkedToNext);
            Assert.AreEqual("hw", clone.Screens[2].HardwareId);
            Assert.AreEqual("mdl", clone.Screens[2].ModelName);
            Assert.AreEqual(1, clone.DirectHotkeys.Count);
            Assert.AreEqual(-1, clone.DirectHotkeys[0].TargetSlotIndex);
            Assert.AreEqual(7, clone.DirectHotkeys[0].Brightness);
            Assert.IsFalse(clone.DirectHotkeys[0].LockRatio);
            Assert.AreEqual(src.Profiles.Count, clone.Profiles.Count);
            for (int i = 0; i < src.Profiles.Count; i++)
            {
                Assert.AreEqual(src.Profiles[i].Id, clone.Profiles[i].Id);
                Assert.AreEqual(src.Profiles[i].Name, clone.Profiles[i].Name);
                Assert.AreEqual(src.Profiles[i].Targets.Count, clone.Profiles[i].Targets.Count);
                Assert.AreNotSame(src.Profiles[i], clone.Profiles[i]);
            }
        }

        [TestMethod]
        public void Concurrent_LoadSaveAndIterate_NeverThrowsInvalidOperation()
        {
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource();
            int iterations = 0;

            var writers = Enumerable.Range(0, 3).Select(w => Task.Run(() =>
            {
                int n = 0;
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var cfg = ScreenBrightnessService.LoadConfig();
                        cfg.Screens[w % 3].CustomNickname = $"W{w}-{n++}";
                        cfg.Profiles.Add(new ScreenBrightnessProfile { Name = "tmp" + n });
                        if (cfg.Profiles.Count > 6) cfg.Profiles.RemoveAt(cfg.Profiles.Count - 1);
                        cfg.Screens.RemoveAll(s => s.SlotIndex == 2 && (n % 2 == 0));
                        ScreenBrightnessService.SaveConfig(cfg);
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            })).ToArray();

            var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        var cfg = ScreenBrightnessService.LoadConfig();
                        int total = 0;
                        foreach (var s in cfg.Screens) total += s.LastBrightness;
                        foreach (var p in cfg.Profiles)
                        {
                            foreach (var t in p.Targets) total += t.Brightness;
                        }
                        foreach (var d in cfg.DirectHotkeys) total += d.Brightness;
                        Assert.AreEqual(ScreenBrightnessService.MaxScreens, cfg.Screens.Count);
                        Interlocked.Increment(ref iterations);
                        _ = total;
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            })).ToArray();

            Thread.Sleep(1500);
            cts.Cancel();
            Task.WaitAll(writers.Concat(readers).ToArray(), TimeSpan.FromSeconds(30));

            var invalid = errors.Where(e => e is InvalidOperationException || e is AssertFailedException).ToList();
            Assert.AreEqual(0, invalid.Count, "Concurrent access threw: " + string.Join(" | ", invalid.Select(e => e.Message)));
            Assert.IsTrue(iterations > 0, "Readers must have made progress.");
        }

        [TestMethod]
        public void Concurrent_NormalizingSharedCallerConfig_DoesNotBreakEnumeration()
        {
            var shared = ScreenBrightnessService.LoadConfig();
            var errors = new System.Collections.Concurrent.ConcurrentBag<Exception>();
            using var cts = new CancellationTokenSource();

            var normalizer = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try { ScreenBrightnessService.GetOrderedCycleProfiles(shared); }
                    catch (Exception ex) { errors.Add(ex); }
                }
            });
            var reader = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    try
                    {
                        foreach (var s in shared.Screens) _ = s.SlotIndex;
                        foreach (var p in shared.Profiles) _ = p.Name;
                    }
                    catch (InvalidOperationException ex) { errors.Add(ex); }
                }
            });

            Thread.Sleep(1000);
            cts.Cancel();
            Task.WaitAll(new[] { normalizer, reader }, TimeSpan.FromSeconds(30));

            var invalid = errors.OfType<InvalidOperationException>().ToList();
            Assert.AreEqual(0, invalid.Count, "Enumeration raced with normalization: " + string.Join(" | ", invalid.Select(e => e.Message)));
        }

        [TestMethod]
        public void HighFrequencyLoad_CloningOverheadIsNegligible()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++)
            {
                _ = ScreenBrightnessService.LoadConfig();
            }
            sw.Stop();
            Assert.IsTrue(sw.ElapsedMilliseconds < 2000, $"2000 snapshots took {sw.ElapsedMilliseconds} ms.");
        }
    }
}
