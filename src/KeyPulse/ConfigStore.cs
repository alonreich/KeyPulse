using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace KeyPulse
{
    /// <summary>
    /// Single owner of %APPDATA%\KeyPulse\config.json.
    /// Guarantees: a damaged file is never silently replaced by an empty one (ISSUE_4),
    /// and every write is atomic so a power loss cannot truncate the live config.
    /// </summary>
    public static class ConfigStore
    {
        private static string? _customConfigDirectory;
        public static string ConfigDirectory
        {
            get => _customConfigDirectory ?? ResolveConfigDirectory();
            set => _customConfigDirectory = value;
        }

        public static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");
        public static string ConfigLkgPath => Path.Combine(ConfigDirectory, "config.lkg.json");
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// DEVDATA_01: Route dev-mode configuration to isolated developer sandbox.
        /// Also guarantees that any test runner (VSTest, testhost, MSTest) automatically
        /// resolves to an isolated temporary sandbox instead of production %APPDATA%\KeyPulse.
        /// </summary>
        private static string ResolveConfigDirectory()
        {
            var envDataDir = Environment.GetEnvironmentVariable("KEYPULSE_DEV_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envDataDir))
            {
                return Path.GetFullPath(envDataDir);
            }

            if (IsTestExecutionEnvironment())
            {
                var testDir = Path.Combine(Path.GetTempPath(), "KeyPulse_Test_Sandbox");
                Directory.CreateDirectory(testDir);
                return testDir;
            }

            if (Program.IsDevMode)
            {
                return Path.Combine(Program.DevHomeDir, "data");
            }

            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            if (File.Exists(Path.Combine(appDir, "portable.dat")))
            {
                return Path.Combine(appDir, "data");
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");
        }

        internal static bool IsTestExecutionEnvironment()
        {
            try
            {
                var proc = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
                if (proc.Contains("testhost", StringComparison.OrdinalIgnoreCase) ||
                    proc.Contains("vstest", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var entryAssm = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "";
                if (entryAssm.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                    entryAssm.Contains("vstest", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSTEST_RUNNER")) ||
                    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KEYPULSE_TEST_MODE")))
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static readonly object IoLock = new object();

        // ---- Background writer (audit #4) ---------------------------------------------------------
        // Saves used to run synchronously on the UI thread: read the live file, write a backup, list
        // and prune the folder, fsync the atomic write (with sleep-retries) and fsync the LKG copy -
        // on every column resize or settings click. QueueSave serializes a snapshot on the caller's
        // thread and hands the disk work to one background drain. Every write carries a sequence
        // number, so an older snapshot can never land on top of a newer one, and Load() returns the
        // newest queued snapshot until it is on disk (read-your-writes).
        private static readonly object PendingLock = new object();
        private static string? _pendingJson;
        private static bool _pendingHasHotkeys;
        private static long _pendingSeq;
        private static long _nextSeq;
        private static long _lastWrittenSeq;
        private static int _drainScheduled;

        /// <summary>Raised on the writer thread when a queued save fails.</summary>
        public static event Action<string>? BackgroundSaveFailed;

        /// <summary>
        /// Audit #5: the live in-memory owner of AppConfig (the main window). When one is registered,
        /// TryUpdate applies its mutation to that live object instead of doing a disk
        /// read-modify-write that the owner's next whole-object save would silently overwrite.
        /// </summary>
        private static Func<Action<AppConfig>, string?>? _liveOwner;

        public static void RegisterLiveOwner(Func<Action<AppConfig>, string?>? applyMutation)
        {
            _liveOwner = applyMutation;
        }

        public static bool HasPendingWrite
        {
            get { lock (PendingLock) return _pendingJson != null; }
        }

        public static AppConfig Load(out string? loadError, out string? quarantinePath)
        {
            loadError = null;
            quarantinePath = null;

            string? pending;
            lock (PendingLock) pending = _pendingJson;
            if (pending != null)
            {
                try
                {
                    var queued = JsonSerializer.Deserialize(pending, AppConfigJsonContext.Default.AppConfig);
                    if (queued != null)
                    {
                        if (queued.Hotkeys == null) queued.Hotkeys = new List<HotkeyEntry>();
                        return queued;
                    }
                }
                catch { }
            }

            // Audit #5: Load never writes config.json or the LKG copy. A recovered configuration is
            // persisted by the next ordinary Save, so a background Load() cannot write underneath
            // the running window. A damaged file is still renamed aside (never deleted).
            lock (IoLock)
            {
                if (!File.Exists(ConfigPath))
                {
                    if (File.Exists(ConfigLkgPath))
                    {
                        try
                        {
                            var lkgJson = File.ReadAllText(ConfigLkgPath);
                            var recovered = JsonSerializer.Deserialize(lkgJson, AppConfigJsonContext.Default.AppConfig);
                            if (recovered != null)
                            {
                                if (recovered.Hotkeys == null) recovered.Hotkeys = new List<HotkeyEntry>();
                                MigrateConfig(recovered);
                                loadError = "Your KeyPulse settings file was missing and has been automatically restored from your last known good backup.";
                                return recovered;
                            }
                        }
                        catch { }
                    }
                    return new AppConfig();
                }

                string json;
                try
                {
                    json = File.ReadAllText(ConfigPath);
                }
                catch (Exception ex)
                {
                    loadError = "Your KeyPulse settings file could not be opened (" + ex.Message +
                                "). Your shortcuts were not loaded and will not be overwritten.";
                    return new AppConfig { IsReadOnlySession = true };
                }

                if (string.IsNullOrWhiteSpace(json))
                {
                    quarantinePath = Quarantine();
                    if (TryRecoverFromLkg(out var lkgRecovered))
                    {
                        loadError = "Your KeyPulse settings file was empty and was restored from your last known good backup. Preserved as " + quarantinePath;
                        return lkgRecovered;
                    }
                    loadError = "Your KeyPulse settings file was empty. KeyPulse is in read-only mode to prevent overwriting.";
                    return new AppConfig { IsReadOnlySession = true };
                }

                try
                {
                    var loaded = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                    if (loaded == null) throw new InvalidDataException("The settings file contained no data.");
                    if (loaded.Hotkeys == null) loaded.Hotkeys = new List<HotkeyEntry>();
                    MigrateConfig(loaded);

                    return loaded;
                }
                catch (Exception ex)
                {
                    quarantinePath = Quarantine();
                    if (TryRecoverFromLkg(out var lkgRecovered))
                    {
                        loadError = "Your KeyPulse settings file was damaged (" + ex.Message + ") and has been automatically restored from your last known good backup. The damaged file was preserved as " + quarantinePath + ".";
                        return lkgRecovered;
                    }

                    loadError = "Your KeyPulse settings file is damaged and could not be read (" + ex.Message + "). Settings are locked in read-only mode to prevent data loss. Damaged file was preserved as " + quarantinePath + ".";
                    return new AppConfig { IsReadOnlySession = true };
                }
            }
        }

        /// <summary>Synchronous save; supersedes any queued snapshot. Use where the result matters (restore, setup).</summary>
        public static bool Save(AppConfig config, out string error)
        {
            error = string.Empty;

            if (config.IsReadOnlySession)
            {
                error = "Settings are locked because the existing settings file could not be read.";
                return false;
            }

            string json;
            long seq;
            try
            {
                config.SchemaVersion = Math.Max(config.SchemaVersion, CurrentSchemaVersion);
                json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            lock (PendingLock)
            {
                seq = ++_nextSeq;
                _pendingJson = null;
            }

            return WriteSerialized(json, config.Hotkeys != null && config.Hotkeys.Count > 0, seq, out error);
        }

        /// <summary>
        /// Non-blocking save for UI callers. The snapshot is serialized now (so later edits to the
        /// object cannot tear it) and written by the background drain. Returns false only for a
        /// read-only session or a serialization failure.
        /// </summary>
        public static bool QueueSave(AppConfig config, out string error)
        {
            error = string.Empty;
            if (config.IsReadOnlySession)
            {
                error = "Settings are locked because the existing settings file could not be read.";
                return false;
            }

            string json;
            try
            {
                config.SchemaVersion = Math.Max(config.SchemaVersion, CurrentSchemaVersion);
                json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            lock (PendingLock)
            {
                _pendingJson = json;
                _pendingHasHotkeys = config.Hotkeys != null && config.Hotkeys.Count > 0;
                _pendingSeq = ++_nextSeq;
            }

            if (Interlocked.Exchange(ref _drainScheduled, 1) == 0)
            {
                ThreadPool.QueueUserWorkItem(_ => DrainPending());
            }
            return true;
        }

        /// <summary>Writes any queued snapshot now, on the calling thread. Call on exit and before restores.</summary>
        public static bool Flush(out string error)
        {
            error = string.Empty;
            while (true)
            {
                string? json;
                bool hasHotkeys;
                long seq;
                lock (PendingLock)
                {
                    json = _pendingJson;
                    hasHotkeys = _pendingHasHotkeys;
                    seq = _pendingSeq;
                }
                if (json == null) return true;

                var ok = WriteSerialized(json, hasHotkeys, seq, out error);
                lock (PendingLock)
                {
                    if (_pendingSeq == seq) _pendingJson = null;
                }
                if (!ok) return false;
            }
        }

        public static void Flush() => Flush(out _);

        private static void DrainPending()
        {
            while (true)
            {
                if (!Flush(out var error))
                {
                    // The snapshot stays queued: the next QueueSave, or Flush on exit, retries it.
                    Program.LogCrash("Could not save configuration (background): " + error);
                    try { BackgroundSaveFailed?.Invoke(error); } catch { }
                    Interlocked.Exchange(ref _drainScheduled, 0);
                    return;
                }

                Interlocked.Exchange(ref _drainScheduled, 0);
                lock (PendingLock)
                {
                    if (_pendingJson == null) return;
                }
                if (Interlocked.Exchange(ref _drainScheduled, 1) != 0) return;
            }
        }

        private static bool WriteSerialized(string json, bool hasHotkeys, long seq, out string error)
        {
            error = string.Empty;
            lock (IoLock)
            {
                if (seq <= _lastWrittenSeq) return true;

                try
                {
                    Directory.CreateDirectory(ConfigDirectory);

                    if (File.Exists(ConfigPath))
                    {
                        try
                        {
                            var existingRaw = File.ReadAllText(ConfigPath);
                            if (string.Equals(existingRaw, json, StringComparison.Ordinal))
                            {
                                _lastWrittenSeq = seq;
                                return true;
                            }

                            bool hadShortcuts = existingRaw.Contains("\"KeyCombinations\"") || existingRaw.Contains("\"KeyCombination\"");
                            if (hadShortcuts)
                            {
                                var now = DateTime.UtcNow;
                                if (ShouldTakeBackup(ConfigDirectory, now))
                                {
                                    var backupName = $"config.backup-{now:yyyyMMdd-HHmmss}.json";
                                    var backupTarget = Path.Combine(ConfigDirectory, backupName);
                                    if (!File.Exists(backupTarget))
                                    {
                                        AtomicFileWriter.WriteAtomic(backupTarget, existingRaw, out _);
                                    }
                                    PruneOldBackups(ConfigDirectory, now);
                                }

                                if (!hasHotkeys)
                                {
                                    var safetyPath = Path.Combine(ConfigDirectory, "config.safety-shortcuts.json");
                                    AtomicFileWriter.WriteAtomic(safetyPath, existingRaw, out _);
                                    Program.LogDebug("DATA SAFETY: Preserved config.safety-shortcuts.json before saving configuration with 0 shortcuts.");
                                }
                            }
                        }
                        catch { }
                    }

                    var ok = AtomicFileWriter.WriteAtomic(ConfigPath, json, out error);
                    if (ok)
                    {
                        _lastWrittenSeq = seq;
                        UpdateLkgCopy(json);
                    }
                    return ok;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        // ---- Backup retention (audit #4) ---------------------------------------------------------
        // The old policy wrote a backup on EVERY save and kept the newest 10, so ten quick edits
        // replaced every useful history point (six backups within 19 seconds were observed).
        // Now: at most one backup per BackupMinInterval, and retention keeps the newest
        // BackupKeepRecent plus the newest backup of each of the last BackupKeepDays days.
        internal static readonly TimeSpan BackupMinInterval = TimeSpan.FromMinutes(30);
        internal const int BackupKeepRecent = 5;
        internal const int BackupKeepDays = 14;
        private const string BackupPrefix = "config.backup-";

        internal static bool TryParseBackupTimestamp(string fileName, out DateTime utc)
        {
            utc = default;
            if (!fileName.StartsWith(BackupPrefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return false;
            var stamp = fileName.Substring(BackupPrefix.Length, fileName.Length - BackupPrefix.Length - 5);
            return DateTime.TryParseExact(stamp, "yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out utc);
        }

        private static bool ShouldTakeBackup(string dir, DateTime nowUtc)
        {
            try
            {
                DateTime newest = DateTime.MinValue;
                foreach (var file in Directory.EnumerateFiles(dir, BackupPrefix + "*.json"))
                {
                    if (TryParseBackupTimestamp(Path.GetFileName(file), out var ts) && ts > newest) newest = ts;
                }
                return nowUtc - newest >= BackupMinInterval;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Pure retention rule: returns the backup names to delete.</summary>
        internal static List<string> SelectBackupsToPrune(IEnumerable<string> fileNames, DateTime nowUtc)
        {
            var dated = new List<(string Name, DateTime Utc)>();
            foreach (var name in fileNames)
            {
                if (TryParseBackupTimestamp(name, out var ts)) dated.Add((name, ts));
            }
            dated.Sort((a, b) => b.Utc.CompareTo(a.Utc));

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < dated.Count && i < BackupKeepRecent; i++) keep.Add(dated[i].Name);

            var cutoff = nowUtc.Date.AddDays(-(BackupKeepDays - 1));
            var daysKept = new HashSet<DateTime>();
            foreach (var (name, utc) in dated)
            {
                if (utc.Date >= cutoff && daysKept.Add(utc.Date)) keep.Add(name);
            }

            var delete = new List<string>();
            foreach (var (name, _) in dated)
            {
                if (!keep.Contains(name)) delete.Add(name);
            }
            return delete;
        }

        private static void PruneOldBackups(string dir, DateTime nowUtc)
        {
            try
            {
                var names = new List<string>();
                foreach (var file in Directory.EnumerateFiles(dir, BackupPrefix + "*.json")) names.Add(Path.GetFileName(file));
                foreach (var name in SelectBackupsToPrune(names, nowUtc))
                {
                    try { File.Delete(Path.Combine(dir, name)); } catch { }
                }
            }
            catch { }
        }

        private static bool TryRecoverFromLkg(out AppConfig config)
        {
            config = new AppConfig { IsReadOnlySession = true };
            if (!File.Exists(ConfigLkgPath)) return false;

            try
            {
                var lkgJson = File.ReadAllText(ConfigLkgPath);
                var loaded = JsonSerializer.Deserialize(lkgJson, AppConfigJsonContext.Default.AppConfig);
                if (loaded != null)
                {
                    if (loaded.Hotkeys == null) loaded.Hotkeys = new List<HotkeyEntry>();
                    MigrateConfig(loaded);
                    config = loaded;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Program.LogCrash("Failed to recover from LKG backup: " + ex.Message);
            }
            return false;
        }

        private static void UpdateLkgCopy(string verifiedJson)
        {
            try
            {
                if (File.Exists(ConfigLkgPath))
                {
                    try
                    {
                        var lkgRaw = File.ReadAllText(ConfigLkgPath);
                        if (string.Equals(lkgRaw, verifiedJson, StringComparison.Ordinal)) return;
                        bool lkgHadShortcuts = lkgRaw.Contains("\"KeyCombinations\"") || lkgRaw.Contains("\"KeyCombination\"");
                        bool newHasShortcuts = verifiedJson.Contains("\"KeyCombinations\"") || verifiedJson.Contains("\"KeyCombination\"");
                        if (lkgHadShortcuts && !newHasShortcuts)
                        {
                            return;
                        }
                    }
                    catch { }
                }

                AtomicFileWriter.WriteAtomic(ConfigLkgPath, verifiedJson, out _);
            }
            catch { }
        }

        private static void MigrateConfig(AppConfig config)
        {
            if (config.SchemaVersion < 1)
            {
                config.SchemaVersion = 1;
            }
        }

        /// <summary>
        /// Reads one flag without disturbing the running window's in-memory state.
        ///
        /// ISSUE_2: refuses to run when the file underneath is damaged or unreadable. Load() quietly
        /// quarantines a corrupt file and hands back a blank one; the old code then saved that blank
        /// over the top, so flipping one Settings checkbox erased every shortcut with no warning.
        /// Nothing is written until the user has seen the problem and chosen what to do.
        /// </summary>
        public static bool TryUpdate(Action<AppConfig> mutate, out string error)
        {
            var owner = _liveOwner;
            if (owner != null)
            {
                var ownerError = owner(mutate);
                error = ownerError ?? string.Empty;
                return ownerError == null;
            }

            var config = Load(out var loadError, out var quarantinePath);

            if (!string.IsNullOrEmpty(loadError) && (config.IsReadOnlySession || quarantinePath != null))
            {
                error = "Your KeyPulse settings file needs attention: " + loadError;
                if (!string.IsNullOrEmpty(quarantinePath))
                {
                    error += " The unreadable file was kept as " + quarantinePath + ".";
                }
                error += " This change was NOT saved, so nothing was lost.";
                Program.LogCrash("TryUpdate refused to save over a damaged settings file.");
                return false;
            }

            mutate(config);
            return Save(config, out error);
        }

        /// <summary>
        /// ISSUE_1: keeps a dated copy of the live settings before a restore replaces them, so a
        /// restore from the wrong file is recoverable. Returns the copy's path, or null.
        /// </summary>
        public static string? SaveRollbackCopy(string reason)
        {
            Flush(); // a queued snapshot must be on disk before it is copied
            lock (IoLock)
            {
                try
                {
                    if (!File.Exists(ConfigPath)) return null;

                    Directory.CreateDirectory(ConfigDirectory);
                    var target = Path.Combine(ConfigDirectory,
                        "config." + reason + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.Copy(ConfigPath, target, true);
                    PruneRollbackCopies(reason);
                    return target;
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Could not write a rollback copy: " + ex.Message);
                    return null;
                }
            }
        }

        /// <summary>
        /// Saves timestamped rollback copies of all active persistent stores (config.json,
        /// desktop_organizer.json, and screen_brightness.json) before a restore operation.
        /// </summary>
        public static List<string> SaveAllRollbackCopies(string reason)
        {
            Flush(); // a queued snapshot must be on disk before it is copied
            var copies = new List<string>();
            lock (IoLock)
            {
                try
                {
                    Directory.CreateDirectory(ConfigDirectory);
                    var ts = DateTime.Now.ToString("yyyyMMdd-HHmmss");

                    var mainTarget = SaveRollbackCopy(reason);
                    if (mainTarget != null) copies.Add(mainTarget);

                    var orgPath = Path.Combine(ConfigDirectory, "desktop_organizer.json");
                    if (File.Exists(orgPath))
                    {
                        var orgTarget = Path.Combine(ConfigDirectory, $"desktop_organizer.{reason}-{ts}.json");
                        File.Copy(orgPath, orgTarget, true);
                        copies.Add(orgTarget);
                    }

                    var sbPath = Path.Combine(ConfigDirectory, "screen_brightness.json");
                    if (File.Exists(sbPath))
                    {
                        var sbTarget = Path.Combine(ConfigDirectory, $"screen_brightness.{reason}-{ts}.json");
                        File.Copy(sbPath, sbTarget, true);
                        copies.Add(sbTarget);
                    }
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Could not save all rollback copies: " + ex.Message);
                }
            }
            return copies;
        }

        /// <summary>Keeps the five most recent rollback copies and removes the rest.</summary>
        private static void PruneRollbackCopies(string reason)
        {
            try
            {
                var existing = Directory.GetFiles(ConfigDirectory, "config." + reason + "-*.json");
                if (existing.Length <= 5) return;

                Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < existing.Length - 5; i++)
                {
                    try { File.Delete(existing[i]); } catch { }
                }
            }
            catch { }
        }

        /// <summary>
        /// ISSUE_7: dated safety copy of the settings file OUTSIDE the KeyPulse config folder, taken
        /// before a "Wipe Settings" install destroys that whole folder. Because it sits beside the
        /// folder (not inside it), the wipe cannot take the copy with it. Three most recent kept.
        /// </summary>
        public static string? SaveWipeSafetyCopy()
        {
            Flush(); // a queued snapshot must be on disk before it is copied
            lock (IoLock)
            {
                try
                {
                    if (!File.Exists(ConfigPath)) return null;

                    var parent = Directory.GetParent(ConfigDirectory)?.FullName;
                    if (string.IsNullOrEmpty(parent)) return null;

                    var target = Path.Combine(parent,
                        "KeyPulse.before-wipe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                    File.Copy(ConfigPath, target, true);

                    try
                    {
                        var existing = Directory.GetFiles(parent, "KeyPulse.before-wipe-*.json");
                        if (existing.Length > 3)
                        {
                            Array.Sort(existing, StringComparer.OrdinalIgnoreCase);
                            for (var i = 0; i < existing.Length - 3; i++)
                            {
                                try { File.Delete(existing[i]); } catch { }
                            }
                        }
                    }
                    catch { }

                    return target;
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Could not write a pre-wipe safety copy: " + ex.Message);
                    return null;
                }
            }
        }

        private static string? Quarantine()
        {
            try
            {
                var target = Path.Combine(ConfigDirectory,
                    "config.damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
                File.Move(ConfigPath, target, true);
                return target;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Creates verified recovery copies of ALL persistent user files (config.json AND desktop_organizer.json)
        /// OUTSIDE the KeyPulse directory before a destructive wipe.
        /// Aborts and returns false if any existing persistent file fails to create a verified copy.
        /// </summary>
        public static bool SaveAllWipeSafetyCopies(out List<string> createdCopies, out string error)
        {
            Flush(); // a queued snapshot must be on disk before it is copied
            createdCopies = new List<string>();
            error = string.Empty;

            lock (IoLock)
            {
                var parent = Directory.GetParent(ConfigDirectory)?.FullName;
                if (string.IsNullOrEmpty(parent))
                {
                    error = "Could not locate parent directory for safety copy.";
                    return false;
                }

                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

                if (File.Exists(ConfigPath))
                {
                    try
                    {
                        var target = Path.Combine(parent, $"KeyPulse.before-wipe-{timestamp}.json");
                        File.Copy(ConfigPath, target, true);
                        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(ConfigPath).Length)
                        {
                            error = "Verification of config.json safety copy failed.";
                            return false;
                        }
                        createdCopies.Add(target);
                    }
                    catch (Exception ex)
                    {
                        error = $"Failed to copy config.json to safety copy: {ex.Message}";
                        return false;
                    }
                }

                if (File.Exists(ConfigLkgPath))
                {
                    try
                    {
                        var target = Path.Combine(parent, $"KeyPulse.config.lkg.before-wipe-{timestamp}.json");
                        File.Copy(ConfigLkgPath, target, true);
                        if (File.Exists(target)) createdCopies.Add(target);
                    }
                    catch { }
                }

                var desktopOrganizerPath = Path.Combine(ConfigDirectory, "desktop_organizer.json");
                if (File.Exists(desktopOrganizerPath))
                {
                    try
                    {
                        var target = Path.Combine(parent, $"KeyPulse.desktop_organizer.before-wipe-{timestamp}.json");
                        File.Copy(desktopOrganizerPath, target, true);
                        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(desktopOrganizerPath).Length)
                        {
                            error = "Verification of desktop_organizer.json safety copy failed.";
                            return false;
                        }
                        createdCopies.Add(target);
                    }
                    catch (Exception ex)
                    {
                        error = $"Failed to copy desktop_organizer.json to safety copy: {ex.Message}";
                        return false;
                    }
                }

                var screenBrightnessPath = Path.Combine(ConfigDirectory, "screen_brightness.json");
                if (File.Exists(screenBrightnessPath))
                {
                    try
                    {
                        var target = Path.Combine(parent, $"KeyPulse.screen_brightness.before-wipe-{timestamp}.json");
                        File.Copy(screenBrightnessPath, target, true);
                        if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(screenBrightnessPath).Length)
                        {
                            error = "Verification of screen_brightness.json safety copy failed.";
                            return false;
                        }
                        createdCopies.Add(target);
                    }
                    catch (Exception ex)
                    {
                        error = $"Failed to copy screen_brightness.json to safety copy: {ex.Message}";
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
