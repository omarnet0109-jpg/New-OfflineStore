def migration_paths():
    paths = list((INFRA/"Migrations").glob("*.sql"))
    for extra in ["025_inventory_adjustments_and_scrap.sql", "026_cashier_shifts.sql", "027_customer_credit_and_aging.sql", "028_sales_discounts_and_tax.sql", "029_return_policy_and_disposition.sql", "030_invoice_reversals.sql"]:
        ep = (INFRA/"Migrations")/extra
        if ep.is_file() and ep not in paths:
            paths.append(ep)
    return sorted(paths, key=lambda x: int(x.name[:3]))

"""Integration regressions for the production v9 supply schema and ledger triggers."""
import pathlib
import sqlite3
import unittest

ROOT=pathlib.Path(__file__).resolve().parents[1]
INFRA=ROOT/"src"/"OfflineStore"/"Infrastructure"


def database(up_to=None):
    db=sqlite3.connect(":memory:")
    db.execute("PRAGMA foreign_keys=ON")
    db.executescript((INFRA/"Schema.sql").read_text(encoding="utf-8"))
    for path in migration_paths():
        if up_to is not None and int(path.name[:3])>up_to: break
        db.executescript(path.read_text(encoding="utf-8"))
    db.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
        VALUES('admin','مدير','ADMIN',zeroblob(32),zeroblob(32),210000,'now')""")
    db.execute("""INSERT INTO products(sku,name,unit,created_utc,last_cost_cents,avg_cost_millicents)
        VALUES('RICE','أرز','قطعة','now',1000,1000000)""")
    db.execute("""INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,is_base)
        VALUES(1,'قطعة',1000,1500,1)""")
    db.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','شركة المواد','now')")
    db.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('CUSTOMER','عميل','now')")
    db.commit()
    return db


def voucher(db, voucher_id=None, payment_type="CREDIT", paid=0, due=10000,
            supplier=1, external="SUP-001", invoice_date="2026-11-01", receipt_date="2026-11-03"):
    cursor=db.execute("""INSERT INTO SupplyVouchers(
        VoucherCode,VoucherDate,SupplierID,SupplierInvoiceNo,SupplierInvoiceDate,
        PaymentType,TaxRateBps,TaxAmountCents,HeaderDiscountCents,TotalDiscountCents,
        SubTotalCents,GrandTotalCents,TotalWordsAr,PaidAmountCents,RemainingAmountCents,
        CreatedByUserID,CreatedUtc,UpdatedUtc)
        VALUES(NULL,?,?,?,?,?,0,0,0,0,10000,10000,'عشرة آلاف جنيه',?,?,1,'now','now')""",
        (receipt_date,supplier,external,invoice_date,payment_type,paid,due))
    id_=cursor.lastrowid
    db.execute("UPDATE SupplyVouchers SET VoucherCode=? WHERE VoucherID=?",(f"ROF-{id_:04d}",id_))
    db.execute("""INSERT INTO SupplyVoucherDetails(
        VoucherID,LineNo,ProductID,ProductUnitID,ItemCode,ItemName,Unit,
        UnitFactorMilli,QuantityMilli,BaseQuantityMilli,UnitCostCents,
        DiscountType,DiscountValue,DiscountCents,GrossTotalCents,LineTotalCents)
        VALUES(?,1,1,1,'RICE','أرز','قطعة',1000,1000,1000,10000,'AMOUNT',0,0,10000,10000)""",(id_,))
    return id_


def legacy_purchase(db, supplier=1):
    doc=db.execute("""INSERT INTO documents(doc_type,party_id,total_cents,created_utc,created_by_user_id)
        VALUES('PURCHASE',?,10000,'now',1)""",(supplier,)).lastrowid
    line=db.execute("""INSERT INTO document_lines(
        document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents,
        unit_cost_snapshot_cents,product_unit_id,entered_qty_milli,entered_unit_name,
        unit_factor_milli,entered_unit_price_cents)
        VALUES(?,1,1,1000,10000,10000,10000,1,1000,'قطعة',1000,10000)""",(doc,)).lastrowid
    db.execute("""INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc)
        VALUES(?,?,1,1000,'now')""",(doc,line))
    db.execute("UPDATE documents SET doc_code=?,state='POSTED' WHERE id=?",(f"P-{doc:06d}",doc))
    return doc


class SupplyVoucherDatabaseTests(unittest.TestCase):
    def setUp(self): self.db=database()
    def tearDown(self): self.db.close()

    def test_real_v9_migration(self):
        self.assertEqual(len(sorted((INFRA/"Migrations").glob("*.sql"))), 23)
        self.assertIn("SupplyVouchers",
            [x[0] for x in self.db.execute("SELECT name FROM sqlite_master WHERE type='table'")])
        self.assertIn("avg_cost_millicents",[x[1] for x in self.db.execute("PRAGMA table_info(products)")])

    def test_supplier_must_exist_and_be_supplier(self):
        with self.assertRaisesRegex(sqlite3.IntegrityError,"SUPPLY_REQUIRES_SUPPLIER"):
            voucher(self.db,supplier=2)

    def test_invoice_date_not_in_future_of_receipt(self):
        with self.assertRaises(sqlite3.IntegrityError):
            voucher(self.db,invoice_date="2026-11-04")

    def test_cash_must_be_fully_paid(self):
        with self.assertRaises(sqlite3.IntegrityError):
            voucher(self.db,payment_type="CASH",paid=0,due=10000)

    def test_master_detail_fk_and_draft_cascade(self):
        v=voucher(self.db)
        self.assertEqual(1,self.db.execute("SELECT COUNT(*) FROM SupplyVoucherDetails").fetchone()[0])
        self.db.execute("DELETE FROM SupplyVouchers WHERE VoucherID=?",(v,))
        self.assertEqual(0,self.db.execute("SELECT COUNT(*) FROM SupplyVoucherDetails").fetchone()[0])

    def test_receipt_code_not_reused_after_draft_deleted(self):
        a=voucher(self.db)
        self.db.execute("DELETE FROM SupplyVouchers WHERE VoucherID=?",(a,))
        b=voucher(self.db)
        self.assertGreater(b,a)
        self.assertEqual(f"ROF-{b:04d}",self.db.execute(
            "SELECT VoucherCode FROM SupplyVouchers WHERE VoucherID=?",(b,)).fetchone()[0])

    def test_draft_does_not_add_to_stock(self):
        voucher(self.db)
        self.assertEqual(0,self.db.execute("SELECT qty_milli FROM v_stock WHERE product_id=1").fetchone()[0])

    def test_cannot_mark_receipt_posted_without_ledger(self):
        v=voucher(self.db)
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("UPDATE SupplyVouchers SET State='POSTED' WHERE VoucherID=?",(v,))

    def test_posted_receipt_links_to_real_purchase_and_stock(self):
        v=voucher(self.db)
        doc=legacy_purchase(self.db)
        self.db.execute("UPDATE SupplyVouchers SET State='POSTED',DocumentID=? WHERE VoucherID=?",(doc,v))
        self.assertEqual(1000,self.db.execute("SELECT qty_milli FROM v_stock WHERE product_id=1").fetchone()[0])
        with self.assertRaisesRegex(sqlite3.IntegrityError,"POSTED_SUPPLY_IMMUTABLE"):
            self.db.execute("DELETE FROM SupplyVouchers WHERE VoucherID=?",(v,))
        with self.assertRaisesRegex(sqlite3.IntegrityError,"POSTED_SUPPLY_IMMUTABLE"):
            self.db.execute("DELETE FROM SupplyVoucherDetails WHERE VoucherID=?",(v,))
        with self.assertRaisesRegex(sqlite3.IntegrityError,"POSTED_SUPPLY_IMMUTABLE"):
            self.db.execute("UPDATE SupplyVouchers SET Notes='bad' WHERE VoucherID=?",(v,))

    def test_posted_supplier_external_invoice_is_unique(self):
        v=voucher(self.db)
        doc=legacy_purchase(self.db)
        self.db.execute("UPDATE SupplyVouchers SET State='POSTED',DocumentID=? WHERE VoucherID=?",(doc,v))
        other=voucher(self.db,external="SUP-001")
        doc2=legacy_purchase(self.db)
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("UPDATE SupplyVouchers SET State='POSTED',DocumentID=? WHERE VoucherID=?",(doc2,other))

    def test_payment_requires_posting_and_cannot_be_modified(self):
        v=voucher(self.db)
        with self.assertRaisesRegex(sqlite3.IntegrityError,"PAYMENT_REQUIRES_POSTED_SUPPLY"):
            self.db.execute("""INSERT INTO SupplyVoucherPayments(
                VoucherID,AmountCents,PaymentMethod,CreatedByUserID,CreatedUtc)
                VALUES (?,100,'CASH',1,'now')""",(v,))
        doc=legacy_purchase(self.db)
        self.db.execute("UPDATE SupplyVouchers SET State='POSTED',DocumentID=? WHERE VoucherID=?",(doc,v))
        p=self.db.execute("""INSERT INTO SupplyVoucherPayments(
            VoucherID,AmountCents,PaymentMethod,CreatedByUserID,CreatedUtc)
            VALUES (?,100,'CASH',1,'now')""",(v,)).lastrowid
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("DELETE FROM SupplyVoucherPayments WHERE PaymentID=?",(p,))

    def test_receipt_service_never_deletes_approved_stock(self):
        s=(ROOT/"src"/"OfflineStore"/"Supply"/"SupplyVoucherService.cs").read_text(encoding="utf-8")
        self.assertIn("WHERE VoucherID=@p0 AND State='DRAFT'",s)
        self.assertIn("tx.Commit();",s)
        self.assertIn("StoreService.ApplyMovingAverageCost(",s)
        self.assertIn("FROM product_units u JOIN products p",s)
        self.assertIn("ROF-",s)

if __name__=="__main__":
    unittest.main()
