import sqlite3
import unittest
from test_v9_goods_receipts import database,legacy_purchase,INFRA

class ReturnCreditTests(unittest.TestCase):
 def setUp(self):self.db=database()
 def tearDown(self):self.db.close()
 def test_credit_accounts_are_separate_from_advances(self):
  self.assertEqual([('CUSTOMER_CREDITS','LIABILITY'),('SUPPLIER_CREDITS','ASSET')],self.db.execute("SELECT code,kind FROM journal_accounts WHERE code IN ('CUSTOMER_CREDITS','SUPPLIER_CREDITS') ORDER BY code").fetchall())
 def test_return_header_requires_named_credit_beneficiary_and_exact_split(self):
  d=self.db.execute("INSERT INTO documents(doc_type,total_cents,created_utc,created_by_user_id) VALUES('SALE',100,'now',1)").lastrowid
  sql="INSERT INTO return_documents(original_document_id,reference,return_date,cash_cents,debt_cents,credit_cents,credit_party_id,total_cents,created_by_user_id,created_utc,memo) VALUES(?,?,'2026-11-06',0,0,100,?,100,1,'now','credit')"
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute(sql,(d,'unnamed',None))
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute(sql,(d,'supplier-is-not-customer',1))
  self.db.execute(sql,(d,'named-customer',2))
  with self.assertRaises(sqlite3.IntegrityError):self.db.execute("INSERT INTO return_documents(original_document_id,reference,return_date,cash_cents,debt_cents,credit_cents,credit_party_id,total_cents,created_by_user_id,created_utc,memo) VALUES(?,'bad-sum','2026-11-06',1,0,100,2,100,1,'now','credit')",(d,))

 def test_nonempty_header_copy_rolls_back_and_preserves_foreign_keys_sequence_and_history(self):
  old=database(20)
  try:
   doc=legacy_purchase(old)
   old.execute("INSERT INTO return_documents(original_document_id,reference,return_date,cash_cents,debt_cents,total_cents,created_by_user_id,created_utc,memo) VALUES(?,'old','2026-11-06',0,10000,10000,1,'now','بيان قديم')",(doc,))
   old.execute("INSERT INTO return_lines(return_id,original_line_id,qty_milli,amount_cents,cost_cents,cost_basis_millicents,name_snapshot,unit_snapshot) VALUES(1,1,1000,10000,10000,10000000,'صنف قديم','قطعة')")
   old.execute("UPDATE sqlite_sequence SET seq=100 WHERE name='return_documents'");old.commit()
   headers=old.execute('SELECT * FROM return_documents').fetchall();lines=old.execute('SELECT * FROM return_lines').fetchall()
   sql=(INFRA/'Migrations'/'021_return_credits.sql').read_text()
   old.execute('PRAGMA foreign_keys=OFF')
   with self.assertRaises(sqlite3.OperationalError):old.executescript('BEGIN;'+sql+'SELECT * FROM injected_failure;COMMIT;')
   old.rollback();old.execute('PRAGMA legacy_alter_table=OFF');old.execute('PRAGMA foreign_keys=ON')
   self.assertEqual(headers,old.execute('SELECT * FROM return_documents').fetchall())
   self.assertNotIn('credit_cents',[r[1] for r in old.execute("PRAGMA table_info(return_documents)")])
   old.executescript('PRAGMA foreign_keys=OFF;BEGIN;'+sql+'COMMIT;PRAGMA foreign_keys=ON;')
   self.assertEqual(headers,[r[:12] for r in old.execute('SELECT * FROM return_documents')])
   self.assertEqual(lines,old.execute('SELECT * FROM return_lines').fetchall())
   self.assertEqual(100,old.execute("SELECT seq FROM sqlite_sequence WHERE name='return_documents'").fetchone()[0])
   self.assertEqual([],old.execute('PRAGMA foreign_key_check').fetchall())
   self.assertEqual('ok',old.execute('PRAGMA integrity_check').fetchone()[0])
   self.assertEqual((0,None),old.execute('SELECT credit_cents,credit_party_id FROM return_documents').fetchone())
  finally:old.close()
