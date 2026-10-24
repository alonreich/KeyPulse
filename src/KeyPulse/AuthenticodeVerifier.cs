using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace KeyPulse
{
    public enum SignatureVerdict
    {
        NotSigned,
        TrustedPinnedSigner,
        Rejected
    }

    /// <summary>
    /// Verifies an update executable's Authenticode signature with WinVerifyTrust and pins the signer.
    ///
    /// The old check extracted the signer certificate with CreateFromSignedFile and accepted any
    /// subject containing "KeyPulse". That validates nothing: a self-signed certificate named
    /// CN=KeyPulse passed, and so did a file whose bytes no longer matched its signature.
    /// WinVerifyTrust proves the digest matches; the pinned thumbprint proves who signed it.
    /// A self-signed release certificate is not in the Trusted Root store, so CERT_E_UNTRUSTEDROOT
    /// is accepted ONLY when the signer is pinned - the digest was still verified in that case.
    /// </summary>
    public static class AuthenticodeVerifier
    {
        /// <summary>SHA-1 thumbprints of release signing certificates (ssl-certificate\KeyPulse_CodeSigning.cer).</summary>
        public static readonly string[] PinnedSignerThumbprints =
        {
            "0E602A471674F7308EB9F7DA23719DC69C8ECED6"
        };

        internal const uint TRUST_E_NOSIGNATURE = 0x800B0100;
        internal const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
        internal const uint CERT_E_UNTRUSTEDROOT = 0x800B0109;
        internal const uint CERT_E_CHAINING = 0x800B010A;

        private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;
        private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
        private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", SetLastError = false)]
        private static extern unsafe int WinVerifyTrust(IntPtr hwnd, Guid* pgActionID, WINTRUST_DATA* pWVTData);

        /// <summary>Pure decision table, separated from the native call so it can be tested.</summary>
        public static SignatureVerdict Evaluate(uint trustResult, string? signerThumbprint, out string reason)
        {
            if (trustResult == TRUST_E_NOSIGNATURE || trustResult == TRUST_E_SUBJECT_FORM_UNKNOWN)
            {
                reason = "The file is not digitally signed.";
                return SignatureVerdict.NotSigned;
            }

            var pinned = !string.IsNullOrWhiteSpace(signerThumbprint)
                && PinnedSignerThumbprints.Any(p => string.Equals(p, signerThumbprint, StringComparison.OrdinalIgnoreCase));

            if (trustResult == 0 || trustResult == CERT_E_UNTRUSTEDROOT || trustResult == CERT_E_CHAINING)
            {
                if (pinned)
                {
                    reason = string.Empty;
                    return SignatureVerdict.TrustedPinnedSigner;
                }

                reason = $"The file is signed by an unknown publisher (thumbprint {signerThumbprint ?? "unavailable"}).";
                return SignatureVerdict.Rejected;
            }

            reason = $"The file's digital signature is invalid or the file was modified after signing (0x{trustResult:X8}).";
            return SignatureVerdict.Rejected;
        }

        public static SignatureVerdict Verify(string filePath, out string reason)
        {
            uint trustResult;
            try
            {
                trustResult = unchecked((uint)RunWinVerifyTrust(filePath));
            }
            catch (Exception ex)
            {
                reason = "Signature verification could not run: " + ex.Message;
                return SignatureVerdict.Rejected;
            }

            string? thumbprint = null;
            if (trustResult != TRUST_E_NOSIGNATURE && trustResult != TRUST_E_SUBJECT_FORM_UNKNOWN)
            {
                try
                {
#pragma warning disable SYSLIB0057
                    using var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromSignedFile(filePath);
#pragma warning restore SYSLIB0057
                    thumbprint = cert.GetCertHashString();
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                }
            }

            return Evaluate(trustResult, thumbprint, out reason);
        }

        private static unsafe int RunWinVerifyTrust(string filePath)
        {
            var pathPtr = Marshal.StringToHGlobalUni(filePath);
            try
            {
                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                    pcwszFilePath = pathPtr
                };

                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)sizeof(WINTRUST_DATA),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = (IntPtr)(&fileInfo),
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    dwProvFlags = WTD_REVOCATION_CHECK_NONE | WTD_CACHE_ONLY_URL_RETRIEVAL
                };

                var action = WintrustActionGenericVerifyV2;
                var result = WinVerifyTrust((IntPtr)(-1), &action, &data);

                data.dwStateAction = WTD_STATEACTION_CLOSE;
                WinVerifyTrust((IntPtr)(-1), &action, &data);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(pathPtr);
            }
        }
    }
}
