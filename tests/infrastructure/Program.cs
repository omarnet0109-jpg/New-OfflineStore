using System;
using System.IO;
using System.Linq;
using System.Data.SQLite;
using System.Security.Cryptography;
using System.Reflection;
using OfflineStore.Infrastructure;
using OfflineStore.Security;

internal static class Program
{
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static void Reject(Action action) { bool rejected=false; try { action(); } catch { rejected=true; } Check(rejected,"Operation unexpectedly accepted"); }
    private static string Hash(string path) { using(var f=File.OpenRead(path)) using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(f)); }
    private static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"OfflineStore-Tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            InventoryValueTests.Run(root);
            for(int scenario=0;scenario<12;scenario++)
            {
                var folder=Path.Combine(root,scenario.ToString()); Directory.CreateDirectory(folder);
                var db=new StoreDb(Path.Combine(folder,"store.db")); db.Initialize();
                var auth=new UserAuthService(db);
                Reject(()=>auth.CreateFirstAdmin("Admin","")); Check(!auth.HasUsers(),"Empty admin persisted");
                auth.CreateFirstAdmin("Admin","92837465");
                var admin=auth.Login("admin","92837465"); Check(auth.Authorize(admin,"USERS"),"Admin login failed");
                auth.CreateUser(admin,"cashier","Cashier","CASHIER","123456");
                Check(!auth.Authorize(auth.Login("cashier","123456"),"USERS"),"Employee privilege escalation");
                Reject(()=>auth.ProtectLegacyAdmin("cashier","654321"));
                if(scenario==0)
                {
                    var lines=new [] {new JournalLine {AccountCode="CASH",DebitCents=10000},
                        new JournalLine {AccountCode="OPENING_EQUITY",CreditCents=10000}};
                    using(var c=db.Open())
                        Reject(()=>JournalService.PostWithin(c,null,"ADJUSTMENT","no-transaction",DateTime.Today,admin.Id,lines));
                    var journal=new JournalService(db,admin);
                    journal.PostManual("OPENING","opening:2026-11-05",DateTime.Today,lines);
                    Reject(()=>journal.PostManual("OPENING","opening:2026-11-05",DateTime.Today,lines));
                    var broken=new [] {new JournalLine {AccountCode="CASH",DebitCents=100},
                        new JournalLine {AccountCode="MISSING",CreditCents=100}};
                    Reject(()=>journal.PostManual("ADJUSTMENT","invalid-account",DateTime.Today,broken));
                    using(var c=db.Open()) using(var cmd=new SQLiteCommand("SELECT COUNT(*) FROM journal_entries",c))
                        Check(Convert.ToInt32(cmd.ExecuteScalar())==1,"Failed journal left a partial header");
                    var cashier=auth.Login("cashier","123456");
                    Reject(()=>new JournalService(db,cashier).PostManual("ADJUSTMENT","unauthorized",DateTime.Today,lines));
                    FinanceTests.Run(db,admin,cashier);
                    auth.SetUserPermissions(admin,cashier.Id,new [] {"SALE"});
                    var appearance=new OfflineStore.Appearance.AppearanceService(db,cashier);
                    Reject(()=>appearance.Save("dark","blue"));
                    Check(appearance.Load().Theme=="light","Denied appearance change persisted");
                    auth.SetUserPermissions(admin,cashier.Id,new [] {"SALE","APPEARANCE"});
                    appearance.Save("dark","blue");
                    Check(appearance.Load().Theme=="dark","Granted appearance change failed");
                }
                var backup=Path.Combine(folder,"backup.db");
                using(var c=db.Open()) using(var cmd=new SQLiteCommand("VACUUM INTO @p",c))
                { cmd.Parameters.AddWithValue("@p",backup); cmd.ExecuteNonQuery(); }
                SQLiteConnection.ClearAllPools();
                var recovery=new SqliteRecoveryService(db);
                var badBackup=Path.Combine(folder,"unbalanced.db"); File.Copy(backup,badBackup);
                using(var c=new StoreDb(badBackup).Open())
                using(var cmd=new SQLiteCommand(@"INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','Vendor','now');
                    INSERT INTO SupplyVouchers(VoucherCode,VoucherDate,SupplierID,SupplierInvoiceDate,PaymentType,
                    SubTotalCents,GrandTotalCents,PaidAmountCents,RemainingAmountCents,CreatedByUserID,CreatedUtc,UpdatedUtc)
                    VALUES('ROF-BAD','2026-11-05',last_insert_rowid(),'2026-11-05','CREDIT',100,100,0,100,1,'now','now');",c))
                    cmd.ExecuteNonQuery();
                Reject(()=>recovery.VerifyBackupFile(badBackup));
                Reject(()=>recovery.StageRestore(badBackup));
                Check(!File.Exists(Path.Combine(folder,"pending-restore.request")),"Unbalanced backup was staged");
                if(scenario==11)
                    using(var c=new StoreDb(backup).Open())
                    using(var cmd=new SQLiteCommand("PRAGMA journal_mode=WAL",c))cmd.ExecuteScalar();
                recovery.StageRestore(backup);
                var marker=Path.Combine(folder,"pending-restore.request");
                var pending=Path.Combine(folder,File.ReadAllText(marker).Split('|')[2]);
                var archive=Path.Combine(folder,"store-before-restore-"+File.ReadAllText(marker).Split('|')[0]+".db");
                var damaged=new byte[]{1,2,3,4,5};
                if(scenario==0 || scenario==1 || scenario==4 || scenario==6 || scenario==7 || scenario==9) File.WriteAllBytes(db.DatabasePath,damaged);
                if(scenario==1) { File.WriteAllBytes(db.DatabasePath+"-wal",new byte[]{7,8}); File.WriteAllBytes(db.DatabasePath+"-shm",new byte[]{9}); }
                if(scenario==2) File.Delete(db.DatabasePath);
                if(scenario==3 || scenario==10 || scenario==11) // Simulate crash after replacement, before journal cleanup.
                { File.Move(db.DatabasePath,archive); File.Copy(pending,db.DatabasePath); File.Delete(pending); }
                if(scenario==10)
                { File.WriteAllBytes(db.DatabasePath+"-wal",new byte[0]); File.WriteAllBytes(db.DatabasePath+"-shm",new byte[32768]); }
                if(scenario==11) recovery.VerifyBackupFile(db.DatabasePath); // Actual read-only WAL-header validation.
                if(scenario==7 || scenario==9) File.Delete(pending);
                if(scenario==8) File.WriteAllBytes(pending,damaged);
                if(scenario==9)
                {
                    // Crash after the old WAL moved, before main-file replacement.
                    File.WriteAllBytes(archive+"-wal",new byte[]{7,8});
                    File.WriteAllBytes(db.DatabasePath+"-shm",new byte[]{9});
                }
                if(scenario==7 || scenario==8 || scenario==9)
                {
                    Check(recovery.VerifyRecoveryAdministrator(backup,"admin","92837465"),"Backup administrator not recognized");
                    recovery.StageRestore(backup,true);
                    Check(Directory.GetFiles(folder,"*.request").Length>=2,"Failed request was not preserved");
                }
                if(scenario==4) File.Delete(pending);
                if(scenario==5) File.WriteAllBytes(pending,damaged);
                if(scenario==6)
                {
                    using(var locked=new FileStream(db.DatabasePath,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                        Reject(()=>recovery.ApplyPendingRestoreAtStartup());
                    Check(File.ReadAllBytes(db.DatabasePath).SequenceEqual(damaged),"Locked original modified");
                }
                if(scenario==4 || scenario==5)
                { var before=Hash(db.DatabasePath); Reject(()=>recovery.ApplyPendingRestoreAtStartup()); Check(Hash(db.DatabasePath)==before,"Rejected restore changed original"); continue; }
                recovery.ApplyPendingRestoreAtStartup();
                recovery.ApplyPendingRestoreAtStartup(); // Must be idempotent.
                Check(Hash(db.DatabasePath)==Hash(backup),"Restored bytes differ");
                Check(!File.Exists(marker),"Completion journal not removed");
                if(scenario==0 || scenario==1 || scenario==6 || scenario==7 || scenario==9) Check(File.ReadAllBytes(archive).SequenceEqual(damaged),"Damaged original not preserved");
                if(scenario==1 || scenario==9) Check(File.Exists(archive+"-wal") && File.Exists(archive+"-shm"),"Sidecars lost");
            }
            ReturnCreditMigrationTests.Run(root);
            ReturnCreditTests.Run(root);
            CutoverTests.Run(root);
            InvoiceFinanceTests.Run(root);
            DataLocationTests.Run(root);
            foreach(int version in new [] {9,10,11,14,15,16,17,18,19,20,21,22,23})
            {
                var folder=Path.Combine(root,"upgrade-"+version); Directory.CreateDirectory(folder);
                var db=new StoreDb(Path.Combine(folder,"store.db"));
                var assembly=Assembly.GetExecutingAssembly();
                using(var c=db.Open())
                {
                    var resources=assembly.GetManifestResourceNames().Where(n=>n.EndsWith(".sql") &&
                        (n.EndsWith(".Schema.sql") || int.Parse(n.Split('.').Reverse().Skip(1).First().Substring(0,3))<=version))
                        .OrderBy(n=>n.EndsWith(".Schema.sql") ? "0" : n);
                    foreach(var resource in resources)
                        using(var input=new StreamReader(assembly.GetManifestResourceStream(resource)))
                        using(var cmd=new SQLiteCommand(input.ReadToEnd(),c)) cmd.ExecuteNonQuery();
                    using(var cmd=new SQLiteCommand("PRAGMA user_version="+version,c))cmd.ExecuteNonQuery();
                }
                if(version==23)using(var c=db.Open())using(var tx=c.BeginTransaction()){InventoryValueService.Seed(c,tx);tx.Commit();}
                var auth=new UserAuthService(db);auth.CreateFirstAdmin("Old Admin","92837465");
                long adminId=auth.Login("admin","92837465").Id;
                var config=Path.Combine(folder,"external-backup-folder.txt");
                File.WriteAllText(config,"Z:\\OfflineStore-Missing-"+Guid.NewGuid().ToString("N")+Environment.NewLine+Guid.NewGuid().ToString("N"));
                bool blocked=false;
                try { db.Initialize(); } catch(UpgradeBackupRequiredException) { blocked=true; }
                Check(blocked,"Missing backup target did not block upgrade safely");
                using(var c=db.Open()) using(var cmd=new SQLiteCommand("PRAGMA user_version",c))
                    Check(Convert.ToInt32(cmd.ExecuteScalar())==version,"Failed preflight partially migrated database");
                var target=Environment.GetEnvironmentVariable("OFFLINESTORE_TEST_BACKUP_ROOT");
                Check(!string.IsNullOrEmpty(target),"Set isolated second-volume backup target for upgrade tests");
                var selected=Path.Combine(target,"v"+version); Directory.CreateDirectory(selected);
                new ExternalBackupService(db).ConfigureFolder(selected);
                db.Initialize();
                Check(auth.Login("admin","92837465").Id==adminId,"Upgrade changed original account");
                using(var c=db.Open()) using(var cmd=new SQLiteCommand("PRAGMA user_version",c))
                    Check(Convert.ToInt32(cmd.ExecuteScalar())==24,"Upgrade did not finish");
            }
            Console.WriteLine("Infrastructure security and recovery scenarios passed (isolated temporary databases).");
        }
        finally { SQLiteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
}
