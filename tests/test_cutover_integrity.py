import unittest
from test_v9_goods_receipts import database,legacy_purchase

class CutoverIntegrityTests(unittest.TestCase):
 def setUp(self):self.db=database()
 def tearDown(self):self.db.close()
 def test_post_cutover_monetary_invoice_without_journal_is_detected(self):
  self.db.execute("INSERT INTO finance_activation VALUES(1,NULL,0,'2026-11-06',1)")
  doc=legacy_purchase(self.db)
  self.assertEqual([(doc,)],self.db.execute('SELECT document_id FROM v_invalid_financial_documents').fetchall())
 def test_legacy_manifest_is_not_a_historical_money_backfill_and_cannot_expand(self):
  doc=legacy_purchase(self.db)
  self.db.execute('INSERT INTO finance_legacy_documents VALUES(?)',(doc,))
  self.db.execute("INSERT INTO finance_activation VALUES(1,NULL,?,'2026-11-06',1)",(doc,))
  self.assertEqual([],self.db.execute('SELECT document_id FROM v_invalid_financial_documents').fetchall())
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM journal_entries').fetchone()[0])
  with self.assertRaises(Exception):self.db.execute('DELETE FROM finance_legacy_documents')
  new=legacy_purchase(self.db)
  with self.assertRaises(Exception):self.db.execute('INSERT INTO finance_legacy_documents VALUES(?)',(new,))

 def test_profit_boundary_is_explicit_and_immutable(self):
  self.db.execute("INSERT INTO finance_activation VALUES(1,NULL,0,'2026-11-06',1)")
  self.db.execute('INSERT INTO finance_activation_boundary VALUES(1,0)')
  self.assertEqual([],self.db.execute('SELECT * FROM v_financial_profit_lines').fetchall())
  with self.assertRaises(Exception):self.db.execute('UPDATE finance_activation_boundary SET journal_through_id=1')
  with self.assertRaises(Exception):self.db.execute('DELETE FROM finance_activation_boundary')

 def post_expense(self,db,key,value):
  entry=db.execute("INSERT INTO journal_entries(source_kind,source_key,entry_date,created_by_user_id,created_utc) VALUES('ADJUSTMENT',?,'2026-11-06',1,'now')",(key,)).lastrowid
  db.executemany("INSERT INTO journal_lines(entry_id,line_no,account_code,debit_cents,credit_cents) VALUES(?,?,?,?,?)",[(entry,1,'EXPENSES',value,0),(entry,2,'OPENING_EQUITY',0,value)])
  db.execute("UPDATE journal_entries SET state='POSTED' WHERE id=?",(entry,))
  return entry
 def test_same_day_legacy_results_are_excluded_but_new_results_are_included(self):
  old=self.post_expense(self.db,'legacy',100)
  self.db.execute("INSERT INTO finance_activation VALUES(1,NULL,0,'2026-11-06',1)")
  with self.assertRaises(Exception):self.db.execute('INSERT INTO finance_activation_boundary VALUES(1,0)')
  self.db.execute('INSERT INTO finance_activation_boundary VALUES(1,?)',(old,))
  new=self.post_expense(self.db,'current',50)
  self.assertEqual([(new,50)],self.db.execute("SELECT entry_id,debit_cents FROM v_financial_profit_lines WHERE account_code='EXPENSES'").fetchall())
 def test_upgrade_preserves_already_active_profit_without_guessing_boundary(self):
  from test_v9_goods_receipts import INFRA
  db=database(up_to=21)
  try:
   self.post_expense(db,'existing',100)
   db.execute("INSERT INTO finance_activation VALUES(1,NULL,0,'2026-11-06',1)")
   db.executescript((INFRA/'Migrations/022_activation_results.sql').read_text())
   self.assertEqual((0,),db.execute('SELECT journal_through_id FROM finance_activation_boundary').fetchone())
   self.assertEqual((100,),db.execute("SELECT SUM(debit_cents-credit_cents) FROM v_financial_profit_lines WHERE account_code='EXPENSES'").fetchone())
  finally:db.close()
