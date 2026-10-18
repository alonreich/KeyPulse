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
            // SemVer 2.0: normal release has higher precedence than same version with prerelease
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
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "KeyPulse.csproj")))
            {
                dir = dir.Parent;
            }
            Assert.IsNotNull(dir, "Repository root containing KeyPulse.csproj must be found");

            var changelogPath = Path.Combine(dir.FullName, "docs", "CHANGELOG.md");
            Assert.IsTrue(File.Exists(changelogPath), $"docs/CHANGELOG.md must exist at {changelogPath}");

            var text = File.ReadAllText(changelogPath);
            Assert.IsTrue(text.Contains("## [Unreleased]"), "CHANGELOG.md must contain ## [Unreleased]");
            Assert.IsTrue(text.Contains("AI Agent Directive"), "CHANGELOG.md must contain agent directives");
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
            // Reset custom override to test automatic environment resolution
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

            // Now save a config with 0 shortcuts (e.g. accidental wipe or test)
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
                        Action = (ActionType)999, // Unknown future action type
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
    }
}
