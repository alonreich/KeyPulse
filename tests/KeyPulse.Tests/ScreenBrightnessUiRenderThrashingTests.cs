using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ScreenBrightnessUiRenderThrashingTests
    {
        private static readonly int[] AllSlots = { 0, 1, 2 };
        private string? _sandbox;

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_sb_render_{Guid.NewGuid():N}");
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

        private static List<string> CycleIds(ScreenBrightnessConfig cfg) =>
            cfg.Profiles.Where(p => p.IncludeInCycle)
                .OrderBy(p => p.Targets.First(t => t.SlotIndex == 0).Brightness)
                .Select(p => p.Id)
                .ToList();

        private static List<ProfileRowView> Rows(ScreenBrightnessConfig cfg, IReadOnlyList<int>? slots = null) =>
            ScreenBrightnessListReconciler.BuildProfileRows(cfg, CycleIds(cfg), slots ?? AllSlots);

        private static List<ProfileRowView> RowsFromService(IReadOnlyList<int>? slots = null) =>
            Rows(ScreenBrightnessService.LoadConfig(), slots);

        private static ScreenBrightnessProfile Profile(string name, int a, int b, int c, bool inCycle = true) => new()
        {
            Name = name,
            IncludeInCycle = inCycle,
            Targets = new List<ScreenCycleEntry>
            {
                new() { SlotIndex = 0, Brightness = a, Contrast = a },
                new() { SlotIndex = 1, Brightness = b, Contrast = b },
                new() { SlotIndex = 2, Brightness = c, Contrast = c }
            }
        };


        [TestMethod]
        public void SortProfiles_OrdersByScreenATargetThenAverage()
        {
            var profiles = new List<ScreenBrightnessProfile>
            {
                Profile("High", 80, 80, 80),
                Profile("TieHighAvg", 40, 90, 90),
                Profile("Low", 10, 10, 10),
                Profile("TieLowAvg", 40, 0, 0)
            };

            var sorted = ScreenBrightnessListReconciler.SortProfiles(profiles).Select(p => p.Name).ToArray();

            CollectionAssert.AreEqual(new[] { "Low", "TieLowAvg", "TieHighAvg", "High" }, sorted);
        }

        [TestMethod]
        public void SortProfiles_MissingScreenATarget_FallsBackTo50()
        {
            var noA = Profile("NoA", 0, 0, 0);
            noA.Targets.RemoveAll(t => t.SlotIndex == 0);
            var sorted = ScreenBrightnessListReconciler.SortProfiles(new[] { Profile("Sixty", 60, 60, 60), noA, Profile("Forty", 40, 40, 40) })
                .Select(p => p.Name).ToArray();

            CollectionAssert.AreEqual(new[] { "Forty", "NoA", "Sixty" }, sorted);
        }

        [TestMethod]
        public void FormatCycleBadge_PreservesExactFormats()
        {
            Assert.AreEqual("CYCLE STEP 1/3 (LOWEST • FIRST)", ScreenBrightnessListReconciler.FormatCycleBadge(true, 1, 3));
            Assert.AreEqual("CYCLE STEP 2/3", ScreenBrightnessListReconciler.FormatCycleBadge(true, 2, 3));
            Assert.AreEqual("CYCLE STEP 10/10", ScreenBrightnessListReconciler.FormatCycleBadge(true, 10, 10));
            Assert.AreEqual("NOT IN CYCLE", ScreenBrightnessListReconciler.FormatCycleBadge(false, 0, 3));
        }

        [TestMethod]
        public void BuildProfileRows_DefaultConfig_MatchesLegacyRendering()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.LastCycleProfileId = cfg.Profiles.Single(p => p.Name == "Balanced").Id;

            var rows = Rows(cfg);

            CollectionAssert.AreEqual(new[] { "Night / Low", "Balanced", "Daylight / Competitive" }, rows.Select(r => r.Name).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, rows.Select(r => r.CycleStepIndex).ToArray());
            Assert.AreEqual("CYCLE STEP 1/3 (LOWEST • FIRST)", rows[0].BadgeText);
            Assert.AreEqual("CYCLE STEP 3/3", rows[2].BadgeText);
            CollectionAssert.AreEqual(new[] { false, true, false }, rows.Select(r => r.IsActive).ToArray());
            Assert.IsTrue(rows.All(r => r.CanDelete));
            Assert.IsTrue(rows.All(r => r.Targets.Count == 3));
            CollectionAssert.AreEqual(new[] { "Screen A", "Screen B", "Screen C" }, rows[0].Targets.Select(t => t.SlotTitle).ToArray());
            Assert.AreEqual(25, rows[0].Targets[0].Brightness);
        }

        [TestMethod]
        public void BuildProfileRows_NotInCycle_HasZeroRankAndNotInCycleBadge()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Profiles[1].IncludeInCycle = false;

            var rows = Rows(cfg);
            var off = rows.Single(r => r.Id == cfg.Profiles[1].Id);

            Assert.AreEqual(0, off.CycleStepIndex);
            Assert.AreEqual("NOT IN CYCLE", off.BadgeText);
            Assert.AreEqual(2, rows.First(r => r.IncludeInCycle).TotalCycleSteps);
        }

        [TestMethod]
        public void BuildProfileRows_SingleProfile_CannotDelete()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Profiles.RemoveRange(1, 2);
            Assert.IsFalse(Rows(cfg).Single().CanDelete);
        }

        [TestMethod]
        public void BuildProfileRows_NoConnectedSlots_FallsBackToScreenA()
        {
            var rows = Rows(ScreenBrightnessService.LoadConfig(), Array.Empty<int>());
            Assert.IsTrue(rows.All(r => r.Targets.Count == 1 && r.Targets[0].SlotIndex == 0));
        }

        [TestMethod]
        public void BuildProfileRows_UsesNicknameAndLockRatio_AndDefaultsMissingTarget()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Screens[1].CustomNickname = "Desk Right";
            cfg.Screens[1].LockRatio = true;
            cfg.Profiles[0].Targets.RemoveAll(t => t.SlotIndex == 2);

            var row = Rows(cfg).Single(r => r.Id == cfg.Profiles[0].Id);

            Assert.AreEqual("Desk Right", row.Targets[1].SlotTitle);
            Assert.IsTrue(row.Targets[1].LockRatio);
            Assert.AreEqual(new ProfileTargetView(2, "Screen C", false, true, 50, 50), row.Targets[2]);
        }

        [TestMethod]
        public void BuildProfileRows_DoesNotMutateConfig()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Profiles[0].Targets.RemoveAll(t => t.SlotIndex == 2);
            int before = cfg.Profiles[0].Targets.Count;
            _ = Rows(cfg);
            Assert.AreEqual(before, cfg.Profiles[0].Targets.Count);
        }


        [TestMethod]
        public void FirstRender_AlwaysRebuilds_EvenWhileInteracting()
        {
            var rows = RowsFromService();
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideProfiles(null, rows, userInteracting: false));
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideProfiles(null, rows, userInteracting: true));
        }

        [TestMethod]
        public void UnchangedConfig_DoesNothing()
        {
            Assert.AreEqual(ListReconcileAction.None,
                ScreenBrightnessListReconciler.DecideProfiles(RowsFromService(), RowsFromService(), userInteracting: false));
        }

        [TestMethod]
        public void HeldStepHotkey_ScreenLevelChanges_NeverTouchPresetList()
        {
            var rendered = RowsFromService();
            int rebuilds = 0, updates = 0;

            for (int i = 0; i < 300; i++)
            {
                var cfg = ScreenBrightnessService.LoadConfig();
                foreach (var s in cfg.Screens)
                {
                    s.LastBrightness = (i * 7 + s.SlotIndex * 13) % 101;
                    s.LastContrast = (i * 3) % 101;
                    s.VirtualBrightness = s.LastBrightness;
                }
                ScreenBrightnessService.SaveConfig(cfg);

                var action = ScreenBrightnessListReconciler.DecideProfiles(rendered, RowsFromService(), userInteracting: false);
                if (action == ListReconcileAction.Rebuild) rebuilds++;
                if (action == ListReconcileAction.UpdateInPlace) updates++;
            }

            Assert.AreEqual(0, rebuilds, "Step hotkeys must never clear and rebuild the preset cards.");
            Assert.AreEqual(0, updates, "Step hotkeys do not change anything a preset card shows.");
        }

        [TestMethod]
        public void HeldCycleHotkey_ActivePresetChanges_UpdateInPlaceOnly()
        {
            var rendered = RowsFromService();
            var ids = ScreenBrightnessService.LoadConfig().Profiles.Select(p => p.Id).ToArray();

            for (int i = 0; i < 90; i++)
            {
                var cfg = ScreenBrightnessService.LoadConfig();
                cfg.LastCycleProfileId = ids[i % ids.Length];
                ScreenBrightnessService.SaveConfig(cfg);

                var next = RowsFromService();
                Assert.AreEqual(ListReconcileAction.UpdateInPlace,
                    ScreenBrightnessListReconciler.DecideProfiles(rendered, next, userInteracting: false), $"iteration {i}");
                Assert.AreEqual(1, next.Count(r => r.IsActive));
                Assert.AreEqual(ids[i % ids.Length], next.Single(r => r.IsActive).Id);
                rendered = next;
            }
        }

        [TestMethod]
        public void ActiveChange_WhileTyping_StillUpdatesInPlace()
        {
            var rendered = RowsFromService();
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.LastCycleProfileId = cfg.Profiles[2].Id;
            ScreenBrightnessService.SaveConfig(cfg);

            Assert.AreEqual(ListReconcileAction.UpdateInPlace,
                ScreenBrightnessListReconciler.DecideProfiles(rendered, RowsFromService(), userInteracting: true));
        }

        [TestMethod]
        public void Rename_ToggleAndHotkeyChanges_UpdateInPlace()
        {
            var rendered = RowsFromService();
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Profiles[0].Name = "Renamed";
            cfg.Profiles[1].ShowInTrayQuickActions = false;
            cfg.Profiles[2].Hotkey = "Ctrl+Alt+F9";
            cfg.Profiles[2].Targets[1].Enabled = false;
            ScreenBrightnessService.SaveConfig(cfg);

            var next = RowsFromService();
            Assert.AreEqual(ListReconcileAction.UpdateInPlace, ScreenBrightnessListReconciler.DecideProfiles(rendered, next, false));
            Assert.AreEqual("Renamed", next.Single(r => r.Id == cfg.Profiles[0].Id).Name);
        }

        [TestMethod]
        public void CycleMembershipChange_UpdatesBadgesInPlace()
        {
            var rendered = RowsFromService();
            var cfg = ScreenBrightnessService.LoadConfig();
            var middle = cfg.Profiles.Single(p => p.Name == "Balanced");
            middle.IncludeInCycle = false;
            ScreenBrightnessService.SaveConfig(cfg);

            var next = RowsFromService();
            Assert.AreEqual(ListReconcileAction.UpdateInPlace, ScreenBrightnessListReconciler.DecideProfiles(rendered, next, false));
            CollectionAssert.AreEqual(new[] { "CYCLE STEP 1/2 (LOWEST • FIRST)", "NOT IN CYCLE", "CYCLE STEP 2/2" }, next.Select(r => r.BadgeText).ToArray());
        }

        [TestMethod]
        public void TargetValueChange_KeepingOrder_UpdatesInPlace()
        {
            var rendered = RowsFromService();
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.Profiles.Single(p => p.Name == "Balanced").Targets[1].Brightness = 51;
            ScreenBrightnessService.SaveConfig(cfg);

            Assert.AreEqual(ListReconcileAction.UpdateInPlace, ScreenBrightnessListReconciler.DecideProfiles(rendered, RowsFromService(), false));
        }

        [TestMethod]
        public void AddDeleteOrReorder_Rebuilds()
        {
            var rendered = RowsFromService();

            var add = ScreenBrightnessService.LoadConfig();
            add.Profiles.Add(Profile("New", 50, 50, 50));
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideProfiles(rendered, Rows(add), false));

            var del = ScreenBrightnessService.LoadConfig();
            del.Profiles.RemoveAt(0);
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideProfiles(rendered, Rows(del), false));

            var reorder = ScreenBrightnessService.LoadConfig();
            reorder.Profiles.Single(p => p.Name == "Night / Low").Targets[0].Brightness = 99;
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideProfiles(rendered, Rows(reorder), false));
        }

        [TestMethod]
        public void ScreenLayoutChanges_Rebuild()
        {
            var rendered = RowsFromService();

            var nick = ScreenBrightnessService.LoadConfig();
            nick.Screens[0].CustomNickname = "Laptop";
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideProfiles(rendered, Rows(nick), false));

            var lockRatio = ScreenBrightnessService.LoadConfig();
            lockRatio.Screens[2].LockRatio = !lockRatio.Screens[2].LockRatio;
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideProfiles(rendered, Rows(lockRatio), false));

            Assert.AreEqual(ListReconcileAction.Rebuild,
                ScreenBrightnessListReconciler.DecideProfiles(rendered, RowsFromService(new[] { 0, 1 }), false), "Monitor unplugged");
        }

        [TestMethod]
        public void StructuralChange_WhileUserTypingOrDragging_IsDeferred()
        {
            var rendered = RowsFromService();
            var add = ScreenBrightnessService.LoadConfig();
            add.Profiles.Add(Profile("New", 50, 50, 50));

            Assert.AreEqual(ListReconcileAction.DeferRebuild, ScreenBrightnessListReconciler.DecideProfiles(rendered, Rows(add), userInteracting: true));
        }

        [TestMethod]
        public void LayoutKey_IgnoresDynamicValues_ButTracksIdentityAndColumns()
        {
            var a = RowsFromService()[0];
            var cfg = ScreenBrightnessService.LoadConfig();
            var p = cfg.Profiles.Single(x => x.Id == a.Id);
            p.Name = "Other";
            p.Targets[0].Brightness = 1;
            cfg.LastCycleProfileId = p.Id;
            var b = Rows(cfg).Single(r => r.Id == a.Id);

            Assert.AreEqual(a.LayoutKey, b.LayoutKey);
            Assert.IsFalse(a.ContentEquals(b));
            Assert.IsTrue(a.ContentEquals(RowsFromService()[0]));
        }


        [TestMethod]
        public void DirectHotkeys_SignatureStableAcrossScreenLevelAndActivePresetChanges()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.DirectHotkeys.Add(new ScreenDirectHotkey { Hotkey = "Ctrl+Alt+1", TargetSlotIndex = 0, Brightness = 70, Contrast = 70 });
            ScreenBrightnessService.SaveConfig(cfg);
            string rendered = ScreenBrightnessListReconciler.ComputeDirectHotkeysSignature(ScreenBrightnessService.LoadConfig());

            for (int i = 0; i < 100; i++)
            {
                var c = ScreenBrightnessService.LoadConfig();
                c.Screens[i % 3].LastBrightness = i % 101;
                c.LastCycleProfileId = c.Profiles[i % c.Profiles.Count].Id;
                ScreenBrightnessService.SaveConfig(c);

                string next = ScreenBrightnessListReconciler.ComputeDirectHotkeysSignature(ScreenBrightnessService.LoadConfig());
                Assert.AreEqual(ListReconcileAction.None, ScreenBrightnessListReconciler.DecideDirectHotkeys(rendered, next, false));
            }
        }

        [TestMethod]
        public void DirectHotkeys_EditsAndNicknames_ChangeSignature()
        {
            var cfg = ScreenBrightnessService.LoadConfig();
            cfg.DirectHotkeys.Add(new ScreenDirectHotkey { Id = "d1", Hotkey = "Ctrl+Alt+1", Brightness = 70, Contrast = 70 });
            string baseSig = ScreenBrightnessListReconciler.ComputeDirectHotkeysSignature(cfg);

            void Expect(Action<ScreenBrightnessConfig> change, string what)
            {
                var c = ScreenBrightnessService.CloneConfig(cfg);
                change(c);
                Assert.AreNotEqual(baseSig, ScreenBrightnessListReconciler.ComputeDirectHotkeysSignature(c), what);
            }

            Expect(c => c.DirectHotkeys[0].Brightness = 71, "brightness");
            Expect(c => c.DirectHotkeys[0].Contrast = 1, "contrast");
            Expect(c => c.DirectHotkeys[0].LockRatio = !c.DirectHotkeys[0].LockRatio, "lock");
            Expect(c => c.DirectHotkeys[0].TargetSlotIndex = 2, "target");
            Expect(c => c.DirectHotkeys[0].Hotkey = "Ctrl+Alt+2", "hotkey");
            Expect(c => c.DirectHotkeys.Add(new ScreenDirectHotkey { Id = "d2" }), "added");
            Expect(c => c.DirectHotkeys.Clear(), "removed");
            Expect(c => c.Screens[1].CustomNickname = "TV", "nickname");
        }

        [TestMethod]
        public void DirectHotkeys_Decisions()
        {
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideDirectHotkeys(null, "x", true));
            Assert.AreEqual(ListReconcileAction.None, ScreenBrightnessListReconciler.DecideDirectHotkeys("x", "x", true));
            Assert.AreEqual(ListReconcileAction.Rebuild, ScreenBrightnessListReconciler.DecideDirectHotkeys("x", "y", false));
            Assert.AreEqual(ListReconcileAction.DeferRebuild, ScreenBrightnessListReconciler.DecideDirectHotkeys("x", "y", true));
        }


        [TestMethod]
        public void HighFrequencyReconcile_IsCheap()
        {
            var rendered = RowsFromService();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 2000; i++)
            {
                var cfg = ScreenBrightnessService.LoadConfig();
                _ = ScreenBrightnessListReconciler.DecideProfiles(rendered, Rows(cfg), false);
                _ = ScreenBrightnessListReconciler.ComputeDirectHotkeysSignature(cfg);
            }
            sw.Stop();
            Assert.IsTrue(sw.ElapsedMilliseconds < 3000, $"2000 reconcile passes took {sw.ElapsedMilliseconds} ms.");
        }
    }
}
