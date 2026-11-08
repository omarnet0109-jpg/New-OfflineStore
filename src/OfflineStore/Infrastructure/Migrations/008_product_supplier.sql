-- v8: non-financial preferred supplier for catalog entries.
-- This link does NOT create a purchase, payment, or inventory movement.
CREATE TABLE product_preferred_suppliers (
  product_id INTEGER PRIMARY KEY REFERENCES products(id) ON DELETE RESTRICT,
  supplier_id INTEGER NOT NULL REFERENCES parties(id) ON DELETE RESTRICT,
  updated_utc TEXT NOT NULL
);
CREATE INDEX ix_product_preferred_suppliers_supplier ON product_preferred_suppliers(supplier_id);
CREATE TRIGGER preferred_supplier_insert_guard BEFORE INSERT ON product_preferred_suppliers
WHEN NOT EXISTS (SELECT 1 FROM parties p WHERE p.id=NEW.supplier_id
                 AND p.party_type IN ('SUPPLIER','BOTH'))
BEGIN SELECT RAISE(ABORT,'NOT_A_SUPPLIER'); END;
CREATE TRIGGER preferred_supplier_update_guard BEFORE UPDATE OF supplier_id ON product_preferred_suppliers
WHEN NOT EXISTS (SELECT 1 FROM parties p WHERE p.id=NEW.supplier_id
                 AND p.party_type IN ('SUPPLIER','BOTH'))
BEGIN SELECT RAISE(ABORT,'NOT_A_SUPPLIER'); END;
