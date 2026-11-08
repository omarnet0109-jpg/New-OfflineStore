-- Foundation only: no historical backfill and no automatic invoice/cash postings.
CREATE TABLE journal_accounts (
 code TEXT PRIMARY KEY,
 name TEXT NOT NULL,
 kind TEXT NOT NULL CHECK(kind IN ('ASSET','LIABILITY','EQUITY','INCOME','EXPENSE'))
);
INSERT INTO journal_accounts VALUES
 ('CASH','الخزنة','ASSET'),('RECEIVABLES','العملاء','ASSET'),
 ('INVENTORY','المخزون','ASSET'),('PAYABLES','الموردون','LIABILITY'),
 ('OPENING_EQUITY','رصيد افتتاحي مقابل','EQUITY'),('SALES','المبيعات','INCOME'),
 ('COGS','تكلفة المبيعات','EXPENSE'),('EXPENSES','المصروفات','EXPENSE');
CREATE TABLE journal_entries (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 source_kind TEXT NOT NULL CHECK(source_kind IN ('OPENING','ADJUSTMENT','SALE','PURCHASE','RETURN','CUSTOMER_PAYMENT','SUPPLIER_PAYMENT','EXPENSE')),
 source_key TEXT NOT NULL CHECK(length(trim(source_key)) BETWEEN 1 AND 120),
 entry_date TEXT NOT NULL CHECK(length(entry_date)=10),
 state TEXT NOT NULL DEFAULT 'DRAFT' CHECK(state IN ('DRAFT','POSTED')),
 document_id INTEGER REFERENCES documents(id) ON DELETE RESTRICT,
 created_by_user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE RESTRICT,
 created_utc TEXT NOT NULL,
 UNIQUE(source_kind,source_key)
);
CREATE UNIQUE INDEX journal_single_opening ON journal_entries(source_kind)
 WHERE source_kind='OPENING' AND state='POSTED';
CREATE TABLE journal_lines (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 entry_id INTEGER NOT NULL REFERENCES journal_entries(id) ON DELETE RESTRICT,
 line_no INTEGER NOT NULL CHECK(line_no>0),
 account_code TEXT NOT NULL REFERENCES journal_accounts(code) ON DELETE RESTRICT,
 party_id INTEGER REFERENCES parties(id) ON DELETE RESTRICT,
 debit_cents INTEGER NOT NULL DEFAULT 0 CHECK(typeof(debit_cents)='integer' AND debit_cents>=0),
 credit_cents INTEGER NOT NULL DEFAULT 0 CHECK(typeof(credit_cents)='integer' AND credit_cents>=0),
 UNIQUE(entry_id,line_no),
 CHECK((debit_cents>0 AND credit_cents=0) OR (credit_cents>0 AND debit_cents=0))
);
CREATE INDEX journal_lines_account_party ON journal_lines(account_code,party_id);
CREATE TRIGGER journal_starts_draft BEFORE INSERT ON journal_entries WHEN NEW.state<>'DRAFT'
BEGIN SELECT RAISE(ABORT,'JOURNAL_MUST_START_DRAFT'); END;
CREATE TRIGGER journal_post_balance BEFORE UPDATE OF state ON journal_entries
WHEN NEW.state='POSTED' AND OLD.state='DRAFT'
BEGIN
 SELECT CASE WHEN (SELECT COUNT(*) FROM journal_lines WHERE entry_id=OLD.id)<2
  OR (SELECT SUM(debit_cents) FROM journal_lines WHERE entry_id=OLD.id)
    <>(SELECT SUM(credit_cents) FROM journal_lines WHERE entry_id=OLD.id)
 THEN RAISE(ABORT,'UNBALANCED_JOURNAL') END;
END;
CREATE TRIGGER journal_header_no_update BEFORE UPDATE ON journal_entries WHEN OLD.state='POSTED'
BEGIN SELECT RAISE(ABORT,'POSTED_JOURNAL_IMMUTABLE'); END;
CREATE TRIGGER journal_header_no_delete BEFORE DELETE ON journal_entries WHEN OLD.state='POSTED'
BEGIN SELECT RAISE(ABORT,'POSTED_JOURNAL_IMMUTABLE'); END;
CREATE TRIGGER journal_line_insert BEFORE INSERT ON journal_lines
WHEN (SELECT state FROM journal_entries WHERE id=NEW.entry_id)<>'DRAFT'
BEGIN SELECT RAISE(ABORT,'POSTED_JOURNAL_IMMUTABLE'); END;
CREATE TRIGGER journal_line_update BEFORE UPDATE ON journal_lines
WHEN (SELECT state FROM journal_entries WHERE id=OLD.entry_id)<>'DRAFT'
 OR (SELECT state FROM journal_entries WHERE id=NEW.entry_id)<>'DRAFT'
BEGIN SELECT RAISE(ABORT,'POSTED_JOURNAL_IMMUTABLE'); END;
CREATE TRIGGER journal_line_delete BEFORE DELETE ON journal_lines
WHEN (SELECT state FROM journal_entries WHERE id=OLD.entry_id)<>'DRAFT'
BEGIN SELECT RAISE(ABORT,'POSTED_JOURNAL_IMMUTABLE'); END;
CREATE TRIGGER journal_party_required BEFORE INSERT ON journal_lines
BEGIN
 SELECT CASE WHEN NEW.account_code='RECEIVABLES' AND NOT EXISTS(
  SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('CUSTOMER','BOTH'))
  THEN RAISE(ABORT,'RECEIVABLE_REQUIRES_CUSTOMER') END;
 SELECT CASE WHEN NEW.account_code='PAYABLES' AND NOT EXISTS(
  SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('SUPPLIER','BOTH'))
  THEN RAISE(ABORT,'PAYABLE_REQUIRES_SUPPLIER') END;
END;

CREATE TRIGGER journal_party_required_update BEFORE UPDATE OF account_code,party_id ON journal_lines
BEGIN
 SELECT CASE WHEN NEW.account_code='RECEIVABLES' AND NOT EXISTS(
  SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('CUSTOMER','BOTH'))
  THEN RAISE(ABORT,'RECEIVABLE_REQUIRES_CUSTOMER') END;
 SELECT CASE WHEN NEW.account_code='PAYABLES' AND NOT EXISTS(
  SELECT 1 FROM parties WHERE id=NEW.party_id AND party_type IN ('SUPPLIER','BOTH'))
  THEN RAISE(ABORT,'PAYABLE_REQUIRES_SUPPLIER') END;
END;
