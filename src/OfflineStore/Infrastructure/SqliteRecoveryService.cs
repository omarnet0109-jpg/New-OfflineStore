using System;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace OfflineStore.Infrastructure
{
    /// <summary>
    /// An offline, crash-safe restore coordinator for the application's SQLite database.
    /// The database is never overwritten while the UI is running: a validated, private
    /// copy is staged and switched before opening the database on the next startup.
    /// </summary>
    public sealed class SqliteRecoveryService
    {
        private readonly StoreDb _db;
        private const string PendingName = "pending-restore.db";
        private const string MarkerName = "pending-restore.request";

        public SqliteRecoveryService(StoreDb db)
        {
            if (db == null) throw new ArgumentNullException("db");
            _db = db;
        }

        private string Folder
        {
            get { return Path.GetDirectoryName(_db.DatabasePath); }
        }

        private string Pending
        {
            get { return Path.Combine(Folder, PendingName); }
        }

        private string Marker
        {
            get { return Path.Combine(Folder, MarkerName); }
        }

        private static string FreshBackupPath(string directory)
        {
            return Path.Combine(directory, "store-before-restore-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N") + ".db");
        }

        // Reject corrupt databases, foreign-key faults, and databases from another
        // schema version before offering to replace the current working database.
        private static void Validate(string path)
        {
            var options = new SQLiteConnectionStringBuilder
            {
                DataSource = path,
                Version = 3,
                ReadOnly = true,
                Pooling = false
            };
            using (var connection = new SQLiteConnection(options.ToString()))
            {
                connection.Open();
                using (var cmd = new SQLiteCommand(
                    "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", connection))
                    cmd.ExecuteNonQuery();
                using (var version = new SQLiteCommand("PRAGMA user_version;", connection))
                {
                    long found = Convert.ToInt64(version.ExecuteScalar(), CultureInfo.InvariantCulture);
                    // v9 backups remain restorable and will migrate safely on restart;
                    // v10/v11 files pass full structural and accounting checks.
                    if (found != 9 && found != 10 && found != 11 && found != 12 && found != 13 && found != 14 && found != 15 && found != 16 && found != 17 && found != 18 && found != 19 && found != 20 && found != 21 && found != 22 && found != 23 && found != 24)
                        throw new InvalidOperationException(
                            "النسخة الاحتياطية ليست من إصدار قاعدة بيانات مدعوم (9 إلى 24).");
                }
                // quick_check alone is not a full SQLite page/B-tree check.
                // A backup intended for disaster recovery must pass integrity_check
                // as well as the application's accounting invariants.
                using (var check = new SQLiteCommand("PRAGMA integrity_check;", connection))
                using (var result = check.ExecuteReader())
                {
                    if (!result.Read() ||
                        !string.Equals(result.GetString(0), "ok", StringComparison.OrdinalIgnoreCase) ||
                        result.Read())
                        throw new InvalidOperationException(
                            "فشل فحص سلامة SQLite الكامل للنسخة الاحتياطية.");
                }
                StoreDb.VerifyIntegrity(connection);
            }
        }

        public void VerifyBackupFile(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathRooted(sourcePath))
                throw new ArgumentException("مسار النسخة الاحتياطية غير صالح.");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("ملف النسخة الاحتياطية غير موجود.", sourcePath);
            Validate(Path.GetFullPath(sourcePath));
        }

        private static string Digest(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        private static void DurableText(string path, string value)
        {
            var temp = path + ".new";
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                output.Write(bytes, 0, bytes.Length);
                output.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private static void DurableCopy(string source, string destination)
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(true); }
        }

        public void StageRestore(string sourcePath, bool replaceQueuedRequest = false)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathRooted(sourcePath))
                throw new ArgumentException("اختر ملف نسخة احتياطية بمسار كامل.");
            var source = Path.GetFullPath(sourcePath);
            if (string.Equals(source, Path.GetFullPath(_db.DatabasePath), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(source, Path.GetFullPath(Pending), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("لا يمكن استرجاع الملف الجاري استخدامه.");
            if (File.Exists(Marker) && !replaceQueuedRequest)
                throw new InvalidOperationException("هناك استرجاع معلق. أعد تشغيل البرنامج لاستكماله أولًا.");
            if (File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length > 0)
                throw new IOException("اختر نسخة احتياطية مستقلة، وليس قاعدة مفتوحة ذات WAL.");
            VerifyBackupFile(source);
            string id=Guid.NewGuid().ToString("N");
            if (File.Exists(Marker))
            {
                var previous=File.ReadAllText(Marker).Trim().Split('|'); Guid parsed;
                // Reuse the original archive identity: partially moved WAL/SHM stay
                // associated with the original main file when replacement resumes.
                if (previous.Length>=2 && Guid.TryParseExact(previous[0],"N",out parsed)) id=previous[0];
                DurableCopy(Marker,FreshBackupPath(Folder)+".request");
            }
            var filename="pending-restore-"+Guid.NewGuid().ToString("N")+".db";
            var incoming=Path.Combine(Folder,filename);
            DurableCopy(source,incoming);
            Validate(incoming);
            // Unique immutable stages allow atomic journal replacement without a
            // moment in which the old main file could open without its moved WAL.
            DurableText(Marker,id+"|"+Digest(incoming)+"|"+filename+"|"+(replaceQueuedRequest ? "1" : "0"));
        }

        // A recovery dialog authenticates against the selected BACKUP read-only.
        // It cannot turn an ordinary employee or a passwordless account into an admin.
        public bool VerifyRecoveryAdministrator(string backup, string username, string pin)
        {
            VerifyBackupFile(backup);
            if (string.IsNullOrEmpty(pin) && !new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(
                    System.Security.Principal.WindowsBuiltInRole.Administrator)) return false;
            var options = new SQLiteConnectionStringBuilder { DataSource=backup, ReadOnly=true, Pooling=false };
            using (var c = new SQLiteConnection(options.ToString()))
            {
                c.Open();
                using (var cmd = new SQLiteCommand(@"SELECT salt,password_hash,iterations,
                    COALESCE(locked_until_utc,'') FROM users
                    WHERE username=@name AND role='ADMIN' AND active=1",c))
                {
                    cmd.Parameters.AddWithValue("@name", username);
                    using (var r=cmd.ExecuteReader())
                    {
                        if (!r.Read()) return false;
                        DateTime locked;
                        if (DateTime.TryParse(r.GetString(3),CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind,out locked) && locked>DateTime.UtcNow) return false;
                        using (var derive=new Rfc2898DeriveBytes(pin,(byte[])r[0],r.GetInt32(2),HashAlgorithmName.SHA256))
                        {
                            var actual=derive.GetBytes(32); var expected=(byte[])r[1];
                            int diff=actual.Length^expected.Length;
                            for(int i=0;i<Math.Min(actual.Length,expected.Length);i++) diff|=actual[i]^expected[i];
                            return diff==0;
                        }
                    }
                }
            }
        }

        private static bool AlreadyInstalled(string current, string archive, string hash)
        {
            return File.Exists(current) && (File.Exists(archive) || File.Exists(archive + ".missing"))
                && (!File.Exists(current + "-wal") || new FileInfo(current + "-wal").Length == 0)
                && Digest(current)==hash;
        }

        public void ApplyPendingRestoreAtStartup()
        {
            if (!File.Exists(Marker)) return;
            var request=File.ReadAllText(Marker).Trim();
            // Upgrade a request written by the previous released coordinator.
            if (request==PendingName && File.Exists(Pending))
            {
                Validate(Pending);
                request=Guid.NewGuid().ToString("N") + "|" + Digest(Pending);
                DurableText(Marker,request);
            }
            var parts=request.Split('|'); Guid id;
            if ((parts.Length!=2 && parts.Length!=4) || !Guid.TryParseExact(parts[0],"N",out id) || parts[1].Length!=64)
                throw new InvalidOperationException("طلب الاستعادة غير صالح؛ احتُفظ بالملفات الأصلية.");
            string incoming=Pending;
            bool replacement=false;
            if (parts.Length == 4)
            {
                var filename=parts[2]; Guid stageId;
                if (filename.Length!=51 || !filename.StartsWith("pending-restore-",StringComparison.Ordinal) || !filename.EndsWith(".db",StringComparison.Ordinal)
                    || !Guid.TryParseExact(filename.Substring(16,filename.Length-19),"N",out stageId)
                    || (parts[3]!="0" && parts[3]!="1"))
                    throw new InvalidOperationException("اسم مرحلة الاستعادة غير صالح.");
                incoming=Path.Combine(Folder,filename);
                replacement=parts[3]=="1";
            }
            var archive=Path.Combine(Folder,"store-before-restore-"+parts[0]+".db");
            var current=_db.DatabasePath;
            SQLiteConnection.ClearAllPools();
            if (AlreadyInstalled(current,archive,parts[1]))
            {
                Validate(current);
                File.Delete(Marker);
                if (File.Exists(incoming)) File.Delete(incoming);
                return;
            }
            if (!File.Exists(incoming) || Digest(incoming)!=parts[1])
                throw new InvalidOperationException("نسخة الاستعادة المعلقة مفقودة أو تغيرت؛ لم يُستبدل ملف البيانات.");
            Validate(incoming);
            if (File.Exists(archive) && File.Exists(current) && !replacement)
                throw new IOException("الملف الحالي لا يطابق الاستعادة المكتملة. احتُفظ بنسخة الأمان؛ يلزم فحص الملفات.");
            var archiveThis=archive;
            if (replacement && File.Exists(current) && (File.Exists(archive) ||
                (File.Exists(current+"-wal") && File.Exists(archive+"-wal")) ||
                (File.Exists(current+"-shm") && File.Exists(archive+"-shm"))))
            {
                archiveThis=FreshBackupPath(Folder);
                DurableText(archiveThis+".previous-archive",archive);
            }
            var install=incoming+".install";
            if (File.Exists(install)) File.Delete(install);
            DurableCopy(incoming,install);
            Validate(install);
            var handles=new List<FileStream>();
            try
            {
                // Deny readers/writers while allowing our atomic rename on Windows.
                // If ANY SQLite handle is still open, abort before moving anything.
                foreach(var suffix in new [] { "", "-wal", "-shm" })
                    if(File.Exists(current+suffix))
                        handles.Add(new FileStream(current+suffix,FileMode.Open,FileAccess.Read,FileShare.Delete));
                if (!File.Exists(current)) DurableText(archive+".missing","original database absent");
                foreach(var suffix in new [] { "-wal", "-shm" })
                {
                    var target=suffix=="-wal" ? archiveThis + "-wal" : archiveThis + "-shm";
                    if (File.Exists(current+suffix)) File.Move(current+suffix,target);
                }
                // Preserve raw main/WAL/SHM without trying to open a damaged database.
                // Pending stays immutable, so a crash can always retry from the same bytes.
                if(File.Exists(current)) File.Replace(install,current,archiveThis,true);
                else File.Move(install,current);
            }
            finally { foreach(var handle in handles) handle.Dispose(); }
            Validate(current);
            File.Delete(Marker); // Commit completion before cleaning the reusable stage.
            File.Delete(incoming);
        }
    }
}
