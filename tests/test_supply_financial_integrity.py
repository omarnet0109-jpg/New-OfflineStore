"""Execute the exact production backup/startup SQL on isolated SQLite fixtures."""
from pathlib import Path
import re,sqlite3,unittest
from test_v9_goods_receipts import database,voucher,legacy_purchase
ROOT=Path(__file__).resolve().parents[1]
class SupplyFinancialIntegrityTests(unittest.TestCase):
 def setUp(self):
  self.db=database()
  source=(ROOT/'src/OfflineStore/Infrastructure/StoreDb.cs').read_text(encoding='utf-8')
  match=re.search(r'const string invalidSupply = @"(.*?)";',source,re.S)
  self.assertIsNotNone(match,'Production financial validator missing')
  self.query=match.group(1)
 def tearDown(self): self.db.close()
 def invalid(self):return self.db.execute(self.query).fetchone()
 def posted(self,paid=0):
  v=voucher(self.db,paid=paid,due=10000-paid);doc=legacy_purchase(self.db)
  self.db.execute("UPDATE SupplyVouchers SET State='POSTED',DocumentID=? WHERE VoucherID=?",(doc,v))
  if paid:self.db.execute("INSERT INTO SupplyVoucherPayments(VoucherID,AmountCents,PaymentMethod,CreatedByUserID,CreatedUtc) VALUES(?,?,'CASH',1,'now')",(v,paid))
  return v
 def test_balanced_draft_and_posted_partial_payment_are_accepted(self):
  voucher(self.db);self.assertIsNone(self.invalid())
  self.posted(3000);self.assertIsNone(self.invalid())
 def test_wrong_header_with_valid_sqlite_pages_is_rejected(self):
  v=voucher(self.db)
  self.db.execute('UPDATE SupplyVouchers SET SubTotalCents=1,GrandTotalCents=1,RemainingAmountCents=1 WHERE VoucherID=?',(v,))
  self.assertEqual(('ok',),self.db.execute('PRAGMA integrity_check').fetchone())
  self.assertEqual((v,),self.invalid())
 def test_wrong_discount_tax_and_gross_are_rejected(self):
  for update in ('TaxAmountCents=1','TotalDiscountCents=1','HeaderDiscountCents=1'):
   with self.subTest(update=update):
    self.db.execute('SAVEPOINT case_test');v=voucher(self.db)
    self.db.execute('UPDATE SupplyVouchers SET '+update+' WHERE VoucherID=?',(v,))
    self.assertEqual((v,),self.invalid());self.db.execute('ROLLBACK TO case_test');self.db.execute('RELEASE case_test')
 def test_payment_cannot_disagree_with_posted_paid_amount(self):
  v=self.posted()
  self.db.execute("INSERT INTO SupplyVoucherPayments(VoucherID,AmountCents,PaymentMethod,CreatedByUserID,CreatedUtc) VALUES(?,99999,'CASH',1,'now')",(v,))
  self.assertEqual((v,),self.invalid())
 def test_fractional_money_cannot_pass_backup_validation(self):
  v=voucher(self.db)
  self.db.execute('UPDATE SupplyVouchers SET TaxAmountCents=0.5,GrandTotalCents=10000.5,RemainingAmountCents=10000.5 WHERE VoucherID=?',(v,))
  self.assertEqual((v,),self.invalid())
 def test_correct_tax_is_computed_after_both_discounts(self):
  v=voucher(self.db)
  self.db.execute('UPDATE SupplyVoucherDetails SET DiscountValue=1000,DiscountCents=1000,LineTotalCents=9000 WHERE VoucherID=?',(v,))
  self.db.execute('UPDATE SupplyVouchers SET HeaderDiscountCents=1000,TotalDiscountCents=2000,TaxRateBps=1425,TaxAmountCents=1140,GrandTotalCents=9140,RemainingAmountCents=9140 WHERE VoucherID=?',(v,))
  self.assertIsNone(self.invalid())
