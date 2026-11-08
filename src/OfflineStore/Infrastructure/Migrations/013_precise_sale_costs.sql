-- Additive precision extension. Historical lines are deliberately NOT backfilled:
-- their rounded unit_cost_snapshot_cents remains a legacy estimate, not exact COGS.
-- New sales preserve original moving-average millicents and round the line total once.
CREATE TABLE sale_cost_snapshots (
 document_line_id INTEGER PRIMARY KEY REFERENCES document_lines(id) ON DELETE RESTRICT,
 average_cost_millicents INTEGER NOT NULL CHECK(typeof(average_cost_millicents)='integer' AND average_cost_millicents>=0),
 total_cost_cents INTEGER NOT NULL CHECK(typeof(total_cost_cents)='integer' AND total_cost_cents>=0)
);
CREATE TRIGGER precise_cost_sale_only BEFORE INSERT ON sale_cost_snapshots
WHEN NOT EXISTS(SELECT 1 FROM document_lines l JOIN documents d ON d.id=l.document_id
 WHERE l.id=NEW.document_line_id AND d.doc_type='SALE' AND d.state='DRAFT')
BEGIN SELECT RAISE(ABORT,'PRECISE_COST_REQUIRES_DRAFT_SALE'); END;
CREATE TRIGGER precise_cost_no_update BEFORE UPDATE ON sale_cost_snapshots
BEGIN SELECT RAISE(ABORT,'SALE_COST_IMMUTABLE'); END;
CREATE TRIGGER precise_cost_no_delete BEFORE DELETE ON sale_cost_snapshots
BEGIN SELECT RAISE(ABORT,'SALE_COST_IMMUTABLE'); END;
