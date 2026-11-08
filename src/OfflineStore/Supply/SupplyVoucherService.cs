using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using OfflineStore.Infrastructure;
using OfflineStore.Models;
using OfflineStore.Security;
using OfflineStore.Services;

namespace OfflineStore.Supply
{
    public sealed class SupplyVoucherSummary
    {
        public long VoucherID { get; set; }
        public string VoucherCode { get; set; }
        public string SupplierName { get; set; }
        public string VoucherDate { get; set; }
        public long GrandTotalCents { get; set; }
        public string State { get; set; }
        public string Display
        {
            get { return VoucherCode+" | "+VoucherDate+" | "+SupplierName+
                " | "+Amounts.Price(GrandTotalCents)+" | "+(State=="POSTED"?"معتمد":"مسودة"); }
        }
    }

    public sealed class SupplyVoucherService
    {
        private readonly StoreDb _db;
        private readonly UserSession _actor;
        public SupplyVoucherService(StoreDb db, UserSession actor)
        {
            _db=db; _actor=actor;
        }
        private void Authorize()
        {
            if (!new UserAuthService(_db).Authorize(_actor,"PURCHASE"))
                throw new UnauthorizedAccessException("ليس لديك صلاحية استلام البضاعة.");
        }
        private static string Stamp()
        { return DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture); }
        private static string Date(DateTime input)
        { return input.Date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture); }
        private static SQLiteCommand Sql(SQLiteConnection c, SQLiteTransaction tx,string query,params object[] args)
        {
            var cmd=new SQLiteCommand(query,c,tx);
            for(int i=0;i<args.Length;i++)
                cmd.Parameters.AddWithValue("@p"+i,args[i]??DBNull.Value);
            return cmd;
        }
        private static long LastId(SQLiteConnection c,SQLiteTransaction tx)
        {
            using(var cmd=Sql(c,tx,"SELECT last_insert_rowid()"))
                return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
        }
        private static void Audit(SQLiteConnection c,SQLiteTransaction tx,string action,
            long id,long actor,string details)
        {
            using(var cmd=Sql(c,tx,@"INSERT INTO audit_log
                (action,entity_id,details,created_utc,actor_user_id)
                VALUES(@p0,@p1,@p2,@p3,@p4)",action,id,details,Stamp(),actor))
                cmd.ExecuteNonQuery();
        }
        private static void ValidateSupplier(SQLiteConnection c,SQLiteTransaction tx,long id)
        {
            using(var cmd=Sql(c,tx,@"SELECT COUNT(*) FROM parties
                    WHERE id=@p0 AND party_type IN ('SUPPLIER','BOTH')",id))
                if(Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture)!=1)
                    throw new ArgumentException("المورد غير موجود أو غير صالح.");
        }
        private static void ValidateLine(SQLiteConnection c,SQLiteTransaction tx,
            SupplyVoucherLine line,bool setSnapshot)
        {
            using(var cmd=Sql(c,tx,@"SELECT p.sku,p.name,u.name,u.factor_milli,p.is_countable
                FROM product_units u JOIN products p ON p.id=u.product_id
                WHERE u.id=@p0 AND p.id=@p1 AND u.active=1 AND p.active=1",
                line.ProductUnitID,line.ProductID))
            using(var r=cmd.ExecuteReader())
            {
                if(!r.Read()) throw new ArgumentException("الصنف أو وحدة القياس غير متاحة.");
                string sku=r.GetString(0),name=r.GetString(1),unit=r.GetString(2);
                long factor=r.GetInt64(3);
                bool countable=r.GetInt64(4)==1;
                if(factor!=line.UnitFactorMilli ||
                    Amounts.BaseQuantity(line.QuantityMilli,factor)!=line.BaseQuantityMilli ||
                    (countable && line.BaseQuantityMilli%1000!=0))
                    throw new ArgumentException("تحويل وحدة الصنف أو الكمية غير صحيح.");
                if(setSnapshot)
                {
                    line.ItemCode=sku;
                    line.ItemName=name;
                    line.Unit=unit;
                }
                else if(line.Unit!=unit) // unit identity is immutable by schema.
                    throw new InvalidOperationException("تغيرت وحدة الصنف؛ راجع مسودة التوريد.");
            }
        }

        // Saves an editable DRAFT. No stock movement or payable is created here.
        // Voucher code derives from AUTOINCREMENT inside the writer transaction;
        // no SELECT MAX, no race, no reused number after deleting a draft.
        public SupplyVoucherDraft SaveDraft(SupplyVoucherDraft voucher)
        {
            Authorize();
            if(voucher==null) throw new ArgumentNullException("voucher");
            if((voucher.SupplierInvoiceNo??"").Length>90 || (voucher.Notes??"").Length>2000)
                throw new ArgumentException("رقم الفاتورة أو الملاحظات أطول من الحد المسموح.");
            long savedId=voucher.VoucherID;
            string savedCode=voucher.VoucherCode;
            using(var c=_db.Open())
            using(var tx=c.BeginTransaction())
            {
                ValidateSupplier(c,tx,voucher.SupplierID);
                foreach(var line in voucher.Lines)
                {
                    // Compute amounts first, then verify identities with the database.
                    // Changing a unit factor via UI cannot forge stock quantities.
                    if(line.ProductUnitID<=0 || line.ProductID<=0)
                        throw new ArgumentException("اختر الصنف ووحدة القياس.");
                }
                var totals=SupplyVoucherCalculator.Calculate(voucher);
                foreach(var line in voucher.Lines)
                    ValidateLine(c,tx,line,true);

                if(voucher.VoucherID==0)
                {
                    using(var cmd=Sql(c,tx,@"INSERT INTO SupplyVouchers
                      (VoucherDate,SupplierID,SupplierInvoiceNo,SupplierInvoiceDate,PaymentType,
                       TaxRateBps,TaxAmountCents,HeaderDiscountCents,TotalDiscountCents,SubTotalCents,
                       GrandTotalCents,TotalWordsAr,PaidAmountCents,RemainingAmountCents,Notes,
                       CreatedByUserID,CreatedUtc,UpdatedUtc)
                      VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15,@p16,@p17)",
                      Date(voucher.VoucherDate),voucher.SupplierID,voucher.SupplierInvoiceNo??"",
                      Date(voucher.SupplierInvoiceDate),voucher.PaymentType,
                      voucher.TaxRateBps,totals.TaxAmountCents,voucher.HeaderDiscountCents,
                      totals.TotalDiscountCents,totals.SubTotalCents,totals.GrandTotalCents,
                      totals.TotalWordsAr,totals.PaidAmountCents,totals.RemainingAmountCents,
                      voucher.Notes??"",_actor.Id,Stamp(),Stamp()))
                        cmd.ExecuteNonQuery();
                    savedId=LastId(c,tx);
                    savedCode="ROF-"+savedId.ToString("D4",CultureInfo.InvariantCulture);
                    using(var cmd=Sql(c,tx,"UPDATE SupplyVouchers SET VoucherCode=@p0 WHERE VoucherID=@p1",
                        savedCode,savedId))cmd.ExecuteNonQuery();
                }
                else
                {
                    using(var cmd=Sql(c,tx,@"UPDATE SupplyVouchers SET VoucherDate=@p0,
                       SupplierID=@p1,SupplierInvoiceNo=@p2,SupplierInvoiceDate=@p3,PaymentType=@p4,
                       TaxRateBps=@p5,TaxAmountCents=@p6,HeaderDiscountCents=@p7,
                       TotalDiscountCents=@p8,SubTotalCents=@p9,GrandTotalCents=@p10,
                       TotalWordsAr=@p11,PaidAmountCents=@p12,RemainingAmountCents=@p13,
                       Notes=@p14,UpdatedUtc=@p15
                       WHERE VoucherID=@p16 AND State='DRAFT'",
                       Date(voucher.VoucherDate),voucher.SupplierID,voucher.SupplierInvoiceNo??"",
                       Date(voucher.SupplierInvoiceDate),voucher.PaymentType,voucher.TaxRateBps,
                       totals.TaxAmountCents,voucher.HeaderDiscountCents,totals.TotalDiscountCents,
                       totals.SubTotalCents,totals.GrandTotalCents,totals.TotalWordsAr,
                       totals.PaidAmountCents,totals.RemainingAmountCents,voucher.Notes??"",
                       Stamp(),voucher.VoucherID))
                        if(cmd.ExecuteNonQuery()!=1)
                            throw new InvalidOperationException("لا يمكن تعديل سند معتمد أو غير موجود.");
                    using(var cmd=Sql(c,tx,"DELETE FROM SupplyVoucherDetails WHERE VoucherID=@p0",
                        voucher.VoucherID))cmd.ExecuteNonQuery();
                }
                for(int i=0;i<voucher.Lines.Count;i++)
                {
                    var x=voucher.Lines[i];
                    using(var cmd=Sql(c,tx,@"INSERT INTO SupplyVoucherDetails
                      (VoucherID,LineNo,ProductID,ProductUnitID,ItemCode,ItemName,Unit,UnitFactorMilli,
                       QuantityMilli,BaseQuantityMilli,UnitCostCents,DiscountType,DiscountValue,
                       DiscountCents,GrossTotalCents,LineTotalCents)
                      VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12,@p13,@p14,@p15)",
                      savedId,i+1,x.ProductID,x.ProductUnitID,x.ItemCode,x.ItemName,x.Unit,
                      x.UnitFactorMilli,x.QuantityMilli,x.BaseQuantityMilli,x.UnitCostCents,
                      x.DiscountType,x.DiscountValue,x.DiscountCents,x.GrossTotalCents,x.LineTotalCents))
                        cmd.ExecuteNonQuery();
                }
                Audit(c,tx,"SUPPLY_DRAFT_SAVED",savedId,_actor.Id,
                    "Lines="+voucher.Lines.Count.ToString(CultureInfo.InvariantCulture));
                tx.Commit();
            }
            // Do not mutate the caller's identity until the DB COMMIT succeeded.
            voucher.VoucherID=savedId;
            voucher.VoucherCode=savedCode;
            return Load(savedId);
        }

        public SupplyVoucherDraft Load(long voucherId)
        {
            Authorize();
            using(var c=_db.Open())
                return LoadInternal(c,null,voucherId);
        }
        private static SupplyVoucherDraft LoadInternal(SQLiteConnection c,SQLiteTransaction tx,long id)
        {
            SupplyVoucherDraft v;
            using(var cmd=Sql(c,tx,@"SELECT VoucherID,VoucherCode,VoucherDate,SupplierID,
                    SupplierInvoiceNo,SupplierInvoiceDate,PaymentType,TaxRateBps,
                    HeaderDiscountCents,PaidAmountCents,Notes,State
                    FROM SupplyVouchers WHERE VoucherID=@p0",id))
            using(var r=cmd.ExecuteReader())
            {
                if(!r.Read())throw new ArgumentException("السند غير موجود.");
                v=new SupplyVoucherDraft {
                    VoucherID=r.GetInt64(0),VoucherCode=r.IsDBNull(1)?"":r.GetString(1),
                    VoucherDate=DateTime.ParseExact(r.GetString(2),"yyyy-MM-dd",CultureInfo.InvariantCulture),
                    SupplierID=r.GetInt64(3),SupplierInvoiceNo=r.GetString(4),
                    SupplierInvoiceDate=DateTime.ParseExact(r.GetString(5),"yyyy-MM-dd",CultureInfo.InvariantCulture),
                    PaymentType=r.GetString(6),TaxRateBps=r.GetInt64(7),
                    HeaderDiscountCents=r.GetInt64(8),PaidAmountCents=r.GetInt64(9),
                    Notes=r.GetString(10),State=r.GetString(11)
                };
            }
            using(var cmd=Sql(c,tx,@"SELECT DetailID,ProductID,ProductUnitID,ItemCode,ItemName,
                    Unit,UnitFactorMilli,QuantityMilli,BaseQuantityMilli,UnitCostCents,
                    DiscountType,DiscountValue,DiscountCents,GrossTotalCents,LineTotalCents
                    FROM SupplyVoucherDetails WHERE VoucherID=@p0 ORDER BY LineNo",id))
            using(var r=cmd.ExecuteReader())
                while(r.Read())
                    v.Lines.Add(new SupplyVoucherLine {
                        DetailID=r.GetInt64(0),ProductID=r.GetInt64(1),
                        ProductUnitID=r.GetInt64(2),ItemCode=r.GetString(3),ItemName=r.GetString(4),
                        Unit=r.GetString(5),UnitFactorMilli=r.GetInt64(6),QuantityMilli=r.GetInt64(7),
                        BaseQuantityMilli=r.GetInt64(8),UnitCostCents=r.GetInt64(9),
                        DiscountType=r.GetString(10),DiscountValue=r.GetInt64(11),
                        DiscountCents=r.GetInt64(12),GrossTotalCents=r.GetInt64(13),
                        LineTotalCents=r.GetInt64(14)
                    });
            return v;
        }

        public List<SupplyVoucherSummary> Search(string term)
        {
            Authorize();
            term=(term??"").Trim();
            if(term.Length>90)throw new ArgumentException("البحث طويل جدًا.");
            var result=new List<SupplyVoucherSummary>();
            using(var c=_db.Open())
            using(var cmd=Sql(c,null,@"SELECT v.VoucherID,v.VoucherCode,p.name,v.VoucherDate,
                    v.GrandTotalCents,v.State
                    FROM SupplyVouchers v JOIN parties p ON p.id=v.SupplierID
                    WHERE @p0='' OR v.VoucherCode LIKE @p1
                       OR p.name LIKE @p1 OR v.SupplierInvoiceNo LIKE @p1
                    ORDER BY v.VoucherID DESC LIMIT 100",term,"%"+term+"%"))
            using(var r=cmd.ExecuteReader())
                while(r.Read())
                    result.Add(new SupplyVoucherSummary {
                        VoucherID=r.GetInt64(0),VoucherCode=r.GetString(1),SupplierName=r.GetString(2),
                        VoucherDate=r.GetString(3),GrandTotalCents=r.GetInt64(4),State=r.GetString(5)
                    });
            return result;
        }

        public void DeleteDraft(long voucherId)
        {
            Authorize();
            using(var c=_db.Open())
            using(var tx=c.BeginTransaction())
            {
                using(var cmd=Sql(c,tx,"DELETE FROM SupplyVouchers WHERE VoucherID=@p0 AND State='DRAFT'",
                    voucherId))
                    if(cmd.ExecuteNonQuery()!=1)
                        throw new InvalidOperationException("لا يمكن حذف سند معتمد. استخدم سند مرتجع مستقل.");
                Audit(c,tx,"SUPPLY_DRAFT_DELETED",voucherId,_actor.Id,"Draft deleted only");
                tx.Commit();
            }
        }

        // Everything that affects financial facts and physical stock is in ONE transaction.
        public SupplyVoucherDraft Approve(long voucherId)
        {
            Authorize();
            using(var c=_db.Open())
            using(var tx=c.BeginTransaction())
            {
                var voucher=LoadInternal(c,tx,voucherId);
                if(voucher.State!="DRAFT")
                    throw new InvalidOperationException("تم اعتماد السند سابقًا؛ لا يمكن توريده مرتين.");
                ValidateSupplier(c,tx,voucher.SupplierID);
                if(!string.IsNullOrWhiteSpace(voucher.SupplierInvoiceNo))
                {
                    using(var check=Sql(c,tx,@"SELECT COUNT(*) FROM SupplyVouchers
                        WHERE SupplierID=@p0 AND SupplierInvoiceNo=@p1
                          AND State='POSTED' AND VoucherID<>@p2",
                          voucher.SupplierID,voucher.SupplierInvoiceNo,voucherId))
                        if(Convert.ToInt64(check.ExecuteScalar(),CultureInfo.InvariantCulture)>0)
                            throw new InvalidOperationException("رقم فاتورة المورد مسجل في سند معتمد آخر.");
                }
                var totals=SupplyVoucherCalculator.Calculate(voucher);
                foreach(var line in voucher.Lines)
                    ValidateLine(c,tx,line,false);
                // Never approve a tampered draft whose stored financial header differs
                // from re-calculated immutable item snapshots.
                using(var check=Sql(c,tx,@"SELECT SubTotalCents,TotalDiscountCents,TaxAmountCents,
                    GrandTotalCents,PaidAmountCents,RemainingAmountCents
                    FROM SupplyVouchers WHERE VoucherID=@p0",voucherId))
                using(var r=check.ExecuteReader())
                {
                    if(!r.Read()||r.GetInt64(0)!=totals.SubTotalCents ||
                       r.GetInt64(1)!=totals.TotalDiscountCents ||
                       r.GetInt64(2)!=totals.TaxAmountCents ||
                       r.GetInt64(3)!=totals.GrandTotalCents ||
                       r.GetInt64(4)!=totals.PaidAmountCents ||
                       r.GetInt64(5)!=totals.RemainingAmountCents)
                        throw new InvalidOperationException("أرقام المسودة غير متطابقة مع سطورها. أعد حفظ المسودة.");
                }
                var allocated=SupplyVoucherCalculator.AllocateHeaderDiscount(
                    voucher.Lines,voucher.HeaderDiscountCents);

                var netCosts=voucher.Lines.Select((x,i)=>checked(x.LineTotalCents-allocated[i])).ToList();
                var taxCosts=SupplyVoucherCalculator.AllocateCapitalizedTax(netCosts,totals.TaxAmountCents);
                long documentId;
                string now=Stamp();
                // Legacy document invariant requires full gross cost on each immutable line.
                // Net merchandise, VAT, paid and due live in the linked SupplyVouchers header.
                using(var cmd=Sql(c,tx,@"INSERT INTO documents
                    (doc_type,party_id,total_cents,created_utc,created_by_user_id,note)
                    VALUES('PURCHASE',@p0,@p1,@p2,@p3,@p4)",
                    voucher.SupplierID,totals.SubTotalCents,now,_actor.Id,
                    "Goods receipt "+voucher.VoucherCode))cmd.ExecuteNonQuery();
                documentId=LastId(c,tx);
                var documentCode="P-"+documentId.ToString("D6",CultureInfo.InvariantCulture);
                var staged=new Dictionary<long,long>();
                for(int i=0;i<voucher.Lines.Count;i++)
                {
                    var x=voucher.Lines[i];
                    long netCost=checked(netCosts[i]+taxCosts[i]);
                    if(netCost<0)throw new InvalidOperationException("خصم السند تجاوز قيمة صنف.");
                    long costPerBase=checked((long)decimal.Round(
                        (decimal)netCost*1000m/x.BaseQuantityMilli,
                        0,MidpointRounding.AwayFromZero));
                    using(var cmd=Sql(c,tx,@"INSERT INTO document_lines
                        (document_id,line_no,product_id,qty_milli,unit_price_cents,
                         line_total_cents,unit_cost_snapshot_cents,product_unit_id,
                         entered_qty_milli,entered_unit_name,unit_factor_milli,
                         entered_unit_price_cents)
                        VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11)",
                        documentId,i+1,x.ProductID,x.BaseQuantityMilli,x.UnitCostCents,
                        x.GrossTotalCents,costPerBase,x.ProductUnitID,x.QuantityMilli,x.Unit,
                        x.UnitFactorMilli,x.UnitCostCents))cmd.ExecuteNonQuery();
                    long documentLine=LastId(c,tx);
                    using(var snapshot=Sql(c,tx,"INSERT INTO purchase_cost_snapshots VALUES(@p0,@p1,@p2)",documentLine,netCost,taxCosts[i]))snapshot.ExecuteNonQuery();
                    using(var cmd=Sql(c,tx,@"INSERT INTO stock_movements
                        (document_id,document_line_id,product_id,delta_qty_milli,created_utc)
                        VALUES(@p0,@p1,@p2,@p3,@p4)",documentId,documentLine,x.ProductID,
                        x.BaseQuantityMilli,now))cmd.ExecuteNonQuery();
                    StoreService.ApplyMovingAverageCost(c,tx,x.ProductID,
                        x.BaseQuantityMilli,netCost,staged);
                }
                using(var cmd=Sql(c,tx,@"UPDATE documents SET doc_code=@p0,state='POSTED'
                    WHERE id=@p1 AND state='DRAFT'",documentCode,documentId))
                    if(cmd.ExecuteNonQuery()!=1)
                        throw new InvalidOperationException("فشل اعتماد حركة المخزون.");
                using(var cmd=Sql(c,tx,@"UPDATE SupplyVouchers SET State='POSTED',DocumentID=@p0
                    WHERE VoucherID=@p1 AND State='DRAFT'",documentId,voucherId))
                    if(cmd.ExecuteNonQuery()!=1)
                        throw new InvalidOperationException("فشل اعتماد سند التوريد.");
                if(totals.PaidAmountCents>0)
                {
                    using(var cmd=Sql(c,tx,@"INSERT INTO SupplyVoucherPayments
                       (VoucherID,AmountCents,PaymentMethod,CreatedByUserID,CreatedUtc)
                       VALUES(@p0,@p1,'CASH',@p2,@p3)",
                       voucherId,totals.PaidAmountCents,_actor.Id,now))cmd.ExecuteNonQuery();
                }
                FinanceService.PostInvoiceWithin(c,tx,documentId,true,voucher.SupplierID,
                    totals.GrandTotalCents,totals.PaidAmountCents,0,_actor.Id);
                if(InventoryValueService.Enabled(c,tx))InventoryValueService.Verify(c,tx,false);
                Audit(c,tx,"SUPPLY_POSTED",voucherId,_actor.Id,documentCode);
                tx.Commit();
            }
            return Load(voucherId);
        }
    }
}
