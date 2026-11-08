using System;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace OfflineStore.Infrastructure
{
    public sealed class UpgradeBackupRequiredException : IOException
    {
        public UpgradeBackupRequiredException(Exception inner) : base(
            "تعذر الوصول لوسيط النسخ قبل الترقية. أعد توصيله ثم أعد المحاولة، أو اختر وسيطًا بديلًا. لم تبدأ ترقية المخطط.\n" + inner.Message, inner) { }
    }

    public sealed class StoreDb
    {
        public string DatabasePath { get; private set; }
        private readonly bool _managedLocation;
        private bool _existingDatabaseExpected;

        public StoreDb()
        {
            _managedLocation=true;
            DatabasePath=new DatabaseLocationService().Resolve();
            _existingDatabaseExpected=File.Exists(DatabasePath);
        }

        // Explicit path is internal: integration tests must never open shop data.
        internal StoreDb(string databasePath)
        {
            if (!Path.IsPathRooted(databasePath)) throw new ArgumentException("Absolute database path required.");
            DatabasePath = Path.GetFullPath(databasePath);
            _existingDatabaseExpected=File.Exists(DatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath));
        }

        public SQLiteConnection Open()
        {
            if(_managedLocation)
            {
                var location=new DatabaseLocationService();
                if(location.HasPendingMove)throw new DatabaseLocationUnavailableException("يوجد نقل بيانات معلق. أعد التشغيل قبل متابعة العمل.");
                if(!string.Equals(location.Resolve(),DatabasePath,StringComparison.OrdinalIgnoreCase))
                    throw new DatabaseLocationUnavailableException("تغير مسار البيانات. أغلق البرنامج وأعد تشغيله قبل متابعة العمل.");
            }
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Version = 3,
                FailIfMissing = _existingDatabaseExpected
            };
            var connection = new SQLiteConnection(builder.ToString());
            try { connection.Open();_existingDatabaseExpected=true; }
            catch {connection.Dispose();throw;}
            using (var command = new SQLiteCommand("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", connection))
                command.ExecuteNonQuery();
            return connection;
        }

        private static string EmbeddedSql(string resource)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resource))
            {
                if (stream == null) throw new InvalidOperationException("ملف ترقية قاعدة البيانات غير موجود: " + resource);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        private static void ApplyScript(SQLiteConnection connection, string sql, int version, bool rebuild=false)
        {
            try
            {
                if(rebuild)using(var flags=new SQLiteCommand("PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;",connection))flags.ExecuteNonQuery();
                using(var tx=connection.BeginTransaction())
                {
                    using(var cmd=new SQLiteCommand(sql,connection,tx))cmd.ExecuteNonQuery();
                    if(version==23)InventoryValueService.Seed(connection,tx);
                    if(rebuild)
                        using(var check=new SQLiteCommand("PRAGMA foreign_key_check",connection,tx))using(var reader=check.ExecuteReader())
                            if(reader.Read())throw new InvalidOperationException("ترحيل المرتجعات أحدث رابطًا مفقودًا؛ تراجعت العملية كاملة.");
                    using(var cmd=new SQLiteCommand("PRAGMA user_version="+version.ToString(CultureInfo.InvariantCulture),connection,tx))cmd.ExecuteNonQuery();
                    tx.Commit();
                }
            }
            finally {if(rebuild)using(var flags=new SQLiteCommand("PRAGMA legacy_alter_table=OFF; PRAGMA foreign_keys=ON;",connection))flags.ExecuteNonQuery();}
        }

        // Stop at startup rather than allowing sales on a corrupt database.
        internal static void VerifyIntegrity(SQLiteConnection connection)
        {
            using (var check = new SQLiteCommand("PRAGMA quick_check;", connection))
            {
                var status = Convert.ToString(check.ExecuteScalar(), CultureInfo.InvariantCulture);
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("فشل فحص سلامة قاعدة البيانات. أوقف العمل واستعد نسخة احتياطية سليمة.");
            }
            using (var check = new SQLiteCommand("PRAGMA foreign_key_check;", connection))
            using (var reader = check.ExecuteReader())
                if (reader.Read())
                    throw new InvalidOperationException("قاعدة البيانات تحتوي روابط مفقودة. أوقف المبيعات واستعد نسخة سليمة.");

            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE name='finance_activation_boundary' AND type='table'",connection))
                if(Convert.ToInt64(table.ExecuteScalar())!=0)
                    using(var check=new SQLiteCommand(@"SELECT singleton FROM finance_activation a WHERE NOT EXISTS(SELECT 1 FROM finance_activation_boundary b WHERE b.singleton=a.singleton)
                        UNION ALL SELECT singleton FROM finance_activation_boundary WHERE journal_through_id>(SELECT COALESCE(MAX(id),0) FROM journal_entries WHERE state='POSTED') LIMIT 1",connection))
                        if(check.ExecuteScalar()!=null)throw new InvalidOperationException("حد فصل نتائج الأعمال عن الافتتاح غير مكتمل؛ يلزم استعادة نسخة سليمة.");

            if(InventoryValueService.Enabled(connection))InventoryValueService.Verify(connection);

            // Structural integrity is not enough: a syntactically valid SQLite file
            // may contain a mismatched invoice header or incomplete stock ledger.
            // Never repair money or inventory automatically; only detect and stop.
            const string invalidPosted = @"SELECT d.id FROM documents d
WHERE d.state='POSTED' AND (
 d.doc_code IS NULL
 OR NOT EXISTS (SELECT 1 FROM document_lines l WHERE l.document_id=d.id)
 OR d.total_cents<>COALESCE(
      (SELECT SUM(l.line_total_cents) FROM document_lines l WHERE l.document_id=d.id),0)
 OR EXISTS (SELECT 1 FROM document_lines l WHERE l.document_id=d.id AND
      (typeof(l.qty_milli)<>'integer'
       OR typeof(l.entered_qty_milli)<>'integer'
       OR typeof(l.unit_factor_milli)<>'integer'
       OR typeof(l.entered_unit_price_cents)<>'integer'
       OR typeof(l.unit_price_cents)<>'integer'
       OR typeof(l.line_total_cents)<>'integer'
       OR l.unit_price_cents<>l.entered_unit_price_cents
       OR l.qty_milli<>(l.entered_qty_milli*l.unit_factor_milli)/1000
       OR typeof(l.entered_qty_milli*(l.entered_unit_price_cents/1000)
         +(l.entered_qty_milli*(l.entered_unit_price_cents%1000)+500)/1000)<>'integer'
       OR l.line_total_cents<>l.entered_qty_milli*(l.entered_unit_price_cents/1000)
         +(l.entered_qty_milli*(l.entered_unit_price_cents%1000)+500)/1000))
 OR EXISTS (
    SELECT 1 FROM document_lines l
    LEFT JOIN stock_movements m ON m.document_line_id=l.id
    WHERE l.document_id=d.id AND (
      m.id IS NULL OR m.document_id<>d.id OR m.product_id<>l.product_id
      OR m.delta_qty_milli<>CASE WHEN d.doc_type='PURCHASE'
         THEN l.qty_milli ELSE -l.qty_milli END))
 OR EXISTS (
    SELECT 1 FROM stock_movements m
    LEFT JOIN document_lines l ON l.id=m.document_line_id
    WHERE m.document_id=d.id AND (l.id IS NULL OR l.document_id<>d.id))
) LIMIT 1";
            using (var check = new SQLiteCommand(invalidPosted, connection))
            {
                var invalidId = check.ExecuteScalar();
                if (invalidId != null && invalidId != DBNull.Value)
                    throw new InvalidOperationException("فاتورة معتمدة غير متوازنة رقم " +
                        Convert.ToString(invalidId, CultureInfo.InvariantCulture) +
                        ". أوقف البيع وافحص نسخة احتياطية دون تعديل البيانات الأصلية.");
            }
            // Validate both business ledgers before advertising a recoverable backup.
            const string invalidSupply = @"WITH item_totals AS (
 SELECT VoucherID,COUNT(*) AS line_count,SUM(GrossTotalCents) AS gross,
 SUM(DiscountCents) AS discounts,SUM(LineTotalCents) AS net
 FROM SupplyVoucherDetails GROUP BY VoucherID
), paid AS (
 SELECT VoucherID,SUM(AmountCents) AS amount FROM SupplyVoucherPayments GROUP BY VoucherID
)
SELECT v.VoucherID FROM SupplyVouchers v
LEFT JOIN item_totals t ON t.VoucherID=v.VoucherID
LEFT JOIN paid p ON p.VoucherID=v.VoucherID
LEFT JOIN documents d ON d.id=v.DocumentID
WHERE
 COALESCE(t.line_count,0)=0
 OR typeof(v.SubTotalCents)<>'integer' OR typeof(v.GrandTotalCents)<>'integer'
 OR typeof(v.HeaderDiscountCents)<>'integer' OR typeof(v.TotalDiscountCents)<>'integer'
 OR typeof(v.TaxAmountCents)<>'integer' OR typeof(v.TaxRateBps)<>'integer'
 OR typeof(v.PaidAmountCents)<>'integer' OR typeof(v.RemainingAmountCents)<>'integer'
 OR v.SubTotalCents<>t.gross OR v.TotalDiscountCents<>t.discounts+v.HeaderDiscountCents
 OR v.HeaderDiscountCents>=t.net
 OR v.GrandTotalCents<>v.SubTotalCents-v.TotalDiscountCents+v.TaxAmountCents
 OR v.PaidAmountCents+v.RemainingAmountCents<>v.GrandTotalCents
 OR v.TaxAmountCents<>(t.net-v.HeaderDiscountCents)/10000*v.TaxRateBps
    +(((t.net-v.HeaderDiscountCents)%10000)*v.TaxRateBps+5000)/10000
 OR (v.PaymentType='CASH' AND v.RemainingAmountCents<>0)
 OR EXISTS(SELECT 1 FROM SupplyVoucherDetails x WHERE x.VoucherID=v.VoucherID AND (
    typeof(x.QuantityMilli)<>'integer' OR typeof(x.UnitFactorMilli)<>'integer'
    OR typeof(x.BaseQuantityMilli)<>'integer' OR typeof(x.UnitCostCents)<>'integer'
    OR typeof(x.DiscountValue)<>'integer' OR typeof(x.DiscountCents)<>'integer'
    OR typeof(x.GrossTotalCents)<>'integer' OR typeof(x.LineTotalCents)<>'integer'
    OR x.QuantityMilli>1000000000 OR x.UnitFactorMilli>1000000000
    OR x.UnitFactorMilli%1000<>0 OR x.BaseQuantityMilli<>x.QuantityMilli*(x.UnitFactorMilli/1000)
    OR typeof(x.QuantityMilli*(x.UnitCostCents/1000)+(x.QuantityMilli*(x.UnitCostCents%1000)+500)/1000)<>'integer'
    OR x.GrossTotalCents<>x.QuantityMilli*(x.UnitCostCents/1000)+(x.QuantityMilli*(x.UnitCostCents%1000)+500)/1000
    OR x.DiscountCents>=x.GrossTotalCents OR x.LineTotalCents<>x.GrossTotalCents-x.DiscountCents
    OR (x.DiscountType='AMOUNT' AND x.DiscountCents<>x.DiscountValue)
    OR (x.DiscountType='PERCENT' AND (x.DiscountValue>10000 OR x.DiscountCents<>
       x.GrossTotalCents/10000*x.DiscountValue+((x.GrossTotalCents%10000)*x.DiscountValue+5000)/10000))
 ))
 OR EXISTS(SELECT 1 FROM SupplyVoucherPayments q WHERE q.VoucherID=v.VoucherID
     AND typeof(q.AmountCents)<>'integer')
 OR (v.State='DRAFT' AND COALESCE(p.amount,0)<>0)
 OR (v.State='POSTED' AND (
    COALESCE(p.amount,0)<>v.PaidAmountCents
    OR d.id IS NULL OR d.state<>'POSTED' OR d.doc_type<>'PURCHASE'
    OR d.party_id<>v.SupplierID OR d.total_cents<>v.SubTotalCents
    OR t.line_count<>(SELECT COUNT(*) FROM document_lines l WHERE l.document_id=v.DocumentID)
    OR EXISTS(SELECT 1 FROM SupplyVoucherDetails x WHERE x.VoucherID=v.VoucherID AND NOT EXISTS(
       SELECT 1 FROM document_lines l WHERE l.document_id=v.DocumentID AND l.line_no=x.LineNo
        AND l.product_id=x.ProductID AND l.product_unit_id=x.ProductUnitID
        AND l.qty_milli=x.BaseQuantityMilli AND l.entered_qty_milli=x.QuantityMilli
        AND l.unit_factor_milli=x.UnitFactorMilli AND l.entered_unit_name=x.Unit
        AND l.entered_unit_price_cents=x.UnitCostCents AND l.line_total_cents=x.GrossTotalCents))
 ))
LIMIT 1";
            using (var check = new SQLiteCommand(invalidSupply, connection))
            {
                var invalid = check.ExecuteScalar();
                if (invalid != null && invalid != DBNull.Value)
                    throw new InvalidOperationException("سند توريد أو مدفوعاته غير متزنة رقم " + invalid +
                        ". لم تُعدل البيانات. اختر نسخة سليمة أو راجع المستند الأصلي.");
            }
            bool hasJournal;
            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='journal_entries'",connection))
                hasJournal=Convert.ToInt64(table.ExecuteScalar())!=0;
            if(hasJournal)
            {
                const string invalidJournal = @"SELECT e.id FROM journal_entries e WHERE
                    (e.state='POSTED' AND ((SELECT COUNT(*) FROM journal_lines WHERE entry_id=e.id)<2 OR
                     (SELECT SUM(debit_cents) FROM journal_lines WHERE entry_id=e.id)<>
                     (SELECT SUM(credit_cents) FROM journal_lines WHERE entry_id=e.id)))
                    OR EXISTS(SELECT 1 FROM journal_lines l WHERE l.entry_id=e.id AND (
                     (l.account_code='RECEIVABLES' AND NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=l.party_id AND p.party_type IN ('CUSTOMER','BOTH')))
                     OR (l.account_code='PAYABLES' AND NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=l.party_id AND p.party_type IN ('SUPPLIER','BOTH'))))) LIMIT 1";
                using(var check=new SQLiteCommand(invalidJournal,connection))
                    if(check.ExecuteScalar()!=null) throw new InvalidOperationException("دفتر الأموال غير متزن أو مرتبط بطرف غير صالح.");
            }
            bool hasFinance;
            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='finance_operations'",connection))
                hasFinance=Convert.ToInt64(table.ExecuteScalar())!=0;
            if(hasFinance)
            {
                using(var check=new SQLiteCommand(@"SELECT entry_id FROM v_invalid_finance_operations
                    UNION ALL SELECT id FROM journal_entries e WHERE state='POSTED'
                    AND source_kind IN ('CUSTOMER_PAYMENT','SUPPLIER_PAYMENT','EXPENSE')
                    AND NOT EXISTS(SELECT 1 FROM finance_operations f WHERE f.entry_id=e.id) LIMIT 1",connection))
                    if(check.ExecuteScalar()!=null)throw new InvalidOperationException("حركة تحصيل أو سداد أو مصروف غير متطابقة مع دفتر الأموال.");
                using(var check=new SQLiteCommand(@"SELECT l.id FROM journal_lines l WHERE
                    (account_code='CUSTOMER_ADVANCES' AND NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=l.party_id AND p.party_type IN ('CUSTOMER','BOTH')))
                    OR (account_code='SUPPLIER_ADVANCES' AND NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=l.party_id AND p.party_type IN ('SUPPLIER','BOTH'))) LIMIT 1",connection))
                    if(check.ExecuteScalar()!=null)throw new InvalidOperationException("رصيد مقدم مرتبط بطرف غير صالح.");
            }
            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='advance_applications'",connection))
                if(Convert.ToInt64(table.ExecuteScalar())!=0)
                    using(var check=new SQLiteCommand("SELECT entry_id FROM v_invalid_advance_applications LIMIT 1",connection))
                        if(check.ExecuteScalar()!=null)throw new InvalidOperationException("تسوية رصيد مقدم لا تطابق قيدها المالي.");
            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='invoice_allocations'",connection))
                if(Convert.ToInt64(table.ExecuteScalar())!=0)
                    using(var check=new SQLiteCommand("SELECT id FROM v_invalid_invoice_allocations LIMIT 1",connection))
                        if(check.ExecuteScalar()!=null)throw new InvalidOperationException("توزيع تحصيل أو سداد غير مطابق للفاتورة أو الطرف أو قيمة الحركة.");
            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='return_documents'",connection))
                if(Convert.ToInt64(table.ExecuteScalar())!=0)ReturnService.VerifyIntegrity(connection);
            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='cash_fundings'",connection))
                if(Convert.ToInt64(table.ExecuteScalar())!=0)
                    using(var check=new SQLiteCommand("SELECT entry_id FROM v_invalid_cash_fundings LIMIT 1",connection))
                        if(check.ExecuteScalar()!=null)throw new InvalidOperationException("تمويل خزنة غير مطابق لقيده المالي.");
            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='finance_legacy_documents'",connection))
                if(Convert.ToInt64(table.ExecuteScalar())!=0)
                    using(var check=new SQLiteCommand(@"SELECT document_id FROM v_invalid_financial_documents UNION ALL
                        SELECT x.document_id FROM finance_legacy_documents x JOIN finance_activation a ON x.document_id>a.last_legacy_document_id LIMIT 1",connection))
                        if(check.ExecuteScalar()!=null)throw new InvalidOperationException("فاتورة بعد التفعيل غير مرتبطة بقيدها المالي أو لقطة تكلفتها، أو سجل الفصل غير صالح.");
            using(var table=new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='return_credit_applications'",connection))
                if(Convert.ToInt64(table.ExecuteScalar())!=0)
                {
                    using(var check=new SQLiteCommand("SELECT entry_id FROM v_invalid_return_credit_applications LIMIT 1",connection))
                        if(check.ExecuteScalar()!=null)throw new InvalidOperationException("تطبيق رصيد دائن لا يطابق الطرف أو القيد.");
                    using(var check=new SQLiteCommand(@"SELECT id FROM journal_lines l WHERE
                        (account_code='CUSTOMER_CREDITS' AND NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=l.party_id AND p.party_type IN ('CUSTOMER','BOTH')))
                        OR (account_code='SUPPLIER_CREDITS' AND NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=l.party_id AND p.party_type IN ('SUPPLIER','BOTH'))) LIMIT 1",connection))
                        if(check.ExecuteScalar()!=null)throw new InvalidOperationException("رصيد دائن مرتبط بطرف غير صالح.");
                }
            using (var table = new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='stock_adjustments'", connection))
                if (Convert.ToInt64(table.ExecuteScalar()) != 0)
                {
                    using (var check = new SQLiteCommand(@"SELECT a.id FROM stock_adjustments a WHERE a.state='POSTED'
                        AND NOT EXISTS(SELECT 1 FROM stock_adjustment_lines l WHERE l.adjustment_id=a.id) LIMIT 1", connection))
                        if (check.ExecuteScalar() != null) throw new InvalidOperationException("سند تسوية جردية معتمد بدون سطور.");
                }
            using (var table = new SQLiteCommand("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='stock_scrap_vouchers'", connection))
                if (Convert.ToInt64(table.ExecuteScalar()) != 0)
                {
                    using (var check = new SQLiteCommand(@"SELECT s.id FROM stock_scrap_vouchers s WHERE s.state='POSTED'
                        AND NOT EXISTS(SELECT 1 FROM stock_scrap_lines l WHERE l.scrap_id=s.id) LIMIT 1", connection))
                        if (check.ExecuteScalar() != null) throw new InvalidOperationException("سند إتلاف معتمد بدون سطور.");
                }
            using (var check = new SQLiteCommand(
                "SELECT product_id FROM v_stock WHERE qty_milli < 0 LIMIT 1;", connection))
            {
                var product = check.ExecuteScalar();
                if (product != null && product != DBNull.Value)
                    throw new InvalidOperationException("رصيد مخزون سالب للصنف رقم " +
                        Convert.ToString(product, CultureInfo.InvariantCulture) + ".");
            }
            using (var check = new SQLiteCommand(
                @"SELECT d.id FROM documents d WHERE d.state='DRAFT' AND
                  EXISTS (SELECT 1 FROM stock_movements m WHERE m.document_id=d.id) LIMIT 1;", connection))
            {
                var draft = check.ExecuteScalar();
                if (draft != null && draft != DBNull.Value)
                    throw new InvalidOperationException("يوجد مستند غير معتمد به حركات مخزون (" +
                        Convert.ToString(draft, CultureInfo.InvariantCulture) +
                        "). أوقف العمليات لحين مراجعة المستند؛ لن يُحتسب ضمن الرصيد.");
            }
        }

        private void EnsureUpgradeBackupReady(long version)
        {
            if (version < 9) return;
            var external = new ExternalBackupService(this);
            try
            {
                if (string.IsNullOrWhiteSpace(external.ConfiguredFolder)) return;
                external.EnsureConfiguredFolderReady();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is InvalidOperationException || ex is ArgumentException)
            { throw new UpgradeBackupRequiredException(ex); }
            try { external.CreateManualBackup(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            { throw new UpgradeBackupRequiredException(ex); }
            // Integrity/SQLite failures propagate as data errors, never as a missing USB.
        }

        public void Initialize()
        {
            new SqliteRecoveryService(this).ApplyPendingRestoreAtStartup();
            using (var connection = Open())
            {
                using (var cmd = new SQLiteCommand("PRAGMA journal_mode=WAL;", connection))
                    cmd.ExecuteScalar();
                long version;
                using (var cmd = new SQLiteCommand("PRAGMA user_version;", connection))
                    version = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                var existingVersion = version;
                if (version == 24) { VerifyIntegrity(connection); return; }
                if (version != 0 && version != 1 && version != 2 && version != 3 && version != 4 && version != 5 && version != 6 && version != 7 && version != 8 && version != 9 && version != 10 && version != 11 && version != 12 && version != 13 && version != 14 && version != 15 && version != 16 && version != 17 && version != 18 && version != 19 && version != 20 && version != 21 && version != 22 && version != 23)
                    throw new InvalidOperationException("قاعدة البيانات من إصدار غير مدعوم. لا يمكن فتحها بأمان.");

                // Resolve a disconnected configured target BEFORE applying any migration.
                EnsureUpgradeBackupReady(version);
                if (version == 0)
                {
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Schema.sql"), 1);
                    version = 1;
                }
                else if (version == 1)
                {
                    // A consistent snapshot BEFORE modifying an existing production database.
                    var file = Path.Combine(Path.GetDirectoryName(DatabasePath),
                        "store-before-v2-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                        + "-" + Guid.NewGuid().ToString("N") + ".db");
                    using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                    {
                        backup.Parameters.AddWithValue("@destination", file);
                        backup.ExecuteNonQuery();
                    }
                }
                if (version == 1)
                {
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.002_settings_and_security.sql"), 2);
                    version = 2;
                }
                if (version == 2)
                {
                    // Save a consistent pre-upgrade snapshot for existing v2 databases.
                    if (existingVersion == 2)
                    {
                        var file = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v3-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", file);
                            backup.ExecuteNonQuery();
                        }
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.003_units_users_alerts.sql"), 3);
                    version = 3;
                }
                if (version == 3)
                {
                    if (existingVersion == 3)
                    {
                        var file = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v4-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", file);
                            backup.ExecuteNonQuery();
                        }
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.004_serial_scanner.sql"), 4);
                    version = 4;
                }
                if (version == 4)
                {
                    if (existingVersion == 4)
                    {
                        var file = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v5-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", file);
                            backup.ExecuteNonQuery();
                        }
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.005_invoice_safety.sql"), 5);
                    version = 5;
                }
                if (version == 5)
                {
                    if (existingVersion == 5)
                    {
                        var file = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v6-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", file);
                            backup.ExecuteNonQuery();
                        }
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.006_posted_only_stock.sql"), 6);
                    version = 6;
                }
                if (version == 6)
                {
                    if (existingVersion == 6)
                    {
                        var file = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v7-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", file);
                            backup.ExecuteNonQuery();
                        }
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.007_employee_notes.sql"), 7);
                    version = 7;
                }
                if (version == 7)
                {
                    if (existingVersion == 7)
                    {
                        var file = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v8-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", file);
                            backup.ExecuteNonQuery();
                        }
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.008_product_supplier.sql"), 8);
                    version = 8;
                }
                if (version == 8)
                {
                    if (existingVersion == 8)
                    {
                        var file = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v9-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                            + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", file);
                            backup.ExecuteNonQuery();
                        }
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.009_supply_vouchers.sql"), 9);
                    version = 9;
                }
                if (version == 9)
                {
                    if (existingVersion == 9)
                    {
                        // Keep a consistent rollback copy of the original database.
                        // A configured USB also receives a verified copy before migration.
                        var before = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v10-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",
                                CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", before);
                            backup.ExecuteNonQuery();
                        }
                        // Configured external copy was verified by the upgrade preflight.
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.010_sale_line_names.sql"), 10);
                    version = 10;
                }
                if (version == 10)
                {
                    if (existingVersion == 10)
                    {
                        var before = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v11-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",
                                CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", before);
                            backup.ExecuteNonQuery();
                        }
                        // A separately configured backup target remains mandatory if
                        // available: do not silently downgrade to C: after a USB loss.
                        // Configured external copy was verified by the upgrade preflight.
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.011_employee_permissions.sql"), 11);
                    version = 11;
                }
                if (version == 11)
                {
                    if (existingVersion == 11)
                    {
                        var before = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v12-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss",
                                CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        {
                            backup.Parameters.AddWithValue("@destination", before);
                            backup.ExecuteNonQuery();
                        }
                        // Configured external copy was verified by the upgrade preflight.
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.012_price_levels.sql"), 12);
                    version = 12;
                }
                if (version == 12)
                {
                    if (existingVersion == 12)
                    {
                        var before = Path.Combine(Path.GetDirectoryName(DatabasePath),
                            "store-before-v13-" + Guid.NewGuid().ToString("N") + ".db");
                        using (var backup = new SQLiteCommand("VACUUM INTO @destination", connection))
                        { backup.Parameters.AddWithValue("@destination", before); backup.ExecuteNonQuery(); }
                    }
                    ApplyScript(connection, EmbeddedSql("OfflineStore.Infrastructure.Migrations.013_precise_sale_costs.sql"), 13);
                    version = 13;
                }
                if (version == 13)
                {
                    if (existingVersion == 13)
                    {
                        var before=Path.Combine(Path.GetDirectoryName(DatabasePath),"store-before-v14-"+Guid.NewGuid().ToString("N")+".db");
                        using(var backup=new SQLiteCommand("VACUUM INTO @destination",connection))
                        {backup.Parameters.AddWithValue("@destination",before);backup.ExecuteNonQuery();}
                    }
                    ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.014_journal_foundation.sql"),14);
                    version=14;
                }
                if(version==14)
                {
                    if(existingVersion==14)
                    {
                        var before=Path.Combine(Path.GetDirectoryName(DatabasePath),"store-before-v15-"+Guid.NewGuid().ToString("N")+".db");
                        using(var backup=new SQLiteCommand("VACUUM INTO @destination",connection))
                        {backup.Parameters.AddWithValue("@destination",before);backup.ExecuteNonQuery();}
                    }
                    ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.015_settlements.sql"),15);
                    version=15;
                }
                if(version==15)
                {
                    if(existingVersion==15)
                    {
                        var before=Path.Combine(Path.GetDirectoryName(DatabasePath),"store-before-v16-"+Guid.NewGuid().ToString("N")+".db");
                        using(var backup=new SQLiteCommand("VACUUM INTO @destination",connection))
                        {backup.Parameters.AddWithValue("@destination",before);backup.ExecuteNonQuery();}
                    }
                    ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.016_advance_applications.sql"),16);
                    version=16;
                }
                if(version==16)
                {
                    if(existingVersion==16)
                    {
                        var before=Path.Combine(Path.GetDirectoryName(DatabasePath),"store-before-v17-"+Guid.NewGuid().ToString("N")+".db");
                        using(var backup=new SQLiteCommand("VACUUM INTO @destination",connection))
                        {backup.Parameters.AddWithValue("@destination",before);backup.ExecuteNonQuery();}
                    }
                    ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.017_invoice_allocations.sql"),17);
                    version=17;
                }
                if(version==17)
                {
                    if(existingVersion==17)
                    {
                        var before=Path.Combine(Path.GetDirectoryName(DatabasePath),"store-before-v18-"+Guid.NewGuid().ToString("N")+".db");
                        using(var backup=new SQLiteCommand("VACUUM INTO @destination",connection))
                        {backup.Parameters.AddWithValue("@destination",before);backup.ExecuteNonQuery();}
                    }
                    ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.018_returns.sql"),18);
                    version=18;
                }
                if(version==18)
                {
                    if(existingVersion==18)
                    {
                        var before=Path.Combine(Path.GetDirectoryName(DatabasePath),"store-before-v19-"+Guid.NewGuid().ToString("N")+".db");
                        using(var backup=new SQLiteCommand("VACUUM INTO @destination",connection))
                        {backup.Parameters.AddWithValue("@destination",before);backup.ExecuteNonQuery();}
                    }
                    ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.019_cash_control.sql"),19);
                    version=19;
                }
                if(version==19)
                {
                    ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.020_cutover_integrity.sql"),20);
                    version=20;
                }
                if(version==20)
                {
                    if(existingVersion>0)
                    {
                        var before=Path.Combine(Path.GetDirectoryName(DatabasePath),"store-before-v21-"+Guid.NewGuid().ToString("N")+".db");
                        using(var backup=new SQLiteCommand("VACUUM INTO @destination",connection)){backup.Parameters.AddWithValue("@destination",before);backup.ExecuteNonQuery();}
                    }
                    ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.021_return_credits.sql"),21,true);
                    version=21;
                }
                if(version==21){ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.022_activation_results.sql"),22);version=22;}
                if(version==22){ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.023_inventory_carrying.sql"),23);version=23;}
                if(version==23){ApplyScript(connection,EmbeddedSql("OfflineStore.Infrastructure.Migrations.024_zero_value_returns.sql"),24);version=24;}
                VerifyIntegrity(connection);
            }
        }
    }
}
