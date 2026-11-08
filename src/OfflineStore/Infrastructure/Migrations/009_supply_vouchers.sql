-- v9: Goods receipt master/detail, integrated with the existing immutable
-- documents/document_lines/stock_movements ledger upon approval.
-- Money = integer Egyptian piastres; quantities = milli-units (1000 == 1).
-- Posted vouchers are NEVER cascade-deleted: foreign-key CASCADE applies only to drafts.
CREATE TABLE SupplyVouchers (
  VoucherID INTEGER PRIMARY KEY AUTOINCREMENT,
  VoucherCode TEXT UNIQUE,
  VoucherDate TEXT NOT NULL CHECK(length(VoucherDate)=10),
  SupplierID INTEGER NOT NULL REFERENCES parties(id) ON DELETE RESTRICT,
  SupplierInvoiceNo TEXT NOT NULL DEFAULT '',
  SupplierInvoiceDate TEXT NOT NULL CHECK(length(SupplierInvoiceDate)=10),
  PaymentType TEXT NOT NULL CHECK(PaymentType IN ('CASH','CREDIT')),
  TaxRateBps INTEGER NOT NULL DEFAULT 0 CHECK(TaxRateBps BETWEEN 0 AND 10000),
  TaxAmountCents INTEGER NOT NULL DEFAULT 0 CHECK(TaxAmountCents>=0),
  HeaderDiscountCents INTEGER NOT NULL DEFAULT 0 CHECK(HeaderDiscountCents>=0),
  TotalDiscountCents INTEGER NOT NULL DEFAULT 0 CHECK(TotalDiscountCents>=0),
  SubTotalCents INTEGER NOT NULL DEFAULT 0 CHECK(SubTotalCents>=0),
  GrandTotalCents INTEGER NOT NULL DEFAULT 0 CHECK(GrandTotalCents>=0),
  TotalWordsAr TEXT NOT NULL DEFAULT '',
  PaidAmountCents INTEGER NOT NULL DEFAULT 0 CHECK(PaidAmountCents>=0),
  RemainingAmountCents INTEGER NOT NULL DEFAULT 0 CHECK(RemainingAmountCents>=0),
  Notes TEXT NOT NULL DEFAULT '',
  State TEXT NOT NULL DEFAULT 'DRAFT' CHECK(State IN ('DRAFT','POSTED')),
  DocumentID INTEGER UNIQUE REFERENCES documents(id) ON DELETE RESTRICT,
  CreatedByUserID INTEGER NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  CreatedUtc TEXT NOT NULL,
  UpdatedUtc TEXT NOT NULL,
  CHECK(SupplierInvoiceDate<=VoucherDate),
  CHECK(PaidAmountCents+RemainingAmountCents=GrandTotalCents),
  CHECK(PaymentType<>'CASH' OR RemainingAmountCents=0),
  CHECK(State<>'POSTED' OR (VoucherCode IS NOT NULL AND DocumentID IS NOT NULL))
);
CREATE INDEX ix_supply_vouchers_supplier_date ON SupplyVouchers(SupplierID,VoucherDate);
CREATE INDEX ix_supply_vouchers_state_date ON SupplyVouchers(State,VoucherDate DESC);
CREATE UNIQUE INDEX ux_supply_supplier_external_invoice
 ON SupplyVouchers(SupplierID,SupplierInvoiceNo)
 WHERE State='POSTED' AND SupplierInvoiceNo<>'';

CREATE TABLE SupplyVoucherDetails (
  DetailID INTEGER PRIMARY KEY AUTOINCREMENT,
  VoucherID INTEGER NOT NULL REFERENCES SupplyVouchers(VoucherID) ON DELETE CASCADE,
  LineNo INTEGER NOT NULL CHECK(LineNo>0),
  ProductID INTEGER NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
  ProductUnitID INTEGER NOT NULL REFERENCES product_units(id) ON DELETE RESTRICT,
  ItemCode TEXT NOT NULL,
  ItemName TEXT NOT NULL,
  Unit TEXT NOT NULL,
  UnitFactorMilli INTEGER NOT NULL CHECK(UnitFactorMilli>=1000),
  QuantityMilli INTEGER NOT NULL CHECK(QuantityMilli>0),
  BaseQuantityMilli INTEGER NOT NULL CHECK(BaseQuantityMilli>0),
  UnitCostCents INTEGER NOT NULL CHECK(UnitCostCents>0),
  DiscountType TEXT NOT NULL CHECK(DiscountType IN ('AMOUNT','PERCENT')),
  DiscountValue INTEGER NOT NULL CHECK(DiscountValue>=0),
  DiscountCents INTEGER NOT NULL CHECK(DiscountCents>=0),
  GrossTotalCents INTEGER NOT NULL CHECK(GrossTotalCents>=0),
  LineTotalCents INTEGER NOT NULL CHECK(LineTotalCents>=0),
  UNIQUE(VoucherID,LineNo),
  CHECK(DiscountCents<=GrossTotalCents),
  CHECK(LineTotalCents=GrossTotalCents-DiscountCents)
);
CREATE INDEX ix_supply_lines_product ON SupplyVoucherDetails(ProductID);

-- Record the initial supplier payment without pretending to implement a cash drawer.
CREATE TABLE SupplyVoucherPayments (
  PaymentID INTEGER PRIMARY KEY AUTOINCREMENT,
  VoucherID INTEGER NOT NULL REFERENCES SupplyVouchers(VoucherID) ON DELETE RESTRICT,
  AmountCents INTEGER NOT NULL CHECK(AmountCents>0),
  PaymentMethod TEXT NOT NULL CHECK(PaymentMethod='CASH'),
  CreatedByUserID INTEGER NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
  CreatedUtc TEXT NOT NULL
);

-- Sub-cent moving-average basis, with a one-time estimate for old stock.
-- Future receipts through both purchase paths update this figure.
ALTER TABLE products ADD COLUMN avg_cost_millicents INTEGER NOT NULL DEFAULT 0
  CHECK(avg_cost_millicents>=0);
UPDATE products SET avg_cost_millicents=last_cost_cents*1000;

CREATE TRIGGER sv_no_posted_delete BEFORE DELETE ON SupplyVouchers
WHEN OLD.State='POSTED'
BEGIN SELECT RAISE(ABORT,'POSTED_SUPPLY_IMMUTABLE'); END;
CREATE TRIGGER sv_no_posted_update BEFORE UPDATE ON SupplyVouchers
WHEN OLD.State='POSTED'
BEGIN SELECT RAISE(ABORT,'POSTED_SUPPLY_IMMUTABLE'); END;
CREATE TRIGGER sv_detail_insert_guard BEFORE INSERT ON SupplyVoucherDetails
WHEN (SELECT State FROM SupplyVouchers WHERE VoucherID=NEW.VoucherID)<>'DRAFT'
BEGIN SELECT RAISE(ABORT,'POSTED_SUPPLY_IMMUTABLE'); END;
CREATE TRIGGER sv_detail_update_guard BEFORE UPDATE ON SupplyVoucherDetails
WHEN (SELECT State FROM SupplyVouchers WHERE VoucherID=OLD.VoucherID)<>'DRAFT'
BEGIN SELECT RAISE(ABORT,'POSTED_SUPPLY_IMMUTABLE'); END;
CREATE TRIGGER sv_detail_delete_guard BEFORE DELETE ON SupplyVoucherDetails
WHEN (SELECT State FROM SupplyVouchers WHERE VoucherID=OLD.VoucherID)<>'DRAFT'
BEGIN SELECT RAISE(ABORT,'POSTED_SUPPLY_IMMUTABLE'); END;
CREATE TRIGGER sv_payment_no_update BEFORE UPDATE ON SupplyVoucherPayments
BEGIN SELECT RAISE(ABORT,'SUPPLIER_PAYMENT_IMMUTABLE'); END;
CREATE TRIGGER sv_payment_no_delete BEFORE DELETE ON SupplyVoucherPayments
BEGIN SELECT RAISE(ABORT,'SUPPLIER_PAYMENT_IMMUTABLE'); END;
CREATE TRIGGER sv_payment_only_posted BEFORE INSERT ON SupplyVoucherPayments
WHEN (SELECT State FROM SupplyVouchers WHERE VoucherID=NEW.VoucherID)<>'POSTED'
BEGIN SELECT RAISE(ABORT,'PAYMENT_REQUIRES_POSTED_SUPPLY'); END;

CREATE TRIGGER sv_supplier_guard BEFORE INSERT ON SupplyVouchers
WHEN NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=NEW.SupplierID
 AND p.party_type IN ('SUPPLIER','BOTH'))
BEGIN SELECT RAISE(ABORT,'SUPPLY_REQUIRES_SUPPLIER'); END;
CREATE TRIGGER sv_supplier_change_guard BEFORE UPDATE OF SupplierID ON SupplyVouchers
WHEN NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=NEW.SupplierID
 AND p.party_type IN ('SUPPLIER','BOTH'))
BEGIN SELECT RAISE(ABORT,'SUPPLY_REQUIRES_SUPPLIER'); END;

CREATE TRIGGER sv_post_guard BEFORE UPDATE OF State ON SupplyVouchers
WHEN OLD.State='DRAFT' AND NEW.State='POSTED'
BEGIN
 SELECT CASE WHEN NEW.DocumentID IS NULL OR NEW.VoucherCode IS NULL
   THEN RAISE(ABORT,'SUPPLY_REQUIRES_LEDGER') END;
 SELECT CASE WHEN (SELECT COUNT(*) FROM SupplyVoucherDetails
   WHERE VoucherID=OLD.VoucherID)=0 THEN RAISE(ABORT,'EMPTY_SUPPLY') END;
 SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM documents d
   WHERE d.id=NEW.DocumentID AND d.state='POSTED'
     AND d.doc_type='PURCHASE' AND d.party_id=NEW.SupplierID)
   THEN RAISE(ABORT,'SUPPLY_LEDGER_NOT_POSTED') END;
 SELECT CASE WHEN EXISTS (SELECT 1 FROM SupplyVoucherDetails x
   WHERE x.VoucherID=OLD.VoucherID AND
     NOT EXISTS (SELECT 1 FROM document_lines l
       WHERE l.document_id=NEW.DocumentID AND l.line_no=x.LineNo
         AND l.product_id=x.ProductID AND l.qty_milli=x.BaseQuantityMilli))
   THEN RAISE(ABORT,'SUPPLY_LEDGER_MISMATCH') END;
END;
