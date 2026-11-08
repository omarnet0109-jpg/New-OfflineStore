using System;
using System.Data.SQLite;
using OfflineStore.Infrastructure;
using OfflineStore.Security;
using OfflineStore.Services;
using OfflineStore.Models;

internal static class FinanceTests
{
    static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
    static void Reject(Action action) { bool rejected=false;try{action();}catch{rejected=true;}Check(rejected,"Finance operation unexpectedly accepted"); }
    public static void Run(StoreDb db,UserSession admin,UserSession cashier)
    {
        var service=new FinanceService(db,admin);
        service.Activate(new [] {new JournalLine {AccountCode="CASH",DebitCents=12000},new JournalLine {AccountCode="OPENING_EQUITY",CreditCents=12000}});
        using(var c=db.Open())using(var cmd=new SQLiteCommand("SELECT SUM(debit_cents-credit_cents) FROM v_journal_posted_lines WHERE account_code='CASH'",c))
            Check(Convert.ToInt64(cmd.ExecuteScalar())==12000,"Existing foundation balances duplicated instead of reconciled");
        long customer=service.CreateParty("CUSTOMER","Customer","123");
        long supplier=service.CreateParty("SUPPLIER","Supplier",null);
        // Existing opening is explicitly reconciled to cash 12000. Explicit adjustment adds reconciled receivable/payable.
        new JournalService(db,admin).PostManual("ADJUSTMENT","debts",DateTime.Today,new [] {
            new JournalLine {AccountCode="RECEIVABLES",PartyId=customer,DebitCents=1000},
            new JournalLine {AccountCode="PAYABLES",PartyId=supplier,CreditCents=600},
            new JournalLine {AccountCode="OPENING_EQUITY",CreditCents=400}});
        long receipt=service.Collect(customer,1500,"receipt-1",DateTime.Today,"cash receipt");
        var b=service.PartyBalances();
        var customerRow=b.Select("party_id="+customer)[0];
        Check(Convert.ToInt64(customerRow["receivable_cents"])==0 && Convert.ToInt64(customerRow["customer_advance_cents"])==500,"Overpayment not split into independent advance");
        Reject(()=>service.Collect(customer,1500,"receipt-1",DateTime.Today,"retry"));
        Reject(()=>service.Collect(supplier,100,"wrong-party",DateTime.Today,"wrong"));
        Reject(()=>service.Pay(supplier,0,"zero",DateTime.Today,"zero"));
        Reject(()=>service.Pay(supplier,100000,"insufficient-cash",DateTime.Today,"too much cash"));
        service.Pay(supplier,800,"payment-1",DateTime.Today,"cash payment");
        var supplierRow=service.PartyBalances().Select("party_id="+supplier)[0];
        Check(Convert.ToInt64(supplierRow["payable_cents"])==0 && Convert.ToInt64(supplierRow["supplier_advance_cents"])==200,"Supplier advance is wrong");
        string category=service.CreateExpenseCategory("Rent");
        service.RecordExpense(category,123,"expense-1",DateTime.Today,"November rent");
        Check(service.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==-123,"Expense missing from profit report");
        Reject(()=>new FinanceService(db,cashier).Collect(customer,100,"unauthorized",DateTime.Today,"bad"));
        Reject(()=>service.RecordExpense("COGS",1,"wrong-expense",DateTime.Today,"bad"));
        Reject(()=>service.RecordExpense(category,1,"old-date",DateTime.Today.AddDays(-1),"bad"));
        using(var c=db.Open()) using(var cmd=new SQLiteCommand("SELECT COUNT(*) FROM finance_operations",c))
            Check(Convert.ToInt64(cmd.ExecuteScalar())==3,"Rejected operation left a partial payment");
        // Inject failure after journal insertion: metadata trigger must roll everything back.
        using(var c=db.Open()) using(var cmd=new SQLiteCommand("CREATE TRIGGER test_payment_fail BEFORE INSERT ON finance_operations BEGIN SELECT RAISE(ABORT,'injected'); END",c))cmd.ExecuteNonQuery();
        Reject(()=>service.Collect(customer,1,"interrupted",DateTime.Today,"failure"));
        using(var c=db.Open()) using(var cmd=new SQLiteCommand("SELECT COUNT(*) FROM journal_entries WHERE source_key='interrupted'",c))
            Check(Convert.ToInt64(cmd.ExecuteScalar())==0,"Failure left orphaned journal entry");
        using(var c=db.Open()) using(var cmd=new SQLiteCommand("DROP TRIGGER test_payment_fail",c))cmd.ExecuteNonQuery();
        new JournalService(db,admin).PostManual("ADJUSTMENT","new-invoiced-debts",DateTime.Today,new [] {
            new JournalLine {AccountCode="RECEIVABLES",PartyId=customer,DebitCents=500},
            new JournalLine {AccountCode="PAYABLES",PartyId=supplier,CreditCents=200},
            new JournalLine {AccountCode="OPENING_EQUITY",CreditCents=300}});
        Reject(()=>service.ApplyAdvance(true,customer,501,"too-much",DateTime.Today,"over advance"));
        service.ApplyAdvance(true,customer,500,"customer-offset",DateTime.Today,"apply prepaid amount");
        service.ApplyAdvance(false,supplier,200,"supplier-offset",DateTime.Today,"apply supplier advance");
        Reject(()=>service.ApplyAdvance(true,customer,500,"customer-offset",DateTime.Today,"retry"));
        Check(Convert.ToInt64(service.PartyBalances().Select("party_id="+customer)[0]["customer_advance_cents"])==0,"Customer advance not consumed exactly once");
        Check(Convert.ToInt64(service.PartyBalances().Select("party_id="+supplier)[0]["supplier_advance_cents"])==0,"Supplier advance not consumed exactly once");
        Check(service.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==-123,"Advance application changed profit");
        var adminShop=new StoreService(db);adminShop.SignIn(admin);
        adminShop.AddProduct("STAFF","Staff product","piece",10,10);
        adminShop.Post(true,supplier,new [] {new DraftLine {ProductId=1,ProductName="Staff product",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=2000,QuantityMilli=2000,PriceCents=10}});
        var employeeShop=new StoreService(db);employeeShop.SignIn(cashier);
        var cashLine=new DraftLine {ProductId=1,ProductName="Staff product",ProductUnitId=1,UnitName="piece",UnitFactorMilli=1000,EnteredQuantityMilli=1000,QuantityMilli=1000,PriceCents=10};
        employeeShop.Post(false,null,new [] {cashLine});
        Reject(()=>employeeShop.Post(false,null,new [] {cashLine},customer,0));
        Check(service.ProfitAndLoss(DateTime.Today,DateTime.Today).NetCents==-123,"Normal employee cash sale changed zero-margin calculation or denied credit left money");
        using(var c=db.Open())StoreDb.VerifyIntegrity(c);
    }
}
