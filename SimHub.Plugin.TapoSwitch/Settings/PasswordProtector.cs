using System;
using System.Security.Cryptography;
using System.Text;

namespace SimHub.Plugin.TapoSwitch.Settings
{
    /// <summary>
    /// Encrypts/decrypts device passwords at rest using Windows DPAPI
    /// (CurrentUser scope), so the settings file on disk holds a protected
    /// blob rather than the plaintext password. DPAPI ties the encryption to
    /// the Windows user account SimHub runs as - there's no key for this
    /// plugin to manage or lose, but it also means the encrypted value can
    /// only be decrypted again on the same Windows account/machine.
    /// </summary>
    internal static class PasswordProtector
    {
        // Fixed additional entropy mixed into the DPAPI call. This isn't a
        // secret - it ships in the DLL - so it doesn't add real cryptographic
        // strength on its own. It just scopes the protected blob to this
        // plugin specifically, rather than any DPAPI call from any process
        // under the same Windows account being interchangeable.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SimHub.Plugin.TapoSwitch/Password/v1");

        public static string Protect(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
            {
                return string.Empty;
            }

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipherBytes);
            }
            catch (Exception ex)
            {
                // DPAPI can fail in unusual hosting scenarios (e.g. a service
                // account with no loaded user profile). Falling back to
                // storing the plaintext keeps the plugin working rather than
                // silently losing the saved credential; Unprotect() below
                // will happily hand back a value that fails DPAPI decoding.
                SimHub.Logging.Current.Warn(
                    "SimHub.Plugin.TapoSwitch: DPAPI encryption failed; the password will be stored " +
                    "unencrypted in the SimHub settings file.", ex);
                return plainText;
            }
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored))
            {
                return string.Empty;
            }

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(stored);
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                // Not a valid DPAPI blob for this account/entropy - most
                // likely the plaintext fallback written by Protect() above,
                // or a value carried over some other way. Treat it as the
                // password itself rather than raising, so a decrypt failure
                // degrades to "unencrypted" instead of "credential lost".
                return stored;
            }
        }
    }
}
