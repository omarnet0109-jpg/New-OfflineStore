CREATE TABLE advance_applications (
 entry_id INTEGER PRIMARY KEY REFERENCES journal_entries(id) ON DELETE RESTRICT,
 party_id INTEGER NOT NULL REFERENCES parties(id) ON DELETE RESTRICT,
 is_customer INTEGER NOT NULL CHECK(is_customer IN (0,1)),
 amount_cents INTEGER NOT NULL CHECK(typeof(amount_cents)='integer' AND amount_cents>0),
 memo TEXT NOT NULL CHECK(length(trim(memo)) BETWEEN 1 AND 500)
);
CREATE VIEW v_invalid_advance_applications AS
 SELECT f.entry_id FROM advance_applications f JOIN journal_entries e ON e.id=f.entry_id WHERE
 e.source_kind<>'ADJUSTMENT' OR e.state<>'POSTED'
 OR (SELECT COUNT(*) FROM journal_lines WHERE entry_id=e.id)<>2
 OR EXISTS(SELECT 1 FROM journal_lines WHERE entry_id=e.id AND (party_id IS NULL OR party_id<>f.party_id))
 OR f.amount_cents<>COALESCE((SELECT SUM(debit_cents-credit_cents) FROM journal_lines WHERE entry_id=e.id
    AND account_code=CASE WHEN f.is_customer=1 THEN 'CUSTOMER_ADVANCES' ELSE 'PAYABLES' END),0)
 OR f.amount_cents<>COALESCE((SELECT SUM(credit_cents-debit_cents) FROM journal_lines WHERE entry_id=e.id
    AND account_code=CASE WHEN f.is_customer=1 THEN 'RECEIVABLES' ELSE 'SUPPLIER_ADVANCES' END),0);
CREATE TRIGGER advance_application_validate AFTER INSERT ON advance_applications
WHEN EXISTS(SELECT 1 FROM v_invalid_advance_applications WHERE entry_id=NEW.entry_id)
BEGIN SELECT RAISE(ABORT,'ADVANCE_APPLICATION_MISMATCH'); END;
CREATE TRIGGER advance_application_no_update BEFORE UPDATE ON advance_applications
BEGIN SELECT RAISE(ABORT,'ADVANCE_APPLICATION_IMMUTABLE'); END;
CREATE TRIGGER advance_application_no_delete BEFORE DELETE ON advance_applications
BEGIN SELECT RAISE(ABORT,'ADVANCE_APPLICATION_IMMUTABLE'); END;
