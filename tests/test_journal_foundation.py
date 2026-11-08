"""Foundation only: no inference of historical collections/debts or stock changes."""
from pathlib import Path
import sqlite3,unittest
from test_v9_goods_receipts import database
ROOT=Path(__file__).resolve().parents[1]
class JournalFoundationTests(unittest.TestCase):
 def setUp(self):
  self.db=database()
  self.assertIn('journal_entries',[r[0] for r in self.db.execute("SELECT name FROM sqlite_master WHERE type='table'")])
 def tearDown(self):self.db.close()
 def entry(self,key='test'):
  return self.db.execute("INSERT INTO journal_entries(source_kind,source_key,entry_date,created_by_user_id,created_utc) VALUES('ADJUSTMENT',?,'2026-11-05',1,'now')",(key,)).lastrowid
 def line(self,entry,n,account,debit,credit):
  self.db.execute('INSERT INTO journal_lines(entry_id,line_no,account_code,debit_cents,credit_cents) VALUES(?,?,?,?,?)',(entry,n,account,debit,credit))
 def post(self,entry):self.db.execute("UPDATE journal_entries SET state='POSTED' WHERE id=?",(entry,))
 def test_migration_does_not_create_ledger_from_old_invoices(self):
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM journal_entries').fetchone()[0])
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM stock_movements').fetchone()[0])
 def test_balanced_entry_posts_and_is_immutable(self):
  e=self.entry();self.line(e,1,'CASH',10000,0);self.line(e,2,'OPENING_EQUITY',0,10000);self.post(e)
  for sql in ('UPDATE journal_entries SET entry_date=\'2026-11-06\' WHERE id=?','DELETE FROM journal_entries WHERE id=?','DELETE FROM journal_lines WHERE entry_id=?','UPDATE journal_lines SET debit_cents=1 WHERE entry_id=?'):
   with self.assertRaises(sqlite3.IntegrityError):self.db.execute(sql,(e,))
 def test_unbalanced_and_empty_entries_cannot_post(self):
  e=self.entry();self.line(e,1,'CASH',10000,0);self.line(e,2,'OPENING_EQUITY',0,9999)
  with self.assertRaises(sqlite3.IntegrityError):self.post(e)
  with self.assertRaises(sqlite3.IntegrityError):self.post(self.entry('empty'))
 def test_duplicate_source_key_prevents_duplicate_posting(self):
  self.entry('invoice:1')
  with self.assertRaises(sqlite3.IntegrityError):self.entry('invoice:1')
 def test_money_is_integer_one_sided_and_account_must_exist(self):
  e=self.entry()
  for account,debit,credit in [('CASH',1.5,0),('CASH',1,1),('CASH',-1,0),('CASH',0,0),('MISSING',1,0)]:
   with self.assertRaises(sqlite3.IntegrityError):self.line(e,1,account,debit,credit)

 def test_draft_update_cannot_create_invalid_customer_balance(self):
  e=self.entry();self.line(e,1,'CASH',10000,0);self.line(e,2,'OPENING_EQUITY',0,10000)
  with self.assertRaises(sqlite3.IntegrityError):
   self.db.execute("UPDATE journal_lines SET account_code='RECEIVABLES',party_id=1 WHERE entry_id=? AND line_no=1",(e,))
  self.db.execute("UPDATE journal_lines SET account_code='RECEIVABLES',party_id=2 WHERE entry_id=? AND line_no=1",(e,))
  self.post(e)
