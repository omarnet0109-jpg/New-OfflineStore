import sqlite3
import unittest
from test_v9_goods_receipts import database,legacy_purchase

class ReturnLedgerTests(unittest.TestCase):
 def setUp(self):self.db=database()
 def tearDown(self):self.db.close()
 def test_return_schema_is_additive_and_has_no_historical_backfill(self):
  self.assertIn('return_documents',{r[0] for r in self.db.execute("SELECT name FROM sqlite_master WHERE type='table'")})
  legacy_purchase(self.db)
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM return_documents').fetchone()[0])
 def test_draft_return_does_not_make_stock_available(self):
  doc=legacy_purchase(self.db)
  r=self.db.execute("INSERT INTO return_documents(original_document_id,reference,return_date,cash_cents,debt_cents,total_cents,created_by_user_id,created_utc,memo) VALUES(?,'return-1','2026-11-06',10000,0,10000,1,'now','supplier return')",(doc,)).lastrowid
  line=self.db.execute('SELECT id FROM document_lines WHERE document_id=?',(doc,)).fetchone()[0]
  self.db.execute("INSERT INTO return_lines(return_id,original_line_id,qty_milli,amount_cents,cost_cents,name_snapshot,unit_snapshot) VALUES(?,?,1000,10000,10000,'Item','piece')",(r,line))
  self.assertEqual(1000,self.db.execute('SELECT qty_milli FROM v_stock WHERE product_id=1').fetchone()[0])
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute("UPDATE return_documents SET state='POSTED' WHERE id=?",(r,))
 def test_return_line_cannot_exceed_original_or_reference_another_document(self):
  doc=legacy_purchase(self.db);other=legacy_purchase(self.db)
  r=self.db.execute("INSERT INTO return_documents(original_document_id,reference,return_date,cash_cents,debt_cents,total_cents,created_by_user_id,created_utc,memo) VALUES(?,'return-2','2026-11-06',10000,0,10000,1,'now','return')",(doc,)).lastrowid
  line=self.db.execute('SELECT id FROM document_lines WHERE document_id=?',(other,)).fetchone()[0]
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute("INSERT INTO return_lines(return_id,original_line_id,qty_milli,amount_cents,cost_cents,name_snapshot,unit_snapshot) VALUES(?,?,1000,10000,10000,'Item','piece')",(r,line))
 def test_posted_purchase_return_changes_stock_and_blocks_a_stale_sale(self):
  doc=legacy_purchase(self.db)
  e=self.db.execute("INSERT INTO journal_entries(source_kind,source_key,entry_date,created_by_user_id,created_utc,document_id) VALUES('PURCHASE','purchase','2026-11-06',1,'now',?)",(doc,)).lastrowid
  for n,a,d,c in [(1,'INVENTORY',10000,0),(2,'PAYABLES',0,10000)]:
   self.db.execute('INSERT INTO journal_lines(entry_id,line_no,account_code,party_id,debit_cents,credit_cents) VALUES(?,?,?, ?,?,?)',(e,n,a,1 if a=='PAYABLES' else None,d,c))
  self.db.execute("UPDATE journal_entries SET state='POSTED' WHERE id=?",(e,))
  r=self.db.execute("INSERT INTO return_documents(original_document_id,reference,return_date,cash_cents,debt_cents,total_cents,created_by_user_id,created_utc,memo) VALUES(?,'full','2026-11-06',0,10000,10000,1,'now','full return')",(doc,)).lastrowid
  line=self.db.execute('SELECT id FROM document_lines WHERE document_id=?',(doc,)).fetchone()[0]
  self.db.execute("INSERT INTO return_lines(return_id,original_line_id,qty_milli,amount_cents,cost_cents,cost_basis_millicents,name_snapshot,unit_snapshot) VALUES(?,?,1000,10000,10000,10000000,'Item','piece')",(r,line))
  j=self.db.execute("INSERT INTO journal_entries(source_kind,source_key,entry_date,created_by_user_id,created_utc,document_id) VALUES('RETURN','return:full','2026-11-06',1,'now',?)",(doc,)).lastrowid
  for n,a,d,c in [(1,'PAYABLES',10000,0),(2,'INVENTORY',0,10000)]:
   self.db.execute('INSERT INTO journal_lines(entry_id,line_no,account_code,party_id,debit_cents,credit_cents) VALUES(?,?,?,?,?,?)',(j,n,a,1 if a=='PAYABLES' else None,d,c))
  self.db.execute("UPDATE journal_entries SET state='POSTED' WHERE id=?",(j,))
  self.db.execute('UPDATE return_documents SET journal_entry_id=? WHERE id=?',(j,r))
  self.db.execute('INSERT INTO invoice_allocations(entry_id,document_id,amount_cents) VALUES(?,?,10000)',(j,doc))
  self.db.execute("UPDATE return_documents SET state='POSTED' WHERE id=?",(r,))
  self.assertEqual(0,self.db.execute('SELECT qty_milli FROM v_stock WHERE product_id=1').fetchone()[0])
  self.assertEqual([],self.db.execute('SELECT id FROM v_invalid_return_documents').fetchall())
  sale=self.db.execute("INSERT INTO documents(doc_type,total_cents,created_utc,created_by_user_id) VALUES('SALE',10000,'now',1)").lastrowid
  sale_line=self.db.execute("INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents,unit_cost_snapshot_cents,product_unit_id,entered_qty_milli,entered_unit_name,unit_factor_milli,entered_unit_price_cents) VALUES(?,1,1,1000,10000,10000,10000,1,1000,'قطعة',1000,10000)",(sale,)).lastrowid
  with self.assertRaisesRegex(sqlite3.IntegrityError,'INSUFFICIENT_STOCK'):
   self.db.execute("INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) VALUES(?,?,1,-1000,'now')",(sale,sale_line))
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute('UPDATE return_lines SET qty_milli=1')
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute('DELETE FROM return_documents WHERE id=?',(r,))
