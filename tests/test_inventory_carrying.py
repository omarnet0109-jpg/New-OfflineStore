import unittest
from test_v9_goods_receipts import database

class CarryingContracts(unittest.TestCase):
 def setUp(self): self.db=database()
 def tearDown(self): self.db.close()
 def test_migration_does_not_invent_financial_history(self):
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM inventory_carrying_values').fetchone()[0])
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM journal_entries').fetchone()[0])
  self.assertEqual(('EXPENSE',),self.db.execute("SELECT kind FROM journal_accounts WHERE code='INVENTORY_VARIANCE'").fetchone())
 def test_carrying_value_cannot_remain_when_quantity_is_zero(self):
  # Product 1 exists in the fixture; these failures exercise the value constraints.
  for qty,value in [(0,1),(-1,0),(1,-1)]:
   with self.assertRaises(Exception): self.db.execute('INSERT INTO inventory_carrying_values VALUES(1,?,?)',(qty,value))
 def test_fabricated_carrying_evidence_cannot_be_attached_to_missing_sale(self):
  with self.assertRaisesRegex(Exception,'CARRYING_DRAFT_SALE_REQUIRED'):
   self.db.execute('INSERT INTO inventory_cost_evidence VALUES(999,NULL,3000,100,1000,0)')
