-- v3: a product has ONE base unit, optional package units, and independent unit prices.
-- factor_milli is the number of thousandths of the base unit in ONE selected unit.
-- E.g. piece=1000, dozen=12000, carton of 48=48000. No universal carton size.
ALTER TABLE products ADD COLUMN is_countable INTEGER NOT NULL DEFAULT 1 CHECK(is_countable IN (0,1));
UPDATE products SET is_countable=0 WHERE unit NOT IN ('قطعة','عدد','حبة','وحدة');
CREATE TABLE product_units (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 product_id INTEGER NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
 name TEXT NOT NULL COLLATE NOCASE CHECK(length(trim(name)) BETWEEN 1 AND 40),
 factor_milli INTEGER NOT NULL CHECK(factor_milli >= 1000 AND factor_milli % 1000 = 0),
 sale_price_cents INTEGER NOT NULL CHECK(sale_price_cents >= 0),
 barcode TEXT COLLATE NOCASE UNIQUE,
 is_base INTEGER NOT NULL DEFAULT 0 CHECK(is_base IN (0,1)),
 active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0,1)),
 UNIQUE(product_id,name),
 CHECK((is_base=1 AND factor_milli=1000) OR is_base=0)
);
CREATE UNIQUE INDEX ux_product_single_base ON product_units(product_id) WHERE is_base=1;
CREATE INDEX ix_product_units_product ON product_units(product_id,active);
INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,is_base)
 SELECT id,unit,1000,sale_price_cents,1 FROM products;
CREATE TRIGGER product_unit_immutable_identity BEFORE UPDATE OF product_id,name,factor_milli,is_base
 ON product_units BEGIN SELECT RAISE(ABORT,'UNIT_CONVERSION_IMMUTABLE'); END;
CREATE TRIGGER product_unit_no_delete BEFORE DELETE ON product_units
 BEGIN SELECT RAISE(ABORT,'UNIT_CANNOT_BE_DELETED'); END;
CREATE TRIGGER product_unit_base_stays_active BEFORE UPDATE OF active ON product_units
 WHEN OLD.is_base=1 AND NEW.active=0 BEGIN SELECT RAISE(ABORT,'BASE_UNIT_MUST_REMAIN_ACTIVE'); END;

-- Preserve original typed unit and quantity as an immutable invoice snapshot.
ALTER TABLE document_lines ADD COLUMN product_unit_id INTEGER REFERENCES product_units(id) ON DELETE RESTRICT;
ALTER TABLE document_lines ADD COLUMN entered_qty_milli INTEGER CHECK(entered_qty_milli > 0);
ALTER TABLE document_lines ADD COLUMN entered_unit_name TEXT;
ALTER TABLE document_lines ADD COLUMN unit_factor_milli INTEGER CHECK(unit_factor_milli >= 1000);
ALTER TABLE document_lines ADD COLUMN entered_unit_price_cents INTEGER CHECK(entered_unit_price_cents >= 0);
-- Legacy lines get a snapshot of their original base-unit quantity and price.
-- v1 deliberately forbids ANY line update. Temporarily remove ONLY that guard
-- INSIDE the migration transaction, backfill, and restore it immediately.
DROP TRIGGER prevent_line_update;
UPDATE document_lines SET product_unit_id=(SELECT id FROM product_units u WHERE u.product_id=document_lines.product_id AND is_base=1),
 entered_qty_milli=qty_milli, entered_unit_name=(SELECT unit FROM products WHERE id=document_lines.product_id),
 unit_factor_milli=1000,entered_unit_price_cents=unit_price_cents;
CREATE TRIGGER prevent_line_update
BEFORE UPDATE ON document_lines
BEGIN SELECT RAISE(ABORT, 'DOCUMENT_LINE_IMMUTABLE'); END;
CREATE TRIGGER validate_line_unit BEFORE INSERT ON document_lines
BEGIN
 SELECT CASE WHEN NEW.product_unit_id IS NULL OR NEW.entered_qty_milli IS NULL OR NEW.entered_unit_name IS NULL
    OR NEW.unit_factor_milli IS NULL OR NEW.entered_unit_price_cents IS NULL
    THEN RAISE(ABORT,'UNIT_SNAPSHOT_REQUIRED') END;
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM product_units u WHERE u.id=NEW.product_unit_id
   AND u.product_id=NEW.product_id AND u.name=NEW.entered_unit_name
   AND u.factor_milli=NEW.unit_factor_milli AND u.active=1)
   THEN RAISE(ABORT,'UNIT_PRODUCT_MISMATCH') END;
 SELECT CASE WHEN NEW.entered_qty_milli > 1000000000 OR NEW.unit_factor_milli > 1000000000
   OR NEW.qty_milli <> (NEW.entered_qty_milli * NEW.unit_factor_milli)/1000
   THEN RAISE(ABORT,'BAD_UNIT_CONVERSION') END;
 SELECT CASE WHEN (SELECT is_countable FROM products WHERE id=NEW.product_id)=1
   AND NEW.qty_milli%1000 <> 0 THEN RAISE(ABORT,'FRACTIONAL_PIECES_NOT_ALLOWED') END;
END;

CREATE TABLE users (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 username TEXT NOT NULL COLLATE NOCASE UNIQUE CHECK(length(trim(username)) BETWEEN 3 AND 40),
 display_name TEXT NOT NULL CHECK(length(trim(display_name)) > 0),
 role TEXT NOT NULL CHECK(role IN ('ADMIN','MANAGER','CASHIER','STOCK')),
 active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0,1)),
 salt BLOB NOT NULL CHECK(length(salt)=32),
 password_hash BLOB NOT NULL CHECK(length(password_hash)=32),
 iterations INTEGER NOT NULL CHECK(iterations >= 100000),
 failed_attempts INTEGER NOT NULL DEFAULT 0 CHECK(failed_attempts >= 0),
 locked_until_utc TEXT,
 created_utc TEXT NOT NULL
);
-- Upgrade the previous single-password lock to a real administrator account;
-- existing credentials remain valid. On databases with NO old password,
-- application must require admin creation before exposing the main window.
INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
 SELECT 'admin','مدير النظام','ADMIN',salt,password_hash,iterations,updated_utc FROM local_auth WHERE id=1;
CREATE TRIGGER prevent_last_active_admin BEFORE UPDATE OF role,active ON users
 WHEN OLD.role='ADMIN' AND OLD.active=1 AND (NEW.role<>'ADMIN' OR NEW.active=0)
 AND (SELECT COUNT(*) FROM users WHERE role='ADMIN' AND active=1)=1
 BEGIN SELECT RAISE(ABORT,'LAST_ACTIVE_ADMIN'); END;
CREATE TRIGGER prevent_user_delete BEFORE DELETE ON users
 BEGIN SELECT RAISE(ABORT,'USER_DELETION_DISABLED'); END;
ALTER TABLE documents ADD COLUMN created_by_user_id INTEGER REFERENCES users(id) ON DELETE RESTRICT;
ALTER TABLE audit_log ADD COLUMN actor_user_id INTEGER REFERENCES users(id) ON DELETE RESTRICT;
CREATE INDEX ix_documents_actor ON documents(created_by_user_id);
CREATE VIEW v_low_stock AS
 SELECT product_id,sku,name,unit,qty_milli,reorder_qty_milli,
 CASE WHEN qty_milli=0 THEN 'OUT' ELSE 'LOW' END AS alert_state
 FROM v_stock WHERE active=1 AND reorder_qty_milli>0 AND qty_milli<=reorder_qty_milli;

-- Invoice posting is rejected if even a single line lacks its stock movement,
-- or the header total does not equal the sum of its immutable line totals.
CREATE TRIGGER validate_posted_invoice BEFORE UPDATE OF state ON documents
 WHEN NEW.state='POSTED' AND OLD.state='DRAFT'
 BEGIN
  SELECT CASE WHEN (SELECT COUNT(*) FROM document_lines WHERE document_id=NEW.id)=0
   THEN RAISE(ABORT,'EMPTY_INVOICE') END;
  SELECT CASE WHEN COALESCE((SELECT SUM(line_total_cents) FROM document_lines WHERE document_id=NEW.id),0) <> NEW.total_cents
   THEN RAISE(ABORT,'INVOICE_TOTAL_MISMATCH') END;
  SELECT CASE WHEN (SELECT COUNT(*) FROM stock_movements WHERE document_id=NEW.id) <>
    (SELECT COUNT(*) FROM document_lines WHERE document_id=NEW.id)
   THEN RAISE(ABORT,'MISSING_STOCK_MOVEMENT') END;
 END;

-- A scanner must never resolve one code to two different products/units.
CREATE TRIGGER avoid_product_code_colliding_with_unit_barcode BEFORE INSERT ON products
 WHEN EXISTS(SELECT 1 FROM product_units u WHERE u.barcode=NEW.sku
     OR (NEW.barcode IS NOT NULL AND u.barcode=NEW.barcode))
 BEGIN SELECT RAISE(ABORT,'BARCODE_COLLIDES_WITH_SKU'); END;
CREATE TRIGGER avoid_unit_barcode_colliding_with_product BEFORE INSERT ON product_units
 WHEN NEW.barcode IS NOT NULL AND EXISTS(SELECT 1 FROM products p
    WHERE p.sku=NEW.barcode OR p.barcode=NEW.barcode)
 BEGIN SELECT RAISE(ABORT,'BARCODE_COLLIDES_WITH_SKU'); END;
CREATE TRIGGER avoid_product_code_update_collision BEFORE UPDATE OF sku,barcode ON products
 WHEN EXISTS(SELECT 1 FROM product_units u WHERE u.barcode=NEW.sku
     OR (NEW.barcode IS NOT NULL AND u.barcode=NEW.barcode))
 BEGIN SELECT RAISE(ABORT,'BARCODE_COLLIDES_WITH_SKU'); END;
CREATE TRIGGER avoid_unit_code_update_collision BEFORE UPDATE OF barcode ON product_units
 WHEN NEW.barcode IS NOT NULL AND EXISTS(SELECT 1 FROM products p
    WHERE p.sku=NEW.barcode OR p.barcode=NEW.barcode)
 BEGIN SELECT RAISE(ABORT,'BARCODE_COLLIDES_WITH_SKU'); END;
CREATE TRIGGER avoid_product_sku_vs_product_barcode_insert BEFORE INSERT ON products
 WHEN EXISTS(SELECT 1 FROM products p WHERE p.barcode=NEW.sku
     OR (NEW.barcode IS NOT NULL AND p.sku=NEW.barcode))
 BEGIN SELECT RAISE(ABORT,'PRODUCT_CODE_COLLISION'); END;
CREATE TRIGGER avoid_product_sku_vs_product_barcode_update BEFORE UPDATE OF sku,barcode ON products
 WHEN EXISTS(SELECT 1 FROM products p WHERE p.id<>OLD.id AND
    (p.barcode=NEW.sku OR (NEW.barcode IS NOT NULL AND p.sku=NEW.barcode)))
 BEGIN SELECT RAISE(ABORT,'PRODUCT_CODE_COLLISION'); END;
