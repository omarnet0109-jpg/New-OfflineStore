using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace OfflineStore.Security
{
    // Opt-in, per-Windows-user saved login. DPAPI protection is not equivalent
    // to a Windows lock screen; every person sharing the same unlocked Windows
    // user account can use this remembered employee session.
    public static class RememberedLoginStore
    {
        public sealed class Credential
        {
            public string Username { get; private set; }
            public string Password { get; private set; }
            internal Credential(string name, string password)
            { Username = name; Password = password; }
        }

        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("OfflineStore.EmployeeLogin.WindowsDPAPI.v1");
        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "OfflineStore", "remembered-login.dat"); }
        }

        public static void Forget()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }

        public static void Save(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
                throw new ArgumentException("بيانات الدخول للحفظ غير صالحة.");
            byte[] clear;
            using (var mem = new MemoryStream())
            {
                using (var writer = new BinaryWriter(mem, Encoding.UTF8, true))
                {
                    writer.Write("OfflineStoreCredentialV1");
                    writer.Write(username.Trim());
                    writer.Write(password);
                }
                clear = mem.ToArray();
            }
            byte[] encrypted;
            try
            {
                encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser);
            }
            finally { Array.Clear(clear, 0, clear.Length); }
            var directory = Path.GetDirectoryName(FilePath);
            Directory.CreateDirectory(directory);
            var temp = FilePath + ".new";
            try
            {
                File.WriteAllBytes(temp, encrypted);
                if (File.Exists(FilePath))
                    File.Replace(temp, FilePath, null);
                else
                    File.Move(temp, FilePath);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        public static Credential TryLoad()
        {
            if (!File.Exists(FilePath)) return null;
            try
            {
                var bytes = File.ReadAllBytes(FilePath);
                if (bytes.Length > 4096) throw new InvalidDataException("Bad saved credential.");
                var clear = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    using (var input = new BinaryReader(new MemoryStream(clear), Encoding.UTF8))
                    {
                        if (input.ReadString() != "OfflineStoreCredentialV1")
                            throw new InvalidDataException("Bad saved credential version.");
                        var username = input.ReadString();
                        var password = input.ReadString();
                        if (username.Length < 3 || username.Length > 40 ||
                            password.Length < 1 || password.Length > 128 ||
                            input.BaseStream.Position != input.BaseStream.Length)
                            throw new InvalidDataException("Bad saved credential fields.");
                        return new Credential(username, password);
                    }
                }
                finally { Array.Clear(clear, 0, clear.Length); }
            }
            catch (Exception)
            {
                // Corrupted/Windows-reprofiled token must not block shop startup.
                try { Forget(); } catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return null;
            }
        }
    }
}
