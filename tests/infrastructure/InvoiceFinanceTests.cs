using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Threading.Tasks;
using OfflineStore.Infrastructure;
using OfflineStore.Models;
using OfflineStore.Security;
using OfflineStore.Services;
using OfflineStore.Supply;

internal static class InvoiceFinanceTests
{
    static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    static void Reject(Action action){bool failed=false;try{action();}catch{failed=true;}Check(failed,"Invalid invoice finance accepted");}
    static long Scalar(StoreDb db,string sql){using(var c=db.Open())using(var cmd=new SQLiteCommand(sql,c))return Convert.ToInt64(cmd.ExecuteScalar());}
    public static void Run(string root)
    {
        var db=new StoreDb(Path.Combine(root,"finance-invoices","store.db"));db.Initialize();
        var auth=new UserAuthService(db);auth.CreateFirstAdmin("Admin","92837465");var admin=auth.Login("admin","92837465");
        var store=new StoreService(db);store.SignIn(admin);var finance=store.Finance;
        // The zero opening is explicit, atomic, and never fabricates money.
        finance.Activate(new List<JournalLine>());
        Check(finance.IsActive && Scalar(db,"SELECT COUNT(*) FROM journal_entries")==0,"Zero opening invented a journal balance");
        Reject(()=>finance.Activate(new List<JournalLine>()));
        Reject(()=>finance.RecordExpense("EXPENSES",1,"empty-cash",DateTime.Today,"cannot spend absent cash"));
        Reject(()=>store.Journal.PostManual("ADJUSTMENT","manual-overdraft",DateTime.Today,new [] {new JournalLine {AccountCode="EXPENSES",DebitCents=100},new JournalLine {AccountCode="CASH",CreditCents=100}}));
        Check(Scalar(db,"SELECT COUNT(*) FROM journal_entries")==0,"Rejected cash expense/manual adjustment left partial financial state");
        finance.FundCash(10000,"owner-start",DateTime.Today,"owner capital injection");
        Reject(()=>finance.FundCash(10000,"owner-start",DateTime.Today,"retry"));
        Check(finance.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==0,"Owner funding incorrectly counted as profit");
        long supplier=finance.CreateParty("SUPPLIER","Vendor",null),customer=finance.CreateParty("CUSTOMER","Buyer",null);
        store.AddProduct("TEST","Item","piece",200,0);
        var voucher=new SupplyVoucherDraft {SupplierID=supplier,PaymentType="CREDIT",PaidAmountCents=400,TaxRateBps=1400};
        voucher.Lines.Add(new SupplyVoucherLine {ProductID=1,ProductUnitID=1,ItemCode="TEST",ItemName="Item",Unit="piece",UnitFactorMilli=1000,QuantityMilli=10000,UnitCostCents=100});
        voucher=store.SupplyVouchers.SaveDraft(voucher);store.SupplyVouchers.Approve(voucher.VoucherID);
        Check(Scalar(db,"SELECT avg_cost_millicents FROM products WHERE id=1")==114000,"Purchase VAT not capitalized into moving average");
        Check(Scalar(db,"SELECT SUM(total_cost_cents) FROM purchase_cost_snapshots")==1140,"Purchase cost snapshot not equal to tax-inclusive value");
        Check(Convert.ToInt64(finance.PartyBalances().Select("party_id="+supplier)[0]["payable_cents"])==740,"Supply payable wrong");
        var line=new DraftLine {ProductId=1,ProductName="Item",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=2000,QuantityMilli=2000,PriceCents=200};
        store.Post(false,null,new [] {line},customer,100);
        Check(Scalar(db,"SELECT SUM(total_cost_cents) FROM sale_cost_snapshots")==228,"COGS lost precision or capitalized VAT");
        var profit=finance.ProfitAndLoss(DateTime.Today,DateTime.Today);
        Check(profit.RevenueCents==400 && profit.CostOfSalesCents==228 && profit.NetCents==172,"Sale P&L wrong");
        Check(Convert.ToInt64(finance.PartyBalances().Select("party_id="+customer)[0]["receivable_cents"])==300,"Sale debt wrong");
        Check(Scalar(db,"SELECT SUM(debit_cents-credit_cents) FROM v_journal_posted_lines")==0,"Journal not balanced");
        long receipt=finance.Collect(customer,150,"buyer-payment",DateTime.Today,"partial payment");
        finance.AllocateSettlement(receipt,2,100,"allocate-part-1");
        Reject(()=>finance.AllocateSettlement(receipt,2,100,"allocate-part-1"));
        Reject(()=>finance.AllocateSettlement(receipt,2,60,"too-much-allocation"));
        finance.AllocateSettlement(receipt,2,50,"allocate-part-2");
        Check(Convert.ToInt64(finance.FinancialInvoices().Select("document_id=2")[0]["unallocated_invoice_cents"])==150,"Invoice payment allocation wrong");
        Check(finance.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==172,"Payment allocation changed profit");
        long saleLine=Scalar(db,"SELECT id FROM document_lines WHERE document_id=2");
        var saleReturn=new [] {new ReturnQuantity {OriginalLineId=saleLine,QuantityMilli=1000}};
        var returns=store.Returns;var quoted=returns.Preview(2,saleReturn,50);
        Check(quoted.TotalCents==200 && quoted.CostCents==114 && quoted.CashCents==150,"Partial sale return quote wrong");
        string firstReturn=returns.Post(2,saleReturn,50,"sale-return-1","partial sale return");
        Check(Convert.ToInt64(returns.Receipt(firstReturn).Rows[0]["cash_cents"])==150 && returns.List(DateTime.Today,DateTime.Today).Rows.Count==1,"Return receipt/history does not reflect committed refund");
        Check(Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1")==9000,"Sale return not restored to available stock");
        Check(finance.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==86,"Sale return profit reversal wrong");
        Reject(()=>returns.Post(2,saleReturn,50,"sale-return-1","duplicate"));
        Reject(()=>returns.Preview(2,new [] {new ReturnQuantity {OriginalLineId=saleLine,QuantityMilli=2000}},0));
        var resale=new DraftLine {ProductId=1,ProductName="Item",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=9000,QuantityMilli=9000,PriceCents=200};
        string resaleCode=store.Post(false,null,new [] {resale});
        long resaleLine=Scalar(db,"SELECT l.id FROM document_lines l JOIN documents d ON d.id=l.document_id WHERE d.doc_code='"+resaleCode+"'");
        returns.Post(3,new [] {new ReturnQuantity {OriginalLineId=resaleLine,QuantityMilli=9000}},0,"resale-full-return","returned stock can fund a real sale");
        Check(Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1")==9000,"Returned stock could not fund a real sale and return");
        long purchaseLine=Scalar(db,"SELECT id FROM document_lines WHERE document_id=1");
        var purchaseReturn=new [] {new ReturnQuantity {OriginalLineId=purchaseLine,QuantityMilli=1000}};
        returns.Post(1,purchaseReturn,114,"purchase-return-1","supplier return debt reduction");
        Check(Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1")==8000,"Purchase return stock not reduced");
        Check(Convert.ToInt64(finance.PartyBalances().Select("party_id="+supplier)[0]["payable_cents"])==626,"Purchase return payable wrong");
        store.Post(true,supplier,new [] {new DraftLine {ProductId=1,ProductName="Item",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=10000,QuantityMilli=10000,PriceCents=200}});
        var varianceQuote=returns.Preview(1,purchaseReturn,114);
        Check(varianceQuote.TotalCents==114 && varianceQuote.CostCents==162,"Supplier return did not use current average with original refund basis");
        returns.Post(1,purchaseReturn,114,"purchase-return-variance","different current cost");
        var afterVariance=finance.ProfitAndLoss(DateTime.Today,DateTime.Today);
        Check(afterVariance.NetCents==38 && afterVariance.SalesCents==200 && afterVariance.OtherIncomeCents==-48,"Purchase return variance not booked separately from net sales");
        long beforeAverage=Scalar(db,"SELECT avg_cost_millicents FROM products WHERE id=1");
        long beforeReturns=Scalar(db,"SELECT COUNT(*) FROM return_documents");
        using(var c=db.Open())using(var cmd=new SQLiteCommand("CREATE TRIGGER test_return_fail BEFORE INSERT ON journal_entries WHEN NEW.source_kind='RETURN' BEGIN SELECT RAISE(ABORT,'failure'); END",c))cmd.ExecuteNonQuery();
        Reject(()=>returns.Post(2,saleReturn,100,"interrupted-return","must roll back"));
        Check(Scalar(db,"SELECT COUNT(*) FROM return_documents")==beforeReturns && Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1")==17000 && Scalar(db,"SELECT avg_cost_millicents FROM products WHERE id=1")==beforeAverage,"Failed return left partial stock or header");
        using(var c=db.Open())using(var cmd=new SQLiteCommand("DROP TRIGGER test_return_fail",c))cmd.ExecuteNonQuery();
        long docs=Scalar(db,"SELECT COUNT(*) FROM documents"),stock=Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1");
        using(var c=db.Open())using(var cmd=new SQLiteCommand("CREATE TRIGGER test_invoice_fail BEFORE INSERT ON journal_entries WHEN NEW.source_kind='SALE' BEGIN SELECT RAISE(ABORT,'failure'); END",c))cmd.ExecuteNonQuery();
        Reject(()=>store.Post(false,null,new [] {line}));
        Check(Scalar(db,"SELECT COUNT(*) FROM documents")==docs && Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=1")==stock,"Accounting failure left invoice/stock changes");
        using(var c=db.Open())using(var cmd=new SQLiteCommand("DROP TRIGGER test_invoice_fail",c))cmd.ExecuteNonQuery();
        store.AddProduct("WEIGHT","Weighable","kg",100,100);
        using(var c=db.Open())using(var cmd=new SQLiteCommand("UPDATE products SET is_countable=0 WHERE sku='WEIGHT'",c))cmd.ExecuteNonQuery();
        var weightLine=new DraftLine {ProductId=2,ProductName="Weighable",ProductUnitId=2,UnitName="kg",UnitFactorMilli=1000,EnteredQuantityMilli=1000,QuantityMilli=1000,PriceCents=100};
        store.Post(true,supplier,new [] {weightLine});string weightSale=store.Post(false,null,new [] {weightLine});
        long weightDoc=Scalar(db,"SELECT id FROM documents WHERE doc_code='"+weightSale+"'");
        long weightSource=Scalar(db,"SELECT id FROM document_lines WHERE document_id="+weightDoc);
        returns.Post(weightDoc,new [] {new ReturnQuantity {OriginalLineId=weightSource,QuantityMilli=1}},0,"tiny-zero-return","one gram rounds to zero");
        Check(Scalar(db,"SELECT qty_milli FROM v_stock WHERE product_id=2")==1,"Zero-money quantity return rejected or lost");
        Check(Scalar(db,"SELECT COUNT(*) FROM return_documents WHERE reference='tiny-zero-return' AND journal_entry_id IS NULL")==1,"Zero return invented a money journal");
        returns.Post(weightDoc,new [] {new ReturnQuantity {OriginalLineId=weightSource,QuantityMilli=4}},0,"tiny-rounded-return","cumulative five grams");
        Check(Scalar(db,"SELECT SUM(amount_cents) FROM return_lines WHERE original_line_id="+weightSource)==1,"Cumulative tiny returns lost rounded piastre");
        using(var c=db.Open())StoreDb.VerifyIntegrity(c);
        // Failure between opening and activation rolls BOTH back; a retry remains possible.
        var activationDb=new StoreDb(Path.Combine(root,"finance-activation","store.db"));activationDb.Initialize();
        var activationAuth=new UserAuthService(activationDb);activationAuth.CreateFirstAdmin("Admin","92837465");var actor=activationAuth.Login("admin","92837465");
        var activation=new FinanceService(activationDb,actor);
        var openingStore=new StoreService(activationDb);openingStore.SignIn(actor);
        long openingSupplier=activation.CreateParty("SUPPLIER","Opening vendor",null);
        openingStore.AddProduct("OPEN","Opening item","piece",200,0);
        openingStore.Post(true,openingSupplier,new [] {new DraftLine {ProductId=1,ProductName="Opening item",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,QuantityMilli=1000,EnteredQuantityMilli=1000,PriceCents=100}});
        Reject(()=>activation.Activate(new [] {new JournalLine {AccountCode="CASH",DebitCents=10000},new JournalLine {AccountCode="OPENING_EQUITY",CreditCents=10000}}));
        Check(!activation.IsActive,"Mismatched opening inventory activated");
        var opening=new [] {new JournalLine {AccountCode="CASH",DebitCents=10000},new JournalLine {AccountCode="INVENTORY",DebitCents=100},new JournalLine {AccountCode="OPENING_EQUITY",CreditCents=10100}};
        using(var c=activationDb.Open())using(var cmd=new SQLiteCommand("CREATE TRIGGER test_activation_fail BEFORE INSERT ON finance_activation BEGIN SELECT RAISE(ABORT,'interrupted'); END",c))cmd.ExecuteNonQuery();
        Reject(()=>activation.Activate(opening));
        Check(Scalar(activationDb,"SELECT COUNT(*) FROM journal_entries")==0 && !activation.IsActive,"Interrupted activation stranded opening");
        using(var c=activationDb.Open())using(var cmd=new SQLiteCommand("DROP TRIGGER test_activation_fail",c))cmd.ExecuteNonQuery();
        activation.Activate(opening);Check(activation.IsActive,"Activation retry failed");
        Reject(()=>activation.Activate(opening));
        activation.RecordExpense("EXPENSES",10000,"spend-opening",DateTime.Today,"documented expense");
        var cashSaleLine=new DraftLine {ProductId=1,ProductName="Opening item",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=1000,QuantityMilli=1000,PriceCents=200};
        openingStore.Post(false,null,new [] {cashSaleLine});
        activation.RecordExpense("EXPENSES",200,"spend-sale",DateTime.Today,"documented expense");
        long cashSaleSource=Scalar(activationDb,"SELECT id FROM document_lines WHERE document_id=2");
        var cashReturn=new [] {new ReturnQuantity {OriginalLineId=cashSaleSource,QuantityMilli=1000}};
        Reject(()=>openingStore.Returns.Post(2,cashReturn,0,"no-refund-cash","no cash available"));
        Check(Scalar(activationDb,"SELECT COUNT(*) FROM return_documents")==0,"Rejected cash refund left partial return");
        Reject(()=>openingStore.Post(true,openingSupplier,new [] {cashSaleLine}));
        Check(Scalar(activationDb,"SELECT COUNT(*) FROM documents")==2 && Scalar(activationDb,"SELECT qty_milli FROM v_stock WHERE product_id=1")==0,"Cash purchase rejection left partial stock or invoice");
        activation.FundCash(200,"refund-funding",DateTime.Today,"owner refund funding");
        openingStore.Returns.Post(2,cashReturn,0,"funded-refund","refund after cash funding");
        Check(activation.CashBalanceCents()==0,"Refund cash control is wrong");
        activation.FundCash(100,"concurrent-funding",DateTime.Today,"cash concurrency fixture");
        var expenses=new [] {"concurrent-expense-1","concurrent-expense-2"};
        var tasks=Array.ConvertAll(expenses,key=>Task.Run(()=>{
            try{activation.RecordExpense("EXPENSES",75,key,DateTime.Today,"competing expense");return true;}
            catch(InvalidOperationException){return false;}
            catch(SQLiteException){return false;}
        }));Task.WaitAll(tasks);
        Check((tasks[0].Result?1:0)+(tasks[1].Result?1:0)==1 && activation.CashBalanceCents()==25,"Concurrent expenses overspent cash");
        using(var c=activationDb.Open())StoreDb.VerifyIntegrity(c);
        Console.WriteLine("Finance invoice, VAT, cutover and rollback scenarios passed.");
    }
}
