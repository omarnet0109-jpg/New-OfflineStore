-- Existing activations retain their current reporting; no historical amounts are recomputed.
-- New activations capture the exact posted journal boundary in their transaction.
CREATE TABLE finance_activation_boundary (
 singleton INTEGER PRIMARY KEY REFERENCES finance_activation(singleton) CHECK(singleton=1),
 journal_through_id INTEGER NOT NULL CHECK(typeof(journal_through_id)='integer' AND journal_through_id>=0)
);
INSERT INTO finance_activation_boundary SELECT singleton,0 FROM finance_activation;
CREATE TRIGGER finance_boundary_valid BEFORE INSERT ON finance_activation_boundary
WHEN NEW.journal_through_id<>(SELECT COALESCE(MAX(id),0) FROM journal_entries WHERE state='POSTED')
BEGIN SELECT RAISE(ABORT,'STALE_FINANCE_BOUNDARY'); END;
CREATE TRIGGER finance_boundary_no_update BEFORE UPDATE ON finance_activation_boundary
BEGIN SELECT RAISE(ABORT,'FINANCE_BOUNDARY_IMMUTABLE'); END;
CREATE TRIGGER finance_boundary_no_delete BEFORE DELETE ON finance_activation_boundary
BEGIN SELECT RAISE(ABORT,'FINANCE_BOUNDARY_IMMUTABLE'); END;
CREATE VIEW v_financial_profit_lines AS
 SELECT l.* FROM v_journal_posted_lines l JOIN finance_activation_boundary b ON b.singleton=1
 WHERE l.entry_id>b.journal_through_id;
