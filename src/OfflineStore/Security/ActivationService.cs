using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OfflineStore.Security
{
    internal static class ActivationService
    {
        private const string Serial = "KHAL-ED-GAL-AL";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OfflineStore.Activation.v1");
        private static string LicensePath
        {
            get
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OfflineStore");
                return Path.Combine(directory, "activation.dat");
            }
        }

        internal static bool IsActivated()
        {
            try
            {
                if (!File.Exists(LicensePath)) return false;
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(LicensePath), Entropy, DataProtectionScope.CurrentUser);
                return string.Equals(Encoding.UTF8.GetString(bytes), Serial, StringComparison.Ordinal);
            }
            catch (CryptographicException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static bool Activate(string entered)
        {
            var normalized = (entered ?? "").Trim().ToUpperInvariant();
            if (!string.Equals(normalized, Serial, StringComparison.Ordinal)) return false;
            var path = LicensePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(Serial), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, bytes);
            return true;
        }
    }
}
