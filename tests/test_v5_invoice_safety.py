"""Execute production SQL against SQLite; these do not test the WPF runtime."""
import pathlib
import sqlite3
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
INFRA = ROOT / 'src/OfflineStore/Infrastructure'
MIGRATION = INFRA / 'Migrations/005_invoice_safety.sql'


class InvoiceSafetyTests(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(':memory:')
        self.db.execute('PRAGMA foreign_keys=ON')
        self.db.executescript((INFRA / 'Schema.sql').read_text(encoding='utf-8'))
        for path in sorted((INFRA / 'Migrations').glob('*.sql')):
            if path.name < '005':
                self.db.executescript(path.read_text(encoding='utf-8'))
        self.db.execute("INSERT INTO products(sku,name,created_utc) VALUES('P','Product','now')")
        self.db.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,is_base) VALUES(1,'kg',1000,100,1)")
        self.db.execute('UPDATE products SET is_countable=0 WHERE id=1')
        self.db.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','Supplier','now')")
        self.db.commit()
        # Empty until implemented: regression tests must fail on behavior, not a missing file.
        if MIGRATION.exists():
            self.db.executescript(MIGRATION.read_text(encoding='utf-8'))

    def tearDown(self):
        self.db.close()

    def draft(self, total=100):
        return self.db.execute("INSERT INTO documents(doc_type,party_id,total_cents,created_utc) VALUES('PURCHASE',1,?,'now')", (total,)).lastrowid

    def line(self, doc, price=100, total=100, entered_price=None, qty=1000):
        return self.db.execute('''INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,
            unit_price_cents,line_total_cents,product_unit_id,entered_qty_milli,entered_unit_name,
            unit_factor_milli,entered_unit_price_cents) VALUES(?,1,1,?,?,?,1,?,'kg',1000,?)''',
            (doc,qty,price,total,qty,price if entered_price is None else entered_price)).lastrowid

    def post_purchase(self, qty=1000, price=100, total=100):
        doc = self.draft(total)
        line = self.line(doc, price=price, total=total, qty=qty)
        self.db.execute("INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) VALUES(?,?,1,?,'now')", (doc,line,qty))
        self.db.execute("UPDATE documents SET state='POSTED',doc_code=? WHERE id=?", ('P-'+str(doc),doc))
        return doc

    def test_direct_posted_insert_rejected(self):
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'DOCUMENT_MUST_START_DRAFT'):
            self.db.execute("INSERT INTO documents(doc_type,state,total_cents,created_utc) VALUES('SALE','POSTED',999,'now')")

    def test_wrong_line_total_rejected_even_when_header_matches(self):
        doc = self.draft(1)
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'LINE_AMOUNT_MISMATCH'):
            self.line(doc,price=500,total=1)

    def test_snapshot_price_disagreement_rejected(self):
        doc = self.draft()
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'LINE_AMOUNT_MISMATCH'):
            self.line(doc,entered_price=900)

    def test_fractional_money_rejected(self):
        doc = self.draft()
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'LINE_AMOUNT_MISMATCH'):
            self.line(doc,price=100.5,entered_price=100.5,total=100.5)

    def test_noninteger_quantity_rejected(self):
        doc = self.draft()
        with self.assertRaises(sqlite3.IntegrityError):
            self.line(doc,qty=1000.5)

    def test_draft_type_cannot_change_after_line(self):
        doc = self.draft()
        self.line(doc)
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'DOCUMENT_IDENTITY_IMMUTABLE'):
            self.db.execute("UPDATE documents SET doc_type='SALE' WHERE id=?", (doc,))

    def test_draft_supplier_cannot_change_after_line(self):
        doc = self.draft()
        self.line(doc)
        self.db.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','Other','now')")
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'DOCUMENT_IDENTITY_IMMUTABLE'):
            self.db.execute('UPDATE documents SET party_id=2 WHERE id=?', (doc,))

    def test_exact_half_cent_rounding_and_large_prices(self):
        for qty,price in [(500,101),(1000,9223372036854775807),(1000000000,9000000000),(999999999,999999999)]:
            total = (qty * price + 500)//1000
            self.post_purchase(qty,price,total)
        self.assertEqual(4,self.db.execute("SELECT COUNT(*) FROM documents WHERE state='POSTED'").fetchone()[0])

    def test_overflow_cannot_be_saved_as_real(self):
        doc = self.draft()
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'LINE_AMOUNT_MISMATCH'):
            self.line(doc,qty=1000000000,price=9223372036854775807,total=9.223372036854776e24)

    def test_invalid_invoice_transaction_rolls_back(self):
        self.db.commit()
        with self.assertRaises(sqlite3.IntegrityError):
            with self.db:
                doc = self.draft()
                self.line(doc,price=500,total=100)
        self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM documents').fetchone()[0])

    def test_upgrade_preserves_existing_posted_invoice(self):
        self.post_purchase()
        self.db.commit()
        before = {table:self.db.execute('SELECT * FROM '+table).fetchall() for table in ('documents','document_lines','stock_movements','products','users')}
        # Re-open upgrade on an otherwise identical v4 schema.
        for name, in self.db.execute("SELECT name FROM sqlite_master WHERE type='trigger' AND name LIKE 'v5_%'").fetchall():
            self.db.execute('DROP TRIGGER '+name)
        if MIGRATION.exists():
            self.db.executescript(MIGRATION.read_text(encoding='utf-8'))
        for table,rows in before.items():
            self.assertEqual(rows,self.db.execute('SELECT * FROM '+table).fetchall())
        self.assertEqual('ok',self.db.execute('PRAGMA integrity_check').fetchone()[0])
        self.assertEqual([],self.db.execute('PRAGMA foreign_key_check').fetchall())


if __name__ == '__main__':
    unittest.main()
