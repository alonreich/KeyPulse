using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class DesktopOrganizerStaticPathSandboxLeakTests
    {
        private string _defaultDir = string.Empty;
        private bool _defaultPrimaryExisted;
        private DateTime _defaultPrimaryWrite;
        private bool _defaultLkgExisted;
        private DateTime _defaultLkgWrite;
        private string? _sandboxA;
        private string? _sandboxB;

        [TestInitialize]
        public void Setup()
        {
            // Snapshot the real profile location first so tests can prove it is never touched.
            ConfigStore.ConfigDirectory = null!;
            _defaultDir = ConfigStore.ConfigDirectory;
            var defPrimary = Path.Combine(_defaultDir, "desktop_organizer.json");
            var defLkg = Path.Combine(_defaultDir, "desktop_organizer.lkg.json");
            _defaultPrimaryExisted = File.Exists(defPrimary);
            _defaultPrimaryWrite = _defaultPrimaryExisted ? File.GetLastWriteTimeUtc(defPrimary) : default;
            _defaultLkgExisted = File.Exists(defLkg);
            _defaultLkgWrite = _defaultLkgExisted ? File.GetLastWriteTimeUtc(defLkg) : default;

            _sandboxA = Path.Combine(Path.GetTempPath(), $"kp_test_do_path_a_{Guid.NewGuid():N}");
            _sandboxB = Path.Combine(Path.GetTempPath(), $"kp_test_do_path_b_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandboxA);
            Directory.CreateDirectory(_sandboxB);
            ConfigStore.ConfigDirectory = _sandboxA;
        }

        [TestCleanup]
        public void Cleanup()
        {
            ConfigStore.ConfigDirectory = null!;
            foreach (var dir in new[] { _sandboxA, _sandboxB })
            {
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }

        private static DesktopOrganizerConfig ConfigWith(params string[] names) => new()
        {
            Profiles = names.Select(n => new DesktopLayoutProfile { Name = n }).ToList(),
            ActiveProfileId = string.Empty
        };

        private void AssertDefaultProfileUntouched()
        {
            Assert.AreNotEqual(_defaultDir, _sandboxA);
            var defPrimary = Path.Combine(_defaultDir, "desktop_organizer.json");
            var defLkg = Path.Combine(_defaultDir, "desktop_organizer.lkg.json");
            Assert.AreEqual(_defaultPrimaryExisted, File.Exists(defPrimary), "Real desktop_organizer.json existence changed.");
            Assert.AreEqual(_defaultLkgExisted, File.Exists(defLkg), "Real desktop_organizer.lkg.json existence changed.");
            if (_defaultPrimaryExisted) Assert.AreEqual(_defaultPrimaryWrite, File.GetLastWriteTimeUtc(defPrimary), "Real primary layout file was rewritten.");
            if (_defaultLkgExisted) Assert.AreEqual(_defaultLkgWrite, File.GetLastWriteTimeUtc(defLkg), "Real LKG layout file was rewritten.");
        }

        // ---------- Path binding ----------

        [TestMethod]
        public void Paths_PreserveFileNames()
        {
            Assert.AreEqual("desktop_organizer.json", Path.GetFileName(DesktopOrganizerService.ConfigPath));
            Assert.AreEqual("desktop_organizer.lkg.json", Path.GetFileName(DesktopOrganizerService.LkgConfigPath));
        }

        [TestMethod]
        public void Paths_FollowConfigDirectory_ImmediatelyOnEveryChange()
        {
            Assert.AreEqual(Path.Combine(_sandboxA!, "desktop_organizer.json"), DesktopOrganizerService.ConfigPath);
            Assert.AreEqual(Path.Combine(_sandboxA!, "desktop_organizer.lkg.json"), DesktopOrganizerService.LkgConfigPath);

            ConfigStore.ConfigDirectory = _sandboxB!;
            Assert.AreEqual(Path.Combine(_sandboxB!, "desktop_organizer.json"), DesktopOrganizerService.ConfigPath);
            Assert.AreEqual(Path.Combine(_sandboxB!, "desktop_organizer.lkg.json"), DesktopOrganizerService.LkgConfigPath);

            ConfigStore.ConfigDirectory = _sandboxA!;
            Assert.AreEqual(Path.Combine(_sandboxA!, "desktop_organizer.json"), DesktopOrganizerService.ConfigPath);
        }

        [TestMethod]
        public void Paths_ResetToDefault_ReturnToResolvedDirectory()
        {
            ConfigStore.ConfigDirectory = null!;
            Assert.AreEqual(Path.Combine(_defaultDir, "desktop_organizer.json"), DesktopOrganizerService.ConfigPath);
            Assert.AreEqual(Path.Combine(_defaultDir, "desktop_organizer.lkg.json"), DesktopOrganizerService.LkgConfigPath);
        }

        [TestMethod]
        public void Paths_MatchConfigStoreBackupNaming()
        {
            // ConfigStore snapshots/backups hardcode the same file name; both must resolve to one file.
            Assert.AreEqual(Path.Combine(ConfigStore.ConfigDirectory, "desktop_organizer.json"), DesktopOrganizerService.ConfigPath);
        }

        // ---------- I/O isolation ----------

        [TestMethod]
        public void Save_WritesPrimaryAndLkg_OnlyInsideSandbox()
        {
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith("Work"), out var err), err);

            Assert.IsTrue(File.Exists(Path.Combine(_sandboxA!, "desktop_organizer.json")));
            Assert.IsTrue(File.Exists(Path.Combine(_sandboxA!, "desktop_organizer.lkg.json")));
            Assert.AreEqual(0, Directory.GetFiles(_sandboxB!).Length);
            AssertDefaultProfileUntouched();
        }

        [TestMethod]
        public void SwitchingSandbox_RedirectsSubsequentReadsAndWrites()
        {
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith("A1", "A2"), out var errA), errA);

            ConfigStore.ConfigDirectory = _sandboxB!;
            var fresh = DesktopOrganizerService.LoadConfigDetailed();
            Assert.AreEqual(DesktopOrganizerLoadStatus.NotFound, fresh.Status, "Sandbox B must not see sandbox A's layouts.");

            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith("B1"), out var errB), errB);
            var loadedB = DesktopOrganizerService.LoadConfig();
            CollectionAssert.AreEqual(new[] { "B1" }, loadedB.Profiles.Select(p => p.Name).ToArray());

            ConfigStore.ConfigDirectory = _sandboxA!;
            var loadedA = DesktopOrganizerService.LoadConfig();
            CollectionAssert.AreEqual(new[] { "A1", "A2" }, loadedA.Profiles.Select(p => p.Name).ToArray());
            AssertDefaultProfileUntouched();
        }

        [TestMethod]
        public void EmptyOverwriteGuard_ConsultsCurrentSandboxOnly()
        {
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith("Keep"), out var err), err);

            // In a new, empty sandbox an empty save has nothing to protect and must stay empty.
            ConfigStore.ConfigDirectory = _sandboxB!;
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith(), out err), err);
            Assert.AreEqual(0, DesktopOrganizerService.LoadConfig().Profiles.Count);

            // Back in sandbox A the guard still protects saved layouts.
            ConfigStore.ConfigDirectory = _sandboxA!;
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith(), out err), err);
            CollectionAssert.AreEqual(new[] { "Keep" }, DesktopOrganizerService.LoadConfig().Profiles.Select(p => p.Name).ToArray());
        }

        // ---------- LKG recovery inside the redirected directory ----------

        [TestMethod]
        public void MissingPrimary_RecoversFromSandboxLkg()
        {
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith("Lkg"), out var err), err);
            File.Delete(Path.Combine(_sandboxA!, "desktop_organizer.json"));

            var res = DesktopOrganizerService.LoadConfigDetailed();

            Assert.AreEqual(DesktopOrganizerLoadStatus.Recovered, res.Status);
            Assert.AreEqual(Path.Combine(_sandboxA!, "desktop_organizer.lkg.json"), res.RecoveryPath);
            CollectionAssert.AreEqual(new[] { "Lkg" }, res.Config.Profiles.Select(p => p.Name).ToArray());
        }

        [TestMethod]
        public void CorruptPrimary_QuarantinedAndRecoveredWithinSandbox()
        {
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith("Good"), out var err), err);
            File.WriteAllText(Path.Combine(_sandboxA!, "desktop_organizer.json"), "{ not json");

            var res = DesktopOrganizerService.LoadConfigDetailed();

            Assert.AreEqual(DesktopOrganizerLoadStatus.Recovered, res.Status);
            StringAssert.StartsWith(res.RecoveryPath, _sandboxA!);
            Assert.AreEqual(1, Directory.GetFiles(_sandboxA!, "desktop_organizer.damaged-*.json").Length);
            Assert.AreEqual(0, Directory.GetFiles(_sandboxB!).Length);
            AssertDefaultProfileUntouched();
        }

        [TestMethod]
        public void ZeroProfilePrimary_RestoresFromSandboxLkg()
        {
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith("P1", "P2"), out var err), err);
            File.WriteAllText(Path.Combine(_sandboxA!, "desktop_organizer.json"), "{\"Profiles\":[]}");

            var res = DesktopOrganizerService.LoadConfigDetailed();

            Assert.AreEqual(DesktopOrganizerLoadStatus.Success, res.Status);
            Assert.AreEqual(2, res.Config.Profiles.Count);
        }

        [TestMethod]
        public void LkgFromPreviousSandbox_IsNotUsedForRecovery()
        {
            Assert.IsTrue(DesktopOrganizerService.SaveConfig(ConfigWith("A"), out var err), err);

            ConfigStore.ConfigDirectory = _sandboxB!;
            File.WriteAllText(Path.Combine(_sandboxB!, "desktop_organizer.json"), "{ broken");
            var res = DesktopOrganizerService.LoadConfigDetailed();

            Assert.AreEqual(DesktopOrganizerLoadStatus.Corrupt, res.Status, "Recovery must not reach into a previous sandbox's LKG.");
            Assert.AreEqual(0, res.Config.Profiles.Count);
        }

        // ---------- Performance ----------

        [TestMethod]
        public void PathEvaluation_IsCheap()
        {
            var sw = Stopwatch.StartNew();
            int total = 0;
            for (int i = 0; i < 100_000; i++)
            {
                total += DesktopOrganizerService.ConfigPath.Length + DesktopOrganizerService.LkgConfigPath.Length;
            }
            sw.Stop();
            Assert.IsTrue(total > 0);
            Assert.IsTrue(sw.ElapsedMilliseconds < 1000, $"200k path evaluations took {sw.ElapsedMilliseconds} ms.");
        }
    }
}
