import sqlite3
import unittest
from test_v9_goods_receipts import database,legacy_purchase

class InvoiceAllocationTests(unittest.TestCase):
 def setUp(self):self.db=database()
 def tearDown(self):self.db.close()
 def journal(self,kind,key,lines,doc=None):
  e=self.db.execute("INSERT INTO journal_entries(source_kind,source_key,entry_date,created_by_user_id,created_utc,document_id) VALUES(?,?,'2026-11-06',1,'now',?)",(kind,key,doc)).lastrowid
  for n,(a,p,d,c) in enumerate(lines,1):self.db.execute('INSERT INTO journal_lines(entry_id,line_no,account_code,party_id,debit_cents,credit_cents) VALUES(?,?,?,?,?,?)',(e,n,a,p,d,c))
  self.db.execute("UPDATE journal_entries SET state='POSTED' WHERE id=?",(e,));return e
 def test_payment_allocation_cannot_exceed_receipt_or_invoice_and_is_immutable(self):
  doc=legacy_purchase(self.db)
  self.journal('PURCHASE','invoice',[('INVENTORY',None,10000,0),('PAYABLES',1,0,10000)],doc)
  pay=self.journal('SUPPLIER_PAYMENT','payment',[('PAYABLES',1,6000,0),('CASH',None,0,6000)])
  self.db.execute('INSERT INTO invoice_allocations(entry_id,document_id,amount_cents) VALUES(?,?,4000)',(pay,doc))
  self.assertEqual(6000,self.db.execute('SELECT unallocated_invoice_cents FROM v_financial_invoices WHERE document_id=?',(doc,)).fetchone()[0])
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute('INSERT INTO invoice_allocations(entry_id,document_id,amount_cents) VALUES(?,?,3000)',(pay,doc))
  for sql in ['DELETE FROM invoice_allocations','UPDATE invoice_allocations SET amount_cents=1']:
   with self.assertRaises(sqlite3.IntegrityError):self.db.execute(sql)
 def test_allocation_requires_same_party_and_correct_debt_side(self):
  doc=legacy_purchase(self.db);self.journal('PURCHASE','invoice',[('INVENTORY',None,10000,0),('PAYABLES',1,0,10000)],doc)
  for kind,key,lines in [('CUSTOMER_PAYMENT','wrong-kind',[('CASH',None,100,0),('RECEIVABLES',2,0,100)]),('ADJUSTMENT','wrong-side',[('CASH',None,100,0),('PAYABLES',1,0,100)])]:
   e=self.journal(kind,key,lines)
   with self.assertRaises(sqlite3.IntegrityError):self.db.execute('INSERT INTO invoice_allocations(entry_id,document_id,amount_cents) VALUES(?,?,100)',(e,doc))
 def test_legacy_stock_invoice_without_financial_posting_is_not_inferred_as_debt(self):
  legacy_purchase(self.db)
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM v_financial_invoices').fetchone()[0])
