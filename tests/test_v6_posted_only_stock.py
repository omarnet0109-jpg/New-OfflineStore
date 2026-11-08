"""Regression tests using the production v1-v6 SQL scripts, not a mock."""
import pathlib
import sqlite3
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
INFRA = ROOT / "src" / "OfflineStore" / "Infrastructure"
MIGRATIONS = INFRA / "Migrations"


def make_database(up_to=6):
    c = sqlite3.connect(":memory:")
    c.execute("PRAGMA foreign_keys=ON")
    c.executescript((INFRA / "Schema.sql").read_text(encoding="utf-8"))
    for n, path in enumerate(sorted(MIGRATIONS.glob("*.sql")), 2):
        if n <= up_to:
            c.executescript(path.read_text(encoding="utf-8"))
    c.execute("INSERT INTO products(sku,name,reorder_qty_milli,created_utc) VALUES ('TEA','Tea',1000,'now')")
    c.execute("""INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,is_base)
              VALUES (1,'قطعة',1000,100,1)""")
    c.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','Vendor','now')")
    c.commit()
    return c


def draft(c, kind, items):
    """Create a draft and its ordinary staged movements, exactly like Post()."""
    total = sum(qty * 100 for qty in items)
    supplier = 1 if kind == "PURCHASE" else None
    doc = c.execute("""INSERT INTO documents(doc_type,party_id,total_cents,created_utc)
                       VALUES (?,?,?,'now')""", (kind, supplier, total)).lastrowid
    sign = 1 if kind == "PURCHASE" else -1
    for number, qty in enumerate(items, 1):
        milli = qty * 1000
        line = c.execute("""INSERT INTO document_lines(
            document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents,
            product_unit_id,entered_qty_milli,entered_unit_name,unit_factor_milli,entered_unit_price_cents)
            VALUES(?,?,1,?,100,?,1,?,'قطعة',1000,100)""",
            (doc, number, milli, qty * 100, milli)).lastrowid
        c.execute("""INSERT INTO stock_movements(
            document_id,document_line_id,product_id,delta_qty_milli,created_utc)
            VALUES(?,?,1,?,'now')""", (doc, line, sign * milli))
    return doc


def post(c, doc):
    c.execute("UPDATE documents SET state='POSTED',doc_code=? WHERE id=?",
              ("DOC-" + str(doc), doc))


def stock(c):
    return c.execute("SELECT qty_milli FROM v_stock WHERE product_id=1").fetchone()[0]


class PostedOnlyInventoryTests(unittest.TestCase):
    def setUp(self):
        self.c = make_database()

    def tearDown(self):
        self.c.close()

    def test_draft_purchase_never_increases_sellable_stock(self):
        draft(self.c, "PURCHASE", [12])
        self.assertEqual(0, stock(self.c))
        self.assertEqual("OUT", self.c.execute("SELECT alert_state FROM v_low_stock").fetchone()[0])

    def test_posted_purchase_becomes_available(self):
        doc = draft(self.c, "PURCHASE", [12])
        post(self.c, doc)
        self.assertEqual(12000, stock(self.c))

    def test_pending_sale_does_not_change_posted_balance(self):
        purchase = draft(self.c, "PURCHASE", [10])
        post(self.c, purchase)
        draft(self.c, "SALE", [4])
        self.assertEqual(10000, stock(self.c))

    def test_pending_purchase_cannot_fund_different_sale(self):
        draft(self.c, "PURCHASE", [10])
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INSUFFICIENT_STOCK"):
            draft(self.c, "SALE", [1])
        self.assertEqual(0, stock(self.c))

    def test_multiline_sale_does_not_oversell_at_insert(self):
        post(self.c, draft(self.c, "PURCHASE", [10]))
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INSUFFICIENT_STOCK"):
            draft(self.c, "SALE", [6, 5])
        self.assertEqual(10000, stock(self.c))

    def test_two_drafts_compete_and_second_rejected_at_posting(self):
        post(self.c, draft(self.c, "PURCHASE", [10]))
        first = draft(self.c, "SALE", [6])
        second = draft(self.c, "SALE", [6])
        post(self.c, first)
        self.assertEqual(4000, stock(self.c))
        with self.assertRaisesRegex(sqlite3.IntegrityError, "INSUFFICIENT_STOCK_AT_POSTING"):
            post(self.c, second)
        self.assertEqual("DRAFT", self.c.execute("SELECT state FROM documents WHERE id=?", (second,)).fetchone()[0])
        self.assertEqual(4000, stock(self.c))

    def test_posted_sale_reduces_stock_only_when_posted(self):
        post(self.c, draft(self.c, "PURCHASE", [8]))
        sale = draft(self.c, "SALE", [3])
        self.assertEqual(8000, stock(self.c))
        post(self.c, sale)
        self.assertEqual(5000, stock(self.c))

    def test_draft_movements_never_surface_in_sale_history(self):
        draft(self.c, "PURCHASE", [9])
        self.assertEqual(0, self.c.execute("""SELECT COUNT(*) FROM stock_movements m
            JOIN documents d ON d.id=m.document_id
            WHERE d.state='POSTED'""").fetchone()[0])

    def test_existing_v5_draft_movements_are_excluded_without_deleting_rows(self):
        self.c.close()
        self.c = make_database(up_to=5)
        doc = draft(self.c, "PURCHASE", [7])
        self.assertEqual(7000, stock(self.c))
        self.c.executescript((MIGRATIONS / "006_posted_only_stock.sql").read_text(encoding="utf-8"))
        self.assertEqual(0, stock(self.c))
        self.assertEqual("DRAFT", self.c.execute("SELECT state FROM documents WHERE id=?", (doc,)).fetchone()[0])
        self.assertEqual(1, self.c.execute("SELECT COUNT(*) FROM stock_movements").fetchone()[0])
        self.assertEqual("ok", self.c.execute("PRAGMA integrity_check").fetchone()[0])

    def test_commit_rejects_second_stale_draft_sale_atomically(self):
        post(self.c, draft(self.c, "PURCHASE", [10]))
        first = draft(self.c, "SALE", [7])
        second = draft(self.c, "SALE", [5])
        post(self.c, first)
        self.c.commit()
        with self.assertRaises(sqlite3.IntegrityError):
            with self.c:
                post(self.c, second)
        self.assertEqual(3000, stock(self.c))

    def test_sql_migration_exposes_pending_documents_to_diagnostics(self):
        draft(self.c, "PURCHASE", [2])
        self.assertEqual(1, self.c.execute("""SELECT COUNT(*) FROM documents d
            WHERE d.state='DRAFT'
            AND EXISTS(SELECT 1 FROM stock_movements m WHERE m.document_id=d.id)""").fetchone()[0])

    def test_service_uses_posted_filter_for_sale_availability(self):
        src = (ROOT / "src" / "OfflineStore" / "Services" / "StoreService.cs").read_text(encoding="utf-8")
        self.assertIn("d.state='POSTED'", src)


if __name__ == "__main__":
    unittest.main()
