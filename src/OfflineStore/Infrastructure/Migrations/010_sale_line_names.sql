-- v10: historical product names for sale receipts.
-- Legacy invoices were stored without a product-name snapshot. Their
-- existing catalog names are copied during migration (best available,
-- NOT a reconstruction of the original name if it was edited).
CREATE TABLE sale_line_names (
    document_line_id INTEGER PRIMARY KEY REFERENCES document_lines(id) ON DELETE RESTRICT,
    product_id INTEGER NOT NULL REFERENCES products(id) ON DELETE RESTRICT,
    name_at_sale TEXT NOT NULL CHECK(length(trim(name_at_sale)) BETWEEN 1 AND 180),
    snapshot_source TEXT NOT NULL CHECK(snapshot_source IN ('MIGRATED','POSTED'))
);
INSERT INTO sale_line_names(document_line_id,product_id,name_at_sale,snapshot_source)
SELECT l.id,l.product_id,p.name,'MIGRATED'
FROM document_lines l
JOIN documents d ON d.id=l.document_id
JOIN products p ON p.id=l.product_id
WHERE d.doc_type='SALE' AND d.state='POSTED';
-- The receipt QR link uses an installation-specific random HMAC key.
-- Stored in SQLite so verified backups preserve printed receipt tokens.
CREATE TABLE receipt_secret (
    id INTEGER PRIMARY KEY CHECK(id=1),
    secret BLOB NOT NULL CHECK(length(secret)=32)
);
CREATE TRIGGER sale_name_no_update BEFORE UPDATE ON sale_line_names
BEGIN SELECT RAISE(ABORT,'SALE_NAME_IMMUTABLE'); END;
CREATE TRIGGER sale_name_no_delete BEFORE DELETE ON sale_line_names
BEGIN SELECT RAISE(ABORT,'SALE_NAME_IMMUTABLE'); END;
CREATE TRIGGER sale_name_only_sale BEFORE INSERT ON sale_line_names
WHEN NOT EXISTS(
 SELECT 1 FROM document_lines l JOIN documents d ON d.id=l.document_id
 WHERE l.id=NEW.document_line_id AND l.product_id=NEW.product_id AND d.doc_type='SALE'
)
BEGIN SELECT RAISE(ABORT,'SALE_NAME_REQUIRES_SALE_LINE'); END;
