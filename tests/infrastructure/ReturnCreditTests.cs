using System;
using System.Data.SQLite;
using System.IO;
using OfflineStore.Infrastructure;
using OfflineStore.Models;
using OfflineStore.Security;
using OfflineStore.Services;

internal static class ReturnCreditTests
{
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 static void Reject(Action action){bool failed=false;try{action();}catch{failed=true;}Check(failed,"Invalid return credit accepted");}
 static long Scalar(StoreDb db,string sql){using(var c=db.Open())using(var q=new SQLiteCommand(sql,c))return Convert.ToInt64(q.ExecuteScalar());}
 static DraftLine Line(long quantity,long price){return new DraftLine {ProductId=1,ProductName="Item",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=quantity,QuantityMilli=quantity,PriceCents=price};}
 public static void Run(string root)
 {
  var db=new StoreDb(Path.Combine(root,"return-credits","store.db"));db.Initialize();var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");var admin=auth.Login("admin","92837465");auth.CreateUser(admin,"staff","Staff","CASHIER","123456");var staff=auth.Login("staff","123456");
  var shop=new StoreService(db);shop.SignIn(admin);var f=shop.Finance;f.Activate(new JournalLine[0]);f.FundCash(10000,"capital",DateTime.Today,"owner cash");
  long supplier=f.CreateParty("SUPPLIER","Supplier",null),customer=f.CreateParty("CUSTOMER","Customer",null),wrong=f.CreateParty("CUSTOMER","Wrong",null);
  shop.AddProduct("CREDIT","Item","piece",200,0);shop.Post(true,supplier,new [] {Line(10000,100)});shop.Post(false,null,new [] {Line(2000,200)},customer);
  var one=new [] {new ReturnQuantity {OriginalLineId=2,QuantityMilli=1000}};
  Reject(()=>shop.Returns.Post(2,one,0,"wrong-beneficiary","wrong",200,wrong));
  long cash=f.CashBalanceCents();string first=shop.Returns.Post(2,one,0,"customer-credit","retain return credit",200);
  Check(f.CashBalanceCents()==cash && Convert.ToInt64(f.PartyBalances().Select("party_id="+customer)[0]["customer_credit_cents"])==200,"Return credit altered cash or did not create liability");
  Check(Convert.ToInt64(f.PartyBalances().Select("party_id="+customer)[0]["customer_advance_cents"])==0,"Return credit mixed with cash advance");
  Reject(()=>shop.Returns.Post(2,one,0,"customer-credit","retry",200));
  shop.Returns.Post(2,one,0,"remaining-cash","other half cash refund");
  Check(f.CashBalanceCents()==cash-200,"Mixed repeated credit/cash returns exceeded or lost original payment");
  shop.Post(false,null,new [] {Line(1000,200)},customer,0);
  long application=f.ApplyReturnCredit(true,customer,200,"apply-credit",DateTime.Today,"apply to next sale");f.AllocateSettlement(application,3,200,"allocate-credit");
  Check(Convert.ToInt64(f.PartyBalances().Select("party_id="+customer)[0]["receivable_cents"])==0 && Convert.ToInt64(f.PartyBalances().Select("party_id="+customer)[0]["customer_credit_cents"])==0,"Credit application did not offset debt exactly");
  Reject(()=>f.ApplyReturnCredit(true,customer,1,"over-credit",DateTime.Today,"no remaining credit"));
  var newSale=new [] {new ReturnQuantity {OriginalLineId=3,QuantityMilli=1000}};
  Reject(()=>shop.Returns.Post(3,newSale,0,"no-fake-cash","credit application is not a cash receipt"));
  shop.Returns.Post(3,newSale,0,"credit-again","return noncash-paid goods as credit",200);
  shop.Post(false,null,new [] {Line(1000,200)});var anonymous=new [] {new ReturnQuantity {OriginalLineId=4,QuantityMilli=1000}};
  Reject(()=>shop.Returns.Post(4,anonymous,0,"unnamed-credit","must choose beneficiary",200));
  shop.Returns.Post(4,anonymous,0,"named-credit","explicit beneficiary for anonymous cash sale",200,customer);
  var purchase=new [] {new ReturnQuantity {OriginalLineId=1,QuantityMilli=1000}};shop.Returns.Post(1,purchase,0,"supplier-credit","supplier retains refund credit",100);
  Check(Convert.ToInt64(f.PartyBalances().Select("party_id="+supplier)[0]["supplier_credit_cents"])==100 && Convert.ToInt64(f.PartyBalances().Select("party_id="+supplier)[0]["supplier_advance_cents"])==0,"Supplier credit mixed with advance");
  shop.Post(true,supplier,new [] {Line(1000,100)},null,0);long supplierApplication=f.ApplyReturnCredit(false,supplier,100,"apply-supplier",DateTime.Today,"apply supplier credit");f.AllocateSettlement(supplierApplication,5,100,"allocate-supplier-credit");
  Check(Convert.ToInt64(f.PartyBalances().Select("party_id="+supplier)[0]["payable_cents"])==0,"Supplier credit application wrong");
  Reject(()=>new ReturnService(db,staff).Post(2,one,0,"staff-return","unauthorized",200));
  Reject(()=>new FinanceService(db,staff).ApplyReturnCredit(true,customer,1,"staff-credit",DateTime.Today,"unauthorized"));
  Check(Convert.ToInt64(shop.Returns.Receipt(first).Rows[0]["credit_cents"])==200,"Credit note missing from printable receipt");
  long before=Scalar(db,"SELECT COUNT(*) FROM return_documents"),stock=Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1");
  using(var c=db.Open())using(var q=new SQLiteCommand("CREATE TRIGGER injected_credit_failure BEFORE INSERT ON journal_entries WHEN NEW.source_kind='RETURN' BEGIN SELECT RAISE(ABORT,'failure'); END",c))q.ExecuteNonQuery();
  Reject(()=>shop.Returns.Post(1,purchase,0,"failed-credit","all changes must roll back",100));
  Check(Scalar(db,"SELECT COUNT(*) FROM return_documents")==before && Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1")==stock,"Failed credit note changed stock/header");
  using(var c=db.Open())using(var q=new SQLiteCommand("DROP TRIGGER injected_credit_failure",c))q.ExecuteNonQuery();
  using(var c=db.Open())StoreDb.VerifyIntegrity(c);
  Console.WriteLine("Return credit, anonymous beneficiary, cash evidence, supplier/customer application and rollback scenarios passed.");
 }
}
