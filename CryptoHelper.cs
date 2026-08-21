using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KeyPulse
{
    /// <summary>
    /// ISSUE_3: shortcut targets are protected with a key derived from the current Windows account
    /// (DPAPI), not with a key hard-coded into the executable. A copy of config.json taken from this
    /// machine is useless on another account or another PC.
    ///
    /// Two rules that must never be relaxed:
    ///   1. NEVER return the stored cipher text when decryption fails. The old code did exactly that,
    ///      so a shortcut's target silently became the literal string "ENC:8Kd2..." and the shortcut
    ///      quietly stopped working with no explanation. Failure is reported, never disguised.
    ///   2. The legacy "ENC:" fixed-key format is still READ so existing users keep their shortcuts,
    ///      but it is never WRITTEN again. Anything loaded that way is re-saved as "DPAPI:".
    /// </summary>
    public static class CryptoHelper
    {
        private const string DpapiPrefix = "DPAPI:";
        private const string LegacyPrefix = "ENC:";

        // Legacy fixed key/IV. Present ONLY so shortcuts saved by older builds can still be read.
        private static readonly byte[] LegacyKey = Encoding.UTF8.GetBytes("KeyPulseSecret!Key1234567890ABCD");
        private static readonly byte[] LegacyIV = Encoding.UTF8.GetBytes("KeyPulseIV67890A");

        // Ties the protected blob to KeyPulse, so another app's DPAPI blob cannot be swapped in.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("KeyPulse.Target.v1");

        /// <summary>True when this machine could not protect data at all (broken CAPI, roaming profile).</summary>
        public static bool ProtectionUnavailable { get; private set; }

        /// <summary>Turns a plain target into the string written to config.json.</summary>
        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText ?? string.Empty;

            try
            {
                var protectedBytes = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
                return DpapiPrefix + Convert.ToBase64String(protectedBytes);
            }
            catch (Exception ex)
            {
                // Never lose the user's shortcut because encryption is unavailable. Store it readable
                // and let the caller surface that fact rather than silently dropping the target.
                ProtectionUnavailable = true;
                Program.LogCrash("Target protection unavailable, storing in readable form: " + ex.Message);
                return plainText;
            }
        }

        /// <summary>
        /// Reads a stored target. Returns false ONLY when the value was encrypted and could not be
        /// decrypted - the caller must then tell the user, not substitute garbage.
        /// </summary>
        public static bool TryUnprotect(string? stored, out string plainText)
        {
            plainText = string.Empty;
            if (string.IsNullOrEmpty(stored)) return true;

            if (stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            {
                try
                {
                    var buffer = Convert.FromBase64String(stored.Substring(DpapiPrefix.Length));
                    var plainBytes = ProtectedData.Unprotect(buffer, Entropy, DataProtectionScope.CurrentUser);
                    plainText = Encoding.UTF8.GetString(plainBytes);
                    return true;
                }
                catch (Exception ex)
                {
                    Program.LogDebug("Target could not be decrypted for this Windows account: " + ex.Message);
                    return false;
                }
            }

            if (stored.StartsWith(LegacyPrefix, StringComparison.Ordinal))
            {
                return TryDecryptLegacy(stored, out plainText);
            }

            // Written before any protection existed, or stored readable because protection failed.
            plainText = stored;
            return true;
        }

        private static bool TryDecryptLegacy(string cipherText, out string plainText)
        {
            plainText = string.Empty;
            try
            {
                var buffer = Convert.FromBase64String(cipherText.Substring(LegacyPrefix.Length));

                using var aes = Aes.Create();
                aes.Key = LegacyKey;
                aes.IV = LegacyIV;

                using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
                using var ms = new MemoryStream(buffer);
                using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
                using var sr = new StreamReader(cs, Encoding.UTF8);
                plainText = sr.ReadToEnd();
                return true;
            }
            catch (Exception ex)
            {
                Program.LogDebug("Legacy target could not be decrypted: " + ex.Message);
                return false;
            }
        }
    }
}
