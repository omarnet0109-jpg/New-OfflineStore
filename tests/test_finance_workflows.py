"""Execute the production finance schema against isolated SQLite databases."""
import sqlite3
import unittest
from test_v9_goods_receipts import database

class FinanceWorkflowTests(unittest.TestCase):
    def setUp(self): self.db=database()
    def tearDown(self): self.db.close()
    def entry(self,kind,key,lines,state='POSTED'):
        e=self.db.execute("INSERT INTO journal_entries(source_kind,source_key,entry_date,created_by_user_id,created_utc) VALUES(?,?,'2026-11-05',1,'now')",(kind,key)).lastrowid
        for n,(account,party,debit,credit) in enumerate(lines,1):
            self.db.execute('INSERT INTO journal_lines(entry_id,line_no,account_code,party_id,debit_cents,credit_cents) VALUES(?,?,?,?,?,?)',(e,n,account,party,debit,credit))
        if state=='POSTED': self.db.execute("UPDATE journal_entries SET state='POSTED' WHERE id=?",(e,))
        return e
    def test_financial_read_models_exist(self):
        views={r[0] for r in self.db.execute("SELECT name FROM sqlite_master WHERE type='view'")}
        self.assertTrue({'v_journal_posted_lines','v_party_balances','v_trial_balance'}<=views)
    def test_balances_keep_debt_and_advances_separate_and_exclude_drafts(self):
        self.entry('OPENING','open',[('RECEIVABLES',2,100,0),('OPENING_EQUITY',None,0,100)])
        self.entry('CUSTOMER_PAYMENT','payment',[('CASH',None,150,0),('RECEIVABLES',2,0,100),('CUSTOMER_ADVANCES',2,0,50)])
        self.entry('ADJUSTMENT','draft',[('RECEIVABLES',2,900,0),('OPENING_EQUITY',None,0,900)],'DRAFT')
        row=self.db.execute('SELECT receivable_cents,payable_cents,customer_advance_cents,supplier_advance_cents FROM v_party_balances WHERE party_id=2').fetchone()
        self.assertEqual((0,0,50,0),row)
        self.assertEqual((250,250),self.db.execute('SELECT SUM(debit_cents),SUM(credit_cents) FROM v_trial_balance').fetchone())
    def test_advance_requires_correct_party_on_insert_and_update(self):
        for account,wrong,right in [('CUSTOMER_ADVANCES',1,2),('SUPPLIER_ADVANCES',2,1)]:
            with self.assertRaises(sqlite3.IntegrityError):
                self.entry('ADJUSTMENT',account,[('CASH',None,1,0),(account,wrong,0,1)])
            e=self.entry('ADJUSTMENT',account+'valid',[(account,right,1,0),('CASH',None,0,1)],'DRAFT')
            with self.assertRaises(sqlite3.IntegrityError):
                self.db.execute('UPDATE journal_lines SET party_id=? WHERE entry_id=? AND account_code=?',(wrong,e,account))
    def test_financial_operation_must_match_immutable_posted_entry(self):
        e=self.entry('CUSTOMER_PAYMENT','receipt',[('CASH',None,150,0),('RECEIVABLES',2,0,100),('CUSTOMER_ADVANCES',2,0,50)])
        sql="INSERT INTO finance_operations(entry_id,party_id,amount_cents,debt_cents,advance_cents,memo) VALUES(?,?,?,?,?,?)"
        for args in [(e,2,149,100,49,'x'),(e,1,150,100,50,'x'),(e,2,150,90,60,'x')]:
            with self.assertRaises(sqlite3.IntegrityError): self.db.execute(sql,args)
        self.db.execute(sql,(e,2,150,100,50,'receipt'))
        for sql2 in ['DELETE FROM finance_operations','UPDATE finance_operations SET memo=\'changed\'']:
            with self.assertRaises(sqlite3.IntegrityError): self.db.execute(sql2)
        with self.assertRaises(sqlite3.IntegrityError): self.db.execute(sql,(e,2,150,100,50,'duplicate'))
    def test_expense_category_and_period_profit_preserve_exact_integer_amounts(self):
        self.db.execute("INSERT INTO journal_accounts VALUES('EXP_RENT','إيجار','EXPENSE')")
        e=self.entry('EXPENSE','rent',[('EXP_RENT',None,12345,0),('CASH',None,0,12345)])
        self.db.execute("INSERT INTO finance_operations(entry_id,amount_cents,debt_cents,advance_cents,memo) VALUES(?,12345,0,0,'rent')",(e,))
        self.assertEqual(12345,self.db.execute("SELECT SUM(debit_cents-credit_cents) FROM v_journal_posted_lines WHERE kind='EXPENSE' AND entry_date BETWEEN '2026-11-01' AND '2026-11-30'").fetchone()[0])
        self.assertEqual(0,self.db.execute("SELECT COUNT(*) FROM v_journal_posted_lines WHERE entry_date<'2026-11-01'").fetchone()[0])
