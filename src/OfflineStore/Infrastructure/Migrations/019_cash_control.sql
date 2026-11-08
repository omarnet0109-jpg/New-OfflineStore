INSERT INTO journal_accounts VALUES('CAPITAL','تمويل المالك للخزنة','EQUITY');
CREATE TABLE cash_fundings (
 entry_id INTEGER PRIMARY KEY REFERENCES journal_entries(id) ON DELETE RESTRICT,
 amount_cents INTEGER NOT NULL CHECK(typeof(amount_cents)='integer' AND amount_cents>0),
 memo TEXT NOT NULL CHECK(length(trim(memo)) BETWEEN 1 AND 500)
);
CREATE VIEW v_invalid_cash_fundings AS
 SELECT f.entry_id FROM cash_fundings f JOIN journal_entries e ON e.id=f.entry_id WHERE
 e.source_kind<>'ADJUSTMENT' OR e.state<>'POSTED'
 OR (SELECT COUNT(*) FROM journal_lines WHERE entry_id=e.id)<>2
 OR EXISTS(SELECT 1 FROM journal_lines WHERE entry_id=e.id AND party_id IS NOT NULL)
 OR f.amount_cents<>COALESCE((SELECT SUM(debit_cents-credit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='CASH'),0)
 OR f.amount_cents<>COALESCE((SELECT SUM(credit_cents-debit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='CAPITAL'),0);
CREATE TRIGGER cash_funding_validate AFTER INSERT ON cash_fundings
WHEN EXISTS(SELECT 1 FROM v_invalid_cash_fundings WHERE entry_id=NEW.entry_id)
BEGIN SELECT RAISE(ABORT,'CASH_FUNDING_MISMATCH'); END;
CREATE TRIGGER cash_funding_no_update BEFORE UPDATE ON cash_fundings
BEGIN SELECT RAISE(ABORT,'CASH_FUNDING_IMMUTABLE'); END;
CREATE TRIGGER cash_funding_no_delete BEFORE DELETE ON cash_fundings
BEGIN SELECT RAISE(ABORT,'CASH_FUNDING_IMMUTABLE'); END;
