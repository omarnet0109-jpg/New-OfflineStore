using System;
using System.Collections.Generic;
using System.Data.SQLite;
using OfflineStore.Models;

namespace OfflineStore.Infrastructure
{
    internal sealed class CarryingIssue
    {
        internal long QtyBefore,ValueBefore,Quantity,Cost;
        internal long Part(long offset,long qty){return Amounts.ProportionalReturn(Cost,offset,qty,Quantity);}
    }
    // Total carrying cents, never a rounded unit price, determine financial stock cost.
    internal static class InventoryValueService
    {
        private static SQLiteCommand Sql(SQLiteConnection c,SQLiteTransaction tx,string sql,params object[] args)
        {var q=new SQLiteCommand(sql,c,tx);for(int i=0;i<args.Length;i++)q.Parameters.AddWithValue("@p"+i,args[i]??DBNull.Value);return q;}
        internal static bool Enabled(SQLiteConnection c,SQLiteTransaction tx=null)
        {using(var q=Sql(c,tx,"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='inventory_valuation_state'"))return Convert.ToInt64(q.ExecuteScalar())!=0;}
        internal static void Seed(SQLiteConnection c,SQLiteTransaction tx)
        {
            var values=new List<long[]>();
            using(var q=Sql(c,tx,"SELECT s.product_id,s.qty_milli,p.avg_cost_millicents FROM v_stock s JOIN products p ON p.id=s.product_id"))
            using(var r=q.ExecuteReader())while(r.Read())values.Add(new [] {r.GetInt64(0),r.GetInt64(1),r.GetInt64(1)==0?0:Amounts.CostTotal(r.GetInt64(1),r.GetInt64(2))});
            long total=0;foreach(var v in values)
            {
                total=checked(total+v[2]);
                using(var q=Sql(c,tx,"INSERT INTO inventory_carrying_values VALUES(@p0,@p1,@p2)",v[0],v[1],v[2]))q.ExecuteNonQuery();
            }
            bool review=FinanceService.IsActiveWithin(c,tx) && Book(c,tx)!=total;
            using(var q=Sql(c,tx,"INSERT INTO inventory_valuation_state VALUES(1,@p0,(SELECT COALESCE(MAX(id),0) FROM documents),(SELECT COALESCE(MAX(id),0) FROM return_documents))",review?1:0))q.ExecuteNonQuery();
        }
        internal static long Book(SQLiteConnection c,SQLiteTransaction tx)
        {using(var q=Sql(c,tx,"SELECT COALESCE(SUM(debit_cents-credit_cents),0) FROM v_journal_posted_lines WHERE account_code='INVENTORY'"))return Convert.ToInt64(q.ExecuteScalar());}
        internal static long Total(SQLiteConnection c,SQLiteTransaction tx)
        {using(var q=Sql(c,tx,"SELECT COALESCE(SUM(value_cents),0) FROM inventory_carrying_values"))return Convert.ToInt64(q.ExecuteScalar());}
        internal static bool NeedsReview(SQLiteConnection c,SQLiteTransaction tx=null)
        {using(var q=Sql(c,tx,"SELECT review_required FROM inventory_valuation_state WHERE singleton=1")){var value=q.ExecuteScalar();if(value==null)throw new InvalidOperationException("تهيئة تقييم المخزون غير مكتملة.");return Convert.ToInt64(value)!=0;}}
        internal static void RequireReady(SQLiteConnection c,SQLiteTransaction tx)
        {if(NeedsReview(c,tx))throw new InvalidOperationException("يوجد فرق تقييم مخزون سابق. يجب أن يراجعه المدير ويعتمد تسويته من شاشة الحسابات قبل أي حركة مخزون.");}
        private static long[] Read(SQLiteConnection c,SQLiteTransaction tx,long product)
        {
            using(var q=Sql(c,tx,"SELECT qty_milli,value_cents FROM inventory_carrying_values WHERE product_id=@p0",product))using(var r=q.ExecuteReader())if(r.Read())return new [] {r.GetInt64(0),r.GetInt64(1)};
            using(var q=Sql(c,tx,"SELECT qty_milli FROM v_stock WHERE product_id=@p0",product))
            {var value=q.ExecuteScalar();if(value==null || Convert.ToInt64(value)!=0)throw new InvalidOperationException("رصيد تقييم الصنف مفقود؛ لن يُخمن من سعر الوحدة.");}
            using(var q=Sql(c,tx,"INSERT INTO inventory_carrying_values VALUES(@p0,0,0)",product))q.ExecuteNonQuery();
            return new long[] {0,0};
        }
        private static void Write(SQLiteConnection c,SQLiteTransaction tx,long product,long qty,long value)
        {
            long average=qty==0?0:checked((long)decimal.Round((decimal)value*1000000m/qty,0,MidpointRounding.AwayFromZero));
            long cents=checked((long)decimal.Round(average/1000m,0,MidpointRounding.AwayFromZero));
            using(var q=Sql(c,tx,"UPDATE inventory_carrying_values SET qty_milli=@p0,value_cents=@p1 WHERE product_id=@p2",qty,value,product))q.ExecuteNonQuery();
            // Preserve the last catalog estimate at zero quantity; it is never used as carrying value.
            if(qty>0)using(var q=Sql(c,tx,"UPDATE products SET avg_cost_millicents=@p0,last_cost_cents=@p1 WHERE id=@p2",average,cents,product))q.ExecuteNonQuery();
        }
        internal static void Receive(SQLiteConnection c,SQLiteTransaction tx,long product,long qty,long value)
        {
            RequireReady(c,tx);if(qty<=0 || value<0)throw new ArgumentException("كمية أو تكلفة وارد غير صحيحة.");var old=Read(c,tx,product);
            Write(c,tx,product,checked(old[0]+qty),checked(old[1]+value));
        }
        internal static CarryingIssue Quote(SQLiteConnection c,SQLiteTransaction tx,long product,long qty)
        {
            RequireReady(c,tx);var old=Read(c,tx,product);
            if(qty<=0 || qty>old[0])throw new InvalidOperationException("كمية الصرف تتجاوز رصيد تقييم المخزون.");
            return new CarryingIssue {QtyBefore=old[0],ValueBefore=old[1],Quantity=qty,Cost=Amounts.ProportionalReturn(old[1],0,qty,old[0])};
        }
        internal static CarryingIssue Issue(SQLiteConnection c,SQLiteTransaction tx,long product,long qty)
        {var issue=Quote(c,tx,product,qty);Write(c,tx,product,checked(issue.QtyBefore-qty),checked(issue.ValueBefore-issue.Cost));return issue;}
        internal static void Evidence(SQLiteConnection c,SQLiteTransaction tx,bool sale,long line,CarryingIssue issue,long offset)
        {using(var q=Sql(c,tx,"INSERT INTO inventory_cost_evidence VALUES(@p0,@p1,@p2,@p3,@p4,@p5)",sale?(object)line:null,sale?null:(object)line,issue.QtyBefore,issue.ValueBefore,issue.Quantity,offset))q.ExecuteNonQuery();}
        internal static void Verify(SQLiteConnection c,SQLiteTransaction tx=null,bool replayHistory=true)
        {
            using(var q=Sql(c,tx,@"SELECT p.id FROM products p JOIN v_stock s ON s.product_id=p.id LEFT JOIN inventory_carrying_values v ON v.product_id=p.id
                WHERE s.qty_milli<>COALESCE(v.qty_milli,0) OR (v.qty_milli=0 AND v.value_cents<>0) LIMIT 1"))
                if(q.ExecuteScalar()!=null)throw new InvalidOperationException("كمية دفتر تقييم المخزون لا تطابق حركة المخزون.");
            bool review=NeedsReview(c,tx);
            if(FinanceService.IsActiveWithin(c,tx) && !review && Total(c,tx)!=Book(c,tx))throw new InvalidOperationException("قيمة المخزون لا تطابق حساب المخزون المالي؛ أوقف الحركة وراجع نسخة سليمة.");
            // Immutable evidence is fully replayed on startup/backup, not on each sale.
            if(!replayHistory)return;
            using(var q=Sql(c,tx,@"SELECT l.id FROM document_lines l JOIN documents d ON d.id=l.document_id
                WHERE d.doc_type='SALE' AND d.state='POSTED' AND d.id>(SELECT last_legacy_document_id FROM inventory_valuation_state)
                AND NOT EXISTS(SELECT 1 FROM inventory_cost_evidence e WHERE e.sale_line_id=l.id)
                UNION ALL SELECT x.id FROM return_lines x JOIN return_documents r ON r.id=x.return_id JOIN documents d ON d.id=r.original_document_id
                WHERE d.doc_type='PURCHASE' AND r.state='POSTED' AND r.id>(SELECT last_legacy_return_id FROM inventory_valuation_state)
                AND NOT EXISTS(SELECT 1 FROM inventory_cost_evidence e WHERE e.return_line_id=x.id) LIMIT 1"))
                if(q.ExecuteScalar()!=null)throw new InvalidOperationException("دليل تكلفة الصرف من الرصيد الفعلي مفقود.");
            string lastGroup=null;long[] group=null;
            using(var q=Sql(c,tx,@"SELECT CASE WHEN e.sale_line_id IS NULL THEN 'R' ELSE 'S' END,COALESCE(l.document_id,x.return_id),COALESCE(l.product_id,o.product_id),e.qty_before,e.value_before,e.group_qty,e.group_offset,COALESCE(l.qty_milli,x.qty_milli)
                FROM inventory_cost_evidence e LEFT JOIN document_lines l ON l.id=e.sale_line_id LEFT JOIN return_lines x ON x.id=e.return_line_id LEFT JOIN document_lines o ON o.id=x.original_line_id
                ORDER BY 1,2,3,COALESCE(l.id,x.id)"))
            using(var r=q.ExecuteReader())while(r.Read())
            {
                string key=r.GetString(0)+":"+r.GetInt64(1)+":"+r.GetInt64(2);
                if(key!=lastGroup)
                {
                    if(group!=null && group[2]!=group[3])throw new InvalidOperationException("دليل توزيع تكلفة الصنف غير مكتمل.");
                    group=new [] {r.GetInt64(3),r.GetInt64(4),r.GetInt64(5),0L};lastGroup=key;
                }
                if(group[0]!=r.GetInt64(3) || group[1]!=r.GetInt64(4) || group[2]!=r.GetInt64(5) || group[3]!=r.GetInt64(6))throw new InvalidOperationException("توزيع تكلفة سطور الصنف غير متصل أو مكرر.");
                group[3]=checked(group[3]+r.GetInt64(7));
            }
            if(group!=null && group[2]!=group[3])throw new InvalidOperationException("دليل توزيع تكلفة الصنف غير مكتمل.");
            using(var q=Sql(c,tx,@"SELECT e.qty_before,e.value_before,e.group_qty,e.group_offset,COALESCE(l.qty_milli,x.qty_milli),COALESCE(s.total_cost_cents,x.cost_cents)
                FROM inventory_cost_evidence e LEFT JOIN document_lines l ON l.id=e.sale_line_id LEFT JOIN sale_cost_snapshots s ON s.document_line_id=l.id LEFT JOIN return_lines x ON x.id=e.return_line_id"))
            using(var r=q.ExecuteReader())while(r.Read())
            {
                long total=Amounts.ProportionalReturn(r.GetInt64(1),0,r.GetInt64(2),r.GetInt64(0));
                if(r.IsDBNull(5) || Amounts.ProportionalReturn(total,r.GetInt64(3),r.GetInt64(4),r.GetInt64(2))!=r.GetInt64(5))throw new InvalidOperationException("لقطة تكلفة الصرف لا تطابق دليل قيمة المخزون المتبقية.");
            }
        }
    }
}
