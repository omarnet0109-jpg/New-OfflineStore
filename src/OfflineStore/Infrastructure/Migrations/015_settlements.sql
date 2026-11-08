-- Additive only: no inferred historic cash, debt, or invoice postings.
INSERT INTO journal_accounts VALUES
 ('CUSTOMER_ADVANCES','دفعات العملاء المقدمة','LIABILITY'),
 ('SUPPLIER_ADVANCES','دفعات الموردين المقدمة','ASSET');
CREATE VIEW v_journal_posted_lines AS
 SELECT l.*,e.source_kind,e.source_key,e.entry_date,e.document_id,a.name AS account_name,a.kind
 FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id
 JOIN journal_accounts a ON a.code=l.account_code WHERE e.state='POSTED';
CREATE VIEW v_trial_balance AS
 SELECT a.code,a.name,a.kind,COALESCE(SUM(l.debit_cents),0) AS debit_cents,
 COALESCE(SUM(l.credit_cents),0) AS credit_cents
 FROM journal_accounts a LEFT JOIN v_journal_posted_lines l ON l.account_code=a.code GROUP BY a.code;
CREATE VIEW v_party_balances AS
 SELECT p.id AS party_id,p.name,p.party_type,
 COALESCE(SUM(CASE WHEN l.account_code='RECEIVABLES' THEN l.debit_cents-l.credit_cents ELSE 0 END),0) AS receivable_cents,
 COALESCE(SUM(CASE WHEN l.account_code='PAYABLES' THEN l.credit_cents-l.debit_cents ELSE 0 END),0) AS payable_cents,
 COALESCE(SUM(CASE WHEN l.account_code='CUSTOMER_ADVANCES' THEN l.credit_cents-l.debit_cents ELSE 0 END),0) AS customer_advance_cents,
 COALESCE(SUM(CASE WHEN l.account_code='SUPPLIER_ADVANCES' THEN l.debit_cents-l.credit_cents ELSE 0 END),0) AS supplier_advance_cents
 FROM parties p LEFT JOIN v_journal_posted_lines l ON l.party_id=p.id GROUP BY p.id;
CREATE TRIGGER journal_advance_party_insert BEFORE INSERT ON journal_lines BEGIN
 SELECT CASE WHEN NEW.account_code='CUSTOMER_ADVANCES' AND NOT EXISTS(
  SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('CUSTOMER','BOTH'))
  THEN RAISE(ABORT,'ADVANCE_REQUIRES_CUSTOMER') END;
 SELECT CASE WHEN NEW.account_code='SUPPLIER_ADVANCES' AND NOT EXISTS(
  SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('SUPPLIER','BOTH'))
  THEN RAISE(ABORT,'ADVANCE_REQUIRES_SUPPLIER') END;
END;
CREATE TRIGGER journal_advance_party_update BEFORE UPDATE OF account_code,party_id ON journal_lines BEGIN
 SELECT CASE WHEN NEW.account_code='CUSTOMER_ADVANCES' AND NOT EXISTS(
  SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('CUSTOMER','BOTH'))
  THEN RAISE(ABORT,'ADVANCE_REQUIRES_CUSTOMER') END;
 SELECT CASE WHEN NEW.account_code='SUPPLIER_ADVANCES' AND NOT EXISTS(
  SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('SUPPLIER','BOTH'))
  THEN RAISE(ABORT,'ADVANCE_REQUIRES_SUPPLIER') END;
END;
CREATE TABLE finance_operations (
 entry_id INTEGER PRIMARY KEY REFERENCES journal_entries(id) ON DELETE RESTRICT,
 party_id INTEGER REFERENCES parties(id) ON DELETE RESTRICT,
 amount_cents INTEGER NOT NULL CHECK(typeof(amount_cents)='integer' AND amount_cents>0),
 debt_cents INTEGER NOT NULL CHECK(typeof(debt_cents)='integer' AND debt_cents>=0),
 advance_cents INTEGER NOT NULL CHECK(typeof(advance_cents)='integer' AND advance_cents>=0),
 memo TEXT NOT NULL CHECK(length(trim(memo)) BETWEEN 1 AND 500)
);
-- The same predicate is used on import/startup, including files whose triggers were removed.
CREATE VIEW v_invalid_finance_operations AS
 SELECT f.entry_id FROM finance_operations f JOIN journal_entries e ON e.id=f.entry_id WHERE
 e.state<>'POSTED' OR e.source_kind NOT IN ('CUSTOMER_PAYMENT','SUPPLIER_PAYMENT','EXPENSE')
 OR f.amount_cents<>(SELECT SUM(debit_cents) FROM journal_lines WHERE entry_id=e.id)
 OR (e.source_kind IN ('CUSTOMER_PAYMENT','SUPPLIER_PAYMENT') AND (
  f.party_id IS NULL OR f.amount_cents<>f.debt_cents+f.advance_cents
  OR EXISTS(SELECT 1 FROM journal_lines l WHERE l.entry_id=e.id AND (
    l.account_code NOT IN ('CASH',CASE WHEN e.source_kind='CUSTOMER_PAYMENT' THEN 'RECEIVABLES' ELSE 'PAYABLES' END,
     CASE WHEN e.source_kind='CUSTOMER_PAYMENT' THEN 'CUSTOMER_ADVANCES' ELSE 'SUPPLIER_ADVANCES' END)
    OR (l.account_code<>'CASH' AND (l.party_id IS NULL OR l.party_id<>f.party_id))))
  OR f.amount_cents<>COALESCE((SELECT SUM(CASE WHEN e.source_kind='CUSTOMER_PAYMENT' THEN debit_cents-credit_cents ELSE credit_cents-debit_cents END)
      FROM journal_lines WHERE entry_id=e.id AND account_code='CASH'),0)
  OR f.debt_cents<>COALESCE((SELECT SUM(CASE WHEN e.source_kind='CUSTOMER_PAYMENT' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END)
      FROM journal_lines WHERE entry_id=e.id AND account_code IN ('RECEIVABLES','PAYABLES')),0)
  OR f.advance_cents<>COALESCE((SELECT SUM(CASE WHEN e.source_kind='CUSTOMER_PAYMENT' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END)
      FROM journal_lines WHERE entry_id=e.id AND account_code IN ('CUSTOMER_ADVANCES','SUPPLIER_ADVANCES')),0)
 ))
 OR (e.source_kind='EXPENSE' AND (f.party_id IS NOT NULL OR f.debt_cents<>0 OR f.advance_cents<>0
  OR f.amount_cents<>COALESCE((SELECT SUM(credit_cents-debit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='CASH'),0)
  OR EXISTS(SELECT 1 FROM journal_lines l JOIN journal_accounts a ON a.code=l.account_code
    WHERE l.entry_id=e.id AND l.account_code<>'CASH' AND (a.kind<>'EXPENSE' OR a.code='COGS' OR l.credit_cents<>0 OR l.party_id IS NOT NULL))));
CREATE TRIGGER finance_operation_validate AFTER INSERT ON finance_operations
WHEN EXISTS(SELECT 1 FROM v_invalid_finance_operations WHERE entry_id=NEW.entry_id)
BEGIN SELECT RAISE(ABORT,'FINANCE_OPERATION_MISMATCH'); END;
CREATE TRIGGER finance_operation_no_update BEFORE UPDATE ON finance_operations
BEGIN SELECT RAISE(ABORT,'FINANCE_OPERATION_IMMUTABLE'); END;
CREATE TRIGGER finance_operation_no_delete BEFORE DELETE ON finance_operations
BEGIN SELECT RAISE(ABORT,'FINANCE_OPERATION_IMMUTABLE'); END;
CREATE TABLE finance_activation (
 singleton INTEGER PRIMARY KEY CHECK(singleton=1),
 opening_entry_id INTEGER UNIQUE REFERENCES journal_entries(id) ON DELETE RESTRICT,
 last_legacy_document_id INTEGER NOT NULL CHECK(last_legacy_document_id>=0),
 activated_date TEXT NOT NULL CHECK(length(activated_date)=10),
 created_by_user_id INTEGER NOT NULL REFERENCES users(id)
);
CREATE TRIGGER finance_activation_valid BEFORE INSERT ON finance_activation
WHEN NEW.opening_entry_id IS NOT NULL AND NOT EXISTS(SELECT 1 FROM journal_entries WHERE id=NEW.opening_entry_id AND source_kind='OPENING' AND state='POSTED')
BEGIN SELECT RAISE(ABORT,'RECONCILED_OPENING_REQUIRED'); END;
CREATE TRIGGER finance_activation_no_update BEFORE UPDATE ON finance_activation
BEGIN SELECT RAISE(ABORT,'FINANCE_ACTIVATION_IMMUTABLE'); END;
CREATE TRIGGER finance_activation_no_delete BEFORE DELETE ON finance_activation
BEGIN SELECT RAISE(ABORT,'FINANCE_ACTIVATION_IMMUTABLE'); END;
CREATE UNIQUE INDEX journal_one_document ON journal_entries(document_id)
 WHERE document_id IS NOT NULL AND source_kind IN ('SALE','PURCHASE');
CREATE TABLE purchase_cost_snapshots (
 document_line_id INTEGER PRIMARY KEY REFERENCES document_lines(id) ON DELETE RESTRICT,
 total_cost_cents INTEGER NOT NULL CHECK(typeof(total_cost_cents)='integer' AND total_cost_cents>=0),
 tax_cents INTEGER NOT NULL CHECK(typeof(tax_cents)='integer' AND tax_cents>=0 AND tax_cents<=total_cost_cents)
);
CREATE TRIGGER purchase_cost_insert BEFORE INSERT ON purchase_cost_snapshots
WHEN NOT EXISTS(SELECT 1 FROM document_lines l JOIN documents d ON d.id=l.document_id
 WHERE l.id=NEW.document_line_id AND d.doc_type='PURCHASE' AND d.state='DRAFT')
BEGIN SELECT RAISE(ABORT,'PURCHASE_COST_REQUIRES_DRAFT'); END;
CREATE TRIGGER purchase_cost_no_update BEFORE UPDATE ON purchase_cost_snapshots
BEGIN SELECT RAISE(ABORT,'PURCHASE_COST_IMMUTABLE'); END;
CREATE TRIGGER purchase_cost_no_delete BEFORE DELETE ON purchase_cost_snapshots
BEGIN SELECT RAISE(ABORT,'PURCHASE_COST_IMMUTABLE'); END;
