using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using OfflineStore.Infrastructure;
using OfflineStore.Appearance;
using OfflineStore.Devices;
using OfflineStore.Security;
using OfflineStore.Models;
using OfflineStore.Supply;

namespace OfflineStore.Services
{
    // All modifications happen here, never from a WPF view.
    public sealed class StoreService
    {
        private readonly StoreDb _db;
        public StoreService() : this(new StoreDb()) { }
        internal StoreService(StoreDb db) { _db=db ?? throw new ArgumentNullException("db"); }
        private static string Now() { return DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture); }
        private UserSession _user;
        public UserSession CurrentUser { get { return _user; } }
        public void SignIn(UserSession session)
        {
            if (!_dbUserAuthorized(session, "VIEW")) throw new UnauthorizedAccessException("جلسة الموظف غير صالحة.");
            _user = session;
        }
        private bool _dbUserAuthorized(UserSession session, string permission) { return new UserAuthService(_db).Authorize(session, permission); }
        public bool Can(string permission) { return _dbUserAuthorized(_user, permission); }
        private void Require(string permission)
        { if (!Can(permission)) throw new UnauthorizedAccessException("ليس لديك صلاحية لهذه العملية: " + permission); }
        public string DatabasePath { get { return _db.DatabasePath; } }
        public UserAuthService Users { get { return new UserAuthService(_db); } }
        public EmployeeNotesService Notes { get { return new EmployeeNotesService(_db, _user); } }
        public SupplyVoucherService SupplyVouchers { get { return new SupplyVoucherService(_db, _user); } }
        public void Initialize() { _db.Initialize(); }
        public AppearanceService Appearance { get { return new AppearanceService(_db, _user); } }
        public JournalService Journal { get { return new JournalService(_db, _user); } }
        public ReturnService Returns { get { return new ReturnService(_db, _user); } }
        public FinanceService Finance { get { return new FinanceService(_db, _user); } }
        public LocalPasswordService Passwords { get { return new LocalPasswordService(_db); } }
        public SerialScannerSettings LoadScannerSettings()
        {
            if (!Can("SALE") && !Can("PURCHASE")) throw new UnauthorizedAccessException("لا توجد صلاحية قراءة الماسح.");
            return new SerialScannerSettingsStore(_db).Load();
        }
        public void SaveScannerSettings(SerialScannerSettings data)
        {
            Require("USERS"); // Only admin changes hardware settings.
            new SerialScannerSettingsStore(_db).Save(data, _user.Id);
        }

        // Exactly the same valuation path is used by legacy purchases and goods receipts.
        internal static void ApplyMovingAverageCost(SQLiteConnection c, SQLiteTransaction tx,
            long productId, long incomingQtyMilli, long netCostCents,
            IDictionary<long,long> stagedQtyByProduct, bool allowInactive = false)
        {
            if(InventoryValueService.Enabled(c,tx))
            {
                using(var check=new SQLiteCommand("SELECT COUNT(*) FROM products WHERE id=@id AND (active=1 OR @allow=1)",c,tx))
                {check.Parameters.AddWithValue("@id",productId);check.Parameters.AddWithValue("@allow",allowInactive?1:0);if(Convert.ToInt64(check.ExecuteScalar())!=1)throw new ArgumentException("الصنف غير موجود أو غير نشط.");}
                InventoryValueService.Receive(c,tx,productId,incomingQtyMilli,netCostCents);return;
            }
            long postedQty, oldAvg;
            using (var read = new SQLiteCommand(@"SELECT s.qty_milli,p.avg_cost_millicents
                FROM products p JOIN v_stock s ON s.product_id=p.id
                WHERE p.id=@id AND (p.active=1 OR @allowInactive=1)", c, tx))
            {
                read.Parameters.AddWithValue("@id", productId);
                read.Parameters.AddWithValue("@allowInactive",allowInactive?1:0);
                using (var r = read.ExecuteReader())
                {
                    if (!r.Read()) throw new ArgumentException("الصنف غير موجود.");
                    postedQty = r.GetInt64(0);
                    oldAvg = r.GetInt64(1);
                }
            }
            long staged = stagedQtyByProduct.ContainsKey(productId) ?
                stagedQtyByProduct[productId] : 0;
            long updated = SupplyVoucherCalculator.WeightedAverageMilliCents(
                checked(postedQty + staged), oldAvg, incomingQtyMilli, netCostCents);
            long roundedCents = checked((long)decimal.Round(updated/1000m,
                0, MidpointRounding.AwayFromZero));
            using (var cmd = new SQLiteCommand(@"UPDATE products
                SET avg_cost_millicents=@avg,last_cost_cents=@cost WHERE id=@id", c, tx))
            {
                cmd.Parameters.AddWithValue("@avg", updated);
                cmd.Parameters.AddWithValue("@cost", roundedCents);
                cmd.Parameters.AddWithValue("@id", productId);
                cmd.ExecuteNonQuery();
            }
            stagedQtyByProduct[productId] = checked(staged + incomingQtyMilli);
        }

        private static SQLiteCommand Cmd(SQLiteConnection c, SQLiteTransaction tx, string sql,
            params object[] args)
        {
            var cmd = new SQLiteCommand(sql, c, tx);
            for (int i = 0; i < args.Length; i++)
                cmd.Parameters.AddWithValue("@p" + i, args[i] ?? DBNull.Value);
            return cmd;
        }

        // Preferred supplier is catalog metadata, not a purchase invoice or stock movement.
        // A new supplier is registered during the SAME transaction as the product.
        private static void SetPreferredSupplier(SQLiteConnection c, SQLiteTransaction tx,
            long productId, string supplierName, long actor)
        {
            using (var remove = Cmd(c, tx, "DELETE FROM product_preferred_suppliers WHERE product_id=@p0", productId))
                remove.ExecuteNonQuery();
            if (string.IsNullOrWhiteSpace(supplierName)) return;
            supplierName = supplierName.Trim();
            if (supplierName.Length > 120)
                throw new ArgumentException("اسم المورد طويل جدًا (الحد الأقصى 120 حرفًا).");
            long supplierId;
            using (var find = Cmd(c, tx, @"SELECT id FROM parties
                WHERE name=@p0 COLLATE NOCASE AND party_type IN ('SUPPLIER','BOTH')
                ORDER BY id LIMIT 1", supplierName))
            {
                var matched = find.ExecuteScalar();
                if (matched != null && matched != DBNull.Value)
                    supplierId = Convert.ToInt64(matched, CultureInfo.InvariantCulture);
                else
                {
                    using (var add = Cmd(c, tx, @"INSERT INTO parties(party_type,name,created_utc)
                        VALUES('SUPPLIER',@p0,@p1)", supplierName, Now()))
                        add.ExecuteNonQuery();
                    using (var last = Cmd(c, tx, "SELECT last_insert_rowid()"))
                        supplierId = Convert.ToInt64(last.ExecuteScalar(), CultureInfo.InvariantCulture);
                    using (var audit = Cmd(c, tx, @"INSERT INTO audit_log
                        (action,entity_id,actor_user_id,created_utc) VALUES('SUPPLIER_CREATED',@p0,@p1,@p2)",
                        supplierId, actor, Now())) audit.ExecuteNonQuery();
                }
            }
            using (var link = Cmd(c, tx, @"INSERT INTO product_preferred_suppliers
                (product_id,supplier_id,updated_utc) VALUES(@p0,@p1,@p2)", productId, supplierId, Now()))
                link.ExecuteNonQuery();
        }

        public void UpdateProduct(long productId, string sku, string name, long saleCents,
            long costCents, long reorderMilli, string barcode, string supplierName)
        {
            Require("CATALOG");
            if (productId <= 0 || string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("حدد صنفًا واكتب الكود والاسم.");
            sku = sku.Trim(); name = name.Trim();
            if (sku.Length > 80 || name.Length > 180 || saleCents < 0 || costCents < 0 || reorderMilli < 0)
                throw new ArgumentException("راجع الكود والاسم والأسعار والحد الأدنى.");
            barcode = string.IsNullOrWhiteSpace(barcode) ? null : barcode.Trim();
            if (barcode != null && (barcode.Length > 80 || string.Equals(barcode,sku,StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("الباركود غير صالح أو مطابق لكود الصنف.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                // Never let catalog editing overwrite a live moving-average valuation.
                // At zero stock a manager may supply an initial estimated cost.
                long stockMilli;
                using (var check = Cmd(c,tx,@"SELECT s.qty_milli,p.last_cost_cents
                    FROM products p JOIN v_stock s ON s.product_id=p.id
                    WHERE p.id=@p0",productId))
                using (var reader = check.ExecuteReader())
                {
                    if (!reader.Read()) throw new ArgumentException("الصنف غير موجود.");
                    stockMilli = reader.GetInt64(0);
                    if (stockMilli > 0 && costCents != reader.GetInt64(1))
                        throw new InvalidOperationException(
                            "تكلفة الصنف مع وجود مخزون تُحسب تلقائيًا من التوريد؛ لا يمكن تعديلها يدويًا.");
                }
                // Exclude this product from collision checks; other products and units must be unique.
                if (barcode != null)
                {
                    using (var check = Cmd(c, tx, @"SELECT
                        (SELECT COUNT(*) FROM products WHERE id<>@p1 AND (sku=@p0 OR barcode=@p0)) +
                        (SELECT COUNT(*) FROM product_units WHERE barcode=@p0)", barcode, productId))
                        if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                            throw new ArgumentException("الباركود مستخدم في صنف أو عبوة أخرى.");
                }
                using (var check = Cmd(c, tx, @"SELECT
                    (SELECT COUNT(*) FROM products WHERE id<>@p1 AND (sku=@p0 OR barcode=@p0)) +
                    (SELECT COUNT(*) FROM product_units WHERE barcode=@p0)", sku, productId))
                    if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                        throw new ArgumentException("كود الصنف مستخدم في صنف أو باركود آخر.");
                // A catalog-only edit must never round away the sub-cent moving
                // average for stock already received. An initial cost estimate
                // can be changed only when the product has no remaining stock.
                using (var update = Cmd(c, tx, @"UPDATE products SET sku=@p0,name=@p1,barcode=@p2,
                    sale_price_cents=@p3,last_cost_cents=@p4,
                    avg_cost_millicents=CASE WHEN @p7>0 THEN avg_cost_millicents ELSE @p4*1000 END,
                    reorder_qty_milli=@p5 WHERE id=@p6 AND active=1",
                    sku, name, barcode == null ? (object)DBNull.Value : barcode,
                    saleCents, costCents, reorderMilli, productId, stockMilli))
                    if (update.ExecuteNonQuery() != 1) throw new ArgumentException("الصنف غير موجود أو غير نشط.");
                using (var unit = Cmd(c, tx, @"UPDATE product_units SET sale_price_cents=@p0
                    WHERE product_id=@p1 AND is_base=1", saleCents, productId))
                    if (unit.ExecuteNonQuery() != 1) throw new InvalidOperationException("الوحدة الأساسية للصنف غير موجودة.");
                SetPreferredSupplier(c, tx, productId, supplierName, _user.Id);
                using (var audit = Cmd(c, tx, @"INSERT INTO audit_log
                    (action,entity_id,actor_user_id,created_utc) VALUES('PRODUCT_UPDATED',@p0,@p1,@p2)",
                    productId, _user.Id, Now())) audit.ExecuteNonQuery();
                tx.Commit();
            }
        }

        public void AddProduct(string sku, string name, string unit, long saleCents, long costCents,
            long reorderMilli = 0, string barcode = null, string supplierName = null)
        {
            Require("CATALOG");
            if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("كود الصنف واسمه مطلوبان.");
            unit = string.IsNullOrWhiteSpace(unit) ? "قطعة" : unit.Trim();
            if (saleCents < 0 || costCents < 0 || reorderMilli < 0) throw new ArgumentException("السعر أو حد إعادة الطلب غير صالح.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                CheckBarcode(c,tx,barcode,sku);
                using (var cmd = Cmd(c, tx, @"INSERT INTO products
                    (sku,name,unit,sale_price_cents,last_cost_cents,reorder_qty_milli,is_countable,created_utc,barcode)
                    VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)",
                    sku.Trim(), name.Trim(), string.IsNullOrWhiteSpace(unit) ? "قطعة" : unit.Trim(),
                    saleCents, costCents, reorderMilli,
                    (unit == "كيلو" || unit == "كجم" || unit == "لتر" || unit == "متر") ? 0 : 1, Now(),
                    string.IsNullOrWhiteSpace(barcode) ? (object)DBNull.Value : barcode.Trim()))
                    cmd.ExecuteNonQuery();
                long productId;
                using (var cmd = Cmd(c, tx, "SELECT last_insert_rowid()")) productId = Convert.ToInt64(cmd.ExecuteScalar());
                using (var init = Cmd(c, tx,"UPDATE products SET avg_cost_millicents=@p0 WHERE id=@p1",
                    checked(costCents*1000), productId)) init.ExecuteNonQuery();
                using (var cmd = Cmd(c, tx, @"INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,is_base)
                    VALUES(@p0,@p1,1000,@p2,1)", productId, string.IsNullOrWhiteSpace(unit) ? "قطعة" : unit.Trim(), saleCents)) cmd.ExecuteNonQuery();
                SetPreferredSupplier(c, tx, productId, supplierName, _user.Id);
                using (var cmd = Cmd(c, tx, "INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('PRODUCT_CREATED',@p0,@p1,@p2)", productId, _user.Id, Now()))
                    cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }

        public void AddSupplier(string name, string phone)
        {
            Require("CATALOG");
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("اسم المورد مطلوب.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = Cmd(c, tx,
                    "INSERT INTO parties(party_type,name,phone,created_utc) VALUES('SUPPLIER',@p0,@p1,@p2)",
                    name.Trim(), string.IsNullOrWhiteSpace(phone) ? (object)DBNull.Value : phone.Trim(), Now()))
                    cmd.ExecuteNonQuery();
                using (var cmd = Cmd(c, tx, "INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('SUPPLIER_CREATED',last_insert_rowid(),@p0,@p1)", _user.Id, Now()))
                    cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }

        public List<ProductRow> Products()
        {
            Require("VIEW");
            var items = new List<ProductRow>();
            bool showCost = Can("PURCHASE");
            using (var c = _db.Open())
            using (var cmd = Cmd(c, null, @"SELECT s.product_id,s.sku,s.name,s.unit,s.sale_price_cents,s.last_cost_cents,s.qty_milli,s.reorder_qty_milli,
                                            p.is_countable,COALESCE(p.barcode,''),COALESCE(supplier.name,'')
                                         FROM v_stock s
                                         JOIN products p ON p.id=s.product_id
                                         LEFT JOIN product_preferred_suppliers preferred ON preferred.product_id=p.id
                                         LEFT JOIN parties supplier ON supplier.id=preferred.supplier_id
                                         WHERE s.active=1 ORDER BY s.name"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) items.Add(new ProductRow
                {
                    Id = r.GetInt64(0), Sku = r.GetString(1), Name = r.GetString(2),
                    Unit = r.GetString(3), SalePriceCents = r.GetInt64(4),
                    LastCostCents = showCost ? r.GetInt64(5) : 0, StockMilli = r.GetInt64(6),
                    ReorderMilli = r.GetInt64(7), IsCountable = r.GetInt64(8) == 1,
                    Barcode = r.GetString(9), PreferredSupplier = showCost ? r.GetString(10) : ""
                });
            return items;
        }

        public List<PartyRow> Customers()
        {
            Require("SALE");var result=new List<PartyRow>();
            using(var c=_db.Open())using(var cmd=Cmd(c,null,"SELECT id,name FROM parties WHERE party_type IN ('CUSTOMER','BOTH') ORDER BY name,id"))
            using(var r=cmd.ExecuteReader())while(r.Read())result.Add(new PartyRow {Id=r.GetInt64(0),Name=r.GetString(1)});
            return result;
        }
        public List<PartyRow> Suppliers()
        {
            Require("PURCHASE");
            var items = new List<PartyRow>();
            using (var c = _db.Open())
            using (var cmd = Cmd(c, null, "SELECT id,name,COALESCE(phone,'') FROM parties WHERE party_type IN ('SUPPLIER','BOTH') ORDER BY name"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) items.Add(new PartyRow
                { Id = r.GetInt64(0), Name = r.GetString(1), Phone = r.GetString(2) });
            return items;
        }

        public SaleReceiptData ReadSaleReceipt(string code)
        {
            if (!Can("SALE") && !Can("REPORTS"))
                throw new UnauthorizedAccessException("ليست لديك صلاحية لعرض فواتير البيع.");
            return new SaleReceiptService(_db).Read(code);
        }

        public List<DocumentRow> Documents()
        {
            Require("REPORTS");
            var result = new List<DocumentRow>();
            using (var c = _db.Open())
            using (var cmd = Cmd(c, null, @"SELECT d.id,d.doc_code,d.doc_type,COALESCE(p.name,'عميل نقدي'),d.created_utc,COALESCE(sv.GrandTotalCents,d.total_cents),
                       COALESCE(u.display_name,'مستخدم قديم')
                FROM documents d LEFT JOIN parties p ON p.id=d.party_id
                  LEFT JOIN users u ON u.id=d.created_by_user_id
                  LEFT JOIN SupplyVouchers sv ON sv.DocumentID=d.id
                WHERE d.state='POSTED' ORDER BY d.id DESC LIMIT 500"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new DocumentRow
                {
                    Id = r.GetInt64(0), Code = r.GetString(1), Type = r.GetString(2),
                    Party = r.GetString(3), CreatedUtc = r.GetString(4), TotalCents = r.GetInt64(5),
                    Employee = r.GetString(6)
                });
            return result;
        }

        public List<MovementRow> Movements()
        {
            Require("REPORTS");
            var result = new List<MovementRow>();
            using (var c = _db.Open())
            using (var cmd = Cmd(c, null, @"SELECT m.reference_code,p.name,m.delta_qty_milli,m.created_utc
                FROM v_posted_stock_movements m JOIN products p ON p.id=m.product_id
                ORDER BY m.created_utc DESC,m.reference_code DESC LIMIT 500"))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) result.Add(new MovementRow
                {
                    DocumentCode = r.GetString(0), ProductName = r.GetString(1),
                    DeltaMilli = r.GetInt64(2), CreatedUtc = r.GetString(3)
                });
            return result;
        }

        // Posting is atomic: invoice + lines + stock ledger + audit in ONE transaction.
        public string Post(bool purchase, long? supplierId, IList<DraftLine> lines, long? customerId = null, long? paidCents = null)
        {
            Require(purchase ? "PURCHASE" : "SALE");
            if (lines == null || lines.Count == 0) throw new ArgumentException("الفاتورة بدون أصناف.");
            if (purchase && !supplierId.HasValue) throw new ArgumentException("حدد المورد أولاً.");
            if (lines.Any(l => l.QuantityMilli <= 0 || l.PriceCents < 0 || l.EnteredQuantityMilli <= 0 || l.ProductUnitId <= 0))
                throw new ArgumentException("يوجد سطر بكمية أو سعر غير صحيح.");
            if (lines.Count > 500) throw new ArgumentException("الحد الأقصى 500 سطر للفاتورة.");
            var type = purchase ? "PURCHASE" : "SALE";
            bool mayOverrideSalePrice = Can("OVERRIDE_PRICE");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                if (purchase)
                {
                    using (var cmd = Cmd(c, tx,
                        "SELECT COUNT(*) FROM parties WHERE id=@p0 AND party_type IN ('SUPPLIER','BOTH')", supplierId.Value))
                        if (Convert.ToInt64(cmd.ExecuteScalar()) != 1)
                            throw new ArgumentException("المورد غير موجود.");
                }
                if(!purchase && customerId.HasValue)
                    using(var cmd=Cmd(c,tx,"SELECT COUNT(*) FROM parties WHERE id=@p0 AND party_type IN ('CUSTOMER','BOTH')",customerId.Value))
                        if(Convert.ToInt64(cmd.ExecuteScalar())!=1)throw new ArgumentException("العميل غير موجود.");
                // Validate product/unit membership and a stable conversion factor at POST time.
                // Price override is forbidden for CASHIER even if UI controls are manipulated.
                var productCosts = new Dictionary<long, long>();
                foreach (var line in lines)
                {
                    using (var verify = Cmd(c, tx, @"SELECT u.factor_milli,u.name,u.sale_price_cents,p.is_countable,
                           COALESCE((SELECT pl.price_cents FROM unit_price_levels pl
                                    WHERE pl.product_unit_id=u.id AND pl.price_level='SEMI'),u.sale_price_cents),
                           COALESCE((SELECT pl.price_cents FROM unit_price_levels pl
                                    WHERE pl.product_unit_id=u.id AND pl.price_level='WHOLESALE'),u.sale_price_cents)
                         FROM product_units u JOIN products p ON p.id=u.product_id
                         WHERE u.id=@p0 AND u.product_id=@p1 AND u.active=1 AND p.active=1",
                         line.ProductUnitId, line.ProductId))
                    using (var r = verify.ExecuteReader())
                    {
                        if (!r.Read()) throw new ArgumentException("وحدة قياس غير صالحة لهذا الصنف.");
                        long factor = r.GetInt64(0);
                        string name = r.GetString(1);
                        long listedPrice = r.GetInt64(2);
                        bool pieces = r.GetInt64(3) == 1;
                        string tier = line.PriceLevel ?? "RETAIL";
                        if (!purchase && tier != "RETAIL" && tier != "SEMI" && tier != "WHOLESALE")
                            throw new ArgumentException("مستوى سعر البيع غير معروف.");
                        if (!purchase)
                            listedPrice = tier == "SEMI" ? r.GetInt64(4)
                                : tier == "WHOLESALE" ? r.GetInt64(5) : listedPrice;
                        if (line.UnitFactorMilli != factor || line.UnitName != name
                            || line.QuantityMilli != Amounts.BaseQuantity(line.EnteredQuantityMilli, factor)
                            || (pieces && line.QuantityMilli % 1000 != 0))
                            throw new ArgumentException("تحويل كمية الصنف غير صحيح.");
                        if (!purchase && !mayOverrideSalePrice && line.PriceCents != listedPrice)
                            throw new UnauthorizedAccessException("الكاشير لا يمكنه تغيير سعر البيع المسجل.");
                        if (!purchase && !mayOverrideSalePrice && line.PriceCents == 0)
                            throw new UnauthorizedAccessException("السعر صفر. اطلب من المدير مراجعة سعر الصنف قبل البيع.");
                    }
                }
                foreach (var line in lines)
                {
                    if (productCosts.ContainsKey(line.ProductId)) continue;
                    using (var cmd = Cmd(c, tx, "SELECT avg_cost_millicents FROM products WHERE id=@p0 AND active=1", line.ProductId))
                    {
                        var value = cmd.ExecuteScalar();
                        if (value == null || value == DBNull.Value) throw new ArgumentException("أحد الأصناف غير موجود.");
                        productCosts[line.ProductId] = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    }
                }
                if (!purchase)
                {
                    foreach (var group in lines.GroupBy(l => l.ProductId))
                    {
                        var requested = group.Aggregate(0L, (acc, l) => checked(acc + l.QuantityMilli));
                        using (var cmd = Cmd(c, tx,
                            "SELECT qty_milli FROM v_stock WHERE product_id=@p0", group.Key))
                            if (Convert.ToInt64(cmd.ExecuteScalar()) < requested)
                                throw new InvalidOperationException("الكمية المطلوبة أكبر من الرصيد: " + group.First().ProductName);
                    }
                }
                long total = 0;
                foreach (var line in lines)
                    total = checked(total + line.LineTotalCents);
                long paid = paidCents ?? total;
                if (!purchase && paid < total && !Can("USERS"))
                    throw new UnauthorizedAccessException("البيع الآجل أو المدفوع جزئيًا يحتاج مدير النظام. البيع النقدي للموظف مستمر بصلاحية البيع.");
                if (!purchase && paid < total && !FinanceService.IsActiveWithin(c, tx))
                    throw new InvalidOperationException("فعّل الحسابات بأرصدة افتتاحية مراجعة قبل البيع الآجل.");
                if (paid < 0 || paid > total || (!purchase && paid < total && !customerId.HasValue))
                    throw new ArgumentException("المدفوع يجب أن يكون بين صفر وإجمالي الفاتورة، مع تحديد العميل للبيع الآجل.");
                var now = Now();
                long saleCost = 0;
                var issues = new Dictionary<long, CarryingIssue>(); var offsets = new Dictionary<long, long>();
                if (!purchase && InventoryValueService.Enabled(c, tx)) foreach (var group in lines.GroupBy(l => l.ProductId))
                    issues.Add(group.Key, InventoryValueService.Issue(c,tx,group.Key, group.Aggregate(0L, (sum, l) => checked(sum + l.QuantityMilli))));
                var stagedReceiptQty = new Dictionary<long, long>();
                using (var cmd = Cmd(c, tx, @"INSERT INTO documents
                    (doc_type,party_id,total_cents,created_utc,created_by_user_id)
                    VALUES(@p0,@p1,@p2,@p3,@p4)",
                    type, purchase ? (object)supplierId.Value : customerId.HasValue ? (object)customerId.Value : DBNull.Value,
                    total, now, _user.Id))
                    cmd.ExecuteNonQuery();
                long documentId;
                using (var cmd = Cmd(c, tx, "SELECT last_insert_rowid()"))
                    documentId = Convert.ToInt64(cmd.ExecuteScalar());
                var code = (purchase ? "P-" : "S-") + documentId.ToString("D6", CultureInfo.InvariantCulture);
                for (int i = 0; i < lines.Count; i++)
                {
                    var line = lines[i];
                    var lineTotal = line.LineTotalCents;
                    // Snapshot approximate price per BASE unit only. NOT a verified valuation/COGS engine.
                    var cost = purchase
                        ? checked((long)decimal.Round((decimal)line.PriceCents * 1000m / line.UnitFactorMilli,
                            0, MidpointRounding.AwayFromZero))
                        : checked((long)decimal.Round(productCosts[line.ProductId]/1000m,
                            0, MidpointRounding.AwayFromZero)); // Legacy compatibility column only.
                    using (var cmd = Cmd(c, tx, @"INSERT INTO document_lines
                        (document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents,unit_cost_snapshot_cents,
                         product_unit_id,entered_qty_milli,entered_unit_name,unit_factor_milli,entered_unit_price_cents,sale_price_level)
                        VALUES(@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8,@p9,@p10,@p11,@p12)",
                        documentId, i + 1, line.ProductId, line.QuantityMilli, line.PriceCents, lineTotal, cost,
                        line.ProductUnitId,line.EnteredQuantityMilli,line.UnitName,line.UnitFactorMilli,line.PriceCents,
                        purchase ? (object)DBNull.Value : (object)(line.PriceLevel ?? "RETAIL")))
                        cmd.ExecuteNonQuery();
                    long lineId;
                    using (var cmd = Cmd(c, tx, "SELECT last_insert_rowid()"))
                        lineId = Convert.ToInt64(cmd.ExecuteScalar());
                    if (!purchase)
                    {
                        long offset=offsets.ContainsKey(line.ProductId)?offsets[line.ProductId]:0;
                        long actualCost=issues.ContainsKey(line.ProductId)?issues[line.ProductId].Part(offset,line.QuantityMilli):Amounts.CostTotal(line.QuantityMilli,productCosts[line.ProductId]);
                        saleCost=checked(saleCost+actualCost);
                        // Authoritative cost for new sales; old rounded snapshots are not rewritten.
                        using (var precise = Cmd(c, tx, @"INSERT INTO sale_cost_snapshots
                            (document_line_id,average_cost_millicents,total_cost_cents) VALUES(@p0,@p1,@p2)",
                            lineId, productCosts[line.ProductId],
                            actualCost))
                            precise.ExecuteNonQuery();
                        if(issues.ContainsKey(line.ProductId))InventoryValueService.Evidence(c,tx,true,lineId,issues[line.ProductId],offset);
                        offsets[line.ProductId]=checked(offset+line.QuantityMilli);
                        // Store the real catalog name once, in the same sale
                        // transaction, so later catalog edits do not rewrite
                        // the customer's historical receipt.
                        using (var snapshot = Cmd(c, tx, @"INSERT INTO sale_line_names
                            (document_line_id,product_id,name_at_sale,snapshot_source)
                            SELECT @p0,id,name,'POSTED' FROM products WHERE id=@p1 AND active=1",
                            lineId, line.ProductId))
                            if (snapshot.ExecuteNonQuery() != 1)
                                throw new InvalidOperationException("تعذر حفظ اسم الصنف في الفاتورة.");
                    }
                    using (var cmd = Cmd(c, tx, @"INSERT INTO stock_movements
                         (document_id,document_line_id,product_id,delta_qty_milli,created_utc)
                         VALUES(@p0,@p1,@p2,@p3,@p4)",
                         documentId, lineId, line.ProductId, purchase ? line.QuantityMilli : -line.QuantityMilli, now))
                        cmd.ExecuteNonQuery();
                    if (purchase)
                    {
                        using(var snapshot=Cmd(c,tx,"INSERT INTO purchase_cost_snapshots VALUES(@p0,@p1,0)",lineId,line.LineTotalCents))snapshot.ExecuteNonQuery();
                        ApplyMovingAverageCost(c, tx, line.ProductId, line.QuantityMilli,
                            line.LineTotalCents, stagedReceiptQty);
                    }
                }
                using (var cmd = Cmd(c, tx,
                    "UPDATE documents SET doc_code=@p0,state='POSTED' WHERE id=@p1 AND state='DRAFT'", code, documentId))
                    cmd.ExecuteNonQuery();
                using (var cmd = Cmd(c, tx,
                    "INSERT INTO audit_log(action,entity_id,details,created_utc,actor_user_id) VALUES('DOCUMENT_POSTED',@p0,@p1,@p2,@p3)",
                    documentId, type, now, _user.Id))
                    cmd.ExecuteNonQuery();
                FinanceService.PostInvoiceWithin(c,tx,documentId,purchase,purchase?supplierId:customerId,total,paid,saleCost,_user.Id);
                if(InventoryValueService.Enabled(c,tx))InventoryValueService.Verify(c,tx,false);
                tx.Commit();
                return code;
            }
        }

        private static void CheckBarcode(SQLiteConnection c, SQLiteTransaction tx, string barcode, string newSku)
        {
            if (string.IsNullOrWhiteSpace(barcode)) return;
            barcode = barcode.Trim();
            if (barcode.Length > 80) throw new ArgumentException("الباركود أطول من المسموح.");
            using (var cmd=Cmd(c,tx,@"SELECT (SELECT COUNT(*) FROM products WHERE barcode=@p0 OR sku=@p0) +
                (SELECT COUNT(*) FROM product_units WHERE barcode=@p0)", barcode))
                if (Convert.ToInt64(cmd.ExecuteScalar())>0)
                    throw new ArgumentException("الباركود مستخدم بالفعل في صنف أو عبوة أخرى.");
            if (!string.IsNullOrWhiteSpace(newSku) && string.Equals(barcode,newSku.Trim(),StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("أدخل باركود مختلفًا عن كود الصنف، أو اتركه فارغًا واستخدم الكود.");
        }
        public Tuple<ProductUnitRow,long> FindUnitByCode(string code)
        {
            Require("VIEW");
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("اكتب الباركود أو كود الصنف.");
            using(var c=_db.Open())
            using(var cmd=Cmd(c,null,@"SELECT u.id,u.product_id,u.name,u.factor_milli,u.sale_price_cents,
                  COALESCE((SELECT pl.price_cents FROM unit_price_levels pl
                    WHERE pl.product_unit_id=u.id AND pl.price_level='SEMI'),u.sale_price_cents),
                  COALESCE((SELECT pl.price_cents FROM unit_price_levels pl
                    WHERE pl.product_unit_id=u.id AND pl.price_level='WHOLESALE'),u.sale_price_cents)
                FROM product_units u JOIN products p ON p.id=u.product_id
                WHERE p.active=1 AND u.active=1 AND (u.barcode=@p0 OR (u.is_base=1 AND (p.sku=@p0 OR p.barcode=@p0)))",
                code.Trim()))
            using(var r=cmd.ExecuteReader())
            {
                if(!r.Read()) throw new ArgumentException("لا يوجد صنف مسجل بهذا الكود.");
                var unit=new ProductUnitRow { Id=r.GetInt64(0),ProductId=r.GetInt64(1),Name=r.GetString(2),
                    FactorMilli=r.GetInt64(3),SalePriceCents=r.GetInt64(4),
                    SemiPriceCents=r.GetInt64(5),WholesalePriceCents=r.GetInt64(6) };
                if(r.Read()) throw new InvalidOperationException("هذا الباركود مرتبط بأكثر من وحدة. راجع بيانات الأصناف.");
                return Tuple.Create(unit,unit.ProductId);
            }
        }

        public List<ProductUnitRow> Units(long productId)
        {
            Require("VIEW");
            var rows = new List<ProductUnitRow>();
            using (var c = _db.Open())
            using (var cmd = Cmd(c, null, @"SELECT u.id,u.product_id,u.name,u.factor_milli,u.sale_price_cents,
                    COALESCE((SELECT pl.price_cents FROM unit_price_levels pl
                      WHERE pl.product_unit_id=u.id AND pl.price_level='SEMI'),u.sale_price_cents),
                    COALESCE((SELECT pl.price_cents FROM unit_price_levels pl
                      WHERE pl.product_unit_id=u.id AND pl.price_level='WHOLESALE'),u.sale_price_cents)
                FROM product_units u WHERE u.product_id=@p0 AND u.active=1
                ORDER BY u.is_base DESC,u.factor_milli", productId))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) rows.Add(new ProductUnitRow {
                    Id=r.GetInt64(0),ProductId=r.GetInt64(1),Name=r.GetString(2),
                    FactorMilli=r.GetInt64(3),SalePriceCents=r.GetInt64(4),
                    SemiPriceCents=r.GetInt64(5),WholesalePriceCents=r.GetInt64(6)
                });
            return rows;
        }
        public void AddUnit(long productId, string name, long factorMilli, long salePriceCents, string barcode = null)
        {
            Require("CATALOG");
            if (string.IsNullOrWhiteSpace(name) || factorMilli <= 1000 || factorMilli % 1000 != 0
                || factorMilli > 1000000000 || salePriceCents < 0)
                throw new ArgumentException("الوحدة الإضافية يلزمها اسم وعدد صحيح أكبر من قطعة واحدة وسعر غير سالب.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                CheckBarcode(c,tx,barcode,null);
                using (var cmd=Cmd(c,tx,"INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,barcode) VALUES(@p0,@p1,@p2,@p3,@p4)",
                    productId,name.Trim(),factorMilli,salePriceCents,
                    string.IsNullOrWhiteSpace(barcode) ? (object)DBNull.Value : barcode.Trim())) cmd.ExecuteNonQuery();
                using (var cmd=Cmd(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('PRODUCT_UNIT_CREATED',last_insert_rowid(),@p0,@p1)", _user.Id,Now())) cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }
        public void SetAllUnitPrices(long unitId, long retail, long semi, long wholesale)
        {
            Require("CATALOG");
            if (retail < 0 || semi < 0 || wholesale < 0)
                throw new ArgumentException("سعر البيع يجب ألا يكون سالبًا.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                long productId;
                bool isBase;
                using (var find = Cmd(c, tx,
                    "SELECT product_id,is_base FROM product_units WHERE id=@p0 AND active=1",unitId))
                using (var reader = find.ExecuteReader())
                {
                    if (!reader.Read()) throw new ArgumentException("وحدة الصنف غير موجودة أو غير نشطة.");
                    productId = reader.GetInt64(0);
                    isBase = reader.GetInt64(1) == 1;
                }
                using (var update = Cmd(c, tx, "UPDATE product_units SET sale_price_cents=@p0 WHERE id=@p1",
                    retail,unitId)) update.ExecuteNonQuery();
                if (isBase)
                    using (var update = Cmd(c, tx,
                        "UPDATE products SET sale_price_cents=@p0 WHERE id=@p1",
                        retail,productId)) update.ExecuteNonQuery();
                foreach (var level in new [] { "SEMI", "WHOLESALE" })
                {
                    var price = level == "SEMI" ? semi : wholesale;
                    using (var update = Cmd(c,tx,@"INSERT INTO unit_price_levels
                        (product_unit_id,price_level,price_cents,updated_by_user_id,updated_utc)
                        VALUES(@p0,@p1,@p2,@p3,@p4)
                        ON CONFLICT(product_unit_id,price_level) DO UPDATE SET
                            price_cents=excluded.price_cents,
                            updated_by_user_id=excluded.updated_by_user_id,
                            updated_utc=excluded.updated_utc",
                        unitId,level,price,_user.Id,Now())) update.ExecuteNonQuery();
                }
                using (var log = Cmd(c,tx,@"INSERT INTO audit_log
                    (action,entity_id,actor_user_id,details,created_utc)
                    VALUES('UNIT_PRICE_TIERS_CHANGED',@p0,@p1,@p2,@p3)",
                    unitId,_user.Id,
                    "retail="+retail+";semi="+semi+";wholesale="+wholesale,Now()))
                    log.ExecuteNonQuery();
                tx.Commit();
            }
        }

        public void SetUnitPrice(long unitId, long salePriceCents)
        {
            Require("CATALOG");
            if (salePriceCents < 0) throw new ArgumentException("سعر البيع غير صالح.");
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                long productId;
                using(var cmd=Cmd(c,tx,"SELECT product_id FROM product_units WHERE id=@p0",unitId))
                { var val=cmd.ExecuteScalar(); if(val==null) throw new ArgumentException("الوحدة غير موجودة."); productId=Convert.ToInt64(val); }
                using(var cmd=Cmd(c,tx,"UPDATE product_units SET sale_price_cents=@p0 WHERE id=@p1",salePriceCents,unitId)) cmd.ExecuteNonQuery();
                using(var cmd=Cmd(c,tx,"UPDATE products SET sale_price_cents=@p0 WHERE id=@p1 AND EXISTS(SELECT 1 FROM product_units WHERE id=@p2 AND is_base=1)",salePriceCents,productId,unitId)) cmd.ExecuteNonQuery();
                using(var cmd=Cmd(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('UNIT_PRICE_CHANGED',@p0,@p1,@p2)",unitId,_user.Id,Now())) cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }
        public void SetReorderLevel(long productId, long milli)
        {
            Require("LOW_STOCK");
            if (milli < 0) throw new ArgumentException("الحد لا يمكن أن يكون سالبًا.");
            using (var c=_db.Open())
            using (var tx=c.BeginTransaction())
            {
                using(var cmd=Cmd(c,tx,"UPDATE products SET reorder_qty_milli=@p0 WHERE id=@p1",milli,productId))
                    if(cmd.ExecuteNonQuery()!=1) throw new ArgumentException("الصنف غير موجود.");
                using(var cmd=Cmd(c,tx,"INSERT INTO audit_log(action,entity_id,actor_user_id,created_utc) VALUES('REORDER_LEVEL_CHANGED',@p0,@p1,@p2)",productId,_user.Id,Now())) cmd.ExecuteNonQuery();
                tx.Commit();
            }
        }
        public List<LowStockRow> LowStock()
        {
            Require("VIEW");
            var items = new List<LowStockRow>();
            using(var c=_db.Open())
            using(var cmd=Cmd(c,null,"SELECT product_id,sku,name,unit,qty_milli,reorder_qty_milli FROM v_low_stock ORDER BY qty_milli ASC,name"))
            using(var r=cmd.ExecuteReader())
                while(r.Read()) items.Add(new LowStockRow {
                    ProductId=r.GetInt64(0),Sku=r.GetString(1),ProductName=r.GetString(2),Unit=r.GetString(3),
                    StockMilli=r.GetInt64(4),ReorderMilli=r.GetInt64(5)
                });
            return items;
        }

        // Only BACKUP-authorized staff can configure a target or request a manual copy.
        // Scheduled snapshots always use the same validated, preconfigured volume.
        public string ExternalBackupFolder
        {
            get { return new ExternalBackupService(_db).ConfiguredFolder; }
        }
        public string ConfigureExternalBackupFolder(string selectedFolder)
        {
            Require("BACKUP");
            return new ExternalBackupService(_db).ConfigureFolder(selectedFolder);
        }
        public string CreateExternalBackup()
        {
            Require("BACKUP");
            return new ExternalBackupService(_db).CreateManualBackup();
        }
        public string CreateScheduledExternalBackupIfDue()
        {
            Require("VIEW");
            return new ExternalBackupService(_db).CreateAutomaticBackupIfDue();
        }

        public void StageDataRelocation(string folder)
        {
            Require("USERS");
            new SqliteRecoveryService(_db).VerifyBackupFile(DatabasePath);
            new DatabaseLocationService().Stage(DatabasePath,folder);
        }
        public void StageDatabaseRestore(string backupFile)
        {
            Require("BACKUP");
            new SqliteRecoveryService(_db).StageRestore(backupFile);
        }

        public void BackupTo(string absolutePath)
        {
            Require("BACKUP");
            if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath))
                throw new ArgumentException("اختر مسارًا كاملًا للنسخة الاحتياطية.");
            if (File.Exists(absolutePath)) throw new IOException("ملف النسخة الاحتياطية موجود بالفعل.");
            if (string.Equals(Path.GetFullPath(absolutePath), Path.GetFullPath(DatabasePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("لا يمكن الكتابة فوق قاعدة البيانات الحالية.");
            using (var c = _db.Open())
            using (var cmd = Cmd(c, null, "VACUUM INTO @p0", absolutePath))
                cmd.ExecuteNonQuery();
            // A successful copy is not sufficient: test the on-disk copy before
            // reporting that a recoverable backup was created.
            new SqliteRecoveryService(_db).VerifyBackupFile(absolutePath);
        }

    }
}
