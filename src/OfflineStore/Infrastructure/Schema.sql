-- OfflineStore schema v1. All money is integer minor units (e.g. piastres/cents).
-- Quantities are integer milli-units (1 unit = 1000). Never use REAL for money.
CREATE TABLE IF NOT EXISTS products (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    sku TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK(length(trim(sku)) > 0),
    barcode TEXT COLLATE NOCASE UNIQUE,
    name TEXT NOT NULL CHECK(length(trim(name)) > 0),
    unit TEXT NOT NULL DEFAULT 'قطعة' CHECK(length(trim(unit)) > 0),
    sale_price_cents INTEGER NOT NULL DEFAULT 0 CHECK(sale_price_cents >= 0),
    last_cost_cents INTEGER NOT NULL DEFAULT 0 CHECK(last_cost_cents >= 0),
    reorder_qty_milli INTEGER NOT NULL DEFAULT 0 CHECK(reorder_qty_milli >= 0),
    active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0,1)),
    created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS parties (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    party_type TEXT NOT NULL CHECK(party_type IN ('SUPPLIER','CUSTOMER','BOTH')),
    name TEXT NOT NULL CHECK(length(trim(name)) > 0),
    phone TEXT,
    created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS documents (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    doc_code TEXT UNIQUE,
    doc_type TEXT NOT NULL CHECK(doc_type IN ('PURCHASE','SALE')),
    state TEXT NOT NULL DEFAULT 'DRAFT' CHECK(state IN ('DRAFT','POSTED')),
    party_id INTEGER REFERENCES parties(id) ON DELETE RESTRICT,
    total_cents INTEGER NOT NULL DEFAULT 0 CHECK(total_cents >= 0),
    created_utc TEXT NOT NULL,
    note TEXT,
    CHECK(doc_type <> 'PURCHASE' OR party_id IS NOT NULL)
);
CREATE TABLE IF NOT EXISTS document_lines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    line_no INTEGER NOT NULL CHECK(line_no > 0),
    product_id INTEGER NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
    qty_milli INTEGER NOT NULL CHECK(qty_milli > 0),
    unit_price_cents INTEGER NOT NULL CHECK(unit_price_cents >= 0),
    line_total_cents INTEGER NOT NULL CHECK(line_total_cents >= 0),
    unit_cost_snapshot_cents INTEGER NOT NULL DEFAULT 0 CHECK(unit_cost_snapshot_cents >= 0),
    UNIQUE(document_id, line_no)
);
CREATE TABLE IF NOT EXISTS stock_movements (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
    document_line_id INTEGER NOT NULL UNIQUE REFERENCES document_lines(id) ON DELETE RESTRICT,
    product_id INTEGER NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
    delta_qty_milli INTEGER NOT NULL CHECK(delta_qty_milli <> 0),
    created_utc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS audit_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    action TEXT NOT NULL,
    entity_id INTEGER NOT NULL,
    details TEXT,
    created_utc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_document_lines_product ON document_lines(product_id);
CREATE INDEX IF NOT EXISTS ix_movements_product ON stock_movements(product_id);
CREATE INDEX IF NOT EXISTS ix_movements_document ON stock_movements(document_id);
CREATE INDEX IF NOT EXISTS ix_documents_created ON documents(created_utc);

-- A posted document is immutable; corrections will be separate documents in future releases.
CREATE TRIGGER IF NOT EXISTS immutable_posted_document
BEFORE UPDATE ON documents WHEN OLD.state = 'POSTED'
BEGIN SELECT RAISE(ABORT, 'POSTED_DOCUMENT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS prevent_posted_document_delete
BEFORE DELETE ON documents WHEN OLD.state = 'POSTED'
BEGIN SELECT RAISE(ABORT, 'POSTED_DOCUMENT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS prevent_line_update
BEFORE UPDATE ON document_lines
BEGIN SELECT RAISE(ABORT, 'DOCUMENT_LINE_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS prevent_line_delete
BEFORE DELETE ON document_lines
BEGIN SELECT RAISE(ABORT, 'DOCUMENT_LINE_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS prevent_movement_update
BEFORE UPDATE ON stock_movements
BEGIN SELECT RAISE(ABORT, 'STOCK_MOVEMENT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS prevent_movement_delete
BEFORE DELETE ON stock_movements
BEGIN SELECT RAISE(ABORT, 'STOCK_MOVEMENT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS prevent_audit_update
BEFORE UPDATE ON audit_log
BEGIN SELECT RAISE(ABORT, 'AUDIT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS prevent_audit_delete
BEFORE DELETE ON audit_log
BEGIN SELECT RAISE(ABORT, 'AUDIT_IMMUTABLE'); END;
-- Database-level protection against overselling even if UI/service has a bug.
CREATE TRIGGER IF NOT EXISTS stock_movement_validate BEFORE INSERT ON stock_movements
BEGIN
  SELECT CASE WHEN NOT EXISTS (
      SELECT 1 FROM document_lines l JOIN documents d ON d.id = l.document_id
      WHERE l.id = NEW.document_line_id
        AND l.document_id = NEW.document_id
        AND l.product_id = NEW.product_id
        AND ((d.doc_type='PURCHASE' AND NEW.delta_qty_milli = l.qty_milli)
          OR (d.doc_type='SALE' AND NEW.delta_qty_milli = -l.qty_milli))
  ) THEN RAISE(ABORT, 'INVALID_STOCK_MOVEMENT') END;
  SELECT CASE WHEN NEW.delta_qty_milli < 0 AND
     (SELECT COALESCE(SUM(delta_qty_milli),0) FROM stock_movements
      WHERE product_id = NEW.product_id) + NEW.delta_qty_milli < 0
     THEN RAISE(ABORT, 'INSUFFICIENT_STOCK') END;
END;
CREATE VIEW IF NOT EXISTS v_stock AS
SELECT p.id AS product_id, p.sku, p.name, p.unit, p.sale_price_cents,
       p.last_cost_cents, p.reorder_qty_milli, p.active,
       COALESCE(SUM(m.delta_qty_milli),0) AS qty_milli
FROM products p LEFT JOIN stock_movements m ON p.id = m.product_id
GROUP BY p.id;
CREATE TRIGGER IF NOT EXISTS prevent_posted_line_insert
BEFORE INSERT ON document_lines
WHEN (SELECT state FROM documents WHERE id=NEW.document_id) = 'POSTED'
BEGIN SELECT RAISE(ABORT, 'POSTED_DOCUMENT_IMMUTABLE'); END;
CREATE TRIGGER IF NOT EXISTS prevent_posted_movement_insert
BEFORE INSERT ON stock_movements
WHEN (SELECT state FROM documents WHERE id=NEW.document_id) = 'POSTED'
BEGIN SELECT RAISE(ABORT, 'POSTED_DOCUMENT_IMMUTABLE'); END;
