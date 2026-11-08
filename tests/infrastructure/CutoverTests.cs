using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OfflineStore.Infrastructure;
using OfflineStore.Models;
using OfflineStore.Security;
using OfflineStore.Services;

internal static class CutoverTests
{
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static void Reject(Action action){bool failed=false;try{action();}catch{failed=true;}Check(failed,"Unsafe cutover operation accepted");}
 static long Count(StoreDb db,string sql){using(var c=db.Open())using(var q=new SQLiteCommand(sql,c))return Convert.ToInt64(q.ExecuteScalar());}
 static StoreService Shop(string root,string name,out StoreDb db)
 {
  db=new StoreDb(Path.Combine(root,name,"store.db"));db.Initialize();var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");
  var shop=new StoreService(db);shop.SignIn(auth.Login("admin","92837465"));return shop;
 }
 static JournalLine[] Cash(long value){return new [] {new JournalLine {AccountCode="CASH",DebitCents=value},new JournalLine {AccountCode="OPENING_EQUITY",CreditCents=value}};}
 static DraftLine Line(long product,long qty,long price){return new DraftLine {ProductId=product,ProductName="Product",ProductUnitId=product,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=qty,QuantityMilli=qty,PriceCents=price};}
 public static void Run(string root)
 {
  StoreDb old;var shop=Shop(root,"cutover-old-opening",out old);
  Reject(()=>shop.Journal.PostManual("OPENING","future-opening",DateTime.Today.AddDays(1),Cash(100)));
  shop.Journal.PostManual("OPENING","yesterday-opening",DateTime.Today.AddDays(-1),Cash(10000));
  shop.Journal.PostManual("ADJUSTMENT","yesterday-adjustment",DateTime.Today.AddDays(-1),Cash(100));
  shop.Finance.Activate(Cash(10200));
  Check(shop.Finance.CashBalanceCents()==10200 && Count(old,"SELECT COUNT(*) FROM journal_entries WHERE source_kind='OPENING'")==1,"Old opening duplicated or adjustments lost");
  using(var c=old.Open())using(var q=new SQLiteCommand("SELECT entry_date FROM journal_entries WHERE source_key='yesterday-opening'",c))Check(Convert.ToString(q.ExecuteScalar())==DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd"),"Old opening history changed");
  long entries=Count(old,"SELECT COUNT(*) FROM journal_entries");
  Reject(()=>shop.Journal.PostManual("ADJUSTMENT","future",DateTime.Today.AddDays(1),Cash(1)));
  Reject(()=>shop.Journal.PostManual("ADJUSTMENT","backdated",DateTime.Today.AddDays(-1),Cash(1)));
  Check(Count(old,"SELECT COUNT(*) FROM journal_entries")==entries,"Rejected date left partial journal");
  StoreDb adjustments;var adjusted=Shop(root,"cutover-adjustments-only",out adjustments);
  adjusted.Journal.PostManual("ADJUSTMENT","old-adjustment-only",DateTime.Today.AddDays(-1),Cash(100));
  adjusted.Finance.Activate(Cash(150));
  Check(adjusted.Finance.CashBalanceCents()==150 && Count(adjustments,"SELECT COUNT(*) FROM journal_entries WHERE source_kind='OPENING'")==0 && Count(adjustments,"SELECT COUNT(*) FROM journal_entries WHERE source_key='old-adjustment-only'")==1,"Adjustment-only ledger could not activate without duplicating history");
  using(var c=adjustments.Open())StoreDb.VerifyIntegrity(c);
  StoreDb results;var prior=Shop(root,"cutover-prior-results",out results);
  prior.Journal.PostManual("OPENING","old-results-opening",DateTime.Today.AddDays(-1),Cash(1000));
  prior.Journal.PostManual("ADJUSTMENT","old-expense",DateTime.Today.AddDays(-1),new [] {new JournalLine {AccountCode="EXPENSES",DebitCents=100},new JournalLine {AccountCode="CASH",CreditCents=100}});
  var resultsReview=prior.Finance.CaptureOpeningReview();
  Reject(()=>prior.Finance.Activate(Cash(900),resultsReview));
  Check(!prior.Finance.IsActive && Count(results,"SELECT COUNT(*) FROM journal_entries")==2,"Unapproved legacy results were changed");
  using(var c=results.Open())using(var q=new SQLiteCommand("CREATE TRIGGER fail_cutover_boundary BEFORE INSERT ON finance_activation_boundary BEGIN SELECT RAISE(ABORT,'interrupted'); END",c))q.ExecuteNonQuery();
  Reject(()=>prior.Finance.Activate(Cash(900),resultsReview,true));
  Check(!prior.Finance.IsActive && Count(results,"SELECT COUNT(*) FROM journal_entries")==2,"Legacy closure survived failed activation");
  using(var c=results.Open())using(var q=new SQLiteCommand("DROP TRIGGER fail_cutover_boundary",c))q.ExecuteNonQuery();
  prior.Finance.Activate(Cash(900),resultsReview,true);
  Check(prior.Finance.CashBalanceCents()==900 && Count(results,"SELECT COUNT(*) FROM journal_entries WHERE source_key='old-expense'")==1,"Legacy close changed history or cash");
  Check(prior.Finance.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==0 && prior.Finance.ProfitAndLoss(DateTime.Today.AddDays(-2),DateTime.Today).NetCents==0,"Legacy closure leaked into current profit");
  prior.Finance.RecordExpense("EXPENSES",50,"new-expense",DateTime.Today,"current expense");
  Check(prior.Finance.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==-50,"Post-cutover expense excluded");
  using(var c=results.Open())StoreDb.VerifyIntegrity(c);
  StoreDb closed;var completed=Shop(root,"cutover-closed-results",out closed);
  completed.Journal.PostManual("OPENING","closed-opening",DateTime.Today.AddDays(-1),Cash(1000));
  completed.Journal.PostManual("ADJUSTMENT","closed-expense",DateTime.Today.AddDays(-1),new [] {new JournalLine {AccountCode="EXPENSES",DebitCents=100},new JournalLine {AccountCode="CASH",CreditCents=100}});
  completed.Journal.PostManual("ADJUSTMENT","documented-closure",DateTime.Today,new [] {new JournalLine {AccountCode="OPENING_EQUITY",DebitCents=100},new JournalLine {AccountCode="EXPENSES",CreditCents=100}});
  completed.Finance.Activate(Cash(900));
  Check(completed.Finance.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==0 && Count(closed,"SELECT COUNT(*) FROM journal_entries")==3,"Already closed legacy ledger was duplicated or leaked into current profit");
  StoreDb zero;var empty=Shop(root,"cutover-zero",out zero);empty.Finance.Activate(new List<JournalLine>());
  Reject(()=>empty.Journal.PostManual("OPENING","second-zero-opening",DateTime.Today,Cash(100)));
  empty.Finance.FundCash(1000,"cash",DateTime.Today,"actual owner cash");long supplier=empty.Finance.CreateParty("SUPPLIER","Supplier",null);
  empty.AddProduct("A","Product","piece",200,0);empty.Post(true,supplier,new [] {Line(1,1000,100)});empty.Post(false,null,new [] {Line(1,1000,200)});
  empty.AddProduct("FREE","Free","piece",0,0);empty.Post(true,supplier,new [] {Line(2,1000,0)});
  using(var c=zero.Open())StoreDb.VerifyIntegrity(c); // Legitimate zero-value stock document has no invented money entry.
  using(var c=zero.Open())using(var q=new SQLiteCommand(@"INSERT INTO documents(doc_type,party_id,total_cents,created_utc,created_by_user_id) VALUES('PURCHASE',1,100,'now',1);
    INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents,unit_cost_snapshot_cents,product_unit_id,entered_qty_milli,entered_unit_name,unit_factor_milli,entered_unit_price_cents)
     VALUES(last_insert_rowid(),1,1,1000,100,100,100,1,1000,'piece',1000,100);
    INSERT INTO purchase_cost_snapshots VALUES(last_insert_rowid(),100,0);
    INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) SELECT document_id,id,product_id,qty_milli,'now' FROM document_lines WHERE document_id=(SELECT MAX(id) FROM documents);
    UPDATE documents SET state='POSTED',doc_code='P-ORPHAN' WHERE id=(SELECT MAX(id) FROM documents);",c))q.ExecuteNonQuery();
  using(var c=zero.Open())Reject(()=>StoreDb.VerifyIntegrity(c));
  string backup=Path.Combine(root,"cutover-orphan.db");using(var c=zero.Open())using(var q=new SQLiteCommand("VACUUM INTO @file",c)){q.Parameters.AddWithValue("@file",backup);q.ExecuteNonQuery();}
  Reject(()=>new SqliteRecoveryService(zero).VerifyBackupFile(backup));
  StoreDb race;var racing=Shop(root,"cutover-race",out race);long vendor=racing.Finance.CreateParty("SUPPLIER","Vendor",null);
  racing.AddProduct("RACE","Product","piece",100,0);racing.Post(true,vendor,new [] {Line(1,2000,0)});
  var review=racing.Finance.CaptureOpeningReview();var gate=new ManualResetEventSlim(false);bool activated=false;
  var activate=Task.Run(()=>{gate.Wait();try{racing.Finance.Activate(new List<JournalLine>(),review);activated=true;}catch(InvalidOperationException){}});
  var invoice=Task.Run(()=>{gate.Wait();racing.Post(false,null,new [] {Line(1,1000,100)});});gate.Set();Task.WaitAll(activate,invoice);gate.Dispose();
  if(!activated)
  {
   Check(!racing.Finance.IsActive && Count(race,"SELECT COUNT(*) FROM journal_entries")==0,"Stale review partly activated");
   racing.Finance.Activate(Cash(100),racing.Finance.CaptureOpeningReview());
  }
  long legacy=Count(race,"SELECT COUNT(*) FROM finance_legacy_documents WHERE document_id=2"),posted=Count(race,"SELECT COUNT(*) FROM journal_entries WHERE document_id=2 AND source_kind='SALE' AND state='POSTED'");
  Check(legacy+posted==1 && racing.Finance.CashBalanceCents()==100,"Concurrent invoice omitted or double posted at cutover");
  using(var c=race.Open())StoreDb.VerifyIntegrity(c);
  Console.WriteLine("Cutover dates, legacy reconciliation, orphan backup and concurrent invoice scenarios passed.");
 }
}
