using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using KeyPulse;

namespace KeyPulse.Tests
{
    /// <summary>
    /// TASK 10 - MAINWINDOW_MONOLITHIC_GOD_CLASS: the non-UI responsibilities of MainWindow were extracted into
    /// StartupElevationService, BrowserDetectionService, BackupService (payload mapping) and HotkeyCoordinator.
    /// These tests pin the behaviour those services must keep: exact registry probe order and fallbacks, the
    /// startup wording and UAC relaunch contract, backup mapping and format compatibility, and incremental
    /// (never Clear-all) hotkey reconciliation.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class MainWindowServiceExtractionTests
    {
        private string? _sandbox;

        [TestInitialize]
        public void Setup()
        {
            _sandbox = Path.Combine(Path.GetTempPath(), $"kp_test_mwsplit_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_sandbox);
        }

        [TestCleanup]
        public void Cleanup()
        {
            BrowserDetectionService.RegistryReadOverrideForTesting = null;
            BrowserDetectionService.FileExistsOverrideForTesting = null;
            StartupElevationService.SnapshotOverrideForTesting = null;
            StartupElevationService.SetStartupOverrideForTesting = null;
            StartupElevationService.ProcessStartOverrideForTesting = null;
            HotkeyCoordinator.RegisterOverrideForTesting = null;
            HotkeyCoordinator.UnregisterOverrideForTesting = null;
            if (!string.IsNullOrEmpty(_sandbox) && Directory.Exists(_sandbox))
            {
                try { Directory.Delete(_sandbox, true); } catch { }
            }
        }


        [DataTestMethod]
        [DataRow("Chrome", "chrome.exe")]
        [DataRow("edge", "msedge.exe")]
        [DataRow("FIREFOX", "firefox.exe")]
        [DataRow("brave", "brave.exe")]
        public void Browser_RegistryProbes_AreHkcuThenHklmThenWow64(string key, string exe)
        {
            var probes = BrowserDetectionService.GetRegistryProbes(key);
            Assert.AreEqual(3, probes.Count);
            Assert.AreEqual(BrowserDetectionService.RegistryHive.CurrentUser, probes[0].Hive);
            Assert.AreEqual(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe, probes[0].SubKey);
            Assert.AreEqual(BrowserDetectionService.RegistryHive.LocalMachine, probes[1].Hive);
            Assert.AreEqual(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe, probes[1].SubKey);
            Assert.AreEqual(BrowserDetectionService.RegistryHive.LocalMachine, probes[2].Hive);
            Assert.AreEqual(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\" + exe, probes[2].SubKey);
        }

        [TestMethod]
        public void Browser_InstallFolders_MatchLegacyProbeSequence()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            CollectionAssert.AreEqual(new[]
            {
                Path.Combine(pf, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(pf86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
            }, BrowserDetectionService.GetInstallFolderCandidates("chrome").ToArray());

            CollectionAssert.AreEqual(new[]
            {
                Path.Combine(pf, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(pf86, "Microsoft", "Edge", "Application", "msedge.exe")
            }, BrowserDetectionService.GetInstallFolderCandidates("edge").ToArray());

            CollectionAssert.AreEqual(new[]
            {
                Path.Combine(pf, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(pf86, "Mozilla Firefox", "firefox.exe")
            }, BrowserDetectionService.GetInstallFolderCandidates("firefox").ToArray());

            CollectionAssert.AreEqual(new[]
            {
                Path.Combine(pf, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(pf86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe")
            }, BrowserDetectionService.GetInstallFolderCandidates("brave").ToArray());
        }

        [TestMethod]
        public void Browser_UnknownOrNullKey_FindsNothing()
        {
            BrowserDetectionService.FileExistsOverrideForTesting = _ => true;
            Assert.IsFalse(BrowserDetectionService.TryFindBrowser("opera", out var path));
            Assert.AreEqual(string.Empty, path);
            Assert.IsFalse(BrowserDetectionService.TryFindBrowser(null!, out _));
            Assert.AreEqual(0, BrowserDetectionService.GetRegistryProbes("").Count);
        }

        [TestMethod]
        public void Browser_RegistryValue_WinsOverInstallFolder_AndQuotesAreTrimmed()
        {
            BrowserDetectionService.RegistryReadOverrideForTesting = (hive, sub) =>
                hive == BrowserDetectionService.RegistryHive.LocalMachine && sub.StartsWith(@"Software\Microsoft", StringComparison.Ordinal)
                    ? "\"D:\\Apps\\Chrome\\chrome.exe\""
                    : null;
            var probed = new List<string>();
            BrowserDetectionService.FileExistsOverrideForTesting = p => { probed.Add(p); return true; };

            Assert.IsTrue(BrowserDetectionService.TryFindBrowser("chrome", out var path));
            Assert.AreEqual(@"D:\Apps\Chrome\chrome.exe", path);
            Assert.AreEqual(1, probed.Count, "The first existing candidate must short-circuit the probe.");
        }

        [TestMethod]
        public void Browser_FallsBackToInstallFolder_WhenRegistryEmpty_AndDeduplicatesCaseInsensitively()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var expected = Path.Combine(local, "BraveSoftware", "Brave-Browser", "Application", "brave.exe");
            BrowserDetectionService.RegistryReadOverrideForTesting = (_, _) => expected.ToUpperInvariant();

            var probed = new List<string>();
            BrowserDetectionService.FileExistsOverrideForTesting = p =>
            {
                probed.Add(p);
                return string.Equals(p, expected, StringComparison.OrdinalIgnoreCase);
            };

            Assert.IsTrue(BrowserDetectionService.TryFindBrowser("brave", out var path));
            Assert.AreEqual(expected, path, ignoreCase: true);
            Assert.AreEqual(1, probed.Count(p => string.Equals(p, expected, StringComparison.OrdinalIgnoreCase)),
                "Duplicate candidates (registry vs folder, differing only in case) must be probed once.");
        }

        [TestMethod]
        public void Browser_NotInstalled_ReturnsFalse()
        {
            BrowserDetectionService.RegistryReadOverrideForTesting = (_, _) => null;
            BrowserDetectionService.FileExistsOverrideForTesting = _ => false;
            Assert.IsFalse(BrowserDetectionService.TryFindBrowser("firefox", out var path));
            Assert.AreEqual(string.Empty, path);
        }

        [TestMethod]
        public void Browser_Preference_HonoursLegacyChromeFlag()
        {
            Assert.AreEqual("Edge", BrowserDetectionService.ResolvePreference(new AppConfig { PreferredBrowser = "Edge", UseGoogleChromeForUrls = true }));
            Assert.AreEqual("Chrome", BrowserDetectionService.ResolvePreference(new AppConfig { PreferredBrowser = null!, UseGoogleChromeForUrls = true }));
            Assert.AreEqual("System", BrowserDetectionService.ResolvePreference(new AppConfig { PreferredBrowser = null!, UseGoogleChromeForUrls = false }));
            Assert.IsTrue(BrowserDetectionService.IsSystemDefault("system"));
            Assert.IsFalse(BrowserDetectionService.IsSystemDefault("Chrome"));
        }


        [TestMethod]
        public void Startup_Describe_CoversEveryState()
        {
            var unknown = StartupElevationService.DescribeStartup(new(Program.StartupState.Unknown, false, false));
            Assert.AreEqual(StartupElevationService.UnknownStateMessage, unknown.Text);
            Assert.AreEqual("AppWarningBrush", unknown.BrushKey);

            var off = StartupElevationService.DescribeStartup(new(Program.StartupState.Off, false, false));
            Assert.AreEqual("KeyPulse does not start with Windows.", off.Text);
            Assert.AreEqual("AppTextMutedBrush", off.BrushKey);

            var on = StartupElevationService.DescribeStartup(new(Program.StartupState.On, false, false));
            Assert.AreEqual("KeyPulse starts with Windows.", on.Text);

            var adminOk = StartupElevationService.DescribeStartup(new(Program.StartupState.On, true, true));
            Assert.AreEqual("KeyPulse starts with Windows using its Administrator logon task.", adminOk.Text);
            Assert.AreEqual("AppTextMutedBrush", adminOk.BrushKey);

            var adminIneffective = StartupElevationService.DescribeStartup(new(Program.StartupState.On, true, false));
            StringAssert.Contains(adminIneffective.Text, "NOT running as Administrator");
            Assert.AreEqual("AppWarningBrush", adminIneffective.BrushKey);
        }

        [TestMethod]
        public void Startup_IneffectiveAdmin_RequiresInstalledTaskStartupOnAndNotElevated()
        {
            Assert.IsTrue(new StartupElevationService.StartupSnapshot(Program.StartupState.On, true, false).AdminTaskIneffective);
            Assert.IsFalse(new StartupElevationService.StartupSnapshot(Program.StartupState.On, true, true).AdminTaskIneffective);
            Assert.IsFalse(new StartupElevationService.StartupSnapshot(Program.StartupState.Off, true, false).AdminTaskIneffective);
            Assert.IsFalse(new StartupElevationService.StartupSnapshot(Program.StartupState.On, false, false).AdminTaskIneffective);
        }

        [TestMethod]
        public void Startup_DescribeChange_SuccessAndFailure()
        {
            var on = StartupElevationService.DescribeStartupChange(true, true, string.Empty);
            Assert.AreEqual("KeyPulse will start hidden in the tray when Windows starts.", on.Text);
            Assert.AreEqual("AppSuccessSoftBrush", on.BrushKey);

            var off = StartupElevationService.DescribeStartupChange(false, true, string.Empty);
            Assert.AreEqual("KeyPulse will no longer start with Windows.", off.Text);

            var failed = StartupElevationService.DescribeStartupChange(true, false, "Access is denied.");
            Assert.AreEqual("Startup change failed: Access is denied.", failed.Text);
            Assert.AreEqual("AppDangerBrush", failed.BrushKey);
        }

        [TestMethod]
        public void Startup_SetStartup_AndSnapshot_GoThroughSeams()
        {
            var calls = new List<bool>();
            StartupElevationService.SetStartupOverrideForTesting = enable => { calls.Add(enable); return (false, "boom"); };
            Assert.IsFalse(StartupElevationService.SetStartup(true, out var err));
            Assert.AreEqual("boom", err);
            CollectionAssert.AreEqual(new[] { true }, calls);

            StartupElevationService.SnapshotOverrideForTesting = () => new(Program.StartupState.On, true, false);
            var snap = StartupElevationService.LoadSnapshot();
            Assert.IsTrue(snap.IsStartupOn);
            Assert.IsTrue(snap.AdminTaskIneffective);
        }

        [TestMethod]
        public void Startup_RelaunchStartInfo_IsElevatedRunasWithForUser()
        {
            var info = StartupElevationService.BuildRelaunchStartInfo(StartupElevationService.SetupAdminTaskArgument, @"PC\alice");
            Assert.AreEqual(Program.ExePath, info.FileName);
            Assert.AreEqual("--setup-admin-task --for-user \"PC\\alice\"", info.Arguments);
            Assert.IsTrue(info.UseShellExecute);
            Assert.AreEqual("runas", info.Verb);
            Assert.AreEqual("--remove-admin-task", StartupElevationService.RemoveAdminTaskArgument);
        }

        [TestMethod]
        public void Startup_DeclinedUac_ReportsFailureWithoutThrowing()
        {
            ProcessStartInfo? seen = null;
            StartupElevationService.ProcessStartOverrideForTesting = psi => { seen = psi; return false; };
            Assert.IsFalse(StartupElevationService.TryStartPrivilegeRelaunch(StartupElevationService.RemoveAdminTaskArgument, out var error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsNotNull(seen);
            StringAssert.StartsWith(seen!.Arguments, "--remove-admin-task --for-user \"");
            StringAssert.Contains(seen.Arguments, Environment.UserName);

            StartupElevationService.ProcessStartOverrideForTesting = _ => throw new System.ComponentModel.Win32Exception(1223);
            Assert.IsFalse(StartupElevationService.TryStartPrivilegeRelaunch("--x", out error));
            Assert.IsFalse(string.IsNullOrEmpty(error));

            StartupElevationService.ProcessStartOverrideForTesting = _ => true;
            Assert.IsTrue(StartupElevationService.TryStartPrivilegeRelaunch("--x", out error));
            Assert.AreEqual(string.Empty, error);
        }


        private static HotkeyEntry MakeEntry(string id, params string[] combos) => new HotkeyEntry
        {
            Id = id,
            IsEnabled = true,
            DisplayName = "Name " + id,
            Action = ActionType.OpenFolder,
            Target = @"C:\Temp\" + id,
            IsTargetObfuscated = true,
            AddToQuickActionMenu = true,
            KeyCombinations = new List<string>(combos)
        };

        [TestMethod]
        public void Backup_BuildPayload_CopiesPreferencesLayoutAndRows_ButNotLaunchOnBoot()
        {
            var config = new AppConfig
            {
                PreferredBrowser = null!,
                UseGoogleChromeForUrls = false,
                SoundEnabled = false,
                TypingDelayMs = 18,
                Theme = "Dark",
                UpdateChannel = null!,
                SkippedUpdateVersion = null!,
                LastActiveSettingsTab = 2,
                MainWindowWidth = 1111,
                ShortcutTargetColumnWidth = 555,
                LaunchOnBoot = true
            };
            var rows = new[] { MakeEntry("a", "Ctrl+Alt+A", "Ctrl+Alt+B"), MakeEntry("b") };
            var sb = new ScreenBrightnessConfig();
            var dorg = new DesktopOrganizerConfig();

            var payload = BackupService.BuildPayload(config, rows, dorg, sb);

            Assert.AreEqual("System", payload.PreferredBrowser);
            Assert.AreEqual("Stable", payload.UpdateChannel);
            Assert.AreEqual(string.Empty, payload.SkippedUpdateVersion);
            Assert.IsFalse(payload.SoundEnabled);
            Assert.AreEqual(18, payload.TypingDelayMs);
            Assert.AreEqual("Dark", payload.Theme);
            Assert.AreEqual(2, payload.LastActiveSettingsTab);
            Assert.AreEqual(1111, payload.Window!.MainWindowWidth);
            Assert.AreEqual(555, payload.Window.ShortcutTargetColumnWidth);
            Assert.AreSame(sb, payload.ScreenBrightness);
            Assert.AreSame(dorg, payload.DesktopOrganizer);
            Assert.IsNull(typeof(BackupPayload).GetProperty("LaunchOnBoot"), "LaunchOnBoot must never travel in a backup.");

            Assert.AreEqual(2, payload.Shortcuts.Count);
            var first = payload.Shortcuts[0];
            Assert.AreEqual("a", first.Id);
            CollectionAssert.AreEqual(new[] { "Ctrl+Alt+A", "Ctrl+Alt+B" }, first.KeyCombinations);
            Assert.AreNotSame(rows[0].KeyCombinations, first.KeyCombinations, "Payload must own a copy of the combo list.");
            Assert.AreEqual(@"C:\Temp\a", first.Target);
            Assert.IsTrue(first.IsTargetObfuscated);
            Assert.IsTrue(first.AddToQuickActionMenu);
            Assert.AreEqual(0, payload.Shortcuts[1].KeyCombinations.Count);
        }

        [TestMethod]
        public void Backup_CreateHotkeyEntries_HandlesBlankIdsAndLegacySingleCombo()
        {
            var payload = new BackupPayload
            {
                Shortcuts = new List<BackupShortcut>
                {
                    new BackupShortcut { Id = "", Action = ActionType.TypeText, Target = null!, DisplayName = null, KeyCombinations = new List<string> { "Ctrl+1", "Ctrl+2" } },
                    new BackupShortcut { Id = "keep", Action = ActionType.LaunchProgram, Target = "notepad.exe", LegacyKeyCombination = "Ctrl+Shift+N" },
                    new BackupShortcut { Id = "manual", Action = ActionType.OpenFolder, Target = @"C:\" }
                }
            };

            var entries = BackupService.CreateHotkeyEntries(payload);

            Assert.AreEqual(3, entries.Count);
            Assert.IsTrue(Guid.TryParse(entries[0].Id, out _), "A blank Id must be replaced with a fresh GUID.");
            Assert.AreEqual(string.Empty, entries[0].Target);
            Assert.AreEqual(string.Empty, entries[0].DisplayName);
            CollectionAssert.AreEqual(new[] { "Ctrl+1", "Ctrl+2" }, entries[0].KeyCombinations);
            Assert.AreEqual("keep", entries[1].Id);
            CollectionAssert.AreEqual(new[] { "Ctrl+Shift+N" }, entries[1].KeyCombinations);
            Assert.AreEqual(0, entries[2].KeyCombinations.Count, "Zero-hotkey manual actions must survive restore.");
        }

        [TestMethod]
        public void Backup_ApplyPreferences_UsesLegacyDefaults()
        {
            var config = new AppConfig { LaunchOnBoot = true };
            var payload = new BackupPayload
            {
                PreferredBrowser = "",
                UseGoogleChromeForUrls = true,
                Theme = " ",
                UpdateChannel = "",
                SkippedUpdateVersion = null!,
                ShortcutSortColumn = null!,
                TypingDelayMs = 0,
                LastActiveSettingsTab = 9,
                SoundEnabled = false,
                HasSeenTrayHint = true
            };
            var normalizeCalls = new List<int>();

            BackupService.ApplyPreferences(payload, config, v => { normalizeCalls.Add(v); return 42; });

            Assert.AreEqual("Chrome", config.PreferredBrowser);
            Assert.AreEqual("System", config.Theme);
            Assert.AreEqual("Stable", config.UpdateChannel);
            Assert.AreEqual(string.Empty, config.SkippedUpdateVersion);
            Assert.AreEqual(string.Empty, config.ShortcutSortColumn);
            Assert.AreEqual(42, config.TypingDelayMs);
            CollectionAssert.AreEqual(new[] { 0 }, normalizeCalls);
            Assert.AreEqual(BackupService.SettingsTabCount - 1, config.LastActiveSettingsTab);
            Assert.IsFalse(config.SoundEnabled);
            Assert.IsTrue(config.HasSeenTrayHint);
            Assert.IsTrue(config.LaunchOnBoot, "Restore must leave the machine's startup setting alone.");

            payload.LastActiveSettingsTab = -4;
            payload.UseGoogleChromeForUrls = false;
            BackupService.ApplyPreferences(payload, config, v => v);
            Assert.AreEqual(0, config.LastActiveSettingsTab);
            Assert.AreEqual("System", config.PreferredBrowser);
        }

        [DataTestMethod]
        [DataRow(800d, 600d, true)]
        [DataRow(200d, 150d, true)]
        [DataRow(20000d, 20000d, true)]
        [DataRow(199d, 600d, false)]
        [DataRow(800d, 149d, false)]
        [DataRow(20001d, 600d, false)]
        [DataRow(double.NaN, 600d, false)]
        [DataRow(800d, double.PositiveInfinity, false)]
        public void Backup_IsSaneWindowSize(double w, double h, bool expected)
        {
            Assert.AreEqual(expected, BackupService.IsSaneWindowSize(w, h));
        }

        [TestMethod]
        public void Backup_BuiltPayload_RoundTripsThroughEncryptedFileFormat()
        {
            var config = new AppConfig { Theme = "Light", TypingDelayMs = 6 };
            var rows = new[] { MakeEntry("x", "Ctrl+Alt+X"), MakeEntry("y", "Ctrl+Alt+Y", "Win+Y") };
            var payload = BackupService.BuildPayload(config, rows, null, null);

            var path = Path.Combine(_sandbox!, "roundtrip.kpbackup");
            Assert.IsTrue(BackupService.Write(path, payload, "correct horse battery", out var writeError), writeError);

            var inspection = BackupService.Inspect(path);
            Assert.IsTrue(inspection.Ok, inspection.Error);
            Assert.IsTrue(inspection.IsEncrypted);
            Assert.AreEqual(BackupKind.Native, inspection.Kind);

            var wrong = BackupService.Open(inspection, "not the password");
            Assert.IsFalse(wrong.Ok);
            Assert.IsTrue(wrong.WrongPassphrase);

            var opened = BackupService.Open(inspection, "correct horse battery");
            Assert.IsTrue(opened.Ok, opened.Error);
            var restored = BackupService.CreateHotkeyEntries(opened.Payload!);
            Assert.AreEqual(2, restored.Count);
            CollectionAssert.AreEqual(new[] { "Ctrl+Alt+Y", "Win+Y" }, restored[1].KeyCombinations);
            Assert.AreEqual(@"C:\Temp\y", restored[1].Target);

            var target = new AppConfig();
            BackupService.ApplyPreferences(opened.Payload!, target, v => v);
            Assert.AreEqual("Light", target.Theme);
            Assert.AreEqual(6, target.TypingDelayMs);
        }


        private sealed class FakeOs
        {
            public int NextId = 100;
            public readonly HashSet<string> Taken = new(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> Registered = new();
            public readonly List<int> Unregistered = new();

            public void Install()
            {
                HotkeyCoordinator.RegisterOverrideForTesting = (combo, _) =>
                {
                    if (Taken.Contains(combo)) return (false, 0);
                    Registered.Add(combo);
                    return (true, NextId++);
                };
                HotkeyCoordinator.UnregisterOverrideForTesting = id => Unregistered.Add(id);
            }
        }

        private static List<(string, string)> Combos(params string[] combos)
            => combos.Select(c => (c, c.ToUpperInvariant())).ToList();

        [TestMethod]
        public void Coordinator_RegistersNewCombos_AndReportsCounts()
        {
            var os = new FakeOs(); os.Install();
            var entry = MakeEntry("r1", "Ctrl+A", "Ctrl+B");

            var result = HotkeyCoordinator.Reconcile(entry, Combos("Ctrl+A", "Ctrl+B"), () => { });

            Assert.AreEqual(2, result.SuccessCount);
            Assert.AreEqual(0, result.ConflictCount);
            Assert.IsNull(result.FirstConflictCombo);
            CollectionAssert.AreEqual(new[] { "Ctrl+A", "Ctrl+B" }, os.Registered, "Registration must use the combo as typed.");
            Assert.AreEqual(2, entry.RegisteredHotkeyIds.Count);
            Assert.AreEqual("Active", entry.ComboStatuses["CTRL+A"]);
            Assert.AreEqual(100, entry.RegisteredHotkeyId);
            Assert.AreEqual("CTRL+A", entry.RegisteredCombo);
        }

        [TestMethod]
        public void Coordinator_UnchangedCombosAreNotReRegistered_RemovedOnesReleasedIndividually()
        {
            var os = new FakeOs(); os.Install();
            var entry = MakeEntry("r2");
            HotkeyCoordinator.Reconcile(entry, Combos("Ctrl+A", "Ctrl+B"), () => { });
            var idA = entry.RegisteredHotkeyIds["CTRL+A"];
            var idB = entry.RegisteredHotkeyIds["CTRL+B"];
            entry.ComboHints["CTRL+B"] = "stale hint";
            os.Registered.Clear();

            var result = HotkeyCoordinator.Reconcile(entry, Combos("Ctrl+A", "Ctrl+C"), () => { });

            CollectionAssert.AreEqual(new[] { "Ctrl+C" }, os.Registered, "Ctrl+A was already owned; only Ctrl+C is new.");
            CollectionAssert.AreEqual(new[] { idB }, os.Unregistered, "Only the removed combo may be released (no Clear-all).");
            Assert.AreEqual(idA, entry.RegisteredHotkeyIds["CTRL+A"]);
            Assert.IsFalse(entry.RegisteredHotkeyIds.ContainsKey("CTRL+B"));
            Assert.IsFalse(entry.ComboStatuses.ContainsKey("CTRL+B"));
            Assert.IsFalse(entry.ComboHints.ContainsKey("CTRL+B"));
            Assert.AreEqual(2, result.SuccessCount);
        }

        [TestMethod]
        public void Coordinator_Conflicts_AreMarked_AndFirstConflictReported()
        {
            var os = new FakeOs(); os.Install();
            os.Taken.Add("Ctrl+B");
            os.Taken.Add("Ctrl+C");
            var entry = MakeEntry("r3");

            var result = HotkeyCoordinator.Reconcile(entry, Combos("Ctrl+A", "Ctrl+B", "Ctrl+C"), () => { });

            Assert.AreEqual(1, result.SuccessCount);
            Assert.AreEqual(2, result.ConflictCount);
            Assert.AreEqual("Ctrl+B", result.FirstConflictCombo);
            Assert.AreEqual("Conflict", entry.ComboStatuses["CTRL+B"]);
            Assert.AreEqual("Conflict", entry.ComboStatuses["CTRL+C"]);
            Assert.IsFalse(entry.RegisteredHotkeyIds.ContainsKey("CTRL+B"));
        }

        [TestMethod]
        public void Coordinator_AllConflicts_LeavesNoRegisteredId()
        {
            var os = new FakeOs(); os.Install();
            os.Taken.Add("Ctrl+Z");
            var entry = MakeEntry("r4");

            var result = HotkeyCoordinator.Reconcile(entry, Combos("Ctrl+Z"), () => { });

            Assert.AreEqual(0, result.SuccessCount);
            Assert.AreEqual(0, entry.RegisteredHotkeyId);
            Assert.AreEqual(string.Empty, entry.RegisteredCombo);
        }

        [TestMethod]
        public void Coordinator_CallbackPassedToRegistration_IsTheRowAction()
        {
            Action? captured = null;
            HotkeyCoordinator.RegisterOverrideForTesting = (_, cb) => { captured = cb; return (true, 7); };
            HotkeyCoordinator.UnregisterOverrideForTesting = _ => { };
            var fired = 0;

            HotkeyCoordinator.Reconcile(MakeEntry("r5"), Combos("Ctrl+F"), () => fired++);
            Assert.IsNotNull(captured);
            captured!();
            Assert.AreEqual(1, fired);
        }

        [TestMethod]
        public void Coordinator_Release_UnregistersEverythingIncludingLegacyId()
        {
            var os = new FakeOs(); os.Install();
            var entry = MakeEntry("r6");
            HotkeyCoordinator.Reconcile(entry, Combos("Ctrl+A", "Ctrl+B"), () => { });
            entry.ComboHints["CTRL+A"] = "x";
            var ids = entry.RegisteredHotkeyIds.Values.ToList();

            HotkeyCoordinator.Release(entry);

            CollectionAssert.IsSubsetOf(ids, os.Unregistered);
            Assert.AreEqual(0, entry.RegisteredHotkeyIds.Count);
            Assert.AreEqual(0, entry.ComboStatuses.Count);
            Assert.AreEqual(0, entry.ComboHints.Count);
            Assert.AreEqual(string.Empty, entry.RegisteredCombo);
            Assert.AreEqual(0, entry.RegisteredHotkeyId);

            os.Unregistered.Clear();
            var legacy = MakeEntry("r7");
            legacy.RegisteredHotkeyId = 55;
            HotkeyCoordinator.Release(legacy);
            CollectionAssert.AreEqual(new[] { 55 }, os.Unregistered);
            Assert.AreEqual(0, legacy.RegisteredHotkeyId);
        }

        [TestMethod]
        public void Coordinator_ReleaseAll_ReleasesEveryRow_AndSkipsZeroIds()
        {
            var os = new FakeOs(); os.Install();
            var a = MakeEntry("a"); var b = MakeEntry("b");
            HotkeyCoordinator.Reconcile(a, Combos("Ctrl+1"), () => { });
            HotkeyCoordinator.Reconcile(b, Combos("Ctrl+2"), () => { });
            b.RegisteredHotkeyIds["GHOST"] = 0;

            HotkeyCoordinator.ReleaseAll(new[] { a, b });

            Assert.IsFalse(os.Unregistered.Contains(0), "Zero ids are never handed to UnregisterHotKey.");
            CollectionAssert.AreEquivalent(new[] { 100, 101 }, os.Unregistered.Distinct().ToArray());
            Assert.AreEqual(0, a.RegisteredHotkeyIds.Count + b.RegisteredHotkeyIds.Count);
        }
    }
}
