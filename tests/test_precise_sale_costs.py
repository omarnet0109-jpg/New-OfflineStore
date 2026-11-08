"""Additive cost snapshots preserve old invoices; no historical backfill."""
from pathlib import Path
import unittest
from test_v9_goods_receipts import database,legacy_purchase
ROOT=Path(__file__).resolve().parents[1]
class PreciseSaleCostTests(unittest.TestCase):
 def setUp(self): self.db=database()
 def tearDown(self): self.db.close()
 def test_precise_cost_extension_exists_without_rewriting_legacy_lines(self):
  before=legacy_purchase(self.db)
  self.assertIn('sale_cost_snapshots',[r[0] for r in self.db.execute("SELECT name FROM sqlite_master WHERE type='table'")])
  self.assertEqual(10000,self.db.execute('SELECT unit_cost_snapshot_cents FROM document_lines WHERE document_id=?',(before,)).fetchone()[0])
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM sale_cost_snapshots').fetchone()[0])
 def test_service_records_original_average_and_total_cost(self):
  s=(ROOT/'src/OfflineStore/Services/StoreService.cs').read_text(encoding='utf-8')
  self.assertIn('INSERT INTO sale_cost_snapshots',s)
  self.assertIn('InventoryValueService.Issue(c,tx',s)
  self.assertIn('InventoryValueService.Evidence(c,tx,true,lineId',s)
  m=(ROOT/'src/OfflineStore/Models/Records.cs').read_text(encoding='utf-8')
  self.assertIn('public static long CostTotal(',m)

 def test_additive_migration_does_not_recalculate_old_posted_costs(self):
  self.db.execute('DROP TABLE sale_cost_snapshots')
  doc=legacy_purchase(self.db)
  before=self.db.execute('SELECT * FROM document_lines WHERE document_id=?',(doc,)).fetchall()
  self.db.executescript((ROOT/'src/OfflineStore/Infrastructure/Migrations/013_precise_sale_costs.sql').read_text(encoding='utf-8'))
  self.assertEqual(before,self.db.execute('SELECT * FROM document_lines WHERE document_id=?',(doc,)).fetchall())
  self.assertEqual(0,self.db.execute('SELECT COUNT(*) FROM sale_cost_snapshots').fetchone()[0])
