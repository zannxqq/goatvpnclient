using System;
using System.Security.Cryptography;
using System.Text;

namespace RemnaVpn.Services
{
    public class CryptographyService : ICryptographyService
    {
        private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("RemnaVpnEntropyBytesForExtraSecurity");
        private static byte[]? _dynamicEntropy;

        private byte[] GetEntropy()
        {
            if (_dynamicEntropy == null)
            {
                using var sha256 = SHA256.Create();
                _dynamicEntropy = sha256.ComputeHash(Encoding.UTF8.GetBytes(GenerateHwid() + "RemnaVpnSalt2026"));
            }
            return _dynamicEntropy;
        }

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                try
                {
                    byte[] encryptedBytes = ProtectedData.Protect(plainBytes, GetEntropy(), DataProtectionScope.CurrentUser);
                    return Convert.ToBase64String(encryptedBytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plainBytes);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DPAPI Encryption Error: {ex}");
                return string.Empty;
            }
        }

        public string Decrypt(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return string.Empty;

            try
            {
                byte[] encryptedBytes = Convert.FromBase64String(encryptedText);
                byte[]? plainBytes = null;
                try
                {
                    try
                    {
                        plainBytes = ProtectedData.Unprotect(encryptedBytes, GetEntropy(), DataProtectionScope.CurrentUser);
                    }
                    catch
                    {
                        // Fallback to legacy entropy for backward compatibility with previously encrypted settings
                        plainBytes = ProtectedData.Unprotect(encryptedBytes, LegacyEntropy, DataProtectionScope.CurrentUser);
                    }
                    return Encoding.UTF8.GetString(plainBytes);
                }
                finally
                {
                    if (plainBytes != null)
                    {
                        CryptographicOperations.ZeroMemory(plainBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DPAPI Decryption Error: {ex}");
                return string.Empty;
            }
        }

        public string GenerateHwid()
        {
            try
            {
                string rawId = "";
                try
                {
                    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                    var guid = key?.GetValue("MachineGuid")?.ToString();
                    if (!string.IsNullOrEmpty(guid)) rawId += guid;
                }
                catch { }

                if (string.IsNullOrEmpty(rawId))
                {
                    rawId = $"{Environment.MachineName}-{Environment.ProcessorCount}-{Environment.OSVersion.VersionString}";
                }

                using var sha256 = SHA256.Create();
                byte[] firstHash = sha256.ComputeHash(Encoding.UTF8.GetBytes(rawId));
                string firstHex = Convert.ToHexString(firstHash).ToLowerInvariant();
                byte[] secondHash = sha256.ComputeHash(Encoding.UTF8.GetBytes(firstHex));
                return Convert.ToHexString(secondHash).ToLowerInvariant();
            }
            catch
            {
                return "fallback-hwid-" + Environment.MachineName.ToLowerInvariant();
            }
        }
    }
}
