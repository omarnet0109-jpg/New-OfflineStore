using System;
using System.Data.SQLite;
using System.IO;
using OfflineStore.Infrastructure;
using OfflineStore.Security;

internal static class DataLocationTests
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Reject(Action action){bool failed=false;try{action();}catch{failed=true;}Check(failed,"Unsafe data move accepted");}
    public static void Run(string root)
    {
        string home=Path.Combine(root,"move-profile"),target=Path.Combine(root,"moved-data");Directory.CreateDirectory(home);
        var db=new StoreDb(Path.Combine(home,"store.db"));db.Initialize();var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");
        var originalAdmin=auth.Login("admin","92837465");auth.CreateUser(originalAdmin,"staff","Staff","CASHIER","123456");var employee=auth.Login("staff","123456");
        var location=new DatabaseLocationService(home);
        File.WriteAllText(Path.Combine(home,"external-backup-folder.txt"),"Preserved backup configuration");
        location.Stage(db.DatabasePath,target);
        // Keep a genuine committed WAL open; VACUUM snapshot must include it, never raw-copy main only.
        string completedRequest=null;
        using(var c=db.Open())
        {
            using(var cmd=new SQLiteCommand("INSERT INTO parties(party_type,name,created_utc) VALUES('CUSTOMER','WAL customer','now')",c))cmd.ExecuteNonQuery();
            Check(File.Exists(db.DatabasePath+"-wal"),"WAL fixture missing");
            // Fail pointer publication AFTER verified destination publication.
            Directory.CreateDirectory(Path.Combine(home,"database-location.txt"));
            Reject(()=>location.ApplyPendingMove());
            Check(File.Exists(db.DatabasePath) && File.Exists(Path.Combine(target,"store.db")),"Interrupted move lost source or target");
            Check(!location.CanCancelPendingMove,"A ready protected target was offered as a cancellable source rewind");
            completedRequest=File.ReadAllText(Path.Combine(home,"pending-data-move.request"));
            Directory.Delete(Path.Combine(home,"database-location.txt"));
        }
        SQLiteConnection.ClearAllPools();File.Move(db.DatabasePath,db.DatabasePath+".unavailable");
        location.ApplyPendingMove();location.ApplyPendingMove(); // Verified destination can finish even when source goes missing after copying.
        File.Move(db.DatabasePath+".unavailable",db.DatabasePath);
        string chosen=location.Resolve();Check(chosen==Path.Combine(target,"store.db"),"Location pointer not switched");
        Check(File.Exists(db.DatabasePath),"Original database removed");
        Check(File.ReadAllText(Path.Combine(target,"external-backup-folder.txt"))=="Preserved backup configuration","Backup settings lost");
        var moved=new StoreDb(chosen);Check(new UserAuthService(moved).Login("admin","92837465").Role=="ADMIN","Accounts not preserved");
        using(var c=moved.Open())using(var cmd=new SQLiteCommand("SELECT COUNT(*) FROM parties WHERE name='WAL customer'",c))Check(Convert.ToInt64(cmd.ExecuteScalar())==1,"Committed WAL transaction lost");
        using(var c=moved.Open())using(var cmd=new SQLiteCommand("INSERT INTO parties(party_type,name,created_utc) VALUES('CUSTOMER','Newer target work','now')",c))cmd.ExecuteNonQuery();
        SQLiteConnection.ClearAllPools();
        File.WriteAllText(Path.Combine(home,"pending-data-move.request"),completedRequest);
        File.Delete(Path.Combine(home,"database-location.txt"));Directory.Move(target,target+"-offline");
        Reject(()=>location.CancelPendingMove(originalAdmin));
        Check(File.Exists(Path.Combine(home,"pending-data-move.request")) && File.Exists(Path.Combine(home,"database-location.guard")),"Unverifiable destination rewound to retained source");
        Directory.Move(target+"-offline",target);
        location.Relink(target,new UserAuthService(moved).Login("admin","92837465"));
        using(var c=moved.Open())using(var cmd=new SQLiteCommand("SELECT COUNT(*) FROM parties WHERE name='Newer target work'",c))Check(Convert.ToInt64(cmd.ExecuteScalar())==1,"Relink replayed an old snapshot over newer target work");
        File.Delete(Path.Combine(home,"database-location.txt"));
        Reject(()=>location.Resolve()); // Retained original must never become an automatic fallback.
        string candidate=location.RelinkCandidate(target);
        var relinkAdmin=new UserAuthService(new StoreDb(candidate)).Login("admin","92837465");
        Reject(()=>location.Relink(target,employee));
        Reject(()=>location.Resolve());
        location.Relink(target,relinkAdmin);Check(location.Resolve()==chosen,"Lost pointer could not be safely relinked");
        Reject(()=>location.RelinkCandidate(home));
        File.Move(Path.Combine(target,"database-identity.txt"),Path.Combine(target,"identity-hidden.txt"));
        Reject(()=>location.Resolve());Check(!File.Exists(Path.Combine(home,"new.db")),"Missing target caused empty database creation");
        File.Move(Path.Combine(target,"identity-hidden.txt"),Path.Combine(target,"database-identity.txt"));
        SQLiteConnection.ClearAllPools();File.Move(chosen,chosen+".hidden");
        Reject(()=>location.Resolve());Check(!File.Exists(chosen),"Missing database silently recreated");File.Move(chosen+".hidden",chosen);
        string occupied=Path.Combine(root,"occupied-data");Directory.CreateDirectory(occupied);File.WriteAllText(Path.Combine(occupied,"keep.txt"),"keep");
        Reject(()=>location.Stage(chosen,occupied));Check(File.ReadAllText(Path.Combine(occupied,"keep.txt"))=="keep","Occupied destination changed");
        File.WriteAllText(Path.Combine(target,"pending-restore.request"),"pending");
        Reject(()=>location.Stage(chosen,Path.Combine(root,"blocked-move")));File.Delete(Path.Combine(target,"pending-restore.request"));
        string missing=Path.Combine(root,"missing-target");location.Stage(chosen,missing);Directory.Move(missing,missing+"-hidden");
        Reject(()=>location.ApplyPendingMove());Check(File.Exists(chosen),"Disconnected move target damaged source");
        Directory.Move(missing+"-hidden",missing);location.ApplyPendingMove();Check(location.Resolve()==Path.Combine(missing,"store.db"),"Move could not resume after reconnect");
        string beforeRestore=location.Resolve(),backup=Path.Combine(root,"location-restore-backup.db");
        using(var c=new StoreDb(beforeRestore).Open())using(var cmd=new SQLiteCommand("VACUUM INTO @destination",c)){cmd.Parameters.AddWithValue("@destination",backup);cmd.ExecuteNonQuery();}
        string alternative=location.PrepareAlternateRestoreFolder(Path.Combine(root,"alternate-restored-data"));
        var recovery=new SqliteRecoveryService(new StoreDb(alternative));
        Check(recovery.VerifyRecoveryAdministrator(backup,"admin","92837465"),"Alternate-location backup administrator denied");
        recovery.StageRestore(backup,true);recovery.ApplyPendingRestoreAtStartup();location.AdoptAuthenticatedRestore(alternative);
        Check(location.Resolve()==alternative && File.Exists(beforeRestore),"Alternate restore overwrote previous data");
        Reject(()=>location.RelinkCandidate(Path.GetDirectoryName(beforeRestore))); // Explicit restore retires every previous location identity.
        Console.WriteLine("Data relocation, WAL, interruption, missing-volume and retention scenarios passed.");
    }
}
