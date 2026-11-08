import unittest
from test_v9_goods_receipts import database

class ZeroValueReturns(unittest.TestCase):
 def setUp(self):self.db=database()
 def tearDown(self):self.db.close()
 def free_purchase(self):
  d=self.db.execute("INSERT INTO documents(doc_type,party_id,total_cents,created_utc,created_by_user_id) VALUES('PURCHASE',1,0,'2026-11-06T12:00:00Z',1)").lastrowid
  line=self.db.execute("INSERT INTO document_lines(document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents,unit_cost_snapshot_cents,product_unit_id,entered_qty_milli,entered_unit_name,unit_factor_milli,entered_unit_price_cents) VALUES(?,1,1,1000,0,0,0,1,1000,'قطعة',1000,0)",(d,)).lastrowid
  self.db.execute('INSERT INTO purchase_cost_snapshots VALUES(?,0,0)',(line,))
  self.db.execute("INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) VALUES(?,?,1,1000,'now')",(d,line))
  self.db.execute("UPDATE documents SET state='POSTED',doc_code=? WHERE id=?",(f'P-{d}',d))
  return d
 def test_verified_post_cutover_free_purchase_can_be_returned_without_fake_journal(self):
  self.db.execute("INSERT INTO finance_activation VALUES(1,NULL,0,'2026-11-06',1)")
  doc=self.free_purchase()
  self.assertEqual([(doc,0)],self.db.execute('SELECT document_id,initial_debt_cents FROM v_financial_invoices').fetchall())
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM journal_entries').fetchone()[0])
 def test_legacy_free_purchase_does_not_gain_invented_financial_evidence(self):
  doc=self.free_purchase();self.db.execute('INSERT INTO finance_legacy_documents VALUES(?)',(doc,))
  self.db.execute("INSERT INTO finance_activation VALUES(1,NULL,?,'2026-11-06',1)",(doc,))
  self.assertEqual([],self.db.execute('SELECT document_id FROM v_financial_invoices').fetchall())
 def test_zero_invoice_cannot_consume_another_invoices_payment(self):
  self.db.execute("INSERT INTO finance_activation VALUES(1,NULL,0,'2026-11-06',1)")
  doc=self.free_purchase()
  entry=self.db.execute("INSERT INTO journal_entries(source_kind,source_key,entry_date,created_by_user_id,created_utc) VALUES('SUPPLIER_PAYMENT','actual-payment','2026-11-06',1,'now')").lastrowid
  self.db.execute("INSERT INTO journal_lines(entry_id,line_no,account_code,party_id,debit_cents) VALUES(?,1,'PAYABLES',1,100)",(entry,))
  self.db.execute("INSERT INTO journal_lines(entry_id,line_no,account_code,credit_cents) VALUES(?,2,'CASH',100)",(entry,))
  self.db.execute("UPDATE journal_entries SET state='POSTED' WHERE id=?",(entry,))
  self.db.execute("INSERT INTO finance_operations VALUES(?,1,100,100,0,'actual debt reduction')",(entry,))
  with self.assertRaisesRegex(Exception,'INVALID_INVOICE_ALLOCATION'):
   self.db.execute("INSERT INTO invoice_allocations(entry_id,document_id,amount_cents,reference) VALUES(?,?,100,'misallocate')",(entry,doc))
