using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Security.Cryptography;
using OfflineStore.Infrastructure;

namespace OfflineStore.Security
{
    public sealed class LegacyAdminPinRequiredException : UnauthorizedAccessException
    { public LegacyAdminPinRequiredException() : base("يجب تعيين PIN للحساب الإداري القديم قبل الدخول.") { } }

    public sealed class UserSession
    {
        public long Id { get; internal set; }
        public long UserId => Id;
        public string Username { get; internal set; }
        public string DisplayName { get; internal set; }
        public string Role { get; internal set; }
        // A password reset invalidates the previously authenticated session.
        internal byte[] CredentialProof { get; set; }
    }
    public sealed class UserInfo
    {
        public long Id { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string Role { get; set; }
        public bool Active { get; set; }
        public string StatusText { get { return Active ? "نشط" : "موقوف"; } }
    }
    // Local credentials. Protects the application workflow, not SQLite file from a Windows administrator.
    public sealed class UserAuthService
    {
        private const int Iterations = 210000;
        private readonly StoreDb _db;
        public UserAuthService(StoreDb db) { _db = db; }
        private static string UtcNow() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
        private static byte[] Derive(string password, byte[] salt, int cycles)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(password, salt, cycles, HashAlgorithmName.SHA256))
                return pbkdf2.GetBytes(32);
        }
        private static bool SecureEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
        private static void ValidatePassword(string password)
        {
            if (string.IsNullOrEmpty(password) || password.Length < 6 || password.Length > 12 ||
                password.IndexOfAny(new [] {'0','1','2','3','4','5','6','7','8','9'}) < 0 ||
                !System.Text.RegularExpressions.Regex.IsMatch(password, @"^[0-9]{6,12}$"))
                throw new ArgumentException("الرقم السري الجديد يجب أن يتكون من 6 إلى 12 رقمًا إنجليزيًا.");
        }
        private static byte[] Salt()
        {
            byte[] salt = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            return salt;
        }
        private static SQLiteCommand Command(SQLiteConnection c, SQLiteTransaction tx, string sql)
        { return new SQLiteCommand(sql, c, tx); }
        public bool HasUsers()
        {
            using (var c = _db.Open())
            using (var cmd = new SQLiteCommand("SELECT COUNT(*) FROM users", c))
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
        }
        // Compatibility API: administrative auto-login is permanently disabled.
        public bool CanAutoLoginAdmin() { return false; }

        public void ProtectLegacyAdmin(string username, string password)
        {
            ValidatePassword(password);
            // Authenticate the existing active, unlocked passwordless account only.
            // This temporary session is never exposed to a view or StoreService.
            var legacy = Authenticate(username, "", true);
            if (!IsAdminPasswordless(legacy))
                throw new UnauthorizedAccessException("هذا الحساب لا يحتاج تهيئة كلمة مرور.");
            ResetPassword(legacy, legacy.Id, password);
        }

        public void CreateFirstAdmin(string displayName, string password)
        {
            ValidatePassword(password);
            if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("اكتب اسم المدير.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                using (var count = Command(c, tx, "SELECT COUNT(*) FROM users"))
                    if (Convert.ToInt64(count.ExecuteScalar()) != 0) throw new InvalidOperationException("تم إنشاء مدير النظام مسبقًا.");
                InsertUser(c, tx, "admin", displayName.Trim(), "ADMIN", password);
                using (var log = Command(c, tx, "INSERT INTO audit_log(action,entity_id,created_utc) VALUES('FIRST_ADMIN_CREATED',last_insert_rowid(),@now)"))
                { log.Parameters.AddWithValue("@now", UtcNow()); log.ExecuteNonQuery(); }
                tx.Commit();
            }
        }
        private static void InsertUser(SQLiteConnection c, SQLiteTransaction tx, string username, string name, string role, string password)
        {
            var salt = Salt();
            var hash = Derive(password, salt, Iterations);
            using (var cmd = Command(c, tx, @"INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
                  VALUES(@username,@name,@role,@salt,@hash,@iterations,@created)"))
            {
                cmd.Parameters.AddWithValue("@username", username);
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@role", role);
                cmd.Parameters.AddWithValue("@salt", salt);
                cmd.Parameters.AddWithValue("@hash", hash);
                cmd.Parameters.AddWithValue("@iterations", Iterations);
                cmd.Parameters.AddWithValue("@created", UtcNow());
                cmd.ExecuteNonQuery();
            }
        }
        public UserSession Login(string username, string password)
        { return Authenticate(username, password, false); }

        private UserSession Authenticate(string username, string password, bool passwordSetup)
        {
            if (string.IsNullOrWhiteSpace(username)) throw new UnauthorizedAccessException("أدخل اسم المستخدم.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                long id; string name; string role; bool active; byte[] salt; byte[] hash; int cycles;
                int failures; string until;
                using (var cmd = Command(c, tx, @"SELECT id,display_name,role,active,salt,password_hash,iterations,failed_attempts,
                        COALESCE(locked_until_utc,'') FROM users WHERE username=@username"))
                {
                    cmd.Parameters.AddWithValue("@username", username.Trim());
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) throw new UnauthorizedAccessException("اسم المستخدم أو كلمة المرور غير صحيحة.");
                        id=r.GetInt64(0); name=r.GetString(1); role=r.GetString(2); active=r.GetInt64(3)==1;
                        salt=(byte[])r[4]; hash=(byte[])r[5]; cycles=r.GetInt32(6); failures=r.GetInt32(7); until=r.GetString(8);
                    }
                }
                if (!active) throw new UnauthorizedAccessException("الحساب موقوف. راجع المدير.");
                DateTime locked;
                if (DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out locked)
                    && locked > DateTime.UtcNow)
                    throw new UnauthorizedAccessException("الحساب مغلق مؤقتًا بعد محاولات دخول خاطئة.");
                if (!SecureEqual(Derive(password ?? "", salt, cycles), hash))
                {
                    failures++;
                    var lockUntil = failures >= 5 ? (object)DateTime.UtcNow.AddMinutes(15).ToString("o", CultureInfo.InvariantCulture) : DBNull.Value;
                    using (var cmd = Command(c, tx, "UPDATE users SET failed_attempts=@f,locked_until_utc=@lock WHERE id=@id"))
                    { cmd.Parameters.AddWithValue("@f", failures >= 5 ? 0 : failures); cmd.Parameters.AddWithValue("@lock", lockUntil); cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery(); }
                    tx.Commit();
                    throw new UnauthorizedAccessException("اسم المستخدم أو كلمة المرور غير صحيحة.");
                }
                bool emptyAdmin = role == "ADMIN" && SecureEqual(Derive("", salt, cycles), hash);
                if (emptyAdmin && !passwordSetup) throw new LegacyAdminPinRequiredException();
                if (passwordSetup && !emptyAdmin)
                    throw new UnauthorizedAccessException("تهيئة PIN متاحة للحساب الإداري القديم الفارغ فقط.");
                using (var cmd = Command(c, tx, "UPDATE users SET failed_attempts=0,locked_until_utc=NULL WHERE id=@id"))
                { cmd.Parameters.AddWithValue("@id", id); cmd.ExecuteNonQuery(); }
                using (var cmd = Command(c, tx, "INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('USER_LOGIN',@id,@id,@now)"))
                { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@now", UtcNow()); cmd.ExecuteNonQuery(); }
                tx.Commit();
                return new UserSession { Id=id, Username=username.Trim(), DisplayName=name, Role=role, CredentialProof=(byte[])hash.Clone() };
            }
        }
        public static bool RoleCan(string role, string permission)
        {
            if (role == "ADMIN") return true;
            if (permission == "APPEARANCE" || permission == "VIEW") return true;
            if (role == "MANAGER") return permission == "CATALOG" || permission == "PURCHASE" || permission == "SALE" || permission == "REPORTS" || permission == "BACKUP" || permission == "LOW_STOCK" || permission == "OVERRIDE_PRICE";
            if (role == "CASHIER") return permission == "SALE";
            if (role == "STOCK") return permission == "PURCHASE" || permission == "LOW_STOCK";
            return false;
        }
        public static readonly string[] ConfigurablePermissions = {
            "SALE", "PURCHASE", "CATALOG", "REPORTS", "BACKUP",
            "LOW_STOCK", "OVERRIDE_PRICE", "APPEARANCE"
        };

        private static bool ValidConfigurablePermission(string permission)
        {
            return Array.IndexOf(ConfigurablePermissions, permission) >= 0;
        }

        // Authorization always checks the current database state; disabling an
        // account or revoking permission takes effect on its next operation.
        // USERS is never delegated to a non-ADMIN account.
        public bool Authorize(UserSession session, string permission)
        {
            if (session == null || string.IsNullOrEmpty(permission)) return false;
            using (var c = _db.Open())
            {
                string role;
                byte[] currentHash;
                using (var cmd = new SQLiteCommand("SELECT role,password_hash FROM users WHERE id=@id AND active=1", c))
                {
                    cmd.Parameters.AddWithValue("@id", session.Id);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return false;
                        role = reader.GetString(0);
                        currentHash = (byte[])reader[1];
                    }
                }
                // Revoke invalidated sessions on password reset.
                if (!SecureEqual(session.CredentialProof, currentHash)) return false;
                if (role == "ADMIN") return true;
                if (permission == "USERS") return false;
                if (permission == "VIEW") return true;
                if (!ValidConfigurablePermission(permission)) return false;
                using (var cmd = new SQLiteCommand(
                    "SELECT allowed FROM user_permission_overrides WHERE user_id=@id AND permission=@permission", c))
                {
                    cmd.Parameters.AddWithValue("@id", session.Id);
                    cmd.Parameters.AddWithValue("@permission", permission);
                    var value = cmd.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? RoleCan(role, permission)
                        : Convert.ToInt64(value, CultureInfo.InvariantCulture) == 1;
                }
            }
        }

        private static HashSet<string> ValidatePermissionSet(IEnumerable<string> permissions)
        {
            if (permissions == null) return null;
            var allowed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in permissions)
            {
                if (!ValidConfigurablePermission(permission))
                    throw new ArgumentException("الصلاحية غير معروفة ولا يمكن منحها: " + permission);
                allowed.Add(permission);
            }
            return allowed;
        }

        private static void ReplacePermissions(SQLiteConnection c, SQLiteTransaction tx,
            long userId, long actorId, HashSet<string> allowed)
        {
            using (var clear = Command(c, tx,
                "DELETE FROM user_permission_overrides WHERE user_id=@id"))
            {
                clear.Parameters.AddWithValue("@id", userId);
                clear.ExecuteNonQuery();
            }
            var stamp = UtcNow();
            foreach (var permission in ConfigurablePermissions)
                using (var insert = Command(c, tx, @"INSERT INTO user_permission_overrides
                    (user_id,permission,allowed,changed_by_user_id,changed_utc)
                    VALUES(@id,@permission,@allowed,@actor,@time)"))
                {
                    insert.Parameters.AddWithValue("@id", userId);
                    insert.Parameters.AddWithValue("@permission", permission);
                    insert.Parameters.AddWithValue("@allowed", allowed.Contains(permission) ? 1 : 0);
                    insert.Parameters.AddWithValue("@actor", actorId);
                    insert.Parameters.AddWithValue("@time", stamp);
                    insert.ExecuteNonQuery();
                }
        }

        public HashSet<string> EffectivePermissions(UserSession actor, long userId)
        {
            RequireAdmin(actor);
            using (var c = _db.Open())
            {
                string role;
                using (var cmd = new SQLiteCommand("SELECT role FROM users WHERE id=@id", c))
                {
                    cmd.Parameters.AddWithValue("@id", userId);
                    role = cmd.ExecuteScalar() as string;
                }
                if (role == null) throw new ArgumentException("الموظف غير موجود.");
                var allowed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var permission in ConfigurablePermissions)
                    if (RoleCan(role, permission)) allowed.Add(permission);
                using (var cmd = new SQLiteCommand(
                    "SELECT permission,allowed FROM user_permission_overrides WHERE user_id=@id", c))
                {
                    cmd.Parameters.AddWithValue("@id", userId);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            var permission = reader.GetString(0);
                            if (reader.GetInt64(1) == 1) allowed.Add(permission);
                            else allowed.Remove(permission);
                        }
                }
                return allowed;
            }
        }

        public void SetUserPermissions(UserSession actor, long id, IEnumerable<string> permissions)
        {
            RequireAdmin(actor);
            var allowed = ValidatePermissionSet(permissions);
            if (allowed == null) throw new ArgumentNullException("permissions");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                string role;
                using (var cmd = Command(c, tx, "SELECT role FROM users WHERE id=@id"))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    role = cmd.ExecuteScalar() as string;
                }
                if (role == null) throw new ArgumentException("الموظف غير موجود.");
                if (role == "ADMIN")
                    throw new InvalidOperationException("صلاحيات مدير النظام لا تُعدّل بهذه الصفحة.");
                ReplacePermissions(c, tx, id, actor.Id, allowed);
                using (var log = Command(c, tx, @"INSERT INTO audit_log
                    (action,entity_id,actor_user_id,details,created_utc)
                    VALUES('USER_PERMISSIONS_CHANGED',@id,@actor,@details,@now)"))
                {
                    log.Parameters.AddWithValue("@id", id);
                    log.Parameters.AddWithValue("@actor", actor.Id);
                    log.Parameters.AddWithValue("@details", string.Join(",", allowed));
                    log.Parameters.AddWithValue("@now", UtcNow());
                    log.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        public List<UserInfo> Users(UserSession acting)
        {
            RequireAdmin(acting);
            var result = new List<UserInfo>();
            using (var c = _db.Open())
            using (var cmd = new SQLiteCommand("SELECT id,username,display_name,role,active FROM users ORDER BY id", c))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new UserInfo { Id=r.GetInt64(0),Username=r.GetString(1),DisplayName=r.GetString(2),Role=r.GetString(3),Active=r.GetInt64(4)==1 });
            return result;
        }
        private void RequireAdmin(UserSession user)
        { if (!Authorize(user, "USERS")) throw new UnauthorizedAccessException("إدارة الموظفين للمدير فقط."); }
        public bool IsAdminPasswordless(UserSession actor)
        {
            RequireAdmin(actor);
            using (var c = _db.Open())
            using (var cmd = new SQLiteCommand(
                "SELECT salt,password_hash,iterations FROM users WHERE id=@id AND role='ADMIN' AND active=1",c))
            {
                cmd.Parameters.AddWithValue("@id",actor.Id);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return false;
                    return SecureEqual(Derive("",(byte[])r[0],r.GetInt32(2)),(byte[])r[1]);
                }
            }
        }

        public void CreateUser(UserSession actor, string username, string name, string role, string password)
        {
            CreateUserWithPermissions(actor, username, name, role, password, null);
        }
        public void CreateUserWithPermissions(UserSession actor, string username, string name,
            string role, string password, IEnumerable<string> permissions)
        {
            RequireAdmin(actor);
            if (IsAdminPasswordless(actor))
                throw new InvalidOperationException(
                    "لحماية الموظفين، عيّن رقمًا سريًا لحساب المدير أولًا: حدد المدير من الجدول، واكتب PIN من 6 إلى 12 رقمًا ثم اضغط تغيير كلمة مرور المحدد.");
            ValidatePassword(password);
            var allowed = ValidatePermissionSet(permissions);
            if (role == "ADMIN" && allowed != null)
                throw new ArgumentException("صلاحيات المدير ثابتة ولا يمكن تخصيصها.");
            if (string.IsNullOrWhiteSpace(username) || username.Trim().Length<3 || username.Trim().Length>40 || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("اكتب اسم مستخدم (3-40 حرفًا) واسم موظف.");
            if (role!="ADMIN" && role!="MANAGER" && role!="CASHIER" && role!="STOCK")
                throw new ArgumentException("صلاحية غير معروفة.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                InsertUser(c,tx,username.Trim(),name.Trim(),role,password);
                long createdId;
                using (var idCommand = Command(c, tx, "SELECT last_insert_rowid()"))
                    createdId = Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (allowed != null) ReplacePermissions(c, tx, createdId, actor.Id, allowed);
                using (var cmd=Command(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('USER_CREATED',@id,@actor,@now)"))
                { cmd.Parameters.AddWithValue("@id",createdId);cmd.Parameters.AddWithValue("@actor",actor.Id);cmd.Parameters.AddWithValue("@now",UtcNow());cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
        }
        public void SetActive(UserSession actor, long id, bool active)
        {
            RequireAdmin(actor);
            if (actor.Id==id && !active) throw new InvalidOperationException("لا توقف حسابك المفتوح.");
            using (var c=_db.Open())
            using (var tx=c.BeginTransaction())
            {
                using (var cmd=Command(c,tx,"UPDATE users SET active=@active WHERE id=@id"))
                { cmd.Parameters.AddWithValue("@active",active?1:0);cmd.Parameters.AddWithValue("@id",id);if(cmd.ExecuteNonQuery()!=1) throw new ArgumentException("الموظف غير موجود."); }
                using (var log=Command(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('USER_STATUS_CHANGED',@id,@actor,@now)"))
                { log.Parameters.AddWithValue("@id",id);log.Parameters.AddWithValue("@actor",actor.Id);log.Parameters.AddWithValue("@now",UtcNow());log.ExecuteNonQuery(); }
                tx.Commit();
            }
        }
        public void ResetPassword(UserSession actor, long id, string password)
        {
            RequireAdmin(actor);
            // New and reset credentials use a 6-12-digit PIN; already-created
            // longer legacy passwords remain valid for Login() without forced reset.
            // Passwordless first-admin accounts are supported only for migration.
            ValidatePassword(password);
            var salt=Salt(); var hash=Derive(password ?? "",salt,Iterations);
            using(var c=_db.Open())
            using(var tx=c.BeginTransaction())
            {
                using(var cmd=Command(c,tx,"UPDATE users SET salt=@salt,password_hash=@hash,iterations=@cycles,failed_attempts=0,locked_until_utc=NULL WHERE id=@id"))
                { cmd.Parameters.AddWithValue("@salt",salt);cmd.Parameters.AddWithValue("@hash",hash);cmd.Parameters.AddWithValue("@cycles",Iterations);cmd.Parameters.AddWithValue("@id",id);if(cmd.ExecuteNonQuery()!=1) throw new ArgumentException("الموظف غير موجود."); }
                using(var log=Command(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('USER_PASSWORD_RESET',@id,@actor,@now)"))
                { log.Parameters.AddWithValue("@id",id);log.Parameters.AddWithValue("@actor",actor.Id);log.Parameters.AddWithValue("@now",UtcNow());log.ExecuteNonQuery(); }
                tx.Commit();
            }
        }
    }
}
