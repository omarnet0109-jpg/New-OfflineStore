"""Integration tests of deployed SQLite migrations and ledger; not C# or Windows GUI tests."""
import pathlib
import sqlite3
import unittest
from contextlib import contextmanager

ROOT = pathlib.Path(__file__).resolve().parents[1] / 'src/OfflineStore/Infrastructure'
V1 = (ROOT / 'Schema.sql').read_text(encoding='utf-8')
V2 = (ROOT / 'Migrations/002_settings_and_security.sql').read_text(encoding='utf-8')
V3 = (ROOT / 'Migrations/003_units_users_alerts.sql').read_text(encoding='utf-8')


class UnitTests(unittest.TestCase):
    def setUp(self):
        self.db = sqlite3.connect(':memory:')
        self.db.execute('PRAGMA foreign_keys=ON')
        self.db.executescript(V1)
        self.db.executescript(V2)
        self.db.execute("INSERT INTO products(sku,name,unit,sale_price_cents,reorder_qty_milli,created_utc) "
                        "VALUES('TEA','شاي','قطعة',1500,10000,'2026-01-01')")
        self.db.execute("INSERT INTO products(sku,name,unit,sale_price_cents,created_utc) "
                        "VALUES('KG','سكر','كيلو',2000,'2026-01-01')")
        self.db.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','مورد','2026-01-01')")
        self.db.executescript(V3)
        self.db.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents) VALUES(1,'كرتونة',48000,60000)")
        self.db.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents) VALUES(1,'دستة',12000,17000)")
        self.db.commit()

    def tearDown(self):
        self.db.close()

    def product_unit(self, product, label):
        return self.db.execute('SELECT id,factor_milli,sale_price_cents FROM product_units WHERE product_id=? AND name=?',
                               (product, label)).fetchone()

    def add_document(self, typ, rows):
        """rows: (product_id, unit_name, typed_qty_milli, price_minor_units)"""
        self.db.execute('BEGIN')
        try:
            totals = [round(q * price / 1000) for p, u, q, price in rows]
            doc = self.db.execute('INSERT INTO documents(doc_type,party_id,total_cents,created_utc) VALUES(?,?,?,?)',
                                  (typ, 1 if typ == 'PURCHASE' else None, sum(totals), '2026-01-01')).lastrowid
            for n, ((p, u, q, price), total) in enumerate(zip(rows, totals), 1):
                uid, factor, _ = self.product_unit(p,u)
                base = q * factor // 1000
                line = self.db.execute('''INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,
                     unit_price_cents,line_total_cents,product_unit_id,entered_qty_milli,
                     entered_unit_name,unit_factor_milli,entered_unit_price_cents)
                     VALUES(?,?,?,?,?,?,?,?,?,?,?)''',
                     (doc,n,p,base,price,total,uid,q,u,factor,price)).lastrowid
                self.db.execute('INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) VALUES(?,?,?,?,?)',
                                (doc,line,p,base if typ == 'PURCHASE' else -base,'2026-01-01'))
            self.db.execute("UPDATE documents SET state='POSTED',doc_code=? WHERE id=?", ('D'+str(doc),doc))
            self.db.commit()
        except Exception:
            self.db.rollback()
            raise
        return doc

    def stock(self, product=1):
        return self.db.execute('SELECT qty_milli FROM v_stock WHERE product_id=?', (product,)).fetchone()[0]

    def test_base_unit_backfill_and_carton_custom_factor(self):
        self.assertEqual(self.product_unit(1,'قطعة')[1],1000)
        self.assertEqual(self.product_unit(1,'كرتونة')[1],48000)
        self.assertEqual(self.product_unit(1,'دستة')[1],12000)
        self.assertEqual(self.db.execute('SELECT COUNT(*) FROM users').fetchone()[0],0)

    def test_carton_sale_combined_with_pieces(self):
        self.add_document('PURCHASE', [(1,'كرتونة',2000,42000)])
        self.assertEqual(self.stock(),96000)
        doc=self.add_document('SALE', [(1,'كرتونة',1000,60000),(1,'قطعة',3000,1500)])
        self.assertEqual(self.stock(),45000)
        self.assertEqual(self.db.execute('SELECT total_cents FROM documents WHERE id=?',(doc,)).fetchone()[0],64500)
        snap=self.db.execute('SELECT entered_unit_name,entered_qty_milli,qty_milli,entered_unit_price_cents FROM document_lines WHERE document_id=? ORDER BY line_no',(doc,)).fetchall()
        self.assertEqual(snap, [('كرتونة',1000,48000,60000),('قطعة',3000,3000,1500)])

    def test_low_stock_after_selling(self):
        self.add_document('PURCHASE',[(1,'دستة',1000,15000)])
        self.assertEqual(self.db.execute('SELECT COUNT(*) FROM v_low_stock WHERE product_id=1').fetchone()[0],0)
        self.add_document('SALE',[(1,'قطعة',3000,1500)])
        self.assertEqual(self.stock(),9000)
        self.assertEqual(self.db.execute('SELECT alert_state FROM v_low_stock WHERE product_id=1').fetchone()[0],'LOW')

    def test_insufficient_stock_rolls_back_complete_carton_sale(self):
        self.add_document('PURCHASE', [(1,'دستة',1000,15000)])
        count_before=self.db.execute('SELECT count(*) FROM documents').fetchone()[0]
        with self.assertRaisesRegex(sqlite3.IntegrityError,'INSUFFICIENT_STOCK'):
            self.add_document('SALE',[(1,'قطعة',1000,1500),(1,'كرتونة',1000,60000)])
        self.assertEqual(self.stock(),12000)
        self.assertEqual(self.db.execute('SELECT count(*) FROM documents').fetchone()[0],count_before)

    def test_fractional_piece_rejected(self):
        with self.assertRaisesRegex(sqlite3.IntegrityError,'FRACTIONAL_PIECES_NOT_ALLOWED'):
            self.add_document('PURCHASE',[(1,'قطعة',1500,1500)])

    def test_weight_can_use_fractional_base(self):
        self.add_document('PURCHASE',[(2,'كيلو',1500,2000)])
        self.assertEqual(self.stock(2),1500)

    def test_unit_product_mismatch_rejected(self):
        self.db.execute("INSERT INTO documents(doc_type,party_id,total_cents,created_utc) VALUES('PURCHASE',1,100,'now')")
        with self.assertRaisesRegex(sqlite3.IntegrityError,'UNIT_PRODUCT_MISMATCH'):
            self.db.execute('''INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,
            line_total_cents,product_unit_id,entered_qty_milli,entered_unit_name,unit_factor_milli,entered_unit_price_cents)
            VALUES(1,1,2,48000,100,100,?,1000,'كرتونة',48000,100)''',(self.product_unit(1,'كرتونة')[0],))
        self.db.rollback()

    def test_wrong_conversion_rejected(self):
        self.db.execute("INSERT INTO documents(doc_type,party_id,total_cents,created_utc) VALUES('PURCHASE',1,100,'now')")
        with self.assertRaisesRegex(sqlite3.IntegrityError,'BAD_UNIT_CONVERSION'):
            self.db.execute('''INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,
            line_total_cents,product_unit_id,entered_qty_milli,entered_unit_name,unit_factor_milli,entered_unit_price_cents)
            VALUES(1,1,1,47000,100,100,?,1000,'كرتونة',48000,100)''',(self.product_unit(1,'كرتونة')[0],))
        self.db.rollback()

    def test_immutable_factor_and_cant_remove_unit(self):
        id=self.product_unit(1,'دستة')[0]
        with self.assertRaisesRegex(sqlite3.IntegrityError,'UNIT_CONVERSION_IMMUTABLE'):
            self.db.execute('UPDATE product_units SET factor_milli=11000 WHERE id=?',(id,))
        with self.assertRaisesRegex(sqlite3.IntegrityError,'UNIT_CANNOT_BE_DELETED'):
            self.db.execute('DELETE FROM product_units WHERE id=?',(id,))
        self.db.rollback()

    def test_duplicate_unit_name_case_insensitive(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents) VALUES(1,'دستة',24000,18000)")
        self.db.rollback()
        self.db.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents) VALUES(1,'BOX',6000,10000)")
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents) VALUES(1,'box',12000,15000)")
        self.db.rollback()

    def test_no_deleting_last_active_admin(self):
        self.db.execute("INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc) VALUES('admin','مدير','ADMIN',?,?,210000,'now')", (b'a'*32,b'b'*32))
        with self.assertRaisesRegex(sqlite3.IntegrityError,'LAST_ACTIVE_ADMIN'):
            self.db.execute("UPDATE users SET active=0 WHERE username='admin'")
        with self.assertRaisesRegex(sqlite3.IntegrityError,'LAST_ACTIVE_ADMIN'):
            self.db.execute("UPDATE users SET role='CASHIER' WHERE username='admin'")
        self.db.rollback()

    def test_username_is_case_insensitive(self):
        self.db.execute("INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc) VALUES('Cashier1','كاشير','CASHIER',?,?,210000,'now')", (b'a'*32,b'b'*32))
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc) VALUES('CASHIER1','آخر','CASHIER',?,?,210000,'now')", (b'a'*32,b'b'*32))
        self.db.rollback()

    def test_invoice_totals_and_stock_snapshot_remain_after_price_change(self):
        self.add_document('PURCHASE',[(1,'كرتونة',1000,45000)])
        doc=self.add_document('SALE',[(1,'دستة',2000,16500)])
        self.db.execute("UPDATE product_units SET sale_price_cents=21000 WHERE name='دستة'")
        self.assertEqual(self.stock(),24000)
        self.assertEqual(self.db.execute('SELECT total_cents FROM documents WHERE id=?',(doc,)).fetchone()[0],33000)
        self.assertEqual(self.db.execute('SELECT entered_unit_price_cents FROM document_lines WHERE document_id=?',(doc,)).fetchone()[0],16500)

    def test_migrated_legacy_invoice_keeps_stock_and_price(self):
        old=sqlite3.connect(':memory:')
        old.execute('PRAGMA foreign_keys=ON')
        old.executescript(V1)
        old.execute("INSERT INTO products(sku,name,unit,sale_price_cents,created_utc) VALUES('E1','قديم','قطعة',800,'now')")
        old.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','مورد','now')")
        old.execute("INSERT INTO documents(doc_type,party_id,total_cents,created_utc) VALUES('PURCHASE',1,800,'now')")
        old.execute("INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents) VALUES(1,1,1,1000,800,800)")
        old.execute("INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) VALUES(1,1,1,1000,'now')")
        old.execute("UPDATE documents SET state='POSTED',doc_code='P-000001' WHERE id=1")
        old.commit(); old.executescript(V2); old.executescript(V3)
        self.assertEqual(old.execute('SELECT qty_milli FROM v_stock').fetchone()[0],1000)
        self.assertEqual(old.execute('SELECT unit_factor_milli,entered_unit_price_cents FROM document_lines').fetchone(),(1000,800))
        self.assertEqual(old.execute('SELECT total_cents FROM documents').fetchone()[0],800)
        self.assertEqual(old.execute('PRAGMA integrity_check').fetchone()[0],'ok')
        old.close()

    def test_posting_wrong_header_total_is_rejected(self):
        self.db.execute("INSERT INTO documents(doc_type,party_id,total_cents,created_utc) VALUES('PURCHASE',1,1,'now')")
        unit_id=self.product_unit(1,'قطعة')[0]
        self.db.execute("INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents,product_unit_id,entered_qty_milli,entered_unit_name,unit_factor_milli,entered_unit_price_cents) VALUES(1,1,1,1000,1500,1500,?,1000,'قطعة',1000,1500)",(unit_id,))
        self.db.execute("INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) VALUES(1,1,1,1000,'now')")
        with self.assertRaisesRegex(sqlite3.IntegrityError,'INVOICE_TOTAL_MISMATCH'):
            self.db.execute("UPDATE documents SET state='POSTED' WHERE id=1")
        self.db.rollback()

    def test_cannot_post_with_wrong_total(self):
        self.db.execute("INSERT INTO documents(doc_type,party_id,total_cents,created_utc) VALUES('PURCHASE',1,1,'now')")
        with self.assertRaisesRegex(sqlite3.IntegrityError,'EMPTY_INVOICE'):
            self.db.execute("UPDATE documents SET state='POSTED' WHERE id=1")
        self.db.rollback()
        self.add_document('PURCHASE',[(1,'دستة',1000,16500)])
        self.assertEqual(self.stock(),12000)

    def test_base_price_independent_of_package_price(self):
        self.assertEqual(self.product_unit(1,'قطعة')[2],1500)
        self.assertEqual(self.product_unit(1,'كرتونة')[2],60000)
        self.assertNotEqual(48*self.product_unit(1,'قطعة')[2],self.product_unit(1,'كرتونة')[2])

    def test_barcode_cannot_clash_with_product_code(self):
        with self.assertRaisesRegex(sqlite3.IntegrityError,'BARCODE_COLLIDES_WITH_SKU'):
            self.db.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,barcode) VALUES(1,'علبة',6000,9000,'TEA')")
        self.db.rollback()
        self.db.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,barcode) VALUES(1,'علبة',6000,9000,'CODE-6')")
        with self.assertRaisesRegex(sqlite3.IntegrityError,'BARCODE_COLLIDES_WITH_SKU'):
            self.db.execute("INSERT INTO products(sku,name,created_utc) VALUES('CODE-6','صنف جديد','now')")
        self.db.rollback()

    def test_product_sku_cannot_collide_with_other_product_barcode(self):
        self.db.execute("INSERT INTO products(sku,name,barcode,created_utc) VALUES('A4','حزمة','BR-99','now')")
        with self.assertRaisesRegex(sqlite3.IntegrityError,'PRODUCT_CODE_COLLISION'):
            self.db.execute("INSERT INTO products(sku,name,created_utc) VALUES('BR-99','آخر','now')")
        self.db.rollback()

    def test_existing_legacy_password_becomes_admin(self):
        db = sqlite3.connect(':memory:')
        db.executescript(V1);db.executescript(V2)
        db.execute('INSERT INTO local_auth(id,salt,password_hash,iterations,updated_utc) VALUES(1,?,?,210000,?)',
                   (b'x'*32,b'z'*32,'now'))
        db.commit()
        db.executescript(V3)
        self.assertEqual(db.execute('SELECT username,role,salt,password_hash FROM users').fetchone(),
                         ('admin','ADMIN',b'x'*32,b'z'*32))
        db.close()

if __name__=='__main__':
    unittest.main()
