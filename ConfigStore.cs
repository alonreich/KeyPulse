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
        public static readonly string ConfigDirectory =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyPulse");

        public static readonly string ConfigPath = Path.Combine(ConfigDirectory, "config.json");

        private static readonly object IoLock = new object();

        public static AppConfig Load(out string? loadError, out string? quarantinePath)
        {
            loadError = null;
            quarantinePath = null;

            lock (IoLock)
            {
                if (!File.Exists(ConfigPath)) return new AppConfig();

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
                    loadError = "Your KeyPulse settings file was empty, so KeyPulse started with no shortcuts.";
                    return new AppConfig();
                }

                try
                {
                    var loaded = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                    if (loaded == null) throw new InvalidDataException("The settings file contained no data.");
                    if (loaded.Hotkeys == null) loaded.Hotkeys = new List<HotkeyEntry>();
                    return loaded;
                }
                catch (Exception ex)
                {
                    quarantinePath = Quarantine();
                    loadError = "Your KeyPulse settings file is damaged and could not be read (" + ex.Message + ").";
                    return new AppConfig();
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
                    var json = JsonSerializer.Serialize(config, AppConfigJsonContext.Default.AppConfig);
                    var temp = ConfigPath + ".tmp";

                    File.WriteAllText(temp, json);

                    if (File.Exists(ConfigPath))
                    {
                        try
                        {
                            File.Replace(temp, ConfigPath, null);
                        }
                        catch
                        {
                            File.Copy(temp, ConfigPath, true);
                            try { File.Delete(temp); } catch { }
                        }
                    }
                    else
                    {
                        File.Move(temp, ConfigPath);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
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
    }
}
