-- Additive: immutable historic cost snapshots are never recalculated.
INSERT INTO journal_accounts VALUES('INVENTORY_VARIANCE','فروق تقييم مخزون سابقة معتمدة','EXPENSE');
CREATE TABLE inventory_carrying_values (
 product_id INTEGER PRIMARY KEY REFERENCES products(id),
 qty_milli INTEGER NOT NULL CHECK(typeof(qty_milli)='integer' AND qty_milli>=0),
 value_cents INTEGER NOT NULL CHECK(typeof(value_cents)='integer' AND value_cents>=0 AND (qty_milli>0 OR value_cents=0))
);
CREATE TABLE inventory_valuation_state (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 review_required INTEGER NOT NULL CHECK(review_required IN (0,1)),
 last_legacy_document_id INTEGER NOT NULL CHECK(last_legacy_document_id>=0),
 last_legacy_return_id INTEGER NOT NULL CHECK(last_legacy_return_id>=0)
);
-- Seeded with checked decimal arithmetic by the migration's owning transaction.
CREATE TABLE inventory_cost_evidence (
 sale_line_id INTEGER UNIQUE REFERENCES document_lines(id),
 return_line_id INTEGER UNIQUE REFERENCES return_lines(id),
 qty_before INTEGER NOT NULL CHECK(typeof(qty_before)='integer' AND qty_before>0),
 value_before INTEGER NOT NULL CHECK(typeof(value_before)='integer' AND value_before>=0),
 group_qty INTEGER NOT NULL CHECK(typeof(group_qty)='integer' AND group_qty>0 AND group_qty<=qty_before),
 group_offset INTEGER NOT NULL CHECK(typeof(group_offset)='integer' AND group_offset>=0 AND group_offset<group_qty),
 CHECK((sale_line_id IS NOT NULL AND return_line_id IS NULL) OR (sale_line_id IS NULL AND return_line_id IS NOT NULL))
);
CREATE TRIGGER carrying_evidence_insert BEFORE INSERT ON inventory_cost_evidence BEGIN
 SELECT CASE WHEN NEW.sale_line_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM document_lines l JOIN documents d ON d.id=l.document_id WHERE l.id=NEW.sale_line_id AND d.doc_type='SALE' AND d.state='DRAFT') THEN RAISE(ABORT,'CARRYING_DRAFT_SALE_REQUIRED') END;
 SELECT CASE WHEN NEW.return_line_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM return_lines x JOIN return_documents r ON r.id=x.return_id JOIN documents d ON d.id=r.original_document_id WHERE x.id=NEW.return_line_id AND d.doc_type='PURCHASE' AND r.state='DRAFT') THEN RAISE(ABORT,'CARRYING_DRAFT_PURCHASE_RETURN_REQUIRED') END;
END;
CREATE TRIGGER carrying_evidence_no_update BEFORE UPDATE ON inventory_cost_evidence BEGIN SELECT RAISE(ABORT,'CARRYING_EVIDENCE_IMMUTABLE'); END;
CREATE TRIGGER carrying_evidence_no_delete BEFORE DELETE ON inventory_cost_evidence BEGIN SELECT RAISE(ABORT,'CARRYING_EVIDENCE_IMMUTABLE'); END;
CREATE TRIGGER carrying_cutoff_no_update BEFORE UPDATE OF last_legacy_document_id,last_legacy_return_id ON inventory_valuation_state BEGIN SELECT RAISE(ABORT,'CARRYING_CUTOFF_IMMUTABLE'); END;
CREATE TRIGGER carrying_state_no_delete BEFORE DELETE ON inventory_valuation_state BEGIN SELECT RAISE(ABORT,'CARRYING_STATE_REQUIRED'); END;
