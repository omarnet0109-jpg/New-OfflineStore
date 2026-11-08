using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using OfflineStore.Models;
using OfflineStore.Security;
using OfflineStore.Services;

namespace OfflineStore.Infrastructure
{
    public sealed class ReturnQuantity
    {
        public long OriginalLineId {get;set;}
        public long QuantityMilli {get;set;}
    }
    public sealed class ReturnableLine
    {
        public long OriginalLineId {get;set;}
        public string Name {get;set;}
        public string Unit {get;set;}
        public long AvailableMilli {get;set;}
        public string AvailableText {get {return Amounts.Qty(AvailableMilli);}}
        public string Quantity {get;set;}="0";
    }
    public sealed class ReturnQuote
    {
        public long TotalCents {get;internal set;}
        public long CostCents {get;internal set;}
        public long DebtCents {get;internal set;}
        public long CreditCents {get;internal set;}
        public long CashCents {get {return checked(TotalCents-DebtCents-CreditCents);}}
        internal long? CreditParty;
        public string OriginalCode {get;internal set;}
        public bool Purchase {get;internal set;}
        internal long? Party;
        internal List<CalculatedReturnLine> Lines=new List<CalculatedReturnLine>();
    }
    internal sealed class CalculatedReturnLine
    {
        internal long Id,Product,Qty,Amount,Cost;
        internal long? CostBasis;
        internal CarryingIssue Issue;
        internal long IssueOffset;
        internal string Name,Unit;
    }
    public sealed class ReturnService
    {
        private readonly StoreDb _db;private readonly UserSession _actor;
        public ReturnService(StoreDb db,UserSession actor){_db=db;_actor=actor;}
        private void Authorize(){if(!new UserAuthService(_db).Authorize(_actor,"USERS"))throw new UnauthorizedAccessException("اعتماد المرتجعات وتسويتها للمدير فقط.");}
        private static SQLiteCommand Sql(SQLiteConnection c,SQLiteTransaction tx,string sql,params object[] args)
        {var cmd=new SQLiteCommand(sql,c,tx);for(int i=0;i<args.Length;i++)cmd.Parameters.AddWithValue("@p"+i,args[i]??DBNull.Value);return cmd;}
        public List<ReturnableLine> Lines(long documentId)
        {
            Authorize();var result=new List<ReturnableLine>();
            using(var c=_db.Open())using(var cmd=Sql(c,null,@"SELECT l.id,COALESCE(n.name_at_sale,s.ItemName,p.name),p.unit,
                l.qty_milli-COALESCE((SELECT SUM(x.qty_milli) FROM return_lines x JOIN return_documents r ON r.id=x.return_id WHERE x.original_line_id=l.id AND r.state='POSTED'),0)
                FROM document_lines l JOIN v_financial_invoices f ON f.document_id=l.document_id JOIN products p ON p.id=l.product_id
                LEFT JOIN sale_line_names n ON n.document_line_id=l.id LEFT JOIN SupplyVouchers v ON v.DocumentID=l.document_id
                LEFT JOIN SupplyVoucherDetails s ON s.VoucherID=v.VoucherID AND s.LineNo=l.line_no WHERE l.document_id=@p0 ORDER BY l.line_no",documentId))
            using(var r=cmd.ExecuteReader())while(r.Read())result.Add(new ReturnableLine {OriginalLineId=r.GetInt64(0),Name=r.GetString(1),Unit=r.GetString(2),AvailableMilli=r.GetInt64(3)});
            return result;
        }
        public ReturnQuote Preview(long documentId,IList<ReturnQuantity> quantities,long debtCents,long creditCents=0,long? creditPartyId=null)
        {Authorize();using(var c=_db.Open())using(var tx=c.BeginTransaction())return Calculate(c,tx,documentId,quantities,debtCents,creditCents,creditPartyId);}
        private static ReturnQuote Calculate(SQLiteConnection c,SQLiteTransaction tx,long doc,IList<ReturnQuantity> quantities,long debt,long credit,long? creditParty)
        {
            FinanceService.ValidateDate(c,tx,DateTime.Today);
            if(InventoryValueService.Enabled(c,tx))InventoryValueService.RequireReady(c,tx);
            if(quantities==null || quantities.Count==0 || quantities.Count>500 || quantities.Any(x=>x==null || x.QuantityMilli<=0) || quantities.Select(x=>x.OriginalLineId).Distinct().Count()!=quantities.Count || debt<0 || credit<0)
                throw new ArgumentException("حدد كميات موجبة لسطور أصلية مختلفة، وتسوية غير سالبة.");
            var quote=new ReturnQuote {DebtCents=debt,CreditCents=credit};long unpaid,paid;
            using(var cmd=Sql(c,tx,@"SELECT f.doc_code,f.doc_type,f.party_id,f.unallocated_invoice_cents,
                COALESCE((SELECT SUM(CASE WHEN f.doc_type='SALE' THEN l.debit_cents-l.credit_cents ELSE l.credit_cents-l.debit_cents END)
                 FROM journal_entries e JOIN journal_lines l ON l.entry_id=e.id WHERE e.document_id=f.document_id AND e.source_kind=f.doc_type AND e.state='POSTED' AND l.account_code='CASH'),0)
                FROM v_financial_invoices f WHERE f.document_id=@p0",doc))
            using(var r=cmd.ExecuteReader())
            {
                if(!r.Read())throw new ArgumentException("الفاتورة ليست مرتبطة بدفتر الأموال. لا يمكن تخمين مدفوعاتها أو تكلفتها القديمة.");
                quote.OriginalCode=r.GetString(0);quote.Purchase=r.GetString(1)=="PURCHASE";quote.Party=r.IsDBNull(2)?(long?)null:r.GetInt64(2);unpaid=r.GetInt64(3);paid=r.GetInt64(4);
            }
            foreach(var item in quantities)
            {
                using(var cmd=Sql(c,tx,@"SELECT l.product_id,l.qty_milli,p.is_countable,p.avg_cost_millicents,COALESCE(n.name_at_sale,s.ItemName,p.name),p.unit,
                    CASE WHEN d.doc_type='SALE' THEN l.line_total_cents ELSE pc.total_cost_cents END,sc.total_cost_cents,
                    COALESCE((SELECT SUM(x.qty_milli) FROM return_lines x JOIN return_documents r ON r.id=x.return_id WHERE x.original_line_id=l.id AND r.state='POSTED'),0)
                    FROM document_lines l JOIN documents d ON d.id=l.document_id JOIN products p ON p.id=l.product_id
                    LEFT JOIN sale_cost_snapshots sc ON sc.document_line_id=l.id LEFT JOIN purchase_cost_snapshots pc ON pc.document_line_id=l.id
                    LEFT JOIN sale_line_names n ON n.document_line_id=l.id LEFT JOIN SupplyVouchers v ON v.DocumentID=l.document_id
                    LEFT JOIN SupplyVoucherDetails s ON s.VoucherID=v.VoucherID AND s.LineNo=l.line_no WHERE l.id=@p0 AND l.document_id=@p1",item.OriginalLineId,doc))
                using(var r=cmd.ExecuteReader())
                {
                    if(!r.Read() || r.IsDBNull(6) || (!quote.Purchase && r.IsDBNull(7)))throw new ArgumentException("السطر غير صالح أو لقطة تكلفته الأصلية الدقيقة مفقودة.");
                    if(r.GetInt64(2)==1 && item.QuantityMilli%1000!=0)throw new ArgumentException("كمية الصنف المعدود يجب أن تكون عددًا صحيحًا بالوحدة الأساسية.");
                    long originalQty=r.GetInt64(1),before=r.GetInt64(8);
                    long amount=Amounts.ProportionalReturn(r.GetInt64(6),before,item.QuantityMilli,originalQty);
                    long cost=quote.Purchase?Amounts.CostTotal(item.QuantityMilli,r.GetInt64(3)):Amounts.ProportionalReturn(r.GetInt64(7),before,item.QuantityMilli,originalQty);
                    quote.Lines.Add(new CalculatedReturnLine {Id=item.OriginalLineId,Product=r.GetInt64(0),Qty=item.QuantityMilli,Name=r.GetString(4),Unit=r.GetString(5),Amount=amount,Cost=cost,CostBasis=quote.Purchase?(long?)r.GetInt64(3):null});
                    quote.TotalCents=checked(quote.TotalCents+amount);quote.CostCents=checked(quote.CostCents+cost);
                }
            }
            if(quote.Purchase && InventoryValueService.Enabled(c,tx))
            {
                quote.CostCents=0;
                foreach(var group in quote.Lines.GroupBy(x=>x.Product))
                {
                    var issue=InventoryValueService.Quote(c,tx,group.Key,group.Aggregate(0L,(sum,x)=>checked(sum+x.Qty)));long offset=0;
                    foreach(var line in group){line.Issue=issue;line.IssueOffset=offset;line.Cost=issue.Part(offset,line.Qty);offset=checked(offset+line.Qty);quote.CostCents=checked(quote.CostCents+line.Cost);}
                }
            }
            if(credit>quote.TotalCents || debt>quote.TotalCents-credit || debt>unpaid || (debt>0 && !quote.Party.HasValue))throw new ArgumentException("تخفيض الدين يتجاوز قيمة المرتجع أو المتبقي الأصلي بعد توزيع السداد.");
            quote.CreditParty=credit>0?(creditParty??quote.Party):null;
            if(credit>0)
            {
                if(!quote.CreditParty.HasValue || (quote.Party.HasValue && quote.CreditParty!=quote.Party))throw new ArgumentException("حدد مستفيد الرصيد الدائن؛ لا يجوز تغيير طرف فاتورة مسماة.");
                using(var cmd=Sql(c,tx,"SELECT COUNT(*) FROM parties WHERE id=@p0 AND party_type IN (@p1,'BOTH')",quote.CreditParty.Value,quote.Purchase?"SUPPLIER":"CUSTOMER"))
                    if(Convert.ToInt64(cmd.ExecuteScalar())!=1)throw new ArgumentException("مستفيد الرصيد الدائن ليس من نوع طرف الفاتورة.");
            }
            if(debt>0)
                using(var cmd=Sql(c,tx,"SELECT "+(quote.Purchase?"payable_cents":"receivable_cents")+" FROM v_party_balances WHERE party_id=@p0",quote.Party.Value))
                    if(Convert.ToInt64(cmd.ExecuteScalar())<debt)throw new ArgumentException("تخفيض الدين يتجاوز المديونية الفعلية للطرف. وزع التحصيلات على فواتيرها أولًا.");
            using(var cmd=Sql(c,tx,@"SELECT COALESCE((SELECT SUM(x.amount_cents) FROM invoice_allocations x JOIN journal_entries e ON e.id=x.entry_id WHERE x.document_id=@p0 AND e.source_kind<>'RETURN' AND NOT EXISTS(SELECT 1 FROM return_credit_applications c WHERE c.entry_id=e.id)),0)
                -COALESCE((SELECT SUM(cash_cents) FROM return_documents WHERE original_document_id=@p0 AND state='POSTED'),0)",doc))paid=checked(paid+Convert.ToInt64(cmd.ExecuteScalar()));
            if(quote.CashCents>Math.Max(0,paid))throw new ArgumentException("رد النقد يتجاوز المدفوع الموثق للفاتورة. وزع التحصيل أو السداد المسجل على الفاتورة أولًا.");
            if(quote.Purchase)
                foreach(var group in quote.Lines.GroupBy(x=>x.Product))
                    using(var cmd=Sql(c,tx,"SELECT qty_milli FROM v_stock WHERE product_id=@p0",group.Key))
                        if(Convert.ToInt64(cmd.ExecuteScalar())<group.Aggregate(0L,(sum,x)=>checked(sum+x.Qty)))throw new ArgumentException("المخزون الحالي لا يكفي لمرتجع المورد.");
            return quote;
        }
        public string Post(long documentId,IList<ReturnQuantity> quantities,long debtCents,string reference,string memo,long creditCents=0,long? creditPartyId=null)
        {
            Authorize();if(string.IsNullOrWhiteSpace(reference) || reference.Trim().Length>113 || string.IsNullOrWhiteSpace(memo) || memo.Trim().Length>500)throw new ArgumentException("مرجع مرتجع فريد وبيان واضح مطلوبان.");
            using(var c=_db.Open())using(var tx=c.BeginTransaction())
            {
                var q=Calculate(c,tx,documentId,quantities,debtCents,creditCents,creditPartyId);long id;
                if(!q.Purchase)FinanceService.RequireCashWithin(c,tx,q.CashCents);
                if(q.Purchase && InventoryValueService.Enabled(c,tx))foreach(var group in q.Lines.GroupBy(x=>x.Product))
                {
                    var issue=InventoryValueService.Issue(c,tx,group.Key,group.Sum(x=>x.Qty));var expected=group.First().Issue;
                    if(issue.QtyBefore!=expected.QtyBefore || issue.ValueBefore!=expected.ValueBefore || issue.Cost!=expected.Cost)throw new InvalidOperationException("تغير تقييم المرتجع أثناء الاعتماد.");
                }
                using(var cmd=Sql(c,tx,"INSERT INTO return_documents(original_document_id,reference,return_date,cash_cents,debt_cents,total_cents,created_by_user_id,created_utc,memo,credit_cents,credit_party_id) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10)",documentId,reference.Trim(),DateTime.Today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),q.CashCents,q.DebtCents,q.TotalCents,_actor.Id,DateTime.UtcNow.ToString("o"),memo.Trim(),q.CreditCents,q.CreditParty))cmd.ExecuteNonQuery();
                using(var cmd=Sql(c,tx,"SELECT last_insert_rowid()"))id=Convert.ToInt64(cmd.ExecuteScalar());
                foreach(var line in q.Lines)
                {
                    using(var cmd=Sql(c,tx,"INSERT INTO return_lines(return_id,original_line_id,qty_milli,amount_cents,cost_cents,cost_basis_millicents,name_snapshot,unit_snapshot) VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7)",id,line.Id,line.Qty,line.Amount,line.Cost,line.CostBasis,line.Name,line.Unit))cmd.ExecuteNonQuery();
                    if(line.Issue!=null)
                    {
                        long returnLine;using(var cmd=Sql(c,tx,"SELECT last_insert_rowid()"))returnLine=Convert.ToInt64(cmd.ExecuteScalar());
                        InventoryValueService.Evidence(c,tx,false,returnLine,line.Issue,line.IssueOffset);
                    }
                }
                var lines=new List<JournalLine>();
                Add(lines,"CASH",null,q.Purchase?q.CashCents:0,q.Purchase?0:q.CashCents);
                Add(lines,q.Purchase?"PAYABLES":"RECEIVABLES",q.Party,q.Purchase?q.DebtCents:0,q.Purchase?0:q.DebtCents);
                Add(lines,q.Purchase?"SUPPLIER_CREDITS":"CUSTOMER_CREDITS",q.CreditParty,q.Purchase?q.CreditCents:0,q.Purchase?0:q.CreditCents);
                if(q.Purchase)
                {
                    Add(lines,"INVENTORY",null,0,q.CostCents);long variance=checked(q.TotalCents-q.CostCents);
                    Add(lines,"PURCHASE_RETURN_VARIANCE",null,variance<0?checked(-variance):0,variance>0?variance:0);
                }
                else
                {
                    Add(lines,"SALES",null,q.TotalCents,0);Add(lines,"INVENTORY",null,q.CostCents,0);Add(lines,"COGS",null,0,q.CostCents);
                    var staged=new Dictionary<long,long>();foreach(var line in q.Lines)StoreService.ApplyMovingAverageCost(c,tx,line.Product,line.Qty,line.Cost,staged,true);
                }
                // A tiny valid quantity can have zero cumulative monetary deltas. Keep its stock/cumulative quantity without inventing a zero journal.
                long? entry=null;
                if(lines.Count>0)entry=JournalService.PostWithin(c,tx,"RETURN","return:"+reference.Trim(),DateTime.Today,_actor.Id,lines,documentId);
                using(var cmd=Sql(c,tx,"UPDATE return_documents SET journal_entry_id=@p0 WHERE id=@p1",entry,id))cmd.ExecuteNonQuery();
                if(q.DebtCents>0)
                    using(var cmd=Sql(c,tx,"INSERT INTO invoice_allocations(entry_id,document_id,amount_cents,reference) VALUES(@p0,@p1,@p2,@p3)",entry,documentId,q.DebtCents,"R:"+reference.Trim()))cmd.ExecuteNonQuery();
                using(var cmd=Sql(c,tx,"UPDATE return_documents SET state='POSTED' WHERE id=@p0",id))cmd.ExecuteNonQuery();
                if(InventoryValueService.Enabled(c,tx))InventoryValueService.Verify(c,tx,false);
                using(var cmd=Sql(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,details,created_utc) VALUES('RETURN_POSTED',@p0,@p1,@p2,@p3)",id,_actor.Id,q.OriginalCode+"; "+memo.Trim(),DateTime.UtcNow.ToString("o")))cmd.ExecuteNonQuery();
                tx.Commit();return "R-"+id.ToString("D6",CultureInfo.InvariantCulture);
            }
        }
        public DataTable List(DateTime from,DateTime to)
        {
            Authorize();if(from.Date>to.Date)throw new ArgumentException("ترتيب الفترة غير صحيح.");
            using(var c=_db.Open())using(var cmd=Sql(c,null,@"SELECT printf('R-%06d',r.id) AS code,d.doc_code AS original_code,
                CASE WHEN d.doc_type='SALE' THEN 'بيع' ELSE 'شراء' END AS return_type,r.return_date,COALESCE(p.name,'عميل نقدي') AS party,
                r.total_cents,r.cash_cents,r.debt_cents,r.credit_cents,r.memo FROM return_documents r JOIN documents d ON d.id=r.original_document_id
                LEFT JOIN parties p ON p.id=d.party_id WHERE r.state='POSTED' AND r.return_date BETWEEN @p0 AND @p1 ORDER BY r.id DESC",
                from.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),to.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)))
            using(var reader=cmd.ExecuteReader()){var table=new DataTable();table.Load(reader);return table;}
        }
        public DataTable Receipt(string returnCode)
        {
            Authorize();long id;
            if(returnCode==null || !returnCode.StartsWith("R-",StringComparison.Ordinal) || !long.TryParse(returnCode.Substring(2),out id))throw new ArgumentException("رقم المرتجع غير صالح.");
            using(var c=_db.Open())using(var cmd=Sql(c,null,@"SELECT r.id,d.doc_code,r.return_date,r.memo,r.total_cents,r.cash_cents,r.debt_cents,r.credit_cents,COALESCE(p.name,'') AS credit_party_name,
                x.name_snapshot,x.unit_snapshot,x.qty_milli,x.amount_cents
                FROM return_documents r JOIN documents d ON d.id=r.original_document_id JOIN return_lines x ON x.return_id=r.id LEFT JOIN parties p ON p.id=r.credit_party_id
                WHERE r.id=@p0 AND r.state='POSTED' ORDER BY x.id",id))using(var reader=cmd.ExecuteReader())
            {var result=new DataTable();result.Load(reader);if(result.Rows.Count==0)throw new ArgumentException("المرتجع غير موجود أو غير معتمد.");return result;}
        }
        private static void Add(IList<JournalLine> lines,string account,long? party,long debit,long credit)
        {if(debit>0 || credit>0)lines.Add(new JournalLine {AccountCode=account,PartyId=party,DebitCents=debit,CreditCents=credit});}
        internal static void VerifyIntegrity(SQLiteConnection c)
        {
            using(var cmd=Sql(c,null,"SELECT id FROM v_invalid_return_documents UNION ALL SELECT id FROM journal_entries e WHERE e.source_kind='RETURN' AND e.state='POSTED' AND NOT EXISTS(SELECT 1 FROM return_documents r WHERE r.journal_entry_id=e.id AND r.state='POSTED') LIMIT 1"))
                if(cmd.ExecuteScalar()!=null)throw new InvalidOperationException("مرتجع غير مطابق لحركة الأموال أو المخزون.");
            var previous=new Dictionary<long,long>();
            using(var cmd=Sql(c,null,@"SELECT l.id,l.qty_milli,d.doc_type,l.line_total_cents,pc.total_cost_cents,sc.total_cost_cents,
                x.qty_milli,x.amount_cents,x.cost_cents,x.cost_basis_millicents,x.id
                FROM return_lines x JOIN return_documents r ON r.id=x.return_id JOIN document_lines l ON l.id=x.original_line_id
                JOIN documents d ON d.id=l.document_id LEFT JOIN purchase_cost_snapshots pc ON pc.document_line_id=l.id
                LEFT JOIN sale_cost_snapshots sc ON sc.document_line_id=l.id WHERE r.state='POSTED' ORDER BY r.id,x.id"))
            using(var r=cmd.ExecuteReader())while(r.Read())
            {
                bool purchase=r.GetString(2)=="PURCHASE";long line=r.GetInt64(0),before=previous.ContainsKey(line)?previous[line]:0,qty=r.GetInt64(6);
                if((purchase && (r.IsDBNull(4) || r.IsDBNull(9))) || (!purchase && r.IsDBNull(5)))throw new InvalidOperationException("لقطة تكلفة المرتجع مفقودة.");
                long amount=Amounts.ProportionalReturn(r.GetInt64(purchase?4:3),before,qty,r.GetInt64(1));
                long cost=purchase?Amounts.CostTotal(qty,r.GetInt64(9)):Amounts.ProportionalReturn(r.GetInt64(5),before,qty,r.GetInt64(1));
                if(purchase && InventoryValueService.Enabled(c))
                    using(var evidence=Sql(c,null,"SELECT qty_before,value_before,group_qty,group_offset FROM inventory_cost_evidence WHERE return_line_id=@p0",r.GetInt64(10)))
                    using(var e=evidence.ExecuteReader())if(e.Read())cost=Amounts.ProportionalReturn(Amounts.ProportionalReturn(e.GetInt64(1),0,e.GetInt64(2),e.GetInt64(0)),e.GetInt64(3),qty,e.GetInt64(2));
                if(amount!=r.GetInt64(7) || cost!=r.GetInt64(8))throw new InvalidOperationException("قيمة أو تكلفة مرتجع لا تطابق الأصل أو أساس التكلفة المحفوظ.");
                previous[line]=checked(before+qty);
            }
        }
    }
}
