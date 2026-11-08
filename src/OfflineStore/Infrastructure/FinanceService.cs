using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using OfflineStore.Security;
using OfflineStore.Models;

namespace OfflineStore.Infrastructure
{
    public sealed class ProfitAndLoss
    {
        public long RevenueCents { get; internal set; }
        public long SalesCents { get; internal set; }
        public long OtherIncomeCents { get { return checked(RevenueCents-SalesCents); } }
        public long CostOfSalesCents { get; internal set; }
        public long ExpensesCents { get; internal set; }
        public long NetCents { get { return checked(RevenueCents-CostOfSalesCents-ExpensesCents); } }
    }

    // Reconciled opening balances are explicit. Never infer historic debts from invoice totals.
    public sealed class OpeningReview
    {
        internal string Database;
        internal long Actor,LastDocument,LastJournal;
        internal OpeningReview() { }
    }
    public sealed class FinanceService
    {
        private readonly StoreDb _db;
        private readonly UserSession _actor;
        public FinanceService(StoreDb db,UserSession actor) { _db=db;_actor=actor; }
        private void RequireAdmin()
        {
            if(!new UserAuthService(_db).Authorize(_actor,"USERS"))
                throw new UnauthorizedAccessException("حركة الأموال وإدارة الحسابات للمدير فقط.");
        }
        private void RequireReports()
        {
            if(!new UserAuthService(_db).Authorize(_actor,"REPORTS"))
                throw new UnauthorizedAccessException("لا توجد صلاحية تقارير الحسابات.");
        }
        private static SQLiteCommand Sql(SQLiteConnection c,SQLiteTransaction tx,string sql,params object[] args)
        {
            var cmd=new SQLiteCommand(sql,c,tx);
            for(int i=0;i<args.Length;i++)cmd.Parameters.AddWithValue("@p"+i,args[i]??DBNull.Value);
            return cmd;
        }
        private static DataTable Read(SQLiteConnection c,SQLiteTransaction tx,string sql,params object[] args)
        {
            using(var cmd=Sql(c,tx,sql,args))using(var reader=cmd.ExecuteReader())
            {var table=new DataTable();table.Locale=CultureInfo.InvariantCulture;table.Load(reader);return table;}
        }
        internal static void ValidateDate(SQLiteConnection c,SQLiteTransaction tx,DateTime date)
        {
            if(date.Date>DateTime.Today)throw new ArgumentException("لا يمكن تسجيل حركة بتاريخ مستقبلي.");
            using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM finance_activation"))
                if(Convert.ToInt64(cmd.ExecuteScalar())!=1)
                    throw new InvalidOperationException("سجل أرصدة افتتاحية مراجعة قبل حركة الأموال. الفواتير القديمة لا تثبت المدفوع أو المتبقي.");
            using(var cmd=Sql(c,tx,"SELECT MAX(d) FROM (SELECT entry_date AS d FROM journal_entries WHERE state='POSTED' UNION ALL SELECT activated_date AS d FROM finance_activation)"))
            {
                string latest=Convert.ToString(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
                if(string.CompareOrdinal(date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),latest)<0)
                    throw new ArgumentException("تاريخ الحركة يسبق آخر قيد معتمد. استخدم تسوية موثقة بدل تغيير ترتيب الأرصدة.");
            }
        }
        public bool IsActive
        { get { RequireReports();using(var c=_db.Open())return IsActiveWithin(c,null); } }
        internal static bool IsActiveWithin(SQLiteConnection c,SQLiteTransaction tx)
        {using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM finance_activation"))return Convert.ToInt64(cmd.ExecuteScalar())==1;}
        private static long StockValueWithin(SQLiteConnection c,SQLiteTransaction tx)
        {
            if(InventoryValueService.Enabled(c,tx))return InventoryValueService.Total(c,tx);
            long total=0;
            using(var cmd=Sql(c,tx,"SELECT s.qty_milli,p.avg_cost_millicents FROM v_stock s JOIN products p ON p.id=s.product_id WHERE s.qty_milli>0"))
            using(var r=cmd.ExecuteReader())while(r.Read())total=checked(total+Amounts.CostTotal(r.GetInt64(0),r.GetInt64(1)));
            return total;
        }
        public long OpeningInventoryValueCents()
        {RequireReports();using(var c=_db.Open())return StockValueWithin(c,null);}
        private static long LastPosted(SQLiteConnection c,SQLiteTransaction tx,string table)
        {using(var cmd=Sql(c,tx,"SELECT COALESCE(MAX(id),0) FROM "+table+" WHERE state='POSTED'"))return Convert.ToInt64(cmd.ExecuteScalar());}
        public OpeningReview CaptureOpeningReview()
        {
            RequireAdmin();using(var c=_db.Open())using(var tx=c.BeginTransaction())
                return new OpeningReview {Database=_db.DatabasePath,Actor=_actor.Id,LastDocument=LastPosted(c,tx,"documents"),LastJournal=LastPosted(c,tx,"journal_entries")};
        }
        public long? Activate(IList<JournalLine> reviewedBalances, OpeningReview review = null, bool approveLegacyResults = false)
        {
            RequireAdmin();
            if(reviewedBalances==null)throw new ArgumentNullException("reviewedBalances");
            review=review??CaptureOpeningReview();
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                if(IsActiveWithin(c,tx))throw new InvalidOperationException("الحسابات مفعلة بالفعل.");
                if(review.Actor!=_actor.Id || !string.Equals(review.Database,_db.DatabasePath,StringComparison.OrdinalIgnoreCase)
                    || review.LastDocument!=LastPosted(c,tx,"documents") || review.LastJournal!=LastPosted(c,tx,"journal_entries"))
                    throw new InvalidOperationException("تغيرت الفواتير أو القيود بعد مراجعة الافتتاح. حدّث البيانات وراجع الأرصدة ثم أعد المحاولة.");
                using(var future=Sql(c,tx,"SELECT COUNT(*) FROM journal_entries WHERE state='POSTED' AND entry_date>@p0",DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)))
                    if(Convert.ToInt64(future.ExecuteScalar())!=0)throw new InvalidOperationException("الدفتر القديم يتضمن تاريخًا مستقبليًا؛ يلزم مراجعته قبل التفعيل دون اختلاق أرصدة.");
                long inventory=0;
                foreach(var line in reviewedBalances)
                {
                    if(line==null || string.IsNullOrWhiteSpace(line.AccountCode) || line.DebitCents<0 || line.CreditCents<0 || (line.DebitCents==0)==(line.CreditCents==0))throw new ArgumentException("سطر افتتاحي غير صالح.");
                    using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM journal_accounts WHERE code=@p0 AND kind IN ('ASSET','LIABILITY','EQUITY')",line.AccountCode.Trim().ToUpperInvariant()))
                        if(Convert.ToInt64(cmd.ExecuteScalar())!=1)throw new ArgumentException("الافتتاح للأصول والالتزامات وحقوق الملكية فقط.");
                    if(string.Equals(line.AccountCode.Trim(),"CASH",StringComparison.OrdinalIgnoreCase) && line.CreditCents>0)throw new ArgumentException("رصيد الخزنة الافتتاحي لا يمكن أن يكون سالبًا.");
                    if(string.Equals(line.AccountCode==null?null:line.AccountCode.Trim(),"INVENTORY",StringComparison.OrdinalIgnoreCase))
                        inventory=checked(inventory+line.DebitCents-line.CreditCents);
                }
                if(inventory!=StockValueWithin(c,tx))throw new ArgumentException("رصيد المخزون الافتتاحي لا يطابق قيمة المخزون بمتوسطاته المسجلة. راجع سطر المخزون قبل التفعيل.");
                long? openingId=null;long existing;
                using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM journal_entries WHERE state='POSTED'"))existing=Convert.ToInt64(cmd.ExecuteScalar());
                if(existing==0)
                {
                    if(reviewedBalances.Count>0)
                        openingId=JournalService.PostWithin(c,tx,"OPENING","opening:"+Guid.NewGuid().ToString("N"),DateTime.Today,_actor.Id,reviewedBalances);
                }
                else
                {
                    using(var cmd=Sql(c,tx,"SELECT id FROM journal_entries WHERE state='POSTED' AND source_kind='OPENING'"))
                    {var value=cmd.ExecuteScalar();openingId=value==null?(long?)null:Convert.ToInt64(value);}
                    using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM v_journal_posted_lines WHERE source_kind NOT IN ('OPENING','ADJUSTMENT')"))
                        if(Convert.ToInt64(cmd.ExecuteScalar())!=0)throw new InvalidOperationException("الدفتر السابق يتضمن نتائج أعمال؛ يلزم ترحيل مراجع دون إعادة كتابة التاريخ المالي.");
                    bool hasResults;
                    using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM (SELECT account_code,party_id FROM v_journal_posted_lines WHERE kind IN ('INCOME','EXPENSE') GROUP BY account_code,party_id HAVING SUM(debit_cents-credit_cents)<>0)"))hasResults=Convert.ToInt64(cmd.ExecuteScalar())!=0;
                    if(hasResults && !approveLegacyResults)throw new InvalidOperationException("توجد نتائج أعمال قبل التفعيل. راجعها واعتمد إقفالها صراحةً ضمن الأرصدة الافتتاحية؛ لن تُرحل تلقائيًا.");
                    var delta=new Dictionary<string,JournalLine>();
                    foreach(var line in reviewedBalances)
                    {
                        if(string.IsNullOrWhiteSpace(line.AccountCode) || line.DebitCents<0 || line.CreditCents<0 || (line.DebitCents==0)==(line.CreditCents==0))throw new ArgumentException("سطر افتتاحي غير صالح.");
                        string account=line.AccountCode.Trim().ToUpperInvariant();
                        using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM journal_accounts WHERE code=@p0 AND kind IN ('ASSET','LIABILITY','EQUITY')",account))
                            if(Convert.ToInt64(cmd.ExecuteScalar())!=1)throw new ArgumentException("الافتتاح للأصول والالتزامات وحقوق الملكية فقط.");
                        AddDelta(delta,account,line.PartyId,checked(line.DebitCents-line.CreditCents));
                    }
                    using(var cmd=Sql(c,tx,"SELECT account_code,party_id,SUM(debit_cents)-SUM(credit_cents) FROM v_journal_posted_lines GROUP BY account_code,party_id"))
                    using(var r=cmd.ExecuteReader())while(r.Read())AddDelta(delta,r.GetString(0),r.IsDBNull(1)?(long?)null:r.GetInt64(1),checked(-r.GetInt64(2)));
                    var adjustment=new List<JournalLine>();
                    foreach(var line in delta.Values)
                    {
                        long net=line.DebitCents;if(net==0)continue;
                        adjustment.Add(new JournalLine {AccountCode=line.AccountCode,PartyId=line.PartyId,DebitCents=net>0?net:0,CreditCents=net<0?checked(-net):0});
                    }
                    if(adjustment.Count>0)
                    {
                        long difference=JournalService.PostWithin(c,tx,"ADJUSTMENT","cutover:"+Guid.NewGuid().ToString("N"),DateTime.Today,_actor.Id,adjustment);
                        if(hasResults)using(var audit=Sql(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('LEGACY_RESULTS_CLOSED',@p0,@p1,@p2)",difference,_actor.Id,DateTime.UtcNow.ToString("o")))audit.ExecuteNonQuery();
                    }
                }
                using(var manifest=Sql(c,tx,"INSERT INTO finance_legacy_documents SELECT id FROM documents WHERE state='POSTED'"))manifest.ExecuteNonQuery();
                using(var cmd=Sql(c,tx,"INSERT INTO finance_activation VALUES(1,@p0,(SELECT COALESCE(MAX(id),0) FROM documents WHERE state='POSTED'),@p1,@p2)",openingId,DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),_actor.Id))cmd.ExecuteNonQuery();
                using(var boundary=Sql(c,tx,"INSERT INTO finance_activation_boundary VALUES(1,(SELECT COALESCE(MAX(id),0) FROM journal_entries WHERE state='POSTED'))"))boundary.ExecuteNonQuery();
                using(var cmd=Sql(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('FINANCE_ACTIVATED',@p0,@p1,@p2)",openingId??0,_actor.Id,DateTime.UtcNow.ToString("o")))cmd.ExecuteNonQuery();
                if(InventoryValueService.Enabled(c,tx))InventoryValueService.Verify(c,tx);
                tx.Commit();return openingId;
            }
        }
        private static void AddDelta(IDictionary<string,JournalLine> lines,string account,long? party,long net)
        {
            string key=account+":"+(party.HasValue?party.Value.ToString(CultureInfo.InvariantCulture):"");
            JournalLine line;if(!lines.TryGetValue(key,out line)){line=new JournalLine {AccountCode=account,PartyId=party};lines.Add(key,line);}
            line.DebitCents=checked(line.DebitCents+net);
        }
        public DataTable LegacyResults()
        {RequireReports();using(var c=_db.Open())return Read(c,null,"SELECT account_code,party_id,SUM(debit_cents-credit_cents) AS net FROM v_journal_posted_lines WHERE kind IN ('INCOME','EXPENSE') GROUP BY account_code,party_id HAVING net<>0");}
        public DataTable ReviewedOpeningLines()
        {RequireReports();using(var c=_db.Open())return Read(c,null,@"SELECT account_code,party_id,SUM(net) AS net FROM (
            SELECT CASE WHEN kind IN ('INCOME','EXPENSE') THEN 'OPENING_EQUITY' ELSE account_code END AS account_code,
             CASE WHEN kind IN ('INCOME','EXPENSE') THEN NULL ELSE party_id END AS party_id,debit_cents-credit_cents AS net
            FROM v_journal_posted_lines) GROUP BY account_code,party_id HAVING SUM(net)<>0");}
        // Called by stock posting inside its caller-owned transaction; staff permissions stay on the source operation.
        internal static void PostInvoiceWithin(SQLiteConnection c,SQLiteTransaction tx,long documentId,bool purchase,
            long? party,long total,long paid,long cost,long actor)
        {
            if(!IsActiveWithin(c,tx))
            {
                return;
            }
            if(total<0 || paid<0 || paid>total || cost<0 || (paid<total && !party.HasValue))throw new ArgumentException("تسوية الفاتورة غير صحيحة.");
            ValidateDate(c,tx,DateTime.Today);
            if(purchase)RequireCashWithin(c,tx,paid);
            var lines=new List<JournalLine>();
            if(total>0)
                lines.Add(new JournalLine {AccountCode=purchase?"INVENTORY":"SALES",DebitCents=purchase?total:0,CreditCents=purchase?0:total});
            if(paid>0)lines.Add(new JournalLine {AccountCode="CASH",DebitCents=purchase?0:paid,CreditCents=purchase?paid:0});
            long debt=checked(total-paid);
            if(debt>0)lines.Add(new JournalLine {AccountCode=purchase?"PAYABLES":"RECEIVABLES",PartyId=party,DebitCents=purchase?0:debt,CreditCents=purchase?debt:0});
            if(!purchase && cost>0)
            {lines.Add(new JournalLine {AccountCode="COGS",DebitCents=cost});lines.Add(new JournalLine {AccountCode="INVENTORY",CreditCents=cost});}
            if(lines.Count==0)return; // A zero-value document has no monetary movement.
            JournalService.PostWithin(c,tx,purchase?"PURCHASE":"SALE","document:"+documentId.ToString(CultureInfo.InvariantCulture),DateTime.Today,actor,lines,documentId);
        }
        internal static void RequireCashWithin(SQLiteConnection c,SQLiteTransaction tx,long amount)
        {
            if(amount<0)throw new ArgumentException("مبلغ نقدي غير صالح.");
            if(amount==0)return;
            using(var cmd=Sql(c,tx,"SELECT COALESCE(SUM(debit_cents-credit_cents),0) FROM v_journal_posted_lines WHERE account_code='CASH'"))
                if(Convert.ToInt64(cmd.ExecuteScalar())<amount)throw new InvalidOperationException("رصيد الخزنة لا يكفي. راجع الافتتاح أو سجل تمويلًا نقديًا موثقًا من رأس المال قبل السداد أو رد النقد.");
        }
        public long CashBalanceCents()
        {
            RequireReports();using(var c=_db.Open())using(var cmd=Sql(c,null,"SELECT COALESCE(SUM(debit_cents-credit_cents),0) FROM v_journal_posted_lines WHERE account_code='CASH'"))return Convert.ToInt64(cmd.ExecuteScalar());
        }
        public long FundCash(long amount,string reference,DateTime date,string memo)
        {
            RequireAdmin();ValidateInput(amount,reference,memo);
            if(reference.Trim().Length>112)throw new ArgumentException("مرجع تمويل الخزنة لا يزيد على 112 حرفًا.");
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                ValidateDate(c,tx,date);
                long id=JournalService.PostWithin(c,tx,"ADJUSTMENT","capital:"+reference.Trim(),date,_actor.Id,new [] {
                    new JournalLine {AccountCode="CASH",DebitCents=amount},new JournalLine {AccountCode="CAPITAL",CreditCents=amount}});
                using(var cmd=Sql(c,tx,"INSERT INTO cash_fundings(entry_id,amount_cents,memo) VALUES(@p0,@p1,@p2)",id,amount,memo.Trim()))cmd.ExecuteNonQuery();
                tx.Commit();return id;
            }
        }
        public bool InventoryReviewRequired
        {get {RequireReports();using(var c=_db.Open())return InventoryValueService.Enabled(c) && InventoryValueService.NeedsReview(c);}}
        public long InventoryBookCents()
        {RequireReports();using(var c=_db.Open())return InventoryValueService.Book(c,null);}
        public void ReviewLegacyInventory(OpeningReview review)
        {
            RequireAdmin();if(review==null)throw new ArgumentNullException("review");
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                ValidateDate(c,tx,DateTime.Today);
                if(review.Actor!=_actor.Id || !string.Equals(review.Database,_db.DatabasePath,StringComparison.OrdinalIgnoreCase) || review.LastDocument!=LastPosted(c,tx,"documents") || review.LastJournal!=LastPosted(c,tx,"journal_entries"))throw new InvalidOperationException("تغيرت البيانات بعد المراجعة. حدّث الأرصدة وراجع فرق المخزون مجددًا.");
                if(!InventoryValueService.NeedsReview(c,tx))throw new InvalidOperationException("فرق المخزون تمت مراجعته بالفعل.");
                long difference=checked(InventoryValueService.Total(c,tx)-InventoryValueService.Book(c,tx));
                long entry=0;
                if(difference!=0)entry=JournalService.PostWithin(c,tx,"ADJUSTMENT","inventory-review:"+Guid.NewGuid().ToString("N"),DateTime.Today,_actor.Id,new [] {
                    new JournalLine {AccountCode="INVENTORY",DebitCents=difference>0?difference:0,CreditCents=difference<0?checked(-difference):0},
                    new JournalLine {AccountCode="INVENTORY_VARIANCE",DebitCents=difference<0?checked(-difference):0,CreditCents=difference>0?difference:0}});
                using(var q=Sql(c,tx,"UPDATE inventory_valuation_state SET review_required=0 WHERE singleton=1 AND review_required=1"))q.ExecuteNonQuery();
                using(var q=Sql(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('LEGACY_INVENTORY_REVIEWED',@p0,@p1,@p2)",entry,_actor.Id,DateTime.UtcNow.ToString("o")))q.ExecuteNonQuery();
                InventoryValueService.Verify(c,tx);tx.Commit();
            }
        }
        public long CreateParty(string type,string name,string phone)
        {
            RequireAdmin();
            if((type!="CUSTOMER" && type!="SUPPLIER" && type!="BOTH") || string.IsNullOrWhiteSpace(name) || name.Trim().Length>200 || (phone!=null && phone.Length>100))
                throw new ArgumentException("نوع الطرف واسمه وبياناته غير صحيحة.");
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                using(var cmd=Sql(c,tx,"INSERT INTO parties(party_type,name,phone,created_utc) VALUES(@p0,@p1,@p2,@p3)",type,name.Trim(),phone,DateTime.UtcNow.ToString("o")))cmd.ExecuteNonQuery();
                long id;using(var cmd=Sql(c,tx,"SELECT last_insert_rowid()"))id=Convert.ToInt64(cmd.ExecuteScalar());
                using(var cmd=Sql(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('PARTY_CREATED',@p0,@p1,@p2)",id,_actor.Id,DateTime.UtcNow.ToString("o")))cmd.ExecuteNonQuery();
                tx.Commit();return id;
            }
        }
        public string CreateExpenseCategory(string name)
        {
            RequireAdmin();
            if(string.IsNullOrWhiteSpace(name) || name.Trim().Length>120)throw new ArgumentException("اسم تصنيف المصروف مطلوب، حتى 120 حرفًا.");
            string code="EXP_"+Guid.NewGuid().ToString("N").ToUpperInvariant();
            using(var c=_db.Open())using(var cmd=Sql(c,null,"INSERT INTO journal_accounts(code,name,kind) VALUES(@p0,@p1,'EXPENSE')",code,name.Trim()))cmd.ExecuteNonQuery();
            return code;
        }
        public long Collect(long partyId,long amount,string reference,DateTime date,string memo)
        {return Settle(true,partyId,amount,reference,date,memo);}
        public long Pay(long partyId,long amount,string reference,DateTime date,string memo)
        {return Settle(false,partyId,amount,reference,date,memo);}
        private long Settle(bool customer,long partyId,long amount,string reference,DateTime date,string memo)
        {
            RequireAdmin();ValidateInput(amount,reference,memo);
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                ValidateDate(c,tx,date);
                if(!customer)RequireCashWithin(c,tx,amount);
                using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM parties WHERE id=@p0 AND party_type IN (@p1,'BOTH')",partyId,customer?"CUSTOMER":"SUPPLIER"))
                    if(Convert.ToInt64(cmd.ExecuteScalar())!=1)throw new ArgumentException("الطرف غير موجود أو نوعه لا يطابق الحركة.");
                long due;
                using(var cmd=Sql(c,tx,"SELECT "+(customer?"receivable_cents":"payable_cents")+" FROM v_party_balances WHERE party_id=@p0",partyId))due=Convert.ToInt64(cmd.ExecuteScalar());
                long debt=Math.Min(Math.Max(due,0),amount),advance=checked(amount-debt);
                var lines=new List<JournalLine> {new JournalLine {AccountCode="CASH",DebitCents=customer?amount:0,CreditCents=customer?0:amount}};
                if(debt>0)lines.Add(new JournalLine {AccountCode=customer?"RECEIVABLES":"PAYABLES",PartyId=partyId,DebitCents=customer?0:debt,CreditCents=customer?debt:0});
                if(advance>0)lines.Add(new JournalLine {AccountCode=customer?"CUSTOMER_ADVANCES":"SUPPLIER_ADVANCES",PartyId=partyId,DebitCents=customer?0:advance,CreditCents=customer?advance:0});
                long id=JournalService.PostWithin(c,tx,customer?"CUSTOMER_PAYMENT":"SUPPLIER_PAYMENT",reference,date,_actor.Id,lines);
                SaveOperation(c,tx,id,partyId,amount,debt,advance,memo);
                tx.Commit();return id;
            }
        }
        private static void ValidateInput(long amount,string reference,string memo)
        {
            if(amount<=0 || string.IsNullOrWhiteSpace(reference) || reference.Trim().Length>120 || string.IsNullOrWhiteSpace(memo) || memo.Trim().Length>500)
                throw new ArgumentException("مبلغ موجب ومرجع فريد حتى 120 حرفًا وبيان حتى 500 حرف مطلوبة.");
        }
        private static void SaveOperation(SQLiteConnection c,SQLiteTransaction tx,long id,long? party,long amount,long debt,long advance,string memo)
        {
            using(var cmd=Sql(c,tx,"INSERT INTO finance_operations(entry_id,party_id,amount_cents,debt_cents,advance_cents,memo) VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",id,party,amount,debt,advance,memo.Trim()))cmd.ExecuteNonQuery();
        }
        public long RecordExpense(string category,long amount,string reference,DateTime date,string memo)
        {
            RequireAdmin();ValidateInput(amount,reference,memo);
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                ValidateDate(c,tx,date);
                RequireCashWithin(c,tx,amount);
                using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM journal_accounts WHERE code=@p0 AND kind='EXPENSE' AND code NOT IN ('COGS','INVENTORY_VARIANCE')",category))
                    if(Convert.ToInt64(cmd.ExecuteScalar())!=1)throw new ArgumentException("تصنيف مصروف غير صالح.");
                long id=JournalService.PostWithin(c,tx,"EXPENSE",reference,date,_actor.Id,new [] {
                    new JournalLine {AccountCode=category,DebitCents=amount},new JournalLine {AccountCode="CASH",CreditCents=amount}});
                SaveOperation(c,tx,id,null,amount,0,0,memo);tx.Commit();return id;
            }
        }
        public long ApplyAdvance(bool customer,long partyId,long amount,string reference,DateTime date,string memo)
        {
            RequireAdmin();ValidateInput(amount,reference,memo);
            if(reference.Trim().Length>106)throw new ArgumentException("مرجع تسوية المقدم يجب ألا يزيد على 106 حرفًا.");
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                ValidateDate(c,tx,date);
                string debtColumn=customer?"receivable_cents":"payable_cents",advanceColumn=customer?"customer_advance_cents":"supplier_advance_cents";
                using(var cmd=Sql(c,tx,"SELECT "+debtColumn+","+advanceColumn+" FROM v_party_balances WHERE party_id=@p0",partyId))
                using(var r=cmd.ExecuteReader())
                    if(!r.Read() || amount>r.GetInt64(0) || amount>r.GetInt64(1))throw new ArgumentException("مبلغ التسوية يتجاوز المديونية أو الرصيد المقدم للطرف.");
                long id=JournalService.PostWithin(c,tx,"ADJUSTMENT","apply-advance:"+reference.Trim(),date,_actor.Id,new [] {
                    new JournalLine {AccountCode=customer?"CUSTOMER_ADVANCES":"PAYABLES",PartyId=partyId,DebitCents=amount},
                    new JournalLine {AccountCode=customer?"RECEIVABLES":"SUPPLIER_ADVANCES",PartyId=partyId,CreditCents=amount}});
                using(var cmd=Sql(c,tx,"INSERT INTO advance_applications(entry_id,party_id,is_customer,amount_cents,memo) VALUES(@p0,@p1,@p2,@p3,@p4)",id,partyId,customer?1:0,amount,memo.Trim()))cmd.ExecuteNonQuery();
                tx.Commit();return id;
            }
        }
        public long ApplyReturnCredit(bool customer,long partyId,long amount,string reference,DateTime date,string memo)
        {
            RequireAdmin();ValidateInput(amount,reference,memo);
            if(reference.Trim().Length>107)throw new ArgumentException("مرجع تسوية الرصيد الدائن يجب ألا يزيد على 107 حرفًا.");
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                ValidateDate(c,tx,date);
                string debtColumn=customer?"receivable_cents":"payable_cents",advanceColumn=customer?"customer_credit_cents":"supplier_credit_cents";
                using(var cmd=Sql(c,tx,"SELECT "+debtColumn+","+advanceColumn+" FROM v_party_balances WHERE party_id=@p0",partyId))
                using(var r=cmd.ExecuteReader())
                    if(!r.Read() || amount>r.GetInt64(0) || amount>r.GetInt64(1))throw new ArgumentException("مبلغ التسوية يتجاوز المديونية أو الرصيد الدائن للطرف.");
                long id=JournalService.PostWithin(c,tx,"ADJUSTMENT","apply-credit:"+reference.Trim(),date,_actor.Id,new [] {
                    new JournalLine {AccountCode=customer?"CUSTOMER_CREDITS":"PAYABLES",PartyId=partyId,DebitCents=amount},
                    new JournalLine {AccountCode=customer?"RECEIVABLES":"SUPPLIER_CREDITS",PartyId=partyId,CreditCents=amount}});
                using(var cmd=Sql(c,tx,"INSERT INTO return_credit_applications(entry_id,party_id,is_customer,amount_cents,memo) VALUES(@p0,@p1,@p2,@p3,@p4)",id,partyId,customer?1:0,amount,memo.Trim()))cmd.ExecuteNonQuery();
                tx.Commit();return id;
            }
        }
        public DataTable FinancialInvoices()
        {
            RequireReports();using(var c=_db.Open())return Read(c,null,@"SELECT d.*,COALESCE(p.name,'عميل نقدي') AS party_name,
                d.doc_code||' — '||COALESCE(p.name,'عميل نقدي') AS label
                FROM v_financial_invoices d LEFT JOIN parties p ON p.id=d.party_id ORDER BY d.document_id DESC");
        }
        public DataTable UnallocatedSettlements()
        {
            RequireReports();using(var c=_db.Open())return Read(c,null,@"SELECT e.id,e.source_key,e.entry_date,p.name AS party_name,
                e.source_key||' — '||p.name AS label,s.amount-COALESCE((SELECT SUM(amount_cents) FROM invoice_allocations WHERE entry_id=e.id),0) AS unallocated_cents
                FROM (SELECT entry_id,party_id,debt_cents AS amount FROM finance_operations WHERE debt_cents>0
                      UNION ALL SELECT entry_id,party_id,amount_cents FROM advance_applications
                      UNION ALL SELECT entry_id,party_id,amount_cents FROM return_credit_applications) s
                JOIN journal_entries e ON e.id=s.entry_id JOIN parties p ON p.id=s.party_id
                WHERE s.amount>COALESCE((SELECT SUM(amount_cents) FROM invoice_allocations WHERE entry_id=e.id),0)
                ORDER BY e.id");
        }
        public void AllocateSettlement(long entryId,long documentId,long amount,string reference)
        {
            RequireAdmin();
            if(amount<=0 || string.IsNullOrWhiteSpace(reference) || reference.Trim().Length>120)throw new ArgumentException("مبلغ توزيع موجب ومرجع فريد مطلوبان.");
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                using(var cmd=Sql(c,tx,"INSERT INTO invoice_allocations(entry_id,document_id,amount_cents,reference) VALUES(@p0,@p1,@p2,@p3)",entryId,documentId,amount,reference.Trim()))cmd.ExecuteNonQuery();
                using(var cmd=Sql(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,details,created_utc) VALUES('SETTLEMENT_ALLOCATED',@p0,@p1,@p2,@p3)",entryId,_actor.Id,reference.Trim(),DateTime.UtcNow.ToString("o")))cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }
        public DataTable PartyBalances()
        {RequireReports();using(var c=_db.Open())return Read(c,null,"SELECT * FROM v_party_balances ORDER BY name,party_id");}
        public DataTable Accounts()
        {RequireReports();using(var c=_db.Open())return Read(c,null,"SELECT code,name,kind FROM journal_accounts ORDER BY code");}
        public DataTable TrialBalance(DateTime asOf)
        {
            RequireReports();using(var c=_db.Open())return Read(c,null,@"SELECT a.code,a.name,a.kind,
                COALESCE(SUM(l.debit_cents),0) AS debit_cents,COALESCE(SUM(l.credit_cents),0) AS credit_cents
                FROM journal_accounts a LEFT JOIN v_journal_posted_lines l ON l.account_code=a.code AND l.entry_date<=@p0 GROUP BY a.code ORDER BY a.code",asOf.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
        }
        public DataTable PartyStatement(long partyId,DateTime from,DateTime to)
        {
            RequireReports();if(from.Date>to.Date)throw new ArgumentException("ترتيب الفترة غير صحيح.");
            // Includes the complete brought-forward balance. The caller must label rows before 'from' as opening.
            using(var c=_db.Open())return Read(c,null,@"SELECT entry_id,entry_date,source_kind,source_key,account_name,debit_cents,credit_cents,
                CASE WHEN entry_date<@p1 THEN 'حركة سابقة للفترة' ELSE 'حركة الفترة' END AS brought_forward
                FROM v_journal_posted_lines WHERE party_id=@p0 AND entry_date<=@p2 ORDER BY entry_date,entry_id,line_no",partyId,
                from.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),to.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
        }
        public ProfitAndLoss ProfitAndLoss(DateTime from,DateTime to)
        {
            RequireReports();if(from.Date>to.Date)throw new ArgumentException("ترتيب الفترة غير صحيح.");
            using(var c=_db.Open())using(var cmd=Sql(c,null,@"SELECT
                COALESCE(SUM(CASE WHEN kind='INCOME' THEN credit_cents-debit_cents ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN account_code='COGS' THEN debit_cents-credit_cents ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN kind='EXPENSE' AND account_code<>'COGS' THEN debit_cents-credit_cents ELSE 0 END),0),
                COALESCE(SUM(CASE WHEN account_code='SALES' THEN credit_cents-debit_cents ELSE 0 END),0)
                FROM v_financial_profit_lines WHERE entry_date BETWEEN @p0 AND @p1",
                from.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),to.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)))
            using(var r=cmd.ExecuteReader()) {r.Read();return new ProfitAndLoss {RevenueCents=r.GetInt64(0),CostOfSalesCents=r.GetInt64(1),ExpensesCents=r.GetInt64(2),SalesCents=r.GetInt64(3)};}
        }
    }
}
