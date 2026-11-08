import sqlite3
import unittest
import test_finance_workflows as fixtures

class AdvanceApplicationTests(unittest.TestCase):
    setUp=fixtures.FinanceWorkflowTests.setUp
    tearDown=fixtures.FinanceWorkflowTests.tearDown
    entry=fixtures.FinanceWorkflowTests.entry
    def test_advance_application_matches_party_and_immutable_journal(self):
        e=self.entry('ADJUSTMENT','apply-1',[('CUSTOMER_ADVANCES',2,50,0),('RECEIVABLES',2,0,50)])
        sql="INSERT INTO advance_applications(entry_id,party_id,is_customer,amount_cents,memo) VALUES(?,?,?,?,?)"
        for args in [(e,1,1,50,'wrong party'),(e,2,0,50,'wrong type'),(e,2,1,49,'wrong amount')]:
            with self.assertRaises(sqlite3.IntegrityError):self.db.execute(sql,args)
        self.db.execute(sql,(e,2,1,50,'apply customer advance'))
        with self.assertRaises(sqlite3.IntegrityError):self.db.execute('UPDATE advance_applications SET amount_cents=1')
        with self.assertRaises(sqlite3.IntegrityError):self.db.execute('DELETE FROM advance_applications')
    def test_supplier_advance_application_is_not_income_or_expense(self):
        self.entry('ADJUSTMENT','supplier-opening',[('SUPPLIER_ADVANCES',1,150,0),('PAYABLES',1,0,150)])
        e=self.entry('ADJUSTMENT','apply-2',[('PAYABLES',1,75,0),('SUPPLIER_ADVANCES',1,0,75)])
        self.db.execute("INSERT INTO advance_applications(entry_id,party_id,is_customer,amount_cents,memo) VALUES(?,1,0,75,'apply')",(e,))
        self.assertEqual((75,75),self.db.execute('SELECT payable_cents,supplier_advance_cents FROM v_party_balances WHERE party_id=1').fetchone())
        self.assertEqual(0,self.db.execute("SELECT COUNT(*) FROM v_journal_posted_lines WHERE kind IN ('INCOME','EXPENSE')").fetchone()[0])
