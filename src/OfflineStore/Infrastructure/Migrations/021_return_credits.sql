-- Applied by StoreDb with a pre-upgrade snapshot and a single transaction; no historical value changes.
-- FK toggling is outside that transaction in production. These pragmas also support isolated script fixtures.
PRAGMA foreign_keys=OFF;
PRAGMA legacy_alter_table=ON;
CREATE TEMP TABLE saved_return_sequence(seq INTEGER);
INSERT INTO saved_return_sequence SELECT seq FROM sqlite_sequence WHERE name='return_documents';
CREATE TABLE return_documents_new (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 original_document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
 reference TEXT NOT NULL UNIQUE CHECK(length(trim(reference)) BETWEEN 1 AND 113),
 return_date TEXT NOT NULL CHECK(length(return_date)=10),
 state TEXT NOT NULL DEFAULT 'DRAFT' CHECK(state IN ('DRAFT','POSTED')),
 cash_cents INTEGER NOT NULL CHECK(typeof(cash_cents)='integer' AND cash_cents>=0),
 debt_cents INTEGER NOT NULL CHECK(typeof(debt_cents)='integer' AND debt_cents>=0),
 total_cents INTEGER NOT NULL CHECK(typeof(total_cents)='integer' AND total_cents>=0 AND total_cents=cash_cents+debt_cents+credit_cents AND typeof(cash_cents+debt_cents+credit_cents)='integer'),
 journal_entry_id INTEGER UNIQUE REFERENCES journal_entries(id) ON DELETE RESTRICT,
 created_by_user_id INTEGER NOT NULL REFERENCES users(id),created_utc TEXT NOT NULL,
 memo TEXT NOT NULL CHECK(length(trim(memo)) BETWEEN 1 AND 500),
 credit_cents INTEGER NOT NULL DEFAULT 0 CHECK(typeof(credit_cents)='integer' AND credit_cents>=0),
 credit_party_id INTEGER REFERENCES parties(id) ON DELETE RESTRICT,
 CHECK((credit_cents=0 AND credit_party_id IS NULL) OR (credit_cents>0 AND credit_party_id IS NOT NULL))
);
INSERT INTO return_documents_new(id,original_document_id,reference,return_date,state,cash_cents,debt_cents,total_cents,journal_entry_id,created_by_user_id,created_utc,memo) SELECT id,original_document_id,reference,return_date,state,cash_cents,debt_cents,total_cents,journal_entry_id,created_by_user_id,created_utc,memo FROM return_documents;
CREATE TEMP TABLE return_copy_proof(ok INTEGER CHECK(ok=1));
INSERT INTO return_copy_proof SELECT CASE WHEN
 (SELECT COUNT(*) FROM return_documents)=(SELECT COUNT(*) FROM return_documents_new)
 AND NOT EXISTS(SELECT id,original_document_id,reference,return_date,state,cash_cents,debt_cents,total_cents,journal_entry_id,created_by_user_id,created_utc,memo FROM return_documents EXCEPT SELECT id,original_document_id,reference,return_date,state,cash_cents,debt_cents,total_cents,journal_entry_id,created_by_user_id,created_utc,memo FROM return_documents_new)
 AND NOT EXISTS(SELECT id,original_document_id,reference,return_date,state,cash_cents,debt_cents,total_cents,journal_entry_id,created_by_user_id,created_utc,memo FROM return_documents_new EXCEPT SELECT id,original_document_id,reference,return_date,state,cash_cents,debt_cents,total_cents,journal_entry_id,created_by_user_id,created_utc,memo FROM return_documents)
 THEN 1 ELSE 0 END;
DROP TABLE return_copy_proof;
DROP TABLE return_documents;
ALTER TABLE return_documents_new RENAME TO return_documents;
UPDATE sqlite_sequence SET seq=MAX(seq,COALESCE((SELECT MAX(seq) FROM saved_return_sequence),0)) WHERE name='return_documents';
INSERT INTO sqlite_sequence(name,seq) SELECT 'return_documents',MAX(seq) FROM saved_return_sequence WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='return_documents') HAVING MAX(seq) IS NOT NULL;
DROP TABLE saved_return_sequence;
CREATE INDEX return_documents_original ON return_documents(original_document_id,state);
CREATE TRIGGER return_starts_draft BEFORE INSERT ON return_documents WHEN NEW.state<>'DRAFT'
BEGIN SELECT RAISE(ABORT,'RETURN_STARTS_DRAFT'); END;
CREATE TRIGGER return_header_no_update BEFORE UPDATE ON return_documents WHEN OLD.state='POSTED'
BEGIN SELECT RAISE(ABORT,'POSTED_RETURN_IMMUTABLE'); END;
CREATE TRIGGER return_header_no_delete BEFORE DELETE ON return_documents WHEN OLD.state='POSTED'
BEGIN SELECT RAISE(ABORT,'POSTED_RETURN_IMMUTABLE'); END;
CREATE TRIGGER return_identity BEFORE UPDATE OF original_document_id,reference ON return_documents
WHEN EXISTS(SELECT 1 FROM return_lines WHERE return_id=OLD.id)
BEGIN SELECT RAISE(ABORT,'RETURN_IDENTITY_IMMUTABLE'); END;
INSERT INTO journal_accounts VALUES('CUSTOMER_CREDITS','أرصدة دائنة للعملاء من المرتجعات','LIABILITY'),('SUPPLIER_CREDITS','أرصدة لنا لدى الموردين من المرتجعات','ASSET');
CREATE TRIGGER return_credit_party_insert BEFORE INSERT ON return_documents WHEN NEW.credit_cents>0 BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM documents d JOIN parties p ON p.id=NEW.credit_party_id
 WHERE d.id=NEW.original_document_id AND (d.party_id IS NULL OR d.party_id=p.id)
 AND (p.party_type='BOTH' OR p.party_type=CASE WHEN d.doc_type='SALE' THEN 'CUSTOMER' ELSE 'SUPPLIER' END))
 THEN RAISE(ABORT,'INVALID_RETURN_CREDIT_PARTY') END;
END;
CREATE TRIGGER credit_party_insert BEFORE INSERT ON journal_lines BEGIN
 SELECT CASE WHEN NEW.account_code='CUSTOMER_CREDITS' AND NOT EXISTS(SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('CUSTOMER','BOTH')) THEN RAISE(ABORT,'CREDIT_REQUIRES_CUSTOMER') END;
 SELECT CASE WHEN NEW.account_code='SUPPLIER_CREDITS' AND NOT EXISTS(SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('SUPPLIER','BOTH')) THEN RAISE(ABORT,'CREDIT_REQUIRES_SUPPLIER') END;
END;
CREATE TRIGGER credit_party_update BEFORE UPDATE ON journal_lines BEGIN
 SELECT CASE WHEN NEW.account_code='CUSTOMER_CREDITS' AND NOT EXISTS(SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('CUSTOMER','BOTH')) THEN RAISE(ABORT,'CREDIT_REQUIRES_CUSTOMER') END;
 SELECT CASE WHEN NEW.account_code='SUPPLIER_CREDITS' AND NOT EXISTS(SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('SUPPLIER','BOTH')) THEN RAISE(ABORT,'CREDIT_REQUIRES_SUPPLIER') END;
END;
CREATE TABLE return_credit_applications (
 entry_id INTEGER PRIMARY KEY REFERENCES journal_entries(id) ON DELETE RESTRICT,
 party_id INTEGER NOT NULL REFERENCES parties(id) ON DELETE RESTRICT,
 is_customer INTEGER NOT NULL CHECK(is_customer IN (0,1)),
 amount_cents INTEGER NOT NULL CHECK(typeof(amount_cents)='integer' AND amount_cents>0),
 memo TEXT NOT NULL CHECK(length(trim(memo)) BETWEEN 1 AND 500)
);
CREATE VIEW v_invalid_return_credit_applications AS
 SELECT f.entry_id FROM return_credit_applications f JOIN journal_entries e ON e.id=f.entry_id WHERE
 e.source_kind<>'ADJUSTMENT' OR e.state<>'POSTED'
 OR (SELECT COUNT(*) FROM journal_lines WHERE entry_id=e.id)<>2
 OR EXISTS(SELECT 1 FROM journal_lines WHERE entry_id=e.id AND (party_id IS NULL OR party_id<>f.party_id))
 OR f.amount_cents<>COALESCE((SELECT SUM(debit_cents-credit_cents) FROM journal_lines WHERE entry_id=e.id
    AND account_code=CASE WHEN f.is_customer=1 THEN 'CUSTOMER_CREDITS' ELSE 'PAYABLES' END),0)
 OR f.amount_cents<>COALESCE((SELECT SUM(credit_cents-debit_cents) FROM journal_lines WHERE entry_id=e.id
    AND account_code=CASE WHEN f.is_customer=1 THEN 'RECEIVABLES' ELSE 'SUPPLIER_CREDITS' END),0);
CREATE TRIGGER return_credit_application_validate AFTER INSERT ON return_credit_applications
WHEN EXISTS(SELECT 1 FROM v_invalid_return_credit_applications WHERE entry_id=NEW.entry_id)
BEGIN SELECT RAISE(ABORT,'RETURN_CREDIT_APPLICATION_MISMATCH'); END;
CREATE TRIGGER return_credit_application_no_update BEFORE UPDATE ON return_credit_applications
BEGIN SELECT RAISE(ABORT,'RETURN_CREDIT_APPLICATION_IMMUTABLE'); END;
CREATE TRIGGER return_credit_application_no_delete BEFORE DELETE ON return_credit_applications
BEGIN SELECT RAISE(ABORT,'RETURN_CREDIT_APPLICATION_IMMUTABLE'); END;
DROP VIEW v_party_balances;
CREATE VIEW v_party_balances AS
 SELECT p.id AS party_id,p.name,p.party_type,
 COALESCE(SUM(CASE WHEN l.account_code='RECEIVABLES' THEN l.debit_cents-l.credit_cents ELSE 0 END),0) AS receivable_cents,
 COALESCE(SUM(CASE WHEN l.account_code='PAYABLES' THEN l.credit_cents-l.debit_cents ELSE 0 END),0) AS payable_cents,
 COALESCE(SUM(CASE WHEN l.account_code='CUSTOMER_ADVANCES' THEN l.credit_cents-l.debit_cents ELSE 0 END),0) AS customer_advance_cents,
 COALESCE(SUM(CASE WHEN l.account_code='SUPPLIER_ADVANCES' THEN l.debit_cents-l.credit_cents ELSE 0 END),0) AS supplier_advance_cents,
 COALESCE(SUM(CASE WHEN l.account_code='CUSTOMER_CREDITS' THEN l.credit_cents-l.debit_cents ELSE 0 END),0) AS customer_credit_cents,
 COALESCE(SUM(CASE WHEN l.account_code='SUPPLIER_CREDITS' THEN l.debit_cents-l.credit_cents ELSE 0 END),0) AS supplier_credit_cents
 FROM parties p LEFT JOIN v_journal_posted_lines l ON l.party_id=p.id GROUP BY p.id;
DROP VIEW v_invalid_return_documents;
CREATE VIEW v_invalid_return_documents AS
 SELECT r.id FROM return_documents r JOIN documents d ON d.id=r.original_document_id
 LEFT JOIN journal_entries e ON e.id=r.journal_entry_id WHERE
 (r.state='DRAFT' AND e.state='POSTED') OR (r.state='POSTED' AND (
 NOT EXISTS(SELECT 1 FROM v_financial_invoices f WHERE f.document_id=d.id)
 OR NOT EXISTS(SELECT 1 FROM return_lines WHERE return_id=r.id)
 OR r.total_cents<>COALESCE((SELECT SUM(amount_cents) FROM return_lines WHERE return_id=r.id),0)
 OR (e.id IS NULL AND (r.total_cents<>0 OR (SELECT SUM(cost_cents) FROM return_lines WHERE return_id=r.id)<>0))
 OR (e.id IS NOT NULL AND (e.state<>'POSTED' OR e.source_kind<>'RETURN' OR e.document_id<>d.id OR e.source_key<>'return:'||r.reference))
 OR EXISTS(SELECT 1 FROM return_lines x JOIN document_lines l ON l.id=x.original_line_id WHERE x.return_id=r.id AND
    (l.document_id<>d.id OR (SELECT SUM(z.qty_milli) FROM return_lines z JOIN return_documents q ON q.id=z.return_id WHERE z.original_line_id=l.id AND q.state='POSTED')>l.qty_milli))
 OR EXISTS(SELECT 1 FROM journal_lines l WHERE l.entry_id=e.id AND ((d.doc_type='SALE' AND l.account_code NOT IN ('CASH','RECEIVABLES','SALES','COGS','INVENTORY','CUSTOMER_CREDITS')) OR (d.doc_type='PURCHASE' AND l.account_code NOT IN ('CASH','PAYABLES','INVENTORY','PURCHASE_RETURN_VARIANCE','SUPPLIER_CREDITS'))
    OR (l.account_code IN ('RECEIVABLES','PAYABLES') AND (l.party_id IS NULL OR l.party_id<>d.party_id))))
 OR COALESCE((SELECT SUM(cash_cents) FROM return_documents q WHERE q.original_document_id=d.id AND q.state='POSTED'),0)>
 MAX(0,COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN l.debit_cents-l.credit_cents ELSE l.credit_cents-l.debit_cents END)
 FROM journal_entries j JOIN journal_lines l ON l.entry_id=j.id WHERE j.document_id=d.id AND j.source_kind=d.doc_type AND j.state='POSTED' AND l.account_code='CASH'),0)
 +COALESCE((SELECT SUM(a.amount_cents) FROM invoice_allocations a JOIN journal_entries j ON j.id=a.entry_id WHERE a.document_id=d.id AND j.source_kind<>'RETURN' AND NOT EXISTS(SELECT 1 FROM return_credit_applications c WHERE c.entry_id=j.id)),0))
 OR r.credit_cents<>COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END) FROM journal_lines WHERE entry_id=e.id AND account_code=CASE WHEN d.doc_type='SALE' THEN 'CUSTOMER_CREDITS' ELSE 'SUPPLIER_CREDITS' END),0)
 OR (r.credit_cents>0 AND NOT EXISTS(SELECT 1 FROM parties p WHERE p.id=r.credit_party_id AND (d.party_id IS NULL OR d.party_id=p.id) AND (p.party_type='BOTH' OR p.party_type=CASE WHEN d.doc_type='SALE' THEN 'CUSTOMER' ELSE 'SUPPLIER' END)))
 OR EXISTS(SELECT 1 FROM journal_lines WHERE entry_id=e.id AND account_code IN ('CUSTOMER_CREDITS','SUPPLIER_CREDITS') AND (party_id IS NULL OR party_id<>r.credit_party_id))
 OR (SELECT SUM(debit_cents) FROM journal_lines WHERE entry_id=e.id)<>
   CASE WHEN d.doc_type='SALE' THEN r.total_cents+(SELECT SUM(cost_cents) FROM return_lines WHERE return_id=r.id)
    ELSE MAX(r.total_cents,(SELECT SUM(cost_cents) FROM return_lines WHERE return_id=r.id)) END
 OR r.cash_cents<>COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END) FROM journal_lines WHERE entry_id=e.id AND account_code='CASH'),0)
 OR r.debt_cents<>COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END) FROM journal_lines WHERE entry_id=e.id AND account_code=CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END),0)
 OR r.debt_cents<>COALESCE((SELECT SUM(amount_cents) FROM invoice_allocations WHERE entry_id=e.id AND document_id=d.id),0)
 OR (d.doc_type='SALE' AND (r.total_cents<>COALESCE((SELECT SUM(debit_cents-credit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='SALES'),0)
    OR (SELECT SUM(cost_cents) FROM return_lines WHERE return_id=r.id)<>COALESCE((SELECT SUM(debit_cents-credit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='INVENTORY'),0)
    OR (SELECT SUM(cost_cents) FROM return_lines WHERE return_id=r.id)<>COALESCE((SELECT SUM(credit_cents-debit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='COGS'),0)))
 OR (d.doc_type='PURCHASE' AND ((SELECT SUM(cost_cents) FROM return_lines WHERE return_id=r.id)<>COALESCE((SELECT SUM(credit_cents-debit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='INVENTORY'),0)
    OR r.total_cents-(SELECT SUM(cost_cents) FROM return_lines WHERE return_id=r.id)<>COALESCE((SELECT SUM(credit_cents-debit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='PURCHASE_RETURN_VARIANCE'),0)))
 ));
CREATE TRIGGER return_post_validate AFTER UPDATE OF state ON return_documents WHEN NEW.state='POSTED' BEGIN
 SELECT CASE WHEN EXISTS(SELECT 1 FROM v_invalid_return_documents WHERE id=NEW.id) THEN RAISE(ABORT,'INVALID_RETURN_POSTING') END;
 SELECT CASE WHEN EXISTS(SELECT 1 FROM v_stock WHERE qty_milli<0) THEN RAISE(ABORT,'INSUFFICIENT_RETURN_STOCK') END;
END;

DROP VIEW v_invalid_invoice_allocations;
CREATE VIEW v_invalid_invoice_allocations AS
 SELECT x.id FROM invoice_allocations x
 LEFT JOIN v_financial_invoices d ON d.document_id=x.document_id
 LEFT JOIN journal_entries e ON e.id=x.entry_id
 WHERE d.document_id IS NULL OR d.party_id IS NULL OR e.state<>'POSTED'
 OR NOT ((d.doc_type='SALE' AND (e.source_kind='CUSTOMER_PAYMENT' OR (e.source_kind='RETURN' AND EXISTS(SELECT 1 FROM return_documents r WHERE r.journal_entry_id=e.id AND r.original_document_id=d.document_id)) OR EXISTS(SELECT 1 FROM advance_applications a WHERE a.entry_id=e.id AND a.is_customer=1) OR EXISTS(SELECT 1 FROM return_credit_applications a WHERE a.entry_id=e.id AND a.is_customer=1)))
      OR (d.doc_type='PURCHASE' AND (e.source_kind='SUPPLIER_PAYMENT' OR (e.source_kind='RETURN' AND EXISTS(SELECT 1 FROM return_documents r WHERE r.journal_entry_id=e.id AND r.original_document_id=d.document_id)) OR EXISTS(SELECT 1 FROM advance_applications a WHERE a.entry_id=e.id AND a.is_customer=0) OR EXISTS(SELECT 1 FROM return_credit_applications a WHERE a.entry_id=e.id AND a.is_customer=0))))
 OR EXISTS(SELECT 1 FROM journal_lines l WHERE l.entry_id=e.id AND l.account_code IN ('RECEIVABLES','PAYABLES')
   AND (l.party_id IS NULL OR l.party_id<>d.party_id))
 OR (SELECT SUM(amount_cents) FROM invoice_allocations WHERE entry_id=e.id)>
    COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END)
     FROM journal_lines WHERE entry_id=e.id AND account_code=CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END),0)
 OR d.allocated_cents>d.initial_debt_cents;

PRAGMA legacy_alter_table=OFF;
PRAGMA foreign_keys=ON;
