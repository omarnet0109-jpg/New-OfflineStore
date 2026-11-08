import sqlite3
import unittest
import test_invoice_allocations as fixtures
from test_v9_goods_receipts import database

class CashFundingTests(unittest.TestCase):
 def setUp(self):self.db=database()
 def tearDown(self):self.db.close()
 journal=fixtures.InvoiceAllocationTests.journal
 def test_cash_funding_is_equity_not_revenue_and_metadata_is_immutable(self):
  self.assertEqual(('EQUITY',),self.db.execute("SELECT kind FROM journal_accounts WHERE code='CAPITAL'").fetchone())
  e=self.journal('ADJUSTMENT','capital:cash-1',[('CASH',None,10000,0),('CAPITAL',None,0,10000)])
  sql="INSERT INTO cash_fundings(entry_id,amount_cents,memo) VALUES(?,?,'owner capital')"
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute(sql,(e,9999))
  self.db.execute(sql,(e,10000))
  for change in ['DELETE FROM cash_fundings','UPDATE cash_fundings SET amount_cents=1']:
   with self.assertRaises(sqlite3.IntegrityError):self.db.execute(change)
  self.assertEqual(0,self.db.execute("SELECT COUNT(*) FROM v_journal_posted_lines WHERE kind IN ('INCOME','EXPENSE')").fetchone()[0])
  self.assertEqual(10000,self.db.execute("SELECT SUM(debit_cents-credit_cents) FROM v_journal_posted_lines WHERE account_code='CASH'").fetchone()[0])
