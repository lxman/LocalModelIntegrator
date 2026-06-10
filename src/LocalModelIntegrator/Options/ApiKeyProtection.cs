using System;
using System.Security.Cryptography;
using System.Text;

namespace LocalModelIntegrator.Options
{
    /// <summary>
    /// Protects the API key at rest with Windows DPAPI (current-user scope): the settings store
    /// holds base64 ciphertext that only this Windows account on this machine can decrypt.
    /// Both directions are fail-safe - a value that cannot be (de)protected becomes empty, so
    /// options persistence never throws; the user just re-enters the key.
    /// </summary>
    internal static class ApiKeyProtection
    {
        public static string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
                return string.Empty;
            try
            {
                byte[] cipher = ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipher);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static string Unprotect(string base64)
        {
            if (string.IsNullOrEmpty(base64))
                return string.Empty;
            try
            {
                byte[] plain = ProtectedData.Unprotect(
                    Convert.FromBase64String(base64), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                // Another user/machine's ciphertext, a corrupted value, or stray non-base64 input.
                return string.Empty;
            }
        }
    }
}
