"""Schema integration tests; run with: python -m unittest discover -s tests -v.
Tests the actual SQL file with CPython SQLite, not the C# WPF UI.
"""
import pathlib
import sqlite3
import tempfile
import unittest
from contextlib import closing

ROOT = pathlib.Path(__file__).parents[1]
SCHEMA = (ROOT / 'src' / 'OfflineStore' / 'Infrastructure' / 'Schema.sql').read_text(encoding='utf-8')


class SchemaTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = pathlib.Path(self.temp.name) / 'db.sqlite'
        self.con = sqlite3.connect(self.path)
        self.con.execute('PRAGMA foreign_keys=ON')
        self.con.executescript(SCHEMA)
        self.con.execute("INSERT INTO products(sku,name,unit,sale_price_cents,created_utc) VALUES('ABC','دفتر','قطعة',1500,'2026-01-01')")
        self.con.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','مورد تجريبي','2026-01-01')")
        self.con.commit()

    def tearDown(self):
        self.con.close()
        self.temp.cleanup()

    def _post(self, kind, delta, qty, price=2000):
        partner = 1 if kind == 'PURCHASE' else None
        self.con.execute('BEGIN')
        try:
            d = self.con.execute('INSERT INTO documents(doc_type,party_id,total_cents,created_utc) VALUES(?,?,?,?)',
                                 (kind, partner, qty * price // 1000, '2026-01-01')).lastrowid
            l = self.con.execute('INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents) VALUES(?,?,?,?,?,?)',
                                 (d, 1, 1, qty, price, qty * price // 1000)).lastrowid
            self.con.execute('INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) VALUES(?,?,?,?,?)',
                             (d,l,1,delta,'2026-01-01'))
            self.con.execute("UPDATE documents SET doc_code=?,state='POSTED' WHERE id=?", ('T-' + str(d),d))
            self.con.commit()
        except Exception:
            self.con.rollback()
            raise
        return d

    def stock(self):
        return self.con.execute('SELECT qty_milli FROM v_stock WHERE product_id=1').fetchone()[0]

    def test_create_stock_ledger_and_sale(self):
        self._post('PURCHASE', 3500, 3500)
        self._post('SALE', -1250, 1250)
        self.assertEqual(self.stock(), 2250)
        self.assertEqual(self.con.execute('SELECT count(*) FROM documents WHERE state="POSTED"').fetchone()[0], 2)

    def test_sale_cannot_make_stock_negative(self):
        self._post('PURCHASE', 1000, 1000)
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'INSUFFICIENT_STOCK'):
            self._post('SALE', -1100, 1100)
        self.assertEqual(self.stock(), 1000)
        self.assertEqual(self.con.execute('SELECT count(*) FROM documents').fetchone()[0], 1)

    def test_cannot_sell_without_stock(self):
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'INSUFFICIENT_STOCK'):
            self._post('SALE', -1, 1)
        self.assertEqual(self.stock(), 0)

    def test_movement_must_correspond_to_line_and_doc_type(self):
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'INVALID_STOCK_MOVEMENT'):
            self._post('PURCHASE', -1000, 1000)
        self.assertEqual(self.stock(), 0)

    def test_immutable_document(self):
        doc_id = self._post('PURCHASE', 1000, 1000)
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'POSTED_DOCUMENT_IMMUTABLE'):
            self.con.execute('UPDATE documents SET total_cents=1 WHERE id=?', (doc_id,))
        self.con.rollback()
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'STOCK_MOVEMENT_IMMUTABLE'):
            self.con.execute('DELETE FROM stock_movements')
        self.con.rollback()

    def test_duplicate_sku_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.con.execute("INSERT INTO products(sku,name,created_utc) VALUES('abc','نسخة','2026-01-01')")
        self.con.rollback()

    def test_foreign_keys_work(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.con.execute("INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents) VALUES(999,1,1,1000,100,100)")
        self.con.rollback()

    def test_backup_is_consistent(self):
        self._post('PURCHASE', 1000, 1000)
        destination = pathlib.Path(self.temp.name) / 'backup.db'
        self.con.execute('VACUUM INTO ?', (str(destination),))
        with closing(sqlite3.connect(destination)) as restored:
            self.assertEqual(restored.execute('SELECT qty_milli FROM v_stock').fetchone()[0], 1000)
            self.assertEqual(restored.execute('PRAGMA integrity_check').fetchone()[0], 'ok')

    def test_stock_cannot_be_corrupted_by_insert_to_posted_document(self):
        d = self._post('PURCHASE', 1000, 1000)
        with self.assertRaisesRegex(sqlite3.IntegrityError, 'POSTED_DOCUMENT_IMMUTABLE'):
            self.con.execute('INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents) VALUES(?,?,?,?,?,?)', (d,2,1,1000,100,100))
        self.con.rollback()

if __name__ == '__main__':
    unittest.main()
