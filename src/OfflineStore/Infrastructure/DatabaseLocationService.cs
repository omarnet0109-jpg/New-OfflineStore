using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OfflineStore.Infrastructure
{
    public sealed class DatabaseLocationUnavailableException : IOException
    {
        public DatabaseLocationUnavailableException(string message,Exception inner=null):base(message,inner) { }
    }
    // Stable per-Windows-user pointer. Moves run on the next startup before application connections open.
    public sealed class DatabaseLocationService
    {
        private readonly string _home;
        public static string DefaultHome {get {return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OfflineStore");}}
        public DatabaseLocationService():this(DefaultHome) { }
        internal DatabaseLocationService(string home){_home=Path.GetFullPath(home);Directory.CreateDirectory(_home);}
        private string Pointer {get {return Path.Combine(_home,"database-location.txt");}}
        private string Guard {get {return Path.Combine(_home,"database-location.guard");}}
        private string Request {get {return Path.Combine(_home,"pending-data-move.request");}}
        public bool HasPendingMove {get {return File.Exists(Request);}}
        private static string Encode(string value){return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));}
        private static string Decode(string value){return Encoding.UTF8.GetString(Convert.FromBase64String(value));}
        private static bool Same(string a,string b){return string.Equals(Path.GetFullPath(a),Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);}
        private static string Hash(string path)
        {using(var file=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","");}
        private static void AtomicWrite(string path,string[] lines)
        {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                byte[] bytes=Encoding.UTF8.GetBytes(string.Join("\n",lines));
                using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){file.Write(bytes,0,bytes.Length);file.Flush(true);}
                if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
            }
            finally {if(File.Exists(temp))File.Delete(temp);}
        }
        private static void RequireIdentity(string target,string token)
        {
            string cookie=Path.Combine(Path.GetDirectoryName(target),"database-identity.txt");
            if(!Directory.Exists(Path.GetDirectoryName(target)) || !File.Exists(cookie) || File.ReadAllText(cookie).Trim()!=token)
                throw new DatabaseLocationUnavailableException("مجلد البيانات المختار غير متاح أو أن هوية القرص مختلفة. أعد توصيل القرص الأصلي؛ لن تُنشأ قاعدة فارغة.");
        }
        private string[] ReadGuard()
        {
            var lines=File.ReadAllLines(Guard);Guid token;
            if(lines.Length==5)lines=lines.Concat(new [] {""}).ToArray();
            if(lines.Length!=6 || lines[0]!="OfflineStore.Location.Guard.1" || !Guid.TryParseExact(lines[3],"N",out token)
                || (!string.IsNullOrEmpty(lines[5]) && !Guid.TryParseExact(lines[5],"N",out token))
                || !Path.IsPathRooted(Decode(lines[1])) || !Path.IsPathRooted(Decode(lines[2]))
                || (!string.IsNullOrEmpty(lines[4]) && (lines[4].Length!=64 || lines[4].Any(c=>!Uri.IsHexDigit(c)))))throw new IOException("سجل حماية مكان البيانات غير صالح.");
            return lines;
        }
        private void WriteGuard(string source,string target,string token,string hash,string sourceToken)
        {AtomicWrite(Guard,new [] {"OfflineStore.Location.Guard.1",Encode(source),Encode(target),token,hash,sourceToken});}
        private static bool MatchesMove(string[] guard,string source,string target,string token)
        {return guard!=null && guard[3]==token && Same(Decode(guard[1]),source) && Same(Decode(guard[2]),target);}
        private bool PendingTransition(string current,string[] guard)
        {
            if(!HasPendingMove)return false;
            var request=File.ReadAllLines(Request);
            return request.Length>=4 && request[0]=="OfflineStore.Move.1" && Same(Decode(request[1]),current)
                && MatchesMove(guard,current,Decode(request[2]),request[3]);
        }
        private string CurrentForMove(string source,string target,string token,bool destinationReady)
        {
            if(File.Exists(Pointer))
            {
                if(destinationReady)
                {
                    var pointer=File.ReadAllLines(Pointer);
                    if(pointer.Length==3 && pointer[0]=="OfflineStore.Location.1" && Same(Decode(pointer[1]),source))return source;
                }
                return Resolve();
            }
            if(File.Exists(Guard))
            {
                if(destinationReady && MatchesMove(ReadGuard(),source,target,token))return source;
                throw new DatabaseLocationUnavailableException("ملف مكان البيانات مفقود. لن تُفتح النسخة القديمة تلقائيًا.");
            }
            return Path.Combine(_home,"store.db");
        }
        public string Resolve()
        {
            if(!File.Exists(Pointer))
            {
                if(File.Exists(Guard))throw new DatabaseLocationUnavailableException("ملف مكان البيانات مفقود. اختر المجلد المعتمد بحساب المدير لإعادة ربطه؛ لن تُفتح النسخة القديمة.");
                return Path.Combine(_home,"store.db");
            }
            try
            {
                var lines=File.ReadAllLines(Pointer);Guid token;
                if(lines.Length!=3 || lines[0]!="OfflineStore.Location.1" || !Guid.TryParseExact(lines[2],"N",out token))throw new IOException("ملف مسار البيانات غير صالح.");
                string path=Decode(lines[1]);if(!Path.IsPathRooted(path))throw new IOException("مسار البيانات غير كامل.");
                RequireIdentity(path,lines[2]);
                if(File.Exists(Guard))
                {
                    var guard=ReadGuard();
                    if((guard[3]!=lines[2] || !Same(Decode(guard[2]),path)) && !PendingTransition(path,guard))throw new IOException("ملف المسار يشير إلى مكان قديم يختلف عن آخر مكان معتمد.");
                }
                else WriteGuard(path,path,lines[2],"",lines[2]);
                if(!File.Exists(path))throw new IOException("ملف قاعدة البيانات المختار مفقود.");
                return Path.GetFullPath(path);
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException)
            {throw new DatabaseLocationUnavailableException("تعذر فتح مكان البيانات المحدد. أعد توصيل القرص أو استعد ملفات المجلد الأصلي. لا يوجد رجوع تلقائي لبيانات قديمة.\n"+ex.Message,ex);}
        }
        public void Stage(string source,string folder)
        {
            if(HasPendingMove)throw new InvalidOperationException("يوجد نقل بيانات معلق. أعد تشغيل البرنامج لاستكماله أولًا.");
            source=Path.GetFullPath(source);
            if(!Same(source,Resolve()) || !File.Exists(source))throw new IOException("مصدر البيانات الحالي غير متاح.");
            if(File.Exists(Path.Combine(Path.GetDirectoryName(source),"pending-restore.request")))throw new InvalidOperationException("أكمل طلب الاستعادة قبل نقل البيانات.");
            if(string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))throw new ArgumentException("اختر مجلدًا محليًا كامل المسار.");
            folder=Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(folder.StartsWith(@"\\",StringComparison.Ordinal) || Same(folder,Path.GetPathRoot(folder)))throw new ArgumentException("اختر مجلد بيانات مستقلًا على قرص محلي، وليس جذر القرص أو شبكة.");
            string target=Path.Combine(folder,"store.db");
            if(Same(source,target) || (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))throw new IOException("مجلد البيانات الجديد يجب أن يكون فارغًا ومختلفًا عن الحالي.");
            var drive=new DriveInfo(Path.GetPathRoot(folder));
            if(!drive.IsReady || (drive.DriveType!=DriveType.Fixed && drive.DriveType!=DriveType.Removable))throw new IOException("قرص البيانات غير متاح.");
            Directory.CreateDirectory(folder);string token=Guid.NewGuid().ToString("N");
            string cookie=Path.Combine(folder,"database-identity.txt");
            using(var file=new FileStream(cookie,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {byte[] bytes=Encoding.UTF8.GetBytes(token);file.Write(bytes,0,bytes.Length);file.Flush(true);}
            try {AtomicWrite(Request,new [] {"OfflineStore.Move.1",Encode(source),Encode(target),token,""});}
            catch {if(!HasPendingMove && File.Exists(cookie) && File.ReadAllText(cookie)==token)File.Delete(cookie);throw;}
        }
        public string PendingSourcePath
        {
            get
            {
                var lines=File.ReadAllLines(Request);
                if(lines.Length<4 || lines[0]!="OfflineStore.Move.1")throw new IOException("طلب النقل غير صالح.");
                string source=Decode(lines[1]),target=Decode(lines[2]);
                if(!Path.IsPathRooted(source) || !File.Exists(source))throw new IOException("مصدر النقل غير متاح.");
                var guard=File.Exists(Guard)?ReadGuard():null;
                bool ready=MatchesMove(guard,source,target,lines[3]) && !string.IsNullOrEmpty(guard[4]);
                if(!Same(CurrentForMove(source,target,lines[3],ready),source))throw new IOException("تم تبديل مكان البيانات؛ لا يمكن الرجوع لنسخة قديمة.");
                if(guard!=null && !string.IsNullOrEmpty(guard[5]))RequireIdentity(source,guard[5]);
                return source;
            }
        }
        public bool CanCancelPendingMove
        {
            get
            {
                if(!HasPendingMove)return false;
                try
                {
                    var request=File.ReadAllLines(Request);
                    if(request.Length<4 || request[0]!="OfflineStore.Move.1" || !File.Exists(Decode(request[1])))return false;
                    if(!File.Exists(Guard))return true;
                    var guard=ReadGuard();return string.IsNullOrEmpty(guard[4]) || Same(Decode(guard[2]),Decode(request[1]));
                }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException){return false;}
            }
        }
        public void CancelPendingMove(OfflineStore.Security.UserSession administrator)
        {
            string source=PendingSourcePath;var db=new StoreDb(source);
            if(File.Exists(Guard))
            {
                var protectedMove=ReadGuard();
                if(!string.IsNullOrEmpty(protectedMove[4]) && !Same(Decode(protectedMove[2]),source))
                    throw new IOException("تم تجهيز واعتماد وجهة النقل. لا يمكن إرجاع المصدر القديم، خصوصًا عند غياب الوجهة. أعد ربط الوجهة المعتمدة أو استعد نسخة صراحةً إلى مجلد جديد.");
            }
            if(!new OfflineStore.Security.UserAuthService(db).Authorize(administrator,"USERS"))throw new UnauthorizedAccessException("إلغاء نقل البيانات للمدير فقط.");
            new SqliteRecoveryService(db).VerifyBackupFile(source);
            if(File.Exists(Pointer))
            {
                var pointer=File.ReadAllLines(Pointer);RequireIdentity(source,pointer[2]);WriteGuard(source,source,pointer[2],"",pointer[2]);
            }
            else if(File.Exists(Guard))
            {
                var guard=ReadGuard();
                if(!string.IsNullOrEmpty(guard[5]))
                {
                    RequireIdentity(source,guard[5]);
                    AtomicWrite(Pointer,new [] {"OfflineStore.Location.1",Encode(source),guard[5]});
                    WriteGuard(source,source,guard[5],"",guard[5]);
                }
                else
                {
                    if(!Same(source,Path.Combine(_home,"store.db")))throw new IOException("لا يمكن فتح مصدر غير موثق تلقائيًا.");
                    File.Delete(Guard);
                }
            }
            File.Delete(Request); // Retain all copied files; never rewind a committed pointer.
        }
        private static void RequireLocalDrive(string folder)
        {
            var drive=new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder)));
            if(!drive.IsReady || (drive.DriveType!=DriveType.Fixed && drive.DriveType!=DriveType.Removable))throw new IOException("اختر قرصًا محليًا متاحًا، وليس قرص شبكة.");
        }
        public string RelinkCandidate(string folder)
        {
            if(!File.Exists(Guard))throw new IOException("لا يوجد سجل لمكان بيانات سابق. لا يمكن اختيار قاعدة عشوائية.");
            if(string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder) || folder.StartsWith(@"\\",StringComparison.Ordinal))throw new ArgumentException("اختر مجلد البيانات المحلي المعتمد.");
            RequireLocalDrive(folder);
            string candidate=Path.Combine(Path.GetFullPath(folder),"store.db");var guard=ReadGuard();RequireIdentity(candidate,guard[3]);
            if(!File.Exists(candidate))throw new IOException("ملف البيانات المعتمد مفقود؛ لن يُنشأ ملف فارغ.");
            new SqliteRecoveryService(new StoreDb(candidate)).VerifyBackupFile(candidate);return candidate;
        }
        public void Relink(string folder,OfflineStore.Security.UserSession administrator)
        {
            string candidate=RelinkCandidate(folder);var guard=ReadGuard();
            if(!new OfflineStore.Security.UserAuthService(new StoreDb(candidate)).Authorize(administrator,"USERS"))throw new UnauthorizedAccessException("إعادة ربط مكان البيانات للمدير فقط.");
            WriteGuard(candidate,candidate,guard[3],"",guard[3]);
            AtomicWrite(Pointer,new [] {"OfflineStore.Location.1",Encode(candidate),guard[3]});
            if(HasPendingMove)File.Delete(Request); // Explicitly authenticated reconnect supersedes an uncompleted/stale move; never copies over the chosen data.
        }
        // Only the authenticated startup-recovery UI may call these; neither method overwrites an existing database.
        internal string PrepareAlternateRestoreFolder(string folder)
        {
            if(string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder) || folder.StartsWith(@"\\",StringComparison.Ordinal))throw new ArgumentException("اختر مجلدًا محليًا فارغًا للاستعادة.");
            folder=Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(Same(folder,Path.GetPathRoot(folder)) || (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))throw new IOException("مجلد الاستعادة البديل يجب أن يكون فارغًا ومخصصًا للبيانات.");
            RequireLocalDrive(folder);Directory.CreateDirectory(folder);string token=Guid.NewGuid().ToString("N");
            using(var file=new FileStream(Path.Combine(folder,"database-identity.txt"),FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {byte[] bytes=Encoding.UTF8.GetBytes(token);file.Write(bytes,0,bytes.Length);file.Flush(true);}
            return Path.Combine(folder,"store.db");
        }
        internal void AdoptAuthenticatedRestore(string databasePath)
        {
            string path=Path.GetFullPath(databasePath),cookie=Path.Combine(Path.GetDirectoryName(path),"database-identity.txt");Guid token;
            if(!File.Exists(path) || !File.Exists(cookie) || !Guid.TryParseExact(File.ReadAllText(cookie).Trim(),"N",out token))throw new IOException("الاستعادة البديلة لم تكتمل.");
            new SqliteRecoveryService(new StoreDb(path)).VerifyBackupFile(path);
            string identity=token.ToString("N");
            WriteGuard(path,path,identity,"",identity);
            AtomicWrite(Pointer,new [] {"OfflineStore.Location.1",Encode(path),identity});
            if(HasPendingMove)File.Delete(Request);
        }
        public void ApplyPendingMove()
        {
            if(!HasPendingMove)return;
            try
            {
                var lines=File.ReadAllLines(Request);Guid token;
                // The last hash field can be empty at first; ReadAllLines preserves a final blank line only if explicitly written twice.
                if(lines.Length==4)lines=lines.Concat(new [] {""}).ToArray();
                if(lines.Length!=5 || lines[0]!="OfflineStore.Move.1" || !Guid.TryParseExact(lines[3],"N",out token))throw new IOException("طلب نقل البيانات غير صالح.");
                string source=Decode(lines[1]),target=Decode(lines[2]);
                if(!Path.IsPathRooted(source) || !Path.IsPathRooted(target) || Same(source,target))throw new IOException("مسارات نقل البيانات غير صالحة.");
                RequireIdentity(target,lines[3]);
                var guard=File.Exists(Guard)?ReadGuard():null;
                bool ready=MatchesMove(guard,source,target,lines[3]) && !string.IsNullOrEmpty(guard[4]);
                if(ready && string.IsNullOrEmpty(lines[4])){lines[4]=guard[4];AtomicWrite(Request,lines);}
                string current=CurrentForMove(source,target,lines[3],ready);
                if(Same(current,target)) {File.Delete(Request);return;} // Pointer committed; never replay a stale snapshot over live data.
                if(!Same(current,source) || (!File.Exists(source) && !ready))throw new IOException("ملف المصدر مفقود؛ لن يُستبدل مسار البيانات.");
                if(File.Exists(Path.Combine(Path.GetDirectoryName(source),"pending-restore.request")))throw new IOException("توجد استعادة معلقة يجب إكمالها قبل النقل.");
                var db=new StoreDb(ready?target:source);string snapshot=Path.Combine(Path.GetDirectoryName(target),"move-"+lines[3]+".db");
                if(string.IsNullOrEmpty(lines[4]))
                {
                    if(File.Exists(target))throw new IOException("يوجد ملف بيانات غير متوقع في الوجهة؛ لن يُستبدل.");
                    if(File.Exists(snapshot))File.Delete(snapshot); // Only this request's private interrupted copy.
                    new SqliteRecoveryService(db).VerifyBackupFile(source);
                    using(var c=db.Open())using(var cmd=new SQLiteCommand("VACUUM INTO @destination",c))
                    {cmd.Parameters.AddWithValue("@destination",snapshot);cmd.ExecuteNonQuery();}
                    new SqliteRecoveryService(db).VerifyBackupFile(snapshot);
                    lines[4]=Hash(snapshot);AtomicWrite(Request,lines);
                }
                if(lines[4].Length!=64 || lines[4].Any(c=>!Uri.IsHexDigit(c)))throw new IOException("بصمة نسخة النقل غير صالحة.");
                if(File.Exists(target))
                {if(Hash(target)!=lines[4])throw new IOException("ملف الوجهة لا يطابق نسخة النقل؛ احتُفظ بالمصدر.");}
                else
                {
                    if(!File.Exists(snapshot) || Hash(snapshot)!=lines[4])throw new IOException("نسخة النقل مفقودة أو تالفة؛ احتُفظ بالمصدر.");
                    File.Move(snapshot,target);
                }
                new SqliteRecoveryService(db).VerifyBackupFile(target);
                string settings=Path.Combine(Path.GetDirectoryName(source),"external-backup-folder.txt");
                if(!ready && File.Exists(settings))
                {
                    string dest=Path.Combine(Path.GetDirectoryName(target),"external-backup-folder.txt");
                    if(!File.Exists(dest))File.Copy(settings,dest);
                    else if(!File.ReadAllBytes(settings).SequenceEqual(File.ReadAllBytes(dest)))throw new IOException("إعدادات النسخ في الوجهة مختلفة؛ لم يتغير مسار البيانات.");
                }
                string sourceToken=ready?guard[5]:"";
                if(!ready && File.Exists(Pointer))sourceToken=File.ReadAllLines(Pointer)[2];
                WriteGuard(source,target,lines[3],lines[4],sourceToken);
                AtomicWrite(Pointer,new [] {"OfflineStore.Location.1",Encode(target),lines[3]});
                File.Delete(Request); // Original database and sidecars are retained; the pointer is the only switch.
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is FormatException || ex is ArgumentException || ex is SQLiteException || ex is InvalidOperationException)
            {throw new DatabaseLocationUnavailableException("لم يكتمل نقل البيانات. احتُفظ بالمصدر وطلب الاستكمال. أعد توصيل القرص ثم أعد المحاولة.\n"+ex.Message,ex);}
        }
    }
}
