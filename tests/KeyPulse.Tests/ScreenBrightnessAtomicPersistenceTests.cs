using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class ScreenBrightnessAtomicPersistenceTests
    {
        private string? _testSandboxDir;

        [TestInitialize]
        public void SetupTestSandbox()
        {
            _testSandboxDir = Path.Combine(Path.GetTempPath(), $"kp_test_sb_atomic_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_testSandboxDir);
            ConfigStore.ConfigDirectory = _testSandboxDir;
            ScreenBrightnessService.ResetToDefaults();
        }

        [TestCleanup]
        public void CleanupTestSandbox()
        {
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_testSandboxDir) && Directory.Exists(_testSandboxDir))
            {
                try
                {
                    Directory.Delete(_testSandboxDir, true);
                }
                catch { }
            }
        }

        [TestMethod]
        public void SaveConfig_HappyPath_CreatesConfigAndLkgWithZeroOrphanedTmpFiles()
        {
            var config = ScreenBrightnessService.LoadConfig();
            config.Screens[0].LockRatio = false;
            config.Screens[0].LastBrightness = 72;
            config.Screens[0].LastContrast = 68;

            bool saved = ScreenBrightnessService.SaveConfig(config);
            Assert.IsTrue(saved, "SaveConfig should succeed under normal conditions.");

            Assert.IsTrue(File.Exists(ScreenBrightnessService.ConfigFilePath), "Primary config file should exist.");
            Assert.IsTrue(File.Exists(ScreenBrightnessService.BackupConfigFilePath), "LKG backup config file should exist.");

            var reloaded = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual(72, reloaded.Screens[0].LastBrightness);
            Assert.AreEqual(68, reloaded.Screens[0].LastContrast);

            var tmpFiles = Directory.GetFiles(_testSandboxDir!, "*.tmp");
            Assert.AreEqual(0, tmpFiles.Length, "Zero orphaned .tmp files must remain after successful save: " + string.Join(", ", tmpFiles));
        }

        [TestMethod]
        public void SaveConfig_DeterministicCleanup_DeletesStaleTempFiles()
        {
            var staleTmp1 = Path.Combine(_testSandboxDir!, $"screen_brightness.{Guid.NewGuid():N}.tmp");
            var staleTmp2 = Path.Combine(_testSandboxDir!, $"screen_brightness.json.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(staleTmp1, "stale content 1");
            File.WriteAllText(staleTmp2, "stale content 2");

            Assert.IsTrue(File.Exists(staleTmp1));
            Assert.IsTrue(File.Exists(staleTmp2));

            var config = ScreenBrightnessService.LoadConfig();
            config.Screens[0].LastBrightness = 45;
            bool saved = ScreenBrightnessService.SaveConfig(config);

            Assert.IsTrue(saved);
            Assert.IsFalse(File.Exists(staleTmp1), "Stale screen_brightness.*.tmp file should be deterministically cleaned up.");
            Assert.IsFalse(File.Exists(staleTmp2), "Stale screen_brightness.json.*.tmp file should be deterministically cleaned up.");

            var remainingTmpFiles = Directory.GetFiles(_testSandboxDir!, "*.tmp");
            Assert.AreEqual(0, remainingTmpFiles.Length, "All .tmp files must be deleted.");
        }

        [TestMethod]
        public void SaveConfig_SimulatedFileLockContention_LeavesZeroOrphanedTmpFiles_AndPreservesExistingConfig()
        {
            var initialConfig = ScreenBrightnessService.LoadConfig();
            initialConfig.Screens[0].LastBrightness = 55;
            bool initialSaved = ScreenBrightnessService.SaveConfig(initialConfig);
            Assert.IsTrue(initialSaved);

            string initialConfigContent = File.ReadAllText(ScreenBrightnessService.ConfigFilePath);
            string initialBackupContent = File.ReadAllText(ScreenBrightnessService.BackupConfigFilePath);

            using (var lockStream = new FileStream(
                ScreenBrightnessService.ConfigFilePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                var modifiedConfig = ScreenBrightnessService.LoadConfig();
                modifiedConfig.Screens[0].LastBrightness = 99;

                bool saveResult = ScreenBrightnessService.SaveConfig(modifiedConfig);
                Assert.IsFalse(saveResult, "SaveConfig should fail when primary config is locked exclusively.");

                var tmpFiles = Directory.GetFiles(_testSandboxDir!, "*.tmp");
                Assert.AreEqual(0, tmpFiles.Length, "Zero orphaned .tmp files must remain after failed lock contention: " + string.Join(", ", tmpFiles));
            }

            string currentConfigContent = File.ReadAllText(ScreenBrightnessService.ConfigFilePath);
            string currentBackupContent = File.ReadAllText(ScreenBrightnessService.BackupConfigFilePath);

            Assert.AreEqual(initialConfigContent, currentConfigContent, "Locked file content must be preserved uncorrupted.");
            Assert.AreEqual(initialBackupContent, currentBackupContent, "Backup file content must remain uncorrupted.");
            Assert.IsTrue(currentBackupContent.Length > 0, "Backup file must never be truncated to 0 bytes.");
        }

        [TestMethod]
        public void SaveConfig_TransientLockClearsDuringRetry_SucceedsWithZeroOrphanedTmpFiles()
        {
            var config = ScreenBrightnessService.LoadConfig();
            config.Screens[0].LastBrightness = 30;
            ScreenBrightnessService.SaveConfig(config);

            var lockAcquired = new ManualResetEventSlim(false);

            var lockTask = Task.Run(() =>
            {
                using var fs = new FileStream(
                    ScreenBrightnessService.ConfigFilePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None);
                lockAcquired.Set();
                Thread.Sleep(100);
            });

            lockAcquired.Wait(2000);

            var nextConfig = ScreenBrightnessService.LoadConfig();
            nextConfig.Screens[0].LastBrightness = 85;

            bool saveResult = ScreenBrightnessService.SaveConfig(nextConfig);
            lockTask.Wait(5000);

            Assert.IsTrue(saveResult, "SaveConfig should recover via retry backoff after transient lock clears.");

            var tmpFiles = Directory.GetFiles(_testSandboxDir!, "*.tmp");
            Assert.AreEqual(0, tmpFiles.Length, "Zero orphaned .tmp files must remain: " + string.Join(", ", tmpFiles));

            var reloaded = ScreenBrightnessService.LoadConfig();
            Assert.AreEqual(85, reloaded.Screens[0].LastBrightness);
        }

        [TestMethod]
        public void LoadConfig_PrimaryCorrupted_RecoversFromLkgBackup()
        {
            var config = ScreenBrightnessService.LoadConfig();
            config.Screens[0].LastBrightness = 62;
            ScreenBrightnessService.SaveConfig(config);

            Assert.IsTrue(File.Exists(ScreenBrightnessService.BackupConfigFilePath));
            var backupJson = File.ReadAllText(ScreenBrightnessService.BackupConfigFilePath);
            Assert.IsTrue(backupJson.Contains("\"LastBrightness\":62") || backupJson.Contains("\"LastBrightness\": 62"));

            ScreenBrightnessService.InvalidateConfigCache();
            File.WriteAllText(ScreenBrightnessService.ConfigFilePath, "{\"corrupt\": true, invalid_json...");

            var recoveredConfig = ScreenBrightnessService.LoadConfig();
            Assert.IsNotNull(recoveredConfig);
            Assert.AreEqual(62, recoveredConfig.Screens[0].LastBrightness);
        }

        [TestMethod]
        public void SaveConfig_NonTruncatingLkgRotation_PreservesPreviousValidState()
        {
            var v1 = ScreenBrightnessService.LoadConfig();
            v1.Screens[0].LastBrightness = 35;
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(v1));

            var lkgV1 = File.ReadAllText(ScreenBrightnessService.BackupConfigFilePath);
            Assert.IsTrue(lkgV1.Contains("35"));

            var v2 = ScreenBrightnessService.LoadConfig();
            v2.Screens[0].LastBrightness = 70;
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(v2));

            var lkgV2 = File.ReadAllText(ScreenBrightnessService.BackupConfigFilePath);
            Assert.IsTrue(lkgV2.Length > 0, "LKG backup must not be 0 bytes.");
            Assert.IsTrue(lkgV2.Contains("70"), "LKG backup must reflect updated verified configuration.");

            var tmpFiles = Directory.GetFiles(_testSandboxDir!, "*.tmp");
            Assert.AreEqual(0, tmpFiles.Length);
        }

        [TestMethod]
        public void SaveConfig_WhenDirectoryDoesNotExist_CreatesDirectoryAndSucceeds()
        {
            var nestedDir = Path.Combine(_testSandboxDir!, "nested_subfolder");
            if (Directory.Exists(nestedDir))
            {
                Directory.Delete(nestedDir, true);
            }
            ConfigStore.ConfigDirectory = nestedDir;
            ScreenBrightnessService.InvalidateConfigCache();

            var cfg = ScreenBrightnessService.CreateDefaultConfig();
            cfg.Screens[0].LastBrightness = 88;
            bool saved = ScreenBrightnessService.SaveConfig(cfg);

            Assert.IsTrue(saved, "SaveConfig should succeed even when AppDataFolder does not initially exist.");
            Assert.IsTrue(Directory.Exists(nestedDir), "Directory must be created automatically.");
            Assert.IsTrue(File.Exists(ScreenBrightnessService.ConfigFilePath), "Config file must be created.");
            Assert.IsTrue(File.Exists(ScreenBrightnessService.BackupConfigFilePath), "LKG backup file must be created.");

            var tmpFiles = Directory.GetFiles(nestedDir, "*.tmp");
            Assert.AreEqual(0, tmpFiles.Length, "Zero orphaned tmp files must remain.");
        }

        [TestMethod]
        public void SaveConfig_PreWriteRotation_DoesNotOverwriteLkgWithCorruptedConfig()
        {
            var v1 = ScreenBrightnessService.LoadConfig();
            v1.Screens[0].LastBrightness = 42;
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(v1));

            string validBackupBefore = File.ReadAllText(ScreenBrightnessService.BackupConfigFilePath);
            Assert.IsTrue(validBackupBefore.Contains("42"));

            File.WriteAllText(ScreenBrightnessService.ConfigFilePath, "{ bad json non parseable ### }");

            var v2 = ScreenBrightnessService.LoadConfig();
            v2.Screens[0].LastBrightness = 84;
            bool saved = ScreenBrightnessService.SaveConfig(v2);

            Assert.IsTrue(saved);
            var lkgAfter = File.ReadAllText(ScreenBrightnessService.BackupConfigFilePath);
            Assert.IsTrue(lkgAfter.Contains("84"));
            Assert.IsFalse(lkgAfter.Contains("bad json"));

            var tmpFiles = Directory.GetFiles(_testSandboxDir!, "*.tmp");
            Assert.AreEqual(0, tmpFiles.Length);
        }

        [TestMethod]
        public void SaveConfig_BackupLocked_StillSucceedsAndLeavesZeroOrphanedTmpFiles()
        {
            var v1 = ScreenBrightnessService.LoadConfig();
            v1.Screens[0].LastBrightness = 50;
            Assert.IsTrue(ScreenBrightnessService.SaveConfig(v1));

            using (var lockStream = new FileStream(
                ScreenBrightnessService.BackupConfigFilePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                var v2 = ScreenBrightnessService.LoadConfig();
                v2.Screens[0].LastBrightness = 75;

                bool saved = ScreenBrightnessService.SaveConfig(v2);
                Assert.IsTrue(saved, "SaveConfig primary write should succeed even if backup file is locked.");

                var tmpFiles = Directory.GetFiles(_testSandboxDir!, "*.tmp");
                Assert.AreEqual(0, tmpFiles.Length, "Zero orphaned .tmp files must remain: " + string.Join(", ", tmpFiles));
            }

            var primaryContent = File.ReadAllText(ScreenBrightnessService.ConfigFilePath);
            Assert.IsTrue(primaryContent.Contains("75"));
        }
    }
}
