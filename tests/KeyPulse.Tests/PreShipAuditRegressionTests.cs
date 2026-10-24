using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    /// <summary>Regression coverage for the seven pre-ship architectural audit findings.</summary>
    [TestClass]
    [DoNotParallelize]
    public class PreShipAuditRegressionTests
    {
        private string? _sandbox;

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_preship_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
            ConfigStore.ConfigDirectory = _sandbox;
            ConfigStore.RegisterLiveOwner(null);
        }

        [TestCleanup]
        public void Cleanup()
        {
            ConfigStore.Flush();
            ConfigStore.RegisterLiveOwner(null);
            HotkeyManager.DisplayRefreshOverrideForTesting = null;
            ScreenBrightnessService.PhysicalMonitorsProviderForTesting = null;
            ScreenBrightnessService.DestroyPhysicalMonitorsHookForTesting = null;
            ScreenBrightnessService.InvalidateAllSessions();
            ConfigStore.ConfigDirectory = null!;
            if (!string.IsNullOrEmpty(_sandbox) && Directory.Exists(_sandbox))
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }

        private static string SourcePath(string file)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "KeyPulse", file))) dir = dir.Parent;
            Assert.IsNotNull(dir, "Repository root not found from " + AppContext.BaseDirectory);
            return Path.Combine(dir!.FullName, "src", "KeyPulse", file);
        }

        // ---- #1 Update signature -----------------------------------------------------------------

        [TestMethod]
        public void Signature_Unsigned_IsNotSigned()
        {
            Assert.AreEqual(SignatureVerdict.NotSigned, AuthenticodeVerifier.Evaluate(AuthenticodeVerifier.TRUST_E_NOSIGNATURE, null, out _));
        }

        [TestMethod]
        public void Signature_ValidDigest_PinnedSigner_IsTrusted_EvenWithUntrustedRoot()
        {
            var pin = AuthenticodeVerifier.PinnedSignerThumbprints[0];
            Assert.AreEqual(SignatureVerdict.TrustedPinnedSigner, AuthenticodeVerifier.Evaluate(0, pin, out _));
            Assert.AreEqual(SignatureVerdict.TrustedPinnedSigner, AuthenticodeVerifier.Evaluate(AuthenticodeVerifier.CERT_E_UNTRUSTEDROOT, pin.ToLowerInvariant(), out _));
        }

        [TestMethod]
        public void Signature_SelfSignedImpostorNamedKeyPulse_IsRejected()
        {
            Assert.AreEqual(SignatureVerdict.Rejected, AuthenticodeVerifier.Evaluate(AuthenticodeVerifier.CERT_E_UNTRUSTEDROOT, "AABBCCDDEEFF00112233445566778899AABBCCDD", out var reason));
            StringAssert.Contains(reason, "unknown publisher");
            Assert.AreEqual(SignatureVerdict.Rejected, AuthenticodeVerifier.Evaluate(0, null, out _));
        }

        [TestMethod]
        public void Signature_TamperedDigest_IsRejected_EvenForPinnedSigner()
        {
            const uint TRUST_E_BAD_DIGEST = 0x80096010;
            Assert.AreEqual(SignatureVerdict.Rejected, AuthenticodeVerifier.Evaluate(TRUST_E_BAD_DIGEST, AuthenticodeVerifier.PinnedSignerThumbprints[0], out _));
        }

        [TestMethod]
        public void Signature_RealUnsignedFile_IsNotSigned()
        {
            var path = Path.Combine(_sandbox!, "unsigned.exe");
            File.Copy(typeof(PreShipAuditRegressionTests).Assembly.Location, path);
            Assert.AreEqual(SignatureVerdict.NotSigned, AuthenticodeVerifier.Verify(path, out var reason), reason);
        }

        [TestMethod]
        public void Signature_ValidlySignedByUnpinnedPublisher_IsRejected()
        {
            // The test host is Authenticode-signed by Microsoft: a valid signature, but not ours.
            var signed = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            Assert.AreEqual(SignatureVerdict.Rejected, AuthenticodeVerifier.Verify(signed, out var reason), reason);
            StringAssert.Contains(reason, "unknown publisher", "WinVerifyTrust must have verified the digest (result 0) before the pin check.");
        }

        [TestMethod]
        public void UpdateChecker_NoLongerTrustsSubjectNameAlone()
        {
            var src = File.ReadAllText(SourcePath("UpdateChecker.cs"));
            Assert.IsFalse(src.Contains("Subject.Contains(\"KeyPulse\""), "Signer must be pinned by thumbprint, never matched by subject name.");
            StringAssert.Contains(src, "AuthenticodeVerifier.Verify(");
        }

        // ---- #2 Native library fast path ---------------------------------------------------------

        private string MakeRuntimeDir(string fingerprint, bool validHeaders = true)
        {
            var dir = Path.Combine(_sandbox!, "runtime");
            Directory.CreateDirectory(dir);
            foreach (var lib in Program.RequiredNativeLibs)
            {
                var bytes = new byte[256];
                if (validHeaders) { bytes[0] = 0x4D; bytes[1] = 0x5A; }
                File.WriteAllBytes(Path.Combine(dir, lib), bytes);
                File.WriteAllText(Path.Combine(dir, lib) + ".stamp", "256|ABC");
            }
            File.WriteAllText(Path.Combine(dir, Program.NativeLibFingerprintFileName), fingerprint);
            return dir;
        }

        [TestMethod]
        public void NativeLibFastPath_MatchingFingerprint_IsValid()
        {
            var dir = MakeRuntimeDir("v1|lib:10");
            Assert.IsTrue(Program.IsNativeLibFastPathValid(dir, Path.Combine(dir, Program.NativeLibFingerprintFileName), "v1|lib:10"));
        }

        [TestMethod]
        public void NativeLibFastPath_PreviousBuildFingerprint_ForcesReExtraction()
        {
            var dir = MakeRuntimeDir("v1|lib:10");
            Assert.IsFalse(Program.IsNativeLibFastPathValid(dir, Path.Combine(dir, Program.NativeLibFingerprintFileName), "v2|lib:12"));
        }

        [TestMethod]
        public void NativeLibFastPath_MissingFingerprintOrCorruptDll_ForcesReExtraction()
        {
            var dir = MakeRuntimeDir("v1", validHeaders: false);
            var fp = Path.Combine(dir, Program.NativeLibFingerprintFileName);
            Assert.IsFalse(Program.IsNativeLibFastPathValid(dir, fp, "v1"), "A DLL without an MZ header must be repaired.");

            File.Delete(fp);
            Assert.IsFalse(Program.IsNativeLibFastPathValid(dir, fp, "v1"), "Legacy runtime folders without a fingerprint must be re-verified.");
        }

        [TestMethod]
        public void NativeLibFingerprint_IncludesVersionAndEveryEmbeddedLibrary()
        {
            var asm = typeof(Program).Assembly;
            var fp = Program.BuildNativeLibFingerprint(asm, asm.GetManifestResourceNames());
            StringAssert.StartsWith(fp, Program.AppVersion);
            StringAssert.Contains(fp, "libSkiaSharp.dll.gz:");
        }

        // ---- #3 Display change listener ----------------------------------------------------------

        [TestMethod]
        public void DisplayRefreshTrigger_DecisionTable()
        {
            Assert.IsTrue(HotkeyManager.IsDisplayRefreshTrigger(0x007E, IntPtr.Zero, null, null), "WM_DISPLAYCHANGE");
            Assert.IsTrue(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x0012), null, null), "resume automatic");
            Assert.IsTrue(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x0007), null, null), "resume suspend");
            Assert.IsTrue(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x8013), 1, 0), "monitor off -> on");
            Assert.IsTrue(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x8013), 1, 2), "monitor dimmed -> on");
            Assert.IsFalse(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x8013), 1, null), "baseline report at registration");
            Assert.IsFalse(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x8013), 1, 1), "already on");
            Assert.IsFalse(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x8013), 0, 1), "monitor off");
            Assert.IsFalse(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x8013), 2, 1), "monitor dimmed");
            Assert.IsFalse(HotkeyManager.IsDisplayRefreshTrigger(0x0218, new IntPtr(0x0004), null, null), "suspend");
            Assert.IsFalse(HotkeyManager.IsDisplayRefreshTrigger(0x0312, IntPtr.Zero, null, null), "WM_HOTKEY");
        }

        [TestMethod]
        public void DisplayRefresh_BurstOfEvents_CoalescesIntoOneRefresh()
        {
            int runs = 0;
            HotkeyManager.DisplayRefreshOverrideForTesting = () => Interlocked.Increment(ref runs);
            for (int i = 0; i < 10; i++) HotkeyManager.ScheduleDisplayRefresh();

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref runs) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
            Thread.Sleep(1000);
            Assert.AreEqual(1, Volatile.Read(ref runs));
        }

        [TestMethod]
        public void MessageLoop_NoLongerChecksSentMessagesFromGetMessage()
        {
            var src = File.ReadAllText(SourcePath("HotKeyManager.cs"));
            Assert.IsFalse(src.Contains("msg.message == WM_DISPLAYCHANGE"), "Sent messages never surface from GetMessage; handle them in the window procedure.");
            StringAssert.Contains(src, "SetWindowLongPtr(hWnd, GWLP_WNDPROC");
            StringAssert.Contains(src, "RegisterPowerSettingNotification");
        }

        // ---- #4 Config writer and backups --------------------------------------------------------

        private static AppConfig ConfigWith(params string[] names) => new AppConfig
        {
            Hotkeys = names.Select(n => new HotkeyEntry { DisplayName = n, KeyCombinations = new List<string> { "Ctrl+Alt+" + n } }).ToList()
        };

        [TestMethod]
        public void QueueSave_LoadSeesQueuedSnapshot_AndFlushPersistsNewest()
        {
            Assert.IsTrue(ConfigStore.QueueSave(ConfigWith("A"), out _));
            Assert.IsTrue(ConfigStore.QueueSave(ConfigWith("B"), out _));

            var loaded = ConfigStore.Load(out _, out _);
            Assert.AreEqual("B", loaded.Hotkeys.Single().DisplayName, "Load must return the newest queued snapshot.");

            Assert.IsTrue(ConfigStore.Flush(out var err), err);
            Assert.IsFalse(ConfigStore.HasPendingWrite);
            StringAssert.Contains(File.ReadAllText(ConfigStore.ConfigPath), "\"B\"");
        }

        [TestMethod]
        public void QueueSave_SnapshotIsTakenAtCallTime()
        {
            var cfg = ConfigWith("A");
            ConfigStore.QueueSave(cfg, out _);
            cfg.Hotkeys[0].DisplayName = "MutatedAfterQueue";
            ConfigStore.Flush();
            StringAssert.Contains(File.ReadAllText(ConfigStore.ConfigPath), "\"A\"");
        }

        [TestMethod]
        public void QueueSave_ManyRapidSaves_ReachDiskInOrder()
        {
            for (int i = 0; i < 50; i++) ConfigStore.QueueSave(ConfigWith("N" + i), out _);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (ConfigStore.HasPendingWrite && DateTime.UtcNow < deadline) Thread.Sleep(20);
            ConfigStore.Flush();
            StringAssert.Contains(File.ReadAllText(ConfigStore.ConfigPath), "\"N49\"");
        }

        [TestMethod]
        public void SyncSave_SupersedesOlderQueuedSnapshot()
        {
            ConfigStore.QueueSave(ConfigWith("Old"), out _);
            Assert.IsTrue(ConfigStore.Save(ConfigWith("New"), out var err), err);
            ConfigStore.Flush();
            StringAssert.Contains(File.ReadAllText(ConfigStore.ConfigPath), "\"New\"");
        }

        [TestMethod]
        public void RapidSaves_TakeAtMostOneBackup()
        {
            for (int i = 0; i < 10; i++)
            {
                Assert.IsTrue(ConfigStore.Save(ConfigWith("S" + i), out var err), err);
            }
            var backups = Directory.GetFiles(_sandbox!, "config.backup-*.json");
            Assert.IsTrue(backups.Length <= 1, $"Expected at most one backup for a burst of saves, found {backups.Length}.");
        }

        [TestMethod]
        public void IdenticalSave_DoesNotRewriteFile()
        {
            var cfg = ConfigWith("Same");
            Assert.IsTrue(ConfigStore.Save(cfg, out _));
            var before = File.GetLastWriteTimeUtc(ConfigStore.ConfigPath);
            Thread.Sleep(50);
            Assert.IsTrue(ConfigStore.Save(cfg, out _));
            Assert.AreEqual(before, File.GetLastWriteTimeUtc(ConfigStore.ConfigPath));
        }

        [TestMethod]
        public void BackupRetention_KeepsRecentAndOnePerDay()
        {
            var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
            var names = new List<string>();
            for (int day = 0; day < 20; day++)
            {
                for (int h = 0; h < 3; h++)
                {
                    names.Add($"config.backup-{now.AddDays(-day).AddHours(-h):yyyyMMdd-HHmmss}.json");
                }
            }
            names.Add("config.backup-garbage.json");

            var delete = ConfigStore.SelectBackupsToPrune(names, now);
            var kept = names.Where(n => ConfigStore.TryParseBackupTimestamp(n, out _)).Except(delete).ToList();

            foreach (var newest in names.Take(ConfigStore.BackupKeepRecent)) CollectionAssert.Contains(kept, newest);
            for (int day = 0; day < ConfigStore.BackupKeepDays; day++)
            {
                var date = now.Date.AddDays(-day);
                Assert.IsTrue(kept.Any(n => ConfigStore.TryParseBackupTimestamp(n, out var ts) && ts.Date == date), $"Day {date:d} must keep one backup.");
            }
            Assert.IsFalse(kept.Any(n => ConfigStore.TryParseBackupTimestamp(n, out var ts) && ts.Date < now.Date.AddDays(-(ConfigStore.BackupKeepDays - 1))), "Older than the window must be pruned.");
            CollectionAssert.DoesNotContain(delete, "config.backup-garbage.json", "Unparseable names are never deleted.");
        }

        // ---- #5 Single config owner --------------------------------------------------------------

        [TestMethod]
        public void TryUpdate_WithLiveOwner_MutatesOwnerInsteadOfDisk()
        {
            ConfigStore.Save(ConfigWith("Disk"), out _);
            var live = ConfigWith("Live");
            ConfigStore.RegisterLiveOwner(mutate => { mutate(live); return null; });

            Assert.IsTrue(ConfigStore.TryUpdate(c => c.DesktopOrganizerWindowX = 123, out var err), err);
            Assert.AreEqual(123, live.DesktopOrganizerWindowX);
            Assert.IsFalse(File.ReadAllText(ConfigStore.ConfigPath).Contains("123"), "No disk read-modify-write while a live owner exists.");
        }

        [TestMethod]
        public void TryUpdate_LiveOwnerRefusal_IsReported()
        {
            ConfigStore.RegisterLiveOwner(_ => "locked");
            Assert.IsFalse(ConfigStore.TryUpdate(c => c.LaunchOnBoot = false, out var err));
            Assert.AreEqual("locked", err);
        }

        [TestMethod]
        public void Load_RecoveringFromLkg_DoesNotWriteConfig()
        {
            ConfigStore.Save(ConfigWith("Keep"), out _);
            File.Delete(ConfigStore.ConfigPath);

            var loaded = ConfigStore.Load(out var loadError, out _);
            Assert.AreEqual("Keep", loaded.Hotkeys.Single().DisplayName);
            Assert.IsNotNull(loadError);
            Assert.IsFalse(File.Exists(ConfigStore.ConfigPath), "Load() must stay read-only; the next Save persists the recovery.");
        }

        // ---- #6 IPC handoff ----------------------------------------------------------------------

        [TestMethod]
        public void IpcAck_OnlyPositiveWellFormedReplyCountsAsDelivered()
        {
            Assert.IsTrue(IpcService.IsPositiveAck(Encoding.UTF8.GetBytes("{\"Success\":true,\"Message\":\"Accepted\"}")));
            Assert.IsFalse(IpcService.IsPositiveAck(Encoding.UTF8.GetBytes("{\"Success\":false,\"Message\":\"Invalid message\"}")));
            Assert.IsFalse(IpcService.IsPositiveAck(Encoding.UTF8.GetBytes("not json")));
        }

        [TestMethod]
        public void StagedAdd_IsClaimedByRename_AndLeftoverClaimsAreRecovered()
        {
            File.WriteAllLines(Program.StagedAddPath, new[] { @"C:\one.exe", "", @"C:\two.exe" });
            File.WriteAllLines(Program.StagedAddPath + ".claimed-crashed", new[] { @"C:\zero.exe" });
            File.SetLastWriteTimeUtc(Program.StagedAddPath + ".claimed-crashed", DateTime.UtcNow.AddMinutes(-5));

            var targets = Program.TakeStagedAddTargets();
            CollectionAssert.AreEqual(new[] { @"C:\zero.exe", @"C:\one.exe", @"C:\two.exe" }, targets);
            Assert.IsFalse(File.Exists(Program.StagedAddPath));
            Assert.AreEqual(0, Directory.GetFiles(_sandbox!, "staged-add.txt.claimed-*").Length);
        }

        [TestMethod]
        public void StagedAdd_FileStillOpenByWriter_IsNotLost()
        {
            using (var writer = new FileStream(Program.StagedAddPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                var bytes = Encoding.UTF8.GetBytes("C:\\pending.exe" + Environment.NewLine);
                writer.Write(bytes, 0, bytes.Length);
                writer.Flush();
                Assert.AreEqual(0, Program.TakeStagedAddTargets().Count, "A file being appended must not be consumed.");
            }

            CollectionAssert.AreEqual(new[] { "C:\\pending.exe" }, Program.TakeStagedAddTargets());
        }

        // ---- #7 Exit handle destruction ----------------------------------------------------------

        [TestMethod]
        public void InvalidateAllSessions_BusBusy_LeavesHandlesAlone()
        {
            int destroyCalls = 0;
            ScreenBrightnessService.PhysicalMonitorsProviderForTesting = mon =>
            {
                var arr = new ScreenBrightnessService.PHYSICAL_MONITOR[1];
                arr[0].hPhysicalMonitor = new IntPtr(0x77);
                arr[0].szPhysicalMonitorDescription = "Exit Race Monitor";
                return (true, 1, arr);
            };
            ScreenBrightnessService.DestroyPhysicalMonitorsHookForTesting = (_, _) => Interlocked.Increment(ref destroyCalls);

            Assert.IsNotNull(ScreenBrightnessService.GetOrCreateSessionForTesting(new IntPtr(0x4242)));

            ScreenBrightnessService.BusLockForTesting.Wait();
            try
            {
                Assert.IsFalse(ScreenBrightnessService.InvalidateAllSessions(50));
                Assert.AreEqual(0, Volatile.Read(ref destroyCalls), "Handles must never be destroyed while another thread owns the bus.");
                Assert.AreEqual(1, ScreenBrightnessService.ActiveSessionCountForTesting);
            }
            finally
            {
                ScreenBrightnessService.BusLockForTesting.Release();
            }

            Assert.IsTrue(ScreenBrightnessService.InvalidateAllSessions(1000));
            Assert.AreEqual(1, Volatile.Read(ref destroyCalls));
            Assert.AreEqual(0, ScreenBrightnessService.ActiveSessionCountForTesting);
        }
    }
}
