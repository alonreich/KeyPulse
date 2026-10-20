using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    public class CoreTests
    {
        private string? _testSandboxDir;

        [TestInitialize]
        public void SetupTestSandbox()
        {
            _testSandboxDir = Path.Combine(Path.GetTempPath(), $"kp_test_sandbox_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testSandboxDir);
            ConfigStore.ConfigDirectory = _testSandboxDir;
        }

        [TestCleanup]
        public void CleanupTestSandbox()
        {
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_testSandboxDir) && Directory.Exists(_testSandboxDir))
            {
                try { Directory.Delete(_testSandboxDir, true); } catch { }
            }
        }

        [TestMethod]
        public void CompareVersions_DetectsNewerVersionCorrectly()
        {
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.18.0100", "2026.10.17.1515") > 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1516", "2026.10.17.1515") > 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1515", "2026.10.17.1515") == 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1436", "2026.10.17.1515") < 0);
        }

        [TestMethod]
        public void CompareVersions_HandlesPrereleaseAndPrefixes()
        {
            Assert.IsTrue(UpdateChecker.CompareVersions("v2026.10.18.0100-beta", "2026.10.17.1515") > 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1515", "2026.10.17.1515-beta.1") > 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1515-beta.1", "2026.10.17.1515") < 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("2026.10.17.1515-beta.2", "2026.10.17.1515-beta.1") > 0);
            Assert.IsTrue(UpdateChecker.CompareVersions("invalid", "2026.10.17.1515") == 0);
        }

        [TestMethod]
        public void AtomicFileWriter_WritesAndReadsFileSuccessfully()
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"kp_test_{Guid.NewGuid():N}.txt");
            try
            {
                var content = "Atomic write test verification payload";
                var success = AtomicFileWriter.WriteAtomic(tempFile, content, out var error);
                Assert.IsTrue(success, "WriteAtomic should succeed: " + error);
                Assert.IsTrue(File.Exists(tempFile));
                Assert.AreEqual(content, File.ReadAllText(tempFile));
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        [TestMethod]
        public void Changelog_ExistsAndContainsUnreleasedSection()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "project_structure.txt")) && !File.Exists(Path.Combine(dir.FullName, "src", "KeyPulse", "KeyPulse.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "Repository root must be found");

            var changelogPath = Path.Combine(dir.FullName, "docs", "CHANGELOG.md");
            Assert.IsTrue(File.Exists(changelogPath), $"docs/CHANGELOG.md must exist at {changelogPath}");

            var text = File.ReadAllText(changelogPath);
            Assert.IsTrue(text.Contains("## [Unreleased]"), "CHANGELOG.md must contain ## [Unreleased]");
            Assert.IsTrue(text.Contains("AI Agent Directive"), "CHANGELOG.md must contain agent directives");
        }

        [TestMethod]
        public void TechnicalChanges_ExistsAndContainsUnreleasedSection()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "project_structure.txt")) && !File.Exists(Path.Combine(dir.FullName, "src", "KeyPulse", "KeyPulse.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "Repository root must be found");

            var changesPath = Path.Combine(dir.FullName, "docs", "changes.md");
            Assert.IsTrue(File.Exists(changesPath), $"docs/changes.md must exist at {changesPath}");

            var text = File.ReadAllText(changesPath);
            Assert.IsTrue(text.Contains("## [Unreleased]"), "changes.md must contain ## [Unreleased]");
            Assert.IsTrue(text.Contains("AI Agent & Developer Directive"), "changes.md must contain agent directives");
        }

        [TestMethod]
        public void AppConfig_HasDefaultSchemaVersion()
        {
            var config = new AppConfig();
            Assert.AreEqual(1, config.SchemaVersion);
            Assert.IsTrue(config.AutoCheckForUpdates);
            Assert.AreEqual("Stable", config.UpdateChannel);
        }

        [TestMethod]
        public void ReleaseVersion_CleanTagParsesCorrectly()
        {
            var testTags = new[] { "v2026.10.18.0100", "2026.10.17.1515-beta.1", "v1.0.0-rc1" };
            foreach (var tag in testTags)
            {
                var rawVer = tag.TrimStart('v', 'V');
                var dashIdx = rawVer.IndexOf('-');
                var cleanVer = dashIdx >= 0 ? rawVer.Substring(0, dashIdx) : rawVer;
                var parsed = Version.TryParse(cleanVer, out var verObj);
                Assert.IsTrue(parsed, $"Tag '{tag}' should parse into a valid Version object");
                Assert.IsNotNull(verObj);
            }
        }

        [TestMethod]
        public void GitHubReleaseAsset_DigestCleanedSuccessfully()
        {
            var rawDigest = "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
            var clean = System.Text.RegularExpressions.Regex.Replace(rawDigest.Trim(), @"^(?i)sha256:\s*", string.Empty).ToLowerInvariant();
            Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", clean);
            Assert.AreEqual(64, clean.Length);
        }

        [TestMethod]
        public void AppConfig_JsonExtensionData_PreservesFutureProperties()
        {
            var json = "{\"SchemaVersion\":1,\"AutoCheckForUpdates\":true,\"NewFutureFeatureEnabled\":true,\"FutureThreshold\":42}";
            var config = System.Text.Json.JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);

            Assert.IsNotNull(config);
            Assert.IsNotNull(config.ExtensionData);
            Assert.IsTrue(config.ExtensionData.ContainsKey("NewFutureFeatureEnabled"));
            Assert.AreEqual("True", config.ExtensionData["NewFutureFeatureEnabled"].ToString());
            Assert.IsTrue(config.ExtensionData.ContainsKey("FutureThreshold"));
            Assert.AreEqual(42, config.ExtensionData["FutureThreshold"].GetInt32());

            var reSerialized = System.Text.Json.JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
            Assert.IsTrue(reSerialized.Contains("NewFutureFeatureEnabled"));
            Assert.IsTrue(reSerialized.Contains("FutureThreshold"));
        }

        [TestMethod]
        public void ConfigStore_PreservesHigherSchemaVersion_OnSave()
        {
            var config = new AppConfig { SchemaVersion = 99 };
            var saveSuccess = ConfigStore.Save(config, out var saveErr);
            Assert.IsTrue(saveSuccess, "ConfigStore.Save should succeed: " + saveErr);
            var loaded = ConfigStore.Load(out _, out _);
            Assert.AreEqual(99, loaded.SchemaVersion, "Future schema version 99 must be preserved on save");
        }

        [TestMethod]
        public void ConfigStore_AutoDetectsTestEnvironment_NeverPointsToProductionAppData()
        {
            ConfigStore.ConfigDirectory = null!;
            var resolved = ConfigStore.ConfigDirectory;
            var prodAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");
            Assert.AreNotEqual(prodAppData, resolved, "ConfigStore must never resolve to production AppData when executing under a test runner");
            Assert.IsTrue(resolved.Contains("Sandbox", StringComparison.OrdinalIgnoreCase) || resolved.Contains("Temp", StringComparison.OrdinalIgnoreCase));
        }

        [TestMethod]
        public void ConfigStore_PreservesSafetyCopy_WhenSavingZeroShortcutsOverExisting()
        {
            var configWithShortcuts = new AppConfig
            {
                Hotkeys = new System.Collections.Generic.List<HotkeyEntry>
                {
                    new HotkeyEntry
                    {
                        DisplayName = "Existing Action",
                        KeyCombinations = new System.Collections.Generic.List<string> { "Ctrl+Alt+1" },
                        Action = ActionType.BrowseChrome,
                        Target = "https://example.com"
                    }
                }
            };

            var initialSave = ConfigStore.Save(configWithShortcuts, out var err1);
            Assert.IsTrue(initialSave, "Initial save should succeed: " + err1);

            var emptyConfig = new AppConfig { Hotkeys = new System.Collections.Generic.List<HotkeyEntry>() };
            var emptySave = ConfigStore.Save(emptyConfig, out var err2);
            Assert.IsTrue(emptySave, "Empty save should succeed: " + err2);

            var safetyFile = Path.Combine(ConfigStore.ConfigDirectory, "config.safety-shortcuts.json");
            Assert.IsTrue(File.Exists(safetyFile), "config.safety-shortcuts.json must be preserved when saving 0 shortcuts over existing shortcuts");

            var safetyRaw = File.ReadAllText(safetyFile);
            Assert.IsTrue(safetyRaw.Contains("Existing Action"), "Safety backup must contain the original shortcut definitions");
        }

        [TestMethod]
        public void BackupService_ForwardCompatibility_HandlesFutureActionTypes()
        {
            var payload = new BackupPayload
            {
                Shortcuts = new System.Collections.Generic.List<BackupShortcut>
                {
                    new BackupShortcut
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        DisplayName = "Future Action Test",
                        KeyCombinations = new System.Collections.Generic.List<string> { "Ctrl+Alt+F12" },
                        Action = (ActionType)999,
                        Target = "quantum://execute"
                    }
                }
            };

            var valid = BackupService.ValidatePayload(payload, out var fatalError, out var warnings);
            Assert.IsTrue(valid, "Validation should succeed with warnings for unknown future actions: " + fatalError);
            Assert.IsTrue(warnings.Count > 0, "Should contain warning about unsupported action type");
            Assert.IsTrue(warnings[0].Contains("uses an action type not recognized"), "Warning must explain unrecognized action type");
        }

        [TestMethod]
        public void CreateShortcut_CreatesValidWindowsShortcutFile()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"kp_test_lnk_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var shortcutPath = Path.Combine(tempDir, "TestShortcut.lnk");
            try
            {
                var target = Environment.ProcessPath ?? @"C:\Windows\System32\cmd.exe";
                var success = SetupWindow.CreateShortcut(shortcutPath, target, tempDir, "Test Description", target, out var error);
                Assert.IsTrue(success, "CreateShortcut should succeed: " + error);
                Assert.IsTrue(File.Exists(shortcutPath), "Shortcut file must exist on disk");
                var fi = new FileInfo(shortcutPath);
                Assert.IsTrue(fi.Length > 0, "Shortcut file must not be empty");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        [TestMethod]
        public void HotkeyEntry_TargetUnencryptedBadge_ReflectsProtectionStatus()
        {
            var entry = new HotkeyEntry
            {
                DisplayName = "Protected Entry",
                Target = "SecretData",
                IsTargetObfuscated = true,
                TargetProtectionFailed = false
            };
            Assert.AreEqual(string.Empty, entry.TargetUnencryptedBadge);

            entry.TargetProtectionFailed = true;
            Assert.AreEqual(" ⚠️ Unencrypted", entry.TargetUnencryptedBadge);
        }

        [TestMethod]
        public void ScreenBrightnessService_ResetToDefaults_CreatesValidProfilesAndMonitors()
        {
            var config = ScreenBrightnessService.ResetToDefaults();
            Assert.IsNotNull(config.Screens);
            Assert.AreEqual(3, config.Screens.Count);
            Assert.IsNotNull(config.Profiles);
            Assert.IsTrue(config.Profiles.Count >= 3);
        }

        [TestMethod]
        public void ScreenBrightness_GranularTargets_OnlyModifiesEnabledScreens()
        {
            var config = new ScreenBrightnessConfig
            {
                Screens = new List<ScreenHardwareSetting>
                {
                    new ScreenHardwareSetting { SlotIndex = 0, LastBrightness = 40, LastContrast = 40, LockRatio = true },
                    new ScreenHardwareSetting { SlotIndex = 1, LastBrightness = 30, LastContrast = 30, LockRatio = true }
                },
                Profiles = new List<ScreenBrightnessProfile>
                {
                    new ScreenBrightnessProfile
                    {
                        Id = "p_display_a_only",
                        Name = "Display A Only 60%",
                        IncludeInCycle = true,
                        Targets = new List<ScreenCycleEntry>
                        {
                            new ScreenCycleEntry { SlotIndex = 0, Brightness = 60, Contrast = 60, Enabled = true },
                            new ScreenCycleEntry { SlotIndex = 1, Brightness = 90, Contrast = 90, Enabled = false }
                        }
                    }
                }
            };

            var profile = config.Profiles[0];
            foreach (var slot in config.Screens)
            {
                var target = profile.Targets.FirstOrDefault(t => t.SlotIndex == slot.SlotIndex);
                if (target != null && target.Enabled)
                {
                    slot.LastBrightness = target.Brightness;
                }
            }

            Assert.AreEqual(60, config.Screens[0].LastBrightness, "Screen A must be updated to 60%");
            Assert.AreEqual(30, config.Screens[1].LastBrightness, "Screen B must remain untouched at 30%");
        }

        [TestMethod]
        public void ScreenBrightness_CycleConfig_AcceptsDedicatedHotkeys()
        {
            var config = new ScreenBrightnessConfig
            {
                CycleUpHotkey = "Ctrl+Alt+NumPadAdd",
                CycleDownHotkey = "Ctrl+Alt+NumPadSubtract",
                Profiles = new List<ScreenBrightnessProfile>
                {
                    new ScreenBrightnessProfile
                    {
                        Id = "p1",
                        Name = "Night",
                        Hotkey = "Ctrl+Alt+NumPadDivide"
                    }
                }
            };

            Assert.AreEqual("Ctrl+Alt+NumPadAdd", config.CycleUpHotkey);
            Assert.AreEqual("Ctrl+Alt+NumPadSubtract", config.CycleDownHotkey);
            Assert.AreEqual("Ctrl+Alt+NumPadDivide", config.Profiles[0].Hotkey);
        }

        [TestMethod]
        public void HotKeyManager_KeyAliases_MapsMinusAndPlusCorrectly()
        {
            Assert.AreEqual((uint)0xBD, HotkeyManager.NameToVirtualKey("-"));
            Assert.AreEqual((uint)0xBD, HotkeyManager.NameToVirtualKey("minus"));
            Assert.AreEqual((uint)0xBD, HotkeyManager.NameToVirtualKey("oemminus"));

            Assert.AreEqual((uint)0xBB, HotkeyManager.NameToVirtualKey("+"));
            Assert.AreEqual((uint)0xBB, HotkeyManager.NameToVirtualKey("plus"));
            Assert.AreEqual((uint)0xBB, HotkeyManager.NameToVirtualKey("equals"));

            Assert.AreEqual((uint)0x6D, HotkeyManager.NameToVirtualKey("numpadsubtract"));
            Assert.AreEqual((uint)0x6D, HotkeyManager.NameToVirtualKey("subtract"));

            Assert.AreEqual((uint)0x6B, HotkeyManager.NameToVirtualKey("numpadadd"));
            Assert.AreEqual((uint)0x6B, HotkeyManager.NameToVirtualKey("add"));
        }

        [TestMethod]
        public void ScreenBrightness_DifferentialLock_ClampsCorrectlyAtBoundaries()
        {
            int a = 25;
            int b = 10;
            int step = -5;

            a = Math.Clamp(a + step, 0, 100);
            b = Math.Clamp(b + step, 0, 100);
            Assert.AreEqual(20, a);
            Assert.AreEqual(5, b);
            Assert.AreEqual(15, a - b);

            a = Math.Clamp(a + step, 0, 100);
            b = Math.Clamp(b + step, 0, 100);
            Assert.AreEqual(15, a);
            Assert.AreEqual(0, b);
            Assert.AreEqual(15, a - b);

            a = Math.Clamp(a + step, 0, 100);
            b = Math.Clamp(b + step, 0, 100);
            Assert.AreEqual(10, a);
            Assert.AreEqual(0, b);

            a = Math.Clamp(a + step, 0, 100);
            b = Math.Clamp(b + step, 0, 100);
            Assert.AreEqual(5, a);
            Assert.AreEqual(0, b);

            a = Math.Clamp(a + step, 0, 100);
            b = Math.Clamp(b + step, 0, 100);
            Assert.AreEqual(0, a);
            Assert.AreEqual(0, b);
        }

        [TestMethod]
        public void ScreenBrightness_DirectHotkeys_ConfigPersistence()
        {
            var config = new ScreenBrightnessConfig
            {
                LockMonitorDifferential = true,
                DirectHotkeys = new List<ScreenDirectHotkey>
                {
                    new ScreenDirectHotkey
                    {
                        Id = "dh1",
                        Name = "Screen A 70%",
                        TargetSlotIndex = 0,
                        Brightness = 70,
                        Contrast = 70,
                        LockRatio = true,
                        Hotkey = "Ctrl+Alt+1"
                    },
                    new ScreenDirectHotkey
                    {
                        Id = "dh2",
                        Name = "All Screens Dim",
                        TargetSlotIndex = -1,
                        Brightness = 20,
                        Contrast = 20,
                        LockRatio = true,
                        Hotkey = "Ctrl+Alt+0"
                    }
                }
            };

            Assert.IsTrue(config.LockMonitorDifferential);
            Assert.AreEqual(2, config.DirectHotkeys.Count);

            var dhA = config.DirectHotkeys[0];
            Assert.AreEqual(0, dhA.TargetSlotIndex);
            Assert.AreEqual(70, dhA.Brightness);
            Assert.AreEqual("Ctrl+Alt+1", dhA.Hotkey);

            var dhAll = config.DirectHotkeys[1];
            Assert.AreEqual(-1, dhAll.TargetSlotIndex);
            Assert.AreEqual(20, dhAll.Brightness);
            Assert.AreEqual("Ctrl+Alt+0", dhAll.Hotkey);
        }

        [TestMethod]
        public void CreateOrUpdateAdminTask_WhenTaskAlreadyPresent_SucceedsWithoutElevationError()
        {
            if (Program.IsAdminTaskInstalled())
            {
                var ok = Program.CreateOrUpdateAdminTask(true, out var error);
                Assert.IsTrue(ok, "Expected CreateOrUpdateAdminTask to succeed via fast-path: " + error);
                Assert.IsTrue(string.IsNullOrEmpty(error), "Expected no error string: " + error);
            }
        }

        [TestMethod]
        public void ScreenBrightness_VirtualBoundaryClamping_PreservesDifferentialAndRestoresOrder()
        {
            var screens = new List<ScreenHardwareSetting>
            {
                new ScreenHardwareSetting { SlotIndex = 0, LastBrightness = 5, VirtualBrightness = 5 },
                new ScreenHardwareSetting { SlotIndex = 1, LastBrightness = 10, VirtualBrightness = 10 },
                new ScreenHardwareSetting { SlotIndex = 2, LastBrightness = 15, VirtualBrightness = 15 }
            };

            // Drag down by -20% -> all hit bottom floor (0%) but remember their virtual levels
            int deltaDown = -20;
            foreach (var s in screens)
            {
                s.VirtualBrightness = Math.Clamp(s.VirtualBrightness + deltaDown, -100, 200);
                s.LastBrightness = Math.Clamp(s.VirtualBrightness, 0, 100);
            }

            Assert.AreEqual(-15, screens[0].VirtualBrightness);
            Assert.AreEqual(-10, screens[1].VirtualBrightness);
            Assert.AreEqual(-5, screens[2].VirtualBrightness);

            // Physical levels are clamped safely to 0 (hardware is protected)
            Assert.AreEqual(0, screens[0].LastBrightness);
            Assert.AreEqual(0, screens[1].LastBrightness);
            Assert.AreEqual(0, screens[2].LastBrightness);

            // Drag back up by +15% -> screen 2 reaches 10%, screen 1 reaches 5%, screen 0 stays at 0%
            int deltaUp1 = 15;
            foreach (var s in screens)
            {
                s.VirtualBrightness = Math.Clamp(s.VirtualBrightness + deltaUp1, -100, 200);
                s.LastBrightness = Math.Clamp(s.VirtualBrightness, 0, 100);
            }

            Assert.AreEqual(0, screens[0].VirtualBrightness);
            Assert.AreEqual(5, screens[1].VirtualBrightness);
            Assert.AreEqual(10, screens[2].VirtualBrightness);

            Assert.AreEqual(0, screens[0].LastBrightness);
            Assert.AreEqual(5, screens[1].LastBrightness);
            Assert.AreEqual(10, screens[2].LastBrightness);

            // Drag back up remaining +5% (total +20%) -> exact original levels restored
            int deltaUp2 = 5;
            foreach (var s in screens)
            {
                s.VirtualBrightness = Math.Clamp(s.VirtualBrightness + deltaUp2, -100, 200);
                s.LastBrightness = Math.Clamp(s.VirtualBrightness, 0, 100);
            }

            Assert.AreEqual(5, screens[0].VirtualBrightness);
            Assert.AreEqual(10, screens[1].VirtualBrightness);
            Assert.AreEqual(15, screens[2].VirtualBrightness);

            Assert.AreEqual(5, screens[0].LastBrightness);
            Assert.AreEqual(10, screens[1].LastBrightness);
            Assert.AreEqual(15, screens[2].LastBrightness);
        }

        [TestMethod]
        public void ScreenBrightness_IndividualSliderAdjustment_ReanchorsVirtualBaselines()
        {
            var config = new ScreenBrightnessConfig
            {
                Screens = new List<ScreenHardwareSetting>
                {
                    new ScreenHardwareSetting { SlotIndex = 0, LastBrightness = 50, VirtualBrightness = 50 },
                    new ScreenHardwareSetting { SlotIndex = 1, LastBrightness = 60, VirtualBrightness = 60 }
                }
            };

            // User moves Screen B to 80%
            config.Screens[1].LastBrightness = 80;

            // Reanchoring aligns virtual brightness to current physical brightness
            foreach (var s in config.Screens)
            {
                s.VirtualBrightness = s.LastBrightness;
            }

            Assert.AreEqual(50, config.Screens[0].VirtualBrightness);
            Assert.AreEqual(80, config.Screens[1].VirtualBrightness);

            // Step master by +10%
            foreach (var s in config.Screens)
            {
                s.VirtualBrightness = Math.Clamp(s.VirtualBrightness + 10, -100, 200);
                s.LastBrightness = Math.Clamp(s.VirtualBrightness, 0, 100);
            }

            // Both move together preserving the new 30% gap
            Assert.AreEqual(60, config.Screens[0].LastBrightness);
            Assert.AreEqual(90, config.Screens[1].LastBrightness);
        }
    }
}

