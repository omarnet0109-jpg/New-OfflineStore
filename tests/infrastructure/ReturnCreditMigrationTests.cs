using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Reflection;
using OfflineStore.Infrastructure;
using OfflineStore.Models;
using OfflineStore.Security;
using OfflineStore.Services;

internal static class ReturnCreditMigrationTests
{
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static long Scalar(StoreDb db,string sql){using(var c=db.Open())using(var q=new SQLiteCommand(sql,c))return Convert.ToInt64(q.ExecuteScalar());}
 static string Rows(StoreDb db,string sql){using(var c=db.Open())using(var q=new SQLiteCommand(sql,c))using(var r=q.ExecuteReader()){var text="";while(r.Read()){for(int i=0;i<r.FieldCount;i++)text+=Convert.ToString(r.GetValue(i))+"|";text+="\n";}return text;}}
 public static void Run(string root)
 {
  var db=new StoreDb(Path.Combine(root,"return-credit-upgrade","store.db"));var assembly=Assembly.GetExecutingAssembly();
  using(var c=db.Open())
  {
   var names=assembly.GetManifestResourceNames().Where(n=>n.EndsWith(".sql") && (n.EndsWith(".Schema.sql") || int.Parse(n.Split('.').Reverse().Skip(1).First().Substring(0,3))<=20)).OrderBy(n=>n.EndsWith(".Schema.sql")?"0":n);
   foreach(string name in names)using(var reader=new StreamReader(assembly.GetManifestResourceStream(name)))using(var q=new SQLiteCommand(reader.ReadToEnd(),c))q.ExecuteNonQuery();
   using(var q=new SQLiteCommand("PRAGMA user_version=20",c))q.ExecuteNonQuery();
  }
  var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");var actor=auth.Login("admin","92837465");var shop=new StoreService(db);shop.SignIn(actor);using(var c=db.Open())using(var q=new SQLiteCommand("INSERT INTO finance_activation VALUES(1,NULL,0,'"+DateTime.Today.ToString("yyyy-MM-dd")+"',1)",c))q.ExecuteNonQuery();long supplier=shop.Finance.CreateParty("SUPPLIER","Supplier",null);
  shop.AddProduct("OLD","Product","piece",200,0);shop.Post(true,supplier,new [] {new DraftLine {ProductId=1,ProductName="Product",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=1000,QuantityMilli=1000,PriceCents=100}},null,0);
  using(var c=db.Open())using(var tx=c.BeginTransaction())
  {
   using(var q=new SQLiteCommand("INSERT INTO return_documents(original_document_id,reference,return_date,cash_cents,debt_cents,total_cents,created_by_user_id,created_utc,memo) VALUES(1,'old-return','2026-11-06',0,100,100,1,'now','original unchanged'); INSERT INTO return_lines(return_id,original_line_id,qty_milli,amount_cents,cost_cents,cost_basis_millicents,name_snapshot,unit_snapshot) VALUES(1,1,1000,100,100,100000,'Product','piece');",c,tx))q.ExecuteNonQuery();
   long entry=JournalService.PostWithin(c,tx,"RETURN","return:old-return",DateTime.Today,actor.Id,new [] {new JournalLine {AccountCode="PAYABLES",PartyId=supplier,DebitCents=100},new JournalLine {AccountCode="INVENTORY",CreditCents=100}},1);
   using(var q=new SQLiteCommand("UPDATE return_documents SET journal_entry_id=@entry WHERE id=1; INSERT INTO invoice_allocations(entry_id,document_id,amount_cents,reference) VALUES(@entry,1,100,'old-allocation'); UPDATE return_documents SET state='POSTED' WHERE id=1; UPDATE sqlite_sequence SET seq=100 WHERE name='return_documents';",c,tx)){q.Parameters.AddWithValue("@entry",entry);q.ExecuteNonQuery();}tx.Commit();
  }
  string fields="id,original_document_id,reference,return_date,state,cash_cents,debt_cents,total_cents,journal_entry_id,created_by_user_id,created_utc,memo";
  string headers=Rows(db,"SELECT "+fields+" FROM return_documents"),lines=Rows(db,"SELECT * FROM return_lines"),journal=Rows(db,"SELECT * FROM journal_lines ORDER BY id");
  string script;using(var reader=new StreamReader(assembly.GetManifestResourceStream("OfflineStore.Infrastructure.Migrations.021_return_credits.sql")))script=reader.ReadToEnd();
  bool rejected=false;using(var c=db.Open())try{typeof(StoreDb).GetMethod("ApplyScript",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[] {c,script+" SELECT * FROM injected_missing_table;",21,true});}catch(TargetInvocationException){rejected=true;}
  Check(rejected && Scalar(db,"PRAGMA user_version")==20 && Scalar(db,"SELECT COUNT(*) FROM pragma_table_info('return_documents') WHERE name='credit_cents'")==0,"Failed rebuild did not restore old schema/version");
  Check(Rows(db,"SELECT "+fields+" FROM return_documents")==headers && Rows(db,"SELECT * FROM return_lines")==lines,"Interrupted rebuild changed history");
  db.Initialize();Check(Scalar(db,"PRAGMA user_version")==24,"Return credit migration did not finish");
  Check(Rows(db,"SELECT "+fields+" FROM return_documents")==headers && Rows(db,"SELECT * FROM return_lines")==lines && Rows(db,"SELECT * FROM journal_lines ORDER BY id")==journal,"Credit migration altered historic financial records");
  Check(Scalar(db,"SELECT credit_cents FROM return_documents WHERE id=1")==0 && Scalar(db,"SELECT seq FROM sqlite_sequence WHERE name='return_documents'")>=100,"Migration lost default credit or sequence");
  using(var c=db.Open()){Check(Scalar(db,"PRAGMA foreign_keys")==1,"FK enforcement not restored");StoreDb.VerifyIntegrity(c);}
  bool immutable=false;using(var c=db.Open())using(var q=new SQLiteCommand("UPDATE return_documents SET memo='changed' WHERE id=1",c))try{q.ExecuteNonQuery();}catch(SQLiteException){immutable=true;}Check(immutable,"Migration lost immutable header trigger");
  Console.WriteLine("Nonempty return migration, rollback, exact history, sequence and FK preservation passed.");
 }
}
