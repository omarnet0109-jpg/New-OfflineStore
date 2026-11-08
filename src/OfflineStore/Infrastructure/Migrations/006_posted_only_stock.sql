-- Schema v6: only POSTED document movements are available stock.
-- Draft movements are staged inside a single transaction by StoreService.Post.
-- Do not rewrite historical invoices or quantities.
DROP VIEW IF EXISTS v_low_stock;
DROP VIEW IF EXISTS v_stock;

CREATE VIEW v_stock AS
SELECT p.id AS product_id, p.sku, p.name, p.unit, p.sale_price_cents,
       p.last_cost_cents, p.reorder_qty_milli, p.active,
       COALESCE(SUM(m.delta_qty_milli),0) AS qty_milli
FROM products p
LEFT JOIN stock_movements m ON p.id=m.product_id
  AND EXISTS(SELECT 1 FROM documents d
             WHERE d.id=m.document_id AND d.state='POSTED')
GROUP BY p.id;

CREATE VIEW v_low_stock AS
SELECT product_id,sku,name,unit,qty_milli,reorder_qty_milli,
       CASE WHEN qty_milli=0 THEN 'OUT' ELSE 'LOW' END AS alert_state
FROM v_stock
WHERE active=1 AND reorder_qty_milli>0 AND qty_milli<=reorder_qty_milli;

-- Rebuild the movement guard so a pending purchase in another DRAFT
-- cannot fund a sale, while existing lines of THIS sale are checked together.
DROP TRIGGER stock_movement_validate;
CREATE TRIGGER stock_movement_validate BEFORE INSERT ON stock_movements
BEGIN
  SELECT CASE WHEN NOT EXISTS (
    SELECT 1 FROM document_lines l JOIN documents d ON d.id=l.document_id
    WHERE l.id=NEW.document_line_id AND l.document_id=NEW.document_id
      AND l.product_id=NEW.product_id AND d.state='DRAFT'
      AND ((d.doc_type='PURCHASE' AND NEW.delta_qty_milli=l.qty_milli)
        OR (d.doc_type='SALE' AND NEW.delta_qty_milli=-l.qty_milli))
  ) THEN RAISE(ABORT,'INVALID_STOCK_MOVEMENT') END;

  SELECT CASE WHEN NEW.delta_qty_milli < 0 AND
    COALESCE((SELECT SUM(m.delta_qty_milli)
      FROM stock_movements m JOIN documents d ON d.id=m.document_id
      WHERE d.state='POSTED' AND m.product_id=NEW.product_id),0)
    + COALESCE((SELECT SUM(delta_qty_milli) FROM stock_movements
      WHERE document_id=NEW.document_id AND product_id=NEW.product_id),0)
    + NEW.delta_qty_milli < 0
    THEN RAISE(ABORT,'INSUFFICIENT_STOCK') END;
END;

-- A previously-staged draft may have become stale after another sale
-- committed. Revalidate against committed quantities when approving.
CREATE TRIGGER v6_recheck_sale_stock BEFORE UPDATE OF state ON documents
WHEN NEW.state='POSTED' AND OLD.state='DRAFT' AND NEW.doc_type='SALE'
BEGIN
  SELECT CASE WHEN EXISTS (
    SELECT 1
    FROM (SELECT product_id,SUM(delta_qty_milli) AS outgoing
          FROM stock_movements
          WHERE document_id=OLD.id GROUP BY product_id) pending
    WHERE pending.outgoing < 0 AND
      COALESCE((SELECT SUM(m.delta_qty_milli)
        FROM stock_movements m JOIN documents d ON d.id=m.document_id
        WHERE m.product_id=pending.product_id AND d.state='POSTED'),0)
      + pending.outgoing < 0
  ) THEN RAISE(ABORT,'INSUFFICIENT_STOCK_AT_POSTING') END;
END;
