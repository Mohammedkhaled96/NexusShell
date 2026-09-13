using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace NexusShell.App.Services
{
    /// <summary>
    /// Encrypts / decrypts short secrets (API keys, tokens) at rest using
    /// Windows DPAPI bound to the current user. Stored form is
    /// "enc:v1:" + Base64(ciphertext) so we can detect encrypted vs legacy
    /// plaintext values during settings load.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static class SecretProtector
    {
        private const string EncPrefix = "enc:v1:";

        // App-specific entropy — increases the cost of cross-app/key reuse.
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("NexusShell.App/v1/secret-entropy");

        public static bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(EncPrefix, StringComparison.Ordinal);

        public static string Protect(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return string.Empty;
            byte[] plain = Encoding.UTF8.GetBytes(plaintext);
            byte[] cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            return EncPrefix + Convert.ToBase64String(cipher);
        }

        public static bool TryUnprotect(string protectedValue, out string plaintext)
        {
            plaintext = string.Empty;
            if (!IsProtected(protectedValue)) return false;
            try
            {
                string b64 = protectedValue.Substring(EncPrefix.Length);
                byte[] cipher = Convert.FromBase64String(b64);
                byte[] plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
                plaintext = Encoding.UTF8.GetString(plain);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
