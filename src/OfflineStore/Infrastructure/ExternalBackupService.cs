using System;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace OfflineStore.Infrastructure
{
    /// <summary>
    /// Offline-only backups on a second local volume. A verified snapshot is
    /// published as .db only after VACUUM INTO and semantic integrity checks.
    /// An OS-drive path (normally C:) or a network location is never accepted.
    /// </summary>
    public sealed class ExternalBackupService
    {
        private readonly StoreDb _db;
        private static readonly TimeSpan AutomaticInterval = TimeSpan.FromMinutes(20);
        private const string FolderName = "OfflineStore-Backups";

        public ExternalBackupService(StoreDb db)
        {
            if (db == null) throw new ArgumentNullException("db");
            _db = db;
        }

        private string ConfigFile
        {
            get { return Path.Combine(Path.GetDirectoryName(_db.DatabasePath), "external-backup-folder.txt"); }
        }

        private const string VolumeMarker = ".offlinestore-volume-id";

        public string ConfiguredFolder
        {
            get
            {
                if (!File.Exists(ConfigFile)) return null;
                var lines = File.ReadAllLines(ConfigFile, Encoding.UTF8);
                if (lines.Length == 0) return null;
                var value = lines[0].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        public static string ValidateExternalFolder(string selectedFolder)
        {
            if (string.IsNullOrWhiteSpace(selectedFolder) || !Path.IsPathRooted(selectedFolder))
                throw new ArgumentException("اختر مجلدًا على قرص خارجي أو قرص محلي آخر غير قرص Windows.");
            var full = Path.GetFullPath(selectedFolder);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                throw new ArgumentException("النسخة الاحتياطية يجب أن تكون محلية وليست على شبكة.");
            var root = Path.GetPathRoot(full);
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "ممنوع حفظ النسخ الاحتياطية على قرص Windows. اختر USB أو قرصًا آخر غير C:.");
            var drive = new DriveInfo(root);
            if (!drive.IsReady ||
                (drive.DriveType != DriveType.Removable && drive.DriveType != DriveType.Fixed))
                throw new IOException("قرص النسخ الاحتياطي غير متاح أو نوعه غير مدعوم.");
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException("مجلد النسخ الاحتياطي غير موجود.");
            return full;
        }

        // Selected folder can be a USB root or a custom subdirectory on another volume.
        public string ConfigureFolder(string selectedFolder)
        {
            var parent = ValidateExternalFolder(selectedFolder);
            var tail = Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar,
                                                       Path.AltDirectorySeparatorChar));
            var folder = string.Equals(tail, FolderName, StringComparison.OrdinalIgnoreCase)
                ? parent : Path.Combine(parent, FolderName);
            Directory.CreateDirectory(folder);
            ValidateExternalFolder(folder);
            // Bind the configured drive letter to a random marker on the
            // selected medium. If Windows reuses its letter for another disk,
            // automatic backups refuse to copy customer data to that disk.
            var markerFile = Path.Combine(folder, VolumeMarker);
            Guid parsed;
            var marker = File.Exists(markerFile)
                ? File.ReadAllText(markerFile, Encoding.UTF8).Trim() : "";
            if (!Guid.TryParseExact(marker, "N", out parsed))
            {
                marker = Guid.NewGuid().ToString("N");
                File.WriteAllText(markerFile, marker, Encoding.UTF8);
            }
            var temp = ConfigFile + ".new";
            File.WriteAllText(temp, folder + Environment.NewLine + marker, Encoding.UTF8);
            if (File.Exists(ConfigFile)) File.Replace(temp, ConfigFile, null);
            else File.Move(temp, ConfigFile);
            return folder;
        }

        private string ReadyFolder()
        {
            var folder = ConfiguredFolder;
            if (folder == null)
                throw new InvalidOperationException(
                    "لم يتم اختيار قرص للنسخ الاحتياطي. افتح الإعدادات واختر USB أو قرصًا آخر.");
            var validated = ValidateExternalFolder(folder);
            var lines = File.ReadAllLines(ConfigFile, Encoding.UTF8);
            var marker = Path.Combine(validated, VolumeMarker);
            if (lines.Length != 2 || !File.Exists(marker) ||
                !string.Equals(lines[1].Trim(),
                    File.ReadAllText(marker, Encoding.UTF8).Trim(),
                    StringComparison.Ordinal))
                throw new IOException(
                    "تم تغيير وسيط النسخ أو حرف القرص. أعد اختيار USB من الإعدادات قبل النسخ.");
            return validated;
        }

        private string CreateSnapshot(string folder, string label)
        {
            var filename = "OfflineStore-" + label + "-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N");
            var temp = Path.Combine(folder, filename + ".incomplete");
            var final = Path.Combine(folder, filename + ".db");
            try
            {
                using (var connection = _db.Open())
                using (var command = new SQLiteCommand("VACUUM INTO @path", connection))
                {
                    command.Parameters.AddWithValue("@path", temp);
                    command.ExecuteNonQuery();
                }
                // Never advertise an incomplete/corrupt SQLite copy as a backup.
                new SqliteRecoveryService(_db).VerifyBackupFile(temp);
                File.Move(temp, final);
                return final;
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        internal void EnsureConfiguredFolderReady() { ReadyFolder(); }

        public string CreateManualBackup()
        {
            return CreateSnapshot(ReadyFolder(), "Manual");
        }

        public string CreateAutomaticBackupIfDue()
        {
            if (ConfiguredFolder == null) return null;
            var folder = ReadyFolder();
            var latest = Directory.GetFiles(folder, "OfflineStore-Auto-*.db")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (latest != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(latest) < AutomaticInterval)
            {
                try
                {
                    new SqliteRecoveryService(_db).VerifyBackupFile(latest);
                    return null;
                }
                catch (Exception ex) when (ex is SQLiteException || ex is IOException ||
                                           ex is InvalidOperationException)
                {
                    // Bad existing copy is not trusted; write a fresh verified one.
                }
            }
            return CreateSnapshot(folder, "Auto");
        }
    }
}
