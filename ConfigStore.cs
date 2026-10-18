using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace KeyPulse
{
    /// <summary>
    /// Single owner of %APPDATA%\KeyPulse\config.json.
    /// Guarantees: a damaged file is never silently replaced by an empty one (ISSUE_4),
    /// and every write is atomic so a power loss cannot truncate the live config.
    /// </summary>
    public static class ConfigStore
    {
        public static readonly string ConfigDirectory = ResolveConfigDirectory();

        /// <summary>
        /// DEVDATA_01: Route dev-mode configuration to isolated developer sandbox.
        /// </summary>
        private static string ResolveConfigDirectory()
        {
            var envDataDir = Environment.GetEnvironmentVariable("KEYPULSE_DEV_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(envDataDir))
            {
                return Path.GetFullPath(envDataDir);
            }

            if (Program.IsDevMode)
            {
                return Path.Combine(Program.DevHomeDir, "data");
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");
        }

        public static readonly string ConfigPath = Path.Combine(ConfigDirectory, "config.json");
        public static readonly string ConfigLkgPath = Path.Combine(ConfigDirectory, "config.lkg.json");
        public const int CurrentSchemaVersion = 1;

        private static readonly object IoLock = new object();

        public static AppConfig Load(out string? loadError, out string? quarantinePath)
        {
            loadError = null;
            quarantinePath = null;

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
                                Save(recovered, out _);
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
                    UpdateLkgCopy(json);
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

        public static bool Save(AppConfig config, out string error)
        {
            error = string.Empty;

            if (config.IsReadOnlySession)
            {
                error = "Settings are locked because the existing settings file could not be read.";
                return false;
            }

            lock (IoLock)
            {
                try
                {
                    Directory.CreateDirectory(ConfigDirectory);
                    config.SchemaVersion = CurrentSchemaVersion;
                    var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
                    var ok = AtomicFileWriter.WriteAtomic(ConfigPath, json, out error);
                    if (ok)
                    {
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
                    AtomicFileWriter.WriteAtomic(ConfigPath, lkgJson, out _);
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
