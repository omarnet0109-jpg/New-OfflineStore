using System;
using System.Data.SQLite;
using System.Globalization;
using System.Security.Cryptography;
using OfflineStore.Infrastructure;

namespace OfflineStore.Security
{
    // Local entry protection, NOT disk encryption or multi-user authorization.
    public sealed class LocalPasswordService
    {
        private const int Iterations = 210000;
        private readonly StoreDb _db;
        public LocalPasswordService(StoreDb db) { _db = db; }

        public bool IsEnabled()
        {
            using (var c = _db.Open())
            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM local_auth WHERE id=1", c))
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256))
                return kdf.GetBytes(32);
        }

        private static bool FixedTimeEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            var diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        private static bool VerifyRecord(SQLiteConnection c, SQLiteTransaction tx, string password)
        {
            using (var cmd = new SQLiteCommand("SELECT salt,password_hash,iterations FROM local_auth WHERE id=1", c, tx))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) return true;
                var salt = (byte[])reader[0];
                var hash = (byte[])reader[1];
                var iterations = reader.GetInt32(2);
                return FixedTimeEqual(Derive(password ?? string.Empty, salt, iterations), hash);
            }
        }

        public bool Verify(string password)
        {
            using (var c = _db.Open()) return VerifyRecord(c, null, password);
        }

        public void Change(string currentPassword, string newPassword, bool disable)
        {
            if (!disable)
            {
                if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 10 || newPassword.Length > 128)
                    throw new ArgumentException("كلمة المرور الجديدة يجب أن تكون من 10 إلى 128 حرفًا.");
            }
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                var exists = false;
                using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM local_auth WHERE id=1", c, tx))
                    exists = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
                if (exists && !VerifyRecord(c, tx, currentPassword))
                    throw new UnauthorizedAccessException("كلمة المرور الحالية غير صحيحة.");
                if (disable)
                {
                    if (!exists) throw new InvalidOperationException("الحماية بكلمة مرور غير مفعلة.");
                    using (var cmd = new SQLiteCommand("DELETE FROM local_auth WHERE id=1", c, tx))
                        cmd.ExecuteNonQuery();
                }
                else
                {
                    var salt = new byte[32];
                    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
                    var hash = Derive(newPassword, salt, Iterations);
                    using (var cmd = new SQLiteCommand(@"INSERT OR REPLACE INTO local_auth
                        (id,salt,password_hash,iterations,updated_utc)
                        VALUES(1,@salt,@hash,@iterations,@date)", c, tx))
                    {
                        cmd.Parameters.AddWithValue("@salt", salt);
                        cmd.Parameters.AddWithValue("@hash", hash);
                        cmd.Parameters.AddWithValue("@iterations", Iterations);
                        cmd.Parameters.AddWithValue("@date", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                        cmd.ExecuteNonQuery();
                    }
                }
                using (var audit = new SQLiteCommand(
                    "INSERT INTO audit_log(action,entity_id,created_utc) VALUES(@action,0,@date)", c, tx))
                {
                    audit.Parameters.AddWithValue("@action", disable ? "LOCAL_PASSWORD_DISABLED" : exists ? "LOCAL_PASSWORD_CHANGED" : "LOCAL_PASSWORD_ENABLED");
                    audit.Parameters.AddWithValue("@date", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    audit.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }
}
