using System;
using System.Data.SQLite;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using OfflineStore.Infrastructure;
using OfflineStore.Models;
using OfflineStore.Security;
using OfflineStore.Services;

internal static class InventoryValueTests
{
 static long Scalar(StoreDb db,string sql){using(var c=db.Open())using(var q=new SQLiteCommand(sql,c))return Convert.ToInt64(q.ExecuteScalar());}
 static void Reject(Action action){bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,"Unsafe carrying operation accepted");}
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static DraftLine Item(long qty,long price){return new DraftLine {ProductId=1,ProductName="Item",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,QuantityMilli=qty,EnteredQuantityMilli=qty,PriceCents=price};}
 public static void Run(string root)
 {
  for(int scenario=0;scenario<3;scenario++)
  {
   var db=new StoreDb(Path.Combine(root,"carrying-"+scenario,"store.db"));db.Initialize();
   var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");var shop=new StoreService(db);shop.SignIn(auth.Login("admin","92837465"));
   shop.Finance.Activate(new JournalLine[0]);shop.Finance.FundCash(10000,"fund",DateTime.Today,"owner funding");
   long supplier=shop.Finance.CreateParty("SUPPLIER","Supplier",null);shop.AddProduct("A","Item","piece",200,0);
   long cost=scenario==2?28:100,freeQty=scenario==2?1017000:2000;
   shop.Post(true,supplier,new [] {Item(1000,cost)});shop.Post(true,supplier,new [] {Item(freeQty,0)});
   if(scenario==0)for(int n=0;n<3;n++)shop.Post(false,null,new [] {Item(1000,200)});
   else if(scenario==1)shop.Post(false,null,new [] {Item(1000,200),Item(1000,200),Item(1000,200)});
   else shop.Post(false,null,new [] {Item(1018000,200)});
   Check(Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1")==0,"Stock fixture did not deplete");
   Check(Scalar(db,"SELECT COALESCE(SUM(debit_cents-credit_cents),0) FROM v_journal_posted_lines WHERE account_code='INVENTORY'")==0,"P1: zero stock retains a monetary inventory balance (scenario "+scenario+")");
   Check(Scalar(db,"SELECT SUM(total_cost_cents) FROM sale_cost_snapshots")==cost,"Outgoing costs do not conserve actual receipt value");
   var source=new List<long[]>();using(var c=db.Open())using(var q=new SQLiteCommand("SELECT l.document_id,l.id,l.qty_milli FROM document_lines l JOIN documents d ON d.id=l.document_id WHERE d.doc_type='SALE' ORDER BY l.id",c))using(var r=q.ExecuteReader())while(r.Read())source.Add(new [] {r.GetInt64(0),r.GetInt64(1),r.GetInt64(2)});
   foreach(var group in source.GroupBy(x=>x[0]))shop.Returns.Post(group.Key,group.Select(x=>new ReturnQuantity {OriginalLineId=x[1],QuantityMilli=x[2]}).ToArray(),0,"sale-return-"+group.Key,"original cost restored");
   Check(Scalar(db,"SELECT value_cents FROM inventory_carrying_values WHERE product_id=1")==cost,"Sales returns did not restore original total cost");
   long line1=Scalar(db,"SELECT id FROM document_lines WHERE document_id=1"),line2=Scalar(db,"SELECT id FROM document_lines WHERE document_id=2");
   shop.Returns.Post(1,new [] {new ReturnQuantity {OriginalLineId=line1,QuantityMilli=1000}},0,"supplier-return-1","current carrying cost");
   shop.Returns.Post(2,new [] {new ReturnQuantity {OriginalLineId=line2,QuantityMilli=freeQty}},0,"supplier-return-2","deplete remaining cost");
   Check(Scalar(db,"SELECT value_cents FROM inventory_carrying_values WHERE product_id=1")==0 && Scalar(db,"SELECT SUM(debit_cents-credit_cents) FROM v_journal_posted_lines WHERE account_code='INVENTORY'")==0,"Purchase returns left residual carrying value");
   Check(shop.Finance.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==0,"Complete sale/purchase return cycle invented profit");
   using(var c=db.Open())StoreDb.VerifyIntegrity(c);
  }
  for(int split=0;split<2;split++)
  {
   var db=new StoreDb(Path.Combine(root,"carrying-group-"+split,"store.db"));db.Initialize();var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");var shop=new StoreService(db);shop.SignIn(auth.Login("admin","92837465"));shop.Finance.Activate(new JournalLine[0]);shop.Finance.FundCash(100,"fund",DateTime.Today,"fund");long supplier=shop.Finance.CreateParty("SUPPLIER","Supplier",null);shop.AddProduct("A","Item","piece",200,0);
   shop.Post(true,supplier,new [] {Item(1000,4)});shop.Post(true,supplier,new [] {Item(5000,0)});
   long before=Scalar(db,"SELECT COUNT(*) FROM documents");
   using(var c=db.Open())using(var q=new SQLiteCommand("CREATE TRIGGER fail_carrying BEFORE INSERT ON inventory_cost_evidence BEGIN SELECT RAISE(ABORT,'interrupted'); END",c))q.ExecuteNonQuery();
   Reject(()=>shop.Post(false,null,new [] {Item(1000,200)}));
   Check(Scalar(db,"SELECT qty_milli FROM inventory_carrying_values WHERE product_id=1")==6000 && Scalar(db,"SELECT value_cents FROM inventory_carrying_values WHERE product_id=1")==4 && Scalar(db,"SELECT COUNT(*) FROM documents")==before,"Failed sale left partial carrying value or document");
   using(var c=db.Open())using(var q=new SQLiteCommand("DROP TRIGGER fail_carrying",c))q.ExecuteNonQuery();
   shop.Post(false,null,split==0?new [] {Item(2000,200)}:new [] {Item(1000,200),Item(1000,200)});
   Check(Scalar(db,"SELECT SUM(total_cost_cents) FROM sale_cost_snapshots")==1 && Scalar(db,"SELECT value_cents FROM inventory_carrying_values WHERE product_id=1")==3,"Same-product split lines changed grouped cost");
   Reject(()=>shop.Journal.PostManual("ADJUSTMENT","manual-inventory",DateTime.Today,new [] {new JournalLine {AccountCode="INVENTORY",DebitCents=1},new JournalLine {AccountCode="OPENING_EQUITY",CreditCents=1}}));
   shop.Post(true,supplier,new [] {Item(1000,10)},null,0);
   long payment=shop.Finance.Pay(supplier,10,"actual-payment",DateTime.Today,"pay another invoice");
   Reject(()=>shop.Finance.AllocateSettlement(payment,2,1,"misallocate-zero"));
   Check(Scalar(db,"SELECT COUNT(*) FROM invoice_allocations WHERE reference='misallocate-zero'")==0,"Zero invoice consumed unrelated payment");
   using(var c=db.Open())StoreDb.VerifyIntegrity(c);
   if(split==1)
   {
    using(var c=db.Open())using(var q=new SQLiteCommand("DROP TRIGGER carrying_evidence_no_update; UPDATE inventory_cost_evidence SET group_offset=0",c))q.ExecuteNonQuery();
    using(var c=db.Open())Reject(()=>StoreDb.VerifyIntegrity(c));
    string backup=Path.Combine(root,"bad-carrying-evidence.db");using(var c=db.Open())using(var q=new SQLiteCommand("VACUUM INTO @file",c)){q.Parameters.AddWithValue("@file",backup);q.ExecuteNonQuery();}
    Reject(()=>new SqliteRecoveryService(db).VerifyBackupFile(backup));
   }
  }
  {
   var db=new StoreDb(Path.Combine(root,"carrying-concurrent","store.db"));db.Initialize();var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");var shop=new StoreService(db);shop.SignIn(auth.Login("admin","92837465"));shop.Finance.Activate(new JournalLine[0]);shop.Finance.FundCash(100,"fund",DateTime.Today,"fund");long supplier=shop.Finance.CreateParty("SUPPLIER","Supplier",null);shop.AddProduct("A","Item","piece",200,0);
   shop.Post(true,supplier,new [] {Item(1000,1)});shop.Post(true,supplier,new [] {Item(1000,0)});
   var gate=new ManualResetEventSlim(false);var first=Task.Run(()=>{gate.Wait();shop.Post(false,null,new [] {Item(1000,200)});});var second=Task.Run(()=>{gate.Wait();shop.Post(false,null,new [] {Item(1000,200)});});gate.Set();Task.WaitAll(first,second);gate.Dispose();
   Check(Scalar(db,"SELECT qty_milli FROM inventory_carrying_values WHERE product_id=1")==0 && Scalar(db,"SELECT value_cents FROM inventory_carrying_values WHERE product_id=1")==0 && Scalar(db,"SELECT SUM(total_cost_cents) FROM sale_cost_snapshots")==1,"Concurrent sales lost carrying cents");
   using(var c=db.Open())StoreDb.VerifyIntegrity(c);
  }
  LegacyMigration(root);
  Console.WriteLine("Exact inventory carrying value, split lines and depletion scenarios passed.");
 }
 static void LegacyMigration(string root)
 {
  var db=new StoreDb(Path.Combine(root,"carrying-legacy","store.db"));var assembly=Assembly.GetExecutingAssembly();
  using(var c=db.Open())
  {
   var names=assembly.GetManifestResourceNames().Where(n=>n.EndsWith(".sql") && (n.EndsWith(".Schema.sql") || int.Parse(n.Split('.').Reverse().Skip(1).First().Substring(0,3))<=22)).OrderBy(n=>n.EndsWith(".Schema.sql")?"0":n);
   foreach(string name in names)using(var reader=new StreamReader(assembly.GetManifestResourceStream(name)))using(var q=new SQLiteCommand(reader.ReadToEnd(),c))q.ExecuteNonQuery();
   using(var q=new SQLiteCommand("PRAGMA user_version=22",c))q.ExecuteNonQuery();
  }
  var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");var actor=auth.Login("admin","92837465");auth.CreateUser(actor,"cashier","Cashier","CASHIER","123456");var shop=new StoreService(db);shop.SignIn(actor);shop.Finance.Activate(new JournalLine[0]);shop.Finance.FundCash(1000,"fund",DateTime.Today,"fund");long supplier=shop.Finance.CreateParty("SUPPLIER","Supplier",null);shop.AddProduct("A","Item","piece",200,0);
  shop.Post(true,supplier,new [] {Item(1000,100)});shop.Post(true,supplier,new [] {Item(2000,0)});for(int n=0;n<3;n++)shop.Post(false,null,new [] {Item(1000,200)});
  Check(Scalar(db,"SELECT SUM(total_cost_cents) FROM sale_cost_snapshots")==99,"Legacy regression fixture no longer reproduces");
  string script;using(var reader=new StreamReader(assembly.GetManifestResourceStream("OfflineStore.Infrastructure.Migrations.023_inventory_carrying.sql")))script=reader.ReadToEnd();
  bool failed=false;using(var c=db.Open())try{typeof(StoreDb).GetMethod("ApplyScript",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[] {c,script+" CREATE TRIGGER fail_seed BEFORE INSERT ON inventory_carrying_values BEGIN SELECT RAISE(ABORT,'interrupted'); END;",23,false});}catch(TargetInvocationException){failed=true;}
  Check(failed && Scalar(db,"PRAGMA user_version")==22 && Scalar(db,"SELECT COUNT(*) FROM sqlite_master WHERE name='inventory_carrying_values'")==0,"Interrupted valuation migration was not atomic");
  db.Initialize();Check(shop.Finance.InventoryReviewRequired,"Historical residual was silently adjusted");
  Reject(()=>shop.Post(true,supplier,new [] {Item(1000,1)}));
  long originalSale=Scalar(db,"SELECT id FROM document_lines WHERE document_id=3");Reject(()=>shop.Returns.Post(3,new [] {new ReturnQuantity {OriginalLineId=originalSale,QuantityMilli=1000}},0,"blocked-return","review first"));
  var review=shop.Finance.CaptureOpeningReview();Reject(()=>new FinanceService(db,auth.Login("cashier","123456")).ReviewLegacyInventory(review));
  shop.Finance.FundCash(1,"changed",DateTime.Today,"invalidate review");Reject(()=>shop.Finance.ReviewLegacyInventory(review));
  review=shop.Finance.CaptureOpeningReview();shop.Finance.ReviewLegacyInventory(review);Reject(()=>shop.Finance.ReviewLegacyInventory(review));
  Check(!shop.Finance.InventoryReviewRequired && Scalar(db,"SELECT SUM(total_cost_cents) FROM sale_cost_snapshots")==99 && Scalar(db,"SELECT SUM(debit_cents-credit_cents) FROM v_journal_posted_lines WHERE account_code='INVENTORY'")==0,"Reviewed migration rewrote old COGS or left inventory discrepancy");
  using(var c=db.Open())StoreDb.VerifyIntegrity(c);
 }
}
