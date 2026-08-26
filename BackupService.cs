using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KeyPulse
{
    /// <summary>How a restore candidate was recognised.</summary>
    public enum BackupKind
    {
        /// <summary>KeyPulse's own backup format, version 2 or later.</summary>
        Native,

        /// <summary>A raw copy of config.json written by a build before the backup format existed.</summary>
        LegacyConfigCopy
    }

    public sealed class BackupInspection
    {
        public bool Ok { get; init; }
        public string Error { get; init; } = string.Empty;
        public BackupKind Kind { get; init; }
        public bool IsEncrypted { get; init; }
        public string CreatedUtc { get; init; } = string.Empty;
        public string CreatedByVersion { get; init; } = string.Empty;
        public int ShortcutCount { get; init; }

        /// <summary>Set for an unencrypted file; null when a passphrase is still needed.</summary>
        public BackupPayload? Payload { get; init; }

        /// <summary>Kept so the payload can be opened once the passphrase is known.</summary>
        internal BackupEnvelope? Envelope { get; init; }
    }

    public sealed class BackupOpenResult
    {
        public bool Ok { get; init; }
        public bool WrongPassphrase { get; init; }
        public string Error { get; init; } = string.Empty;
        public BackupPayload? Payload { get; init; }
    }

    /// <summary>
    /// ISSUE_1 / ISSUE_24: reading, writing, protecting and validating backup files.
    ///
    /// Everything about a restore that can refuse, warn, or roll back lives here so the UI cannot
    /// accidentally skip a check. The rule this class exists to enforce: a file gets to replace the
    /// user's shortcuts only after it has proved what it is, proved it is undamaged, and been read
    /// end to end successfully. Nothing is touched before all three hold.
    /// </summary>
    public static class BackupService
    {
        private const int SaltBytes = 16;
        private const int NonceBytes = 12;
        private const int TagBytes = 16;
        private const int KeyBytes = 32;

        /// <summary>
        /// Deliberately high. This runs once per backup and once per restore, so a second of work is
        /// invisible to the user but multiplies the cost of guessing a weak passphrase offline.
        /// Stored in the envelope so a future increase does not break existing files.
        /// </summary>
        public const int DefaultKdfIterations = 300_000;

        private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("KeyPulseBackup.v2");


        /// <summary>
        /// Serializes, hashes, optionally encrypts, writes ATOMICALLY, then reads the result back
        /// and re-validates it. A backup that cannot be read again is not a backup, and finding that
        /// out now is far better than finding it out on the day it is needed.
        /// </summary>
        public static bool Write(string path, BackupPayload payload, string? passphrase, out string error)
        {
            error = string.Empty;

            try
            {
                var payloadJson = JsonSerializer.Serialize(payload, AppConfigJsonContext.Default.BackupPayload);
                var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

                var envelope = new BackupEnvelope
                {
                    FileType = BackupEnvelope.BackupFileType,
                    FormatVersion = BackupEnvelope.CurrentFormatVersion,
                    CreatedUtc = DateTime.UtcNow.ToString("O"),
                    CreatedByVersion = Program.AppVersion,
                    PayloadSha256 = ToHex(SHA256.HashData(payloadBytes))
                };

                if (string.IsNullOrEmpty(passphrase))
                {
                    envelope.Encryption = BackupEnvelope.EncryptionNone;
                    envelope.Payload = Convert.ToBase64String(payloadBytes);
                }
                else
                {
                    var salt = RandomNumberGenerator.GetBytes(SaltBytes);
                    var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
                    var key = DeriveKey(passphrase, salt, DefaultKdfIterations);

                    var cipher = new byte[payloadBytes.Length];
                    var tag = new byte[TagBytes];
                    try
                    {
                        using var aes = new AesGcm(key, TagBytes);
                        aes.Encrypt(nonce, payloadBytes, cipher, tag, AssociatedData);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(key);
                    }

                    envelope.Encryption = BackupEnvelope.EncryptionAesGcm;
                    envelope.KdfIterations = DefaultKdfIterations;
                    envelope.Salt = Convert.ToBase64String(salt);
                    envelope.Nonce = Convert.ToBase64String(nonce);
                    envelope.AuthTag = Convert.ToBase64String(tag);
                    envelope.Payload = Convert.ToBase64String(cipher);
                }

                CryptographicOperations.ZeroMemory(payloadBytes);

                var envelopeJson = JsonSerializer.Serialize(envelope, AppConfigJsonContext.Default.BackupEnvelope);

                var temp = path + ".writing";
                File.WriteAllText(temp, envelopeJson, new UTF8Encoding(false));
                try
                {
                    File.Move(temp, path, true);
                }
                catch
                {
                    File.Copy(temp, path, true);
                    try { File.Delete(temp); } catch { }
                }

                var verification = Inspect(path);
                if (!verification.Ok)
                {
                    error = "The backup was written but could not be read back (" + verification.Error + ").";
                    return false;
                }

                if (!string.IsNullOrEmpty(passphrase))
                {
                    var reopened = Open(verification, passphrase);
                    if (!reopened.Ok)
                    {
                        error = "The backup was written but could not be decrypted again (" + reopened.Error + ").";
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }


        /// <summary>
        /// Identifies a candidate file WITHOUT changing anything. Never throws.
        /// </summary>
        public static BackupInspection Inspect(string path)
        {
            string text;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return Failure("That file no longer exists.");

                if (info.Length > 64L * 1024 * 1024)
                {
                    return Failure("That file is far too large to be a KeyPulse backup.");
                }

                text = File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                return Failure("That file could not be read (" + ex.Message + ").");
            }

            if (string.IsNullOrWhiteSpace(text)) return Failure("That file is empty.");

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(text);
            }
            catch (Exception ex)
            {
                return Failure("That file is not valid JSON (" + ex.Message + ").");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return Failure("That file is not a KeyPulse backup.");
                }

                if (root.TryGetProperty("FileType", out var fileType)
                    && fileType.ValueKind == JsonValueKind.String
                    && string.Equals(fileType.GetString(), BackupEnvelope.BackupFileType, StringComparison.Ordinal))
                {
                    return InspectNative(text);
                }

                if (root.TryGetProperty("Hotkeys", out var hotkeys) && hotkeys.ValueKind == JsonValueKind.Array)
                {
                    return InspectLegacy(text, hotkeys.GetArrayLength());
                }

                return Failure("That file is not a KeyPulse backup (it carries no KeyPulse backup header and no shortcut list).");
            }
        }

        private static BackupInspection InspectNative(string text)
        {
            BackupEnvelope? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize(text, AppConfigJsonContext.Default.BackupEnvelope);
            }
            catch (Exception ex)
            {
                return Failure("That backup is damaged and could not be read (" + ex.Message + ").");
            }

            if (envelope == null) return Failure("That backup is empty.");

            if (envelope.FormatVersion <= 0 || envelope.FormatVersion > BackupEnvelope.CurrentFormatVersion)
            {
                return Failure($"That backup was written by a newer version of KeyPulse (format {envelope.FormatVersion}). Update KeyPulse and try again.");
            }

            if (string.IsNullOrWhiteSpace(envelope.Payload))
            {
                return Failure("That backup contains no data.");
            }

            var encrypted = string.Equals(envelope.Encryption, BackupEnvelope.EncryptionAesGcm, StringComparison.Ordinal);
            if (!encrypted && !string.Equals(envelope.Encryption, BackupEnvelope.EncryptionNone, StringComparison.Ordinal))
            {
                return Failure($"That backup uses an encryption method this version does not understand ({envelope.Encryption}).");
            }

            if (encrypted)
            {
                if (string.IsNullOrWhiteSpace(envelope.Salt)
                    || string.IsNullOrWhiteSpace(envelope.Nonce)
                    || string.IsNullOrWhiteSpace(envelope.AuthTag)
                    || envelope.KdfIterations < 1000)
                {
                    return Failure("That backup says it is password protected but is missing the values needed to unlock it.");
                }

                return new BackupInspection
                {
                    Ok = true,
                    Kind = BackupKind.Native,
                    IsEncrypted = true,
                    CreatedUtc = envelope.CreatedUtc,
                    CreatedByVersion = envelope.CreatedByVersion,
                    ShortcutCount = -1,
                    Envelope = envelope
                };
            }

            var opened = OpenUnencrypted(envelope);
            if (!opened.Ok) return Failure(opened.Error);

            return new BackupInspection
            {
                Ok = true,
                Kind = BackupKind.Native,
                IsEncrypted = false,
                CreatedUtc = envelope.CreatedUtc,
                CreatedByVersion = envelope.CreatedByVersion,
                ShortcutCount = opened.Payload!.Shortcuts.Count,
                Payload = opened.Payload,
                Envelope = envelope
            };
        }

        private static BackupInspection InspectLegacy(string text, int declaredCount)
        {
            AppConfig? config;
            try
            {
                config = JsonSerializer.Deserialize(text, AppConfigJsonContext.Default.AppConfig);
            }
            catch (Exception ex)
            {
                return Failure("That looks like an old KeyPulse settings file but could not be read (" + ex.Message + ").");
            }

            if (config?.Hotkeys == null)
            {
                return Failure("That looks like an old KeyPulse settings file but contains no shortcuts.");
            }

            var unreadable = config.Hotkeys.Count(h => h.TargetUnreadable);

            var payload = new BackupPayload
            {
                UseGoogleChromeForUrls = config.UseGoogleChromeForUrls,
                SoundEnabled = config.SoundEnabled,
                TypingDelayMs = config.TypingDelayMs,
                Theme = config.Theme ?? "System",
                ShortcutSortColumn = config.ShortcutSortColumn ?? string.Empty,
                ShortcutSortDescending = config.ShortcutSortDescending,
                HasSeenTrayHint = config.HasSeenTrayHint,
                Window = new BackupWindowLayout
                {
                    MainWindowX = config.MainWindowX,
                    MainWindowY = config.MainWindowY,
                    MainWindowWidth = config.MainWindowWidth,
                    MainWindowHeight = config.MainWindowHeight,
                    MainWindowState = config.MainWindowState ?? "Normal",
                    SettingsWindowX = config.SettingsWindowX,
                    SettingsWindowY = config.SettingsWindowY,
                    SettingsWindowWidth = config.SettingsWindowWidth,
                    SettingsWindowHeight = config.SettingsWindowHeight,
                    SetupWindowX = config.SetupWindowX,
                    SetupWindowY = config.SetupWindowY,
                    SetupWindowWidth = config.SetupWindowWidth,
                    SetupWindowHeight = config.SetupWindowHeight,
                    ShortcutStatusColumnWidth = config.ShortcutStatusColumnWidth,
                    ShortcutKeyColumnWidth = config.ShortcutKeyColumnWidth,
                    ShortcutActionColumnWidth = config.ShortcutActionColumnWidth,
                    ShortcutTargetColumnWidth = config.ShortcutTargetColumnWidth
                },
                Shortcuts = config.Hotkeys.Select(h => new BackupShortcut
                {
                    Id = h.Id,
                    IsEnabled = h.IsEnabled,
                    AllowRiskyShortcut = h.AllowRiskyShortcut,
                    KeyCombination = h.KeyCombination,
                    Action = h.Action,
                    Target = h.Target,
                    IsTargetObfuscated = h.IsTargetObfuscated
                }).ToList()
            };

            var note = unreadable > 0
                ? $" {unreadable} of them could not be decrypted and will need their target entering again."
                : string.Empty;

            return new BackupInspection
            {
                Ok = true,
                Kind = BackupKind.LegacyConfigCopy,
                IsEncrypted = false,
                CreatedUtc = string.Empty,
                CreatedByVersion = "before backups had a format",
                ShortcutCount = payload.Shortcuts.Count,
                Payload = payload,
                Error = note.Trim()
            };
        }

        /// <summary>Unlocks an encrypted backup. Distinguishes a wrong passphrase from a broken file.</summary>
        public static BackupOpenResult Open(BackupInspection inspection, string? passphrase)
        {
            if (inspection.Payload != null)
            {
                return new BackupOpenResult { Ok = true, Payload = inspection.Payload };
            }

            var envelope = inspection.Envelope;
            if (envelope == null)
            {
                return new BackupOpenResult { Ok = false, Error = "That backup could not be opened." };
            }

            if (!string.Equals(envelope.Encryption, BackupEnvelope.EncryptionAesGcm, StringComparison.Ordinal))
            {
                return OpenUnencrypted(envelope);
            }

            if (string.IsNullOrEmpty(passphrase))
            {
                return new BackupOpenResult { Ok = false, WrongPassphrase = true, Error = "This backup needs its password." };
            }

            byte[] salt, nonce, tag, cipher;
            try
            {
                salt = Convert.FromBase64String(envelope.Salt);
                nonce = Convert.FromBase64String(envelope.Nonce);
                tag = Convert.FromBase64String(envelope.AuthTag);
                cipher = Convert.FromBase64String(envelope.Payload);
            }
            catch (Exception ex)
            {
                return new BackupOpenResult { Ok = false, Error = "That backup is damaged (" + ex.Message + ")." };
            }

            if (nonce.Length != NonceBytes || tag.Length != TagBytes || salt.Length < 8)
            {
                return new BackupOpenResult { Ok = false, Error = "That backup is damaged (its encryption values are the wrong size)." };
            }

            var plain = new byte[cipher.Length];
            var key = DeriveKey(passphrase, salt, envelope.KdfIterations);
            try
            {
                using var aes = new AesGcm(key, TagBytes);
                aes.Decrypt(nonce, cipher, tag, plain, AssociatedData);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plain);
                return new BackupOpenResult
                {
                    Ok = false,
                    WrongPassphrase = true,
                    Error = "That password did not unlock the backup. If you are sure it is right, the file has been altered or damaged since it was created."
                };
            }
            catch (Exception ex)
            {
                CryptographicOperations.ZeroMemory(plain);
                return new BackupOpenResult { Ok = false, Error = ex.Message };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            var result = MaterializePayload(plain, envelope.PayloadSha256);
            CryptographicOperations.ZeroMemory(plain);
            return result;
        }

        private static BackupOpenResult OpenUnencrypted(BackupEnvelope envelope)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(envelope.Payload);
            }
            catch (Exception ex)
            {
                return new BackupOpenResult { Ok = false, Error = "That backup is damaged (" + ex.Message + ")." };
            }

            return MaterializePayload(bytes, envelope.PayloadSha256);
        }

        /// <summary>Checksum first, then parse. Corruption must never reach the shortcut list.</summary>
        private static BackupOpenResult MaterializePayload(byte[] payloadBytes, string expectedSha256)
        {
            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = ToHex(SHA256.HashData(payloadBytes));
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new BackupOpenResult
                    {
                        Ok = false,
                        Error = "That backup failed its integrity check - the file has been changed or damaged since it was written. Nothing was restored."
                    };
                }
            }

            try
            {
                var json = Encoding.UTF8.GetString(payloadBytes);
                var payload = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.BackupPayload);
                if (payload == null) return new BackupOpenResult { Ok = false, Error = "That backup contains no data." };

                payload.Shortcuts ??= new List<BackupShortcut>();
                payload.Window ??= new BackupWindowLayout();
                return new BackupOpenResult { Ok = true, Payload = payload };
            }
            catch (Exception ex)
            {
                return new BackupOpenResult { Ok = false, Error = "That backup is damaged (" + ex.Message + ")." };
            }
        }


        /// <summary>
        /// Separates STRUCTURAL corruption from MACHINE differences.
        ///
        /// Corruption (an action code that is not a real action, a null row) means the file is not
        /// trustworthy, so the whole restore is refused and nothing is touched. A shortcut whose keys
        /// are taken on this PC, or whose folder is missing, is not corruption - it is the normal
        /// state of a backup moving to a new machine, and rejecting the file for that made restore
        /// useless for its main purpose. Those are imported and flagged in the list instead.
        /// </summary>
        public static bool ValidatePayload(BackupPayload payload, out string error, out List<string> warnings)
        {
            error = string.Empty;
            warnings = new List<string>();

            if (payload.Shortcuts == null)
            {
                error = "That backup is damaged (it has no shortcut list).";
                return false;
            }

            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < payload.Shortcuts.Count; i++)
            {
                var item = payload.Shortcuts[i];
                if (item == null)
                {
                    error = $"That backup is damaged (shortcut #{i + 1} is empty).";
                    return false;
                }

                if ((int)item.Action < (int)ActionType.OpenFolder || (int)item.Action > (int)ActionType.InsertText)
                {
                    error = $"That backup is damaged (shortcut #{i + 1} has an action code KeyPulse does not recognise).";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(item.KeyCombination))
                {
                    warnings.Add($"Shortcut #{i + 1} has no key combination and will need one.");
                }
                else if (!HotkeyManager.TryParseCombo(item.KeyCombination, out _, out _))
                {
                    warnings.Add($"\"{item.KeyCombination}\" is not a combination this version understands and will need re-recording.");
                }

                if (string.IsNullOrEmpty(item.Target))
                {
                    warnings.Add($"\"{item.KeyCombination}\" has an empty target.");
                }

                if (!string.IsNullOrWhiteSpace(item.Id) && !seenIds.Add(item.Id))
                {
                    item.Id = string.Empty;
                }
            }

            return true;
        }


        private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations)
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, KeyBytes);
        }

        private static string ToHex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();

        private static BackupInspection Failure(string message) => new BackupInspection { Ok = false, Error = message };
    }
}
