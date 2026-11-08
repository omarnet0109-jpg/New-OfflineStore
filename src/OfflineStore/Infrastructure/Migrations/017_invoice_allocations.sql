-- Allocation is an append-only link to actual recorded debt reductions, never a new money movement.
CREATE TABLE invoice_allocations (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 entry_id INTEGER NOT NULL REFERENCES journal_entries(id) ON DELETE RESTRICT,
 document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
 amount_cents INTEGER NOT NULL CHECK(typeof(amount_cents)='integer' AND amount_cents>0),
 reference TEXT NOT NULL UNIQUE DEFAULT(lower(hex(randomblob(16)))) CHECK(length(trim(reference)) BETWEEN 1 AND 120)
);
CREATE INDEX invoice_allocations_entry ON invoice_allocations(entry_id);
CREATE INDEX invoice_allocations_document ON invoice_allocations(document_id);
CREATE VIEW v_financial_invoices AS
 SELECT d.id AS document_id,d.doc_code,d.doc_type,d.party_id,e.entry_date,
 COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN l.debit_cents-l.credit_cents ELSE l.credit_cents-l.debit_cents END)
  FROM journal_lines l WHERE l.entry_id=e.id AND l.account_code=CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END),0) AS initial_debt_cents,
 COALESCE((SELECT SUM(x.amount_cents) FROM invoice_allocations x WHERE x.document_id=d.id),0) AS allocated_cents,
 COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN l.debit_cents-l.credit_cents ELSE l.credit_cents-l.debit_cents END)
  FROM journal_lines l WHERE l.entry_id=e.id AND l.account_code=CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END),0)
  -COALESCE((SELECT SUM(x.amount_cents) FROM invoice_allocations x WHERE x.document_id=d.id),0) AS unallocated_invoice_cents
 FROM documents d JOIN journal_entries e ON e.document_id=d.id AND e.source_kind=d.doc_type AND e.state='POSTED'
 WHERE d.state='POSTED';
CREATE VIEW v_invalid_invoice_allocations AS
 SELECT x.id FROM invoice_allocations x
 LEFT JOIN v_financial_invoices d ON d.document_id=x.document_id
 LEFT JOIN journal_entries e ON e.id=x.entry_id
 WHERE d.document_id IS NULL OR d.party_id IS NULL OR e.state<>'POSTED'
 OR NOT ((d.doc_type='SALE' AND (e.source_kind='CUSTOMER_PAYMENT' OR EXISTS(SELECT 1 FROM advance_applications a WHERE a.entry_id=e.id AND a.is_customer=1)))
      OR (d.doc_type='PURCHASE' AND (e.source_kind='SUPPLIER_PAYMENT' OR EXISTS(SELECT 1 FROM advance_applications a WHERE a.entry_id=e.id AND a.is_customer=0))))
 OR EXISTS(SELECT 1 FROM journal_lines l WHERE l.entry_id=e.id AND l.account_code IN ('RECEIVABLES','PAYABLES')
   AND (l.party_id IS NULL OR l.party_id<>d.party_id))
 OR (SELECT SUM(amount_cents) FROM invoice_allocations WHERE entry_id=e.id)>
    COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END)
     FROM journal_lines WHERE entry_id=e.id AND account_code=CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END),0)
 OR d.allocated_cents>d.initial_debt_cents;
CREATE TRIGGER invoice_allocation_validate AFTER INSERT ON invoice_allocations
WHEN EXISTS(SELECT 1 FROM v_invalid_invoice_allocations)
BEGIN SELECT RAISE(ABORT,'INVALID_INVOICE_ALLOCATION'); END;
CREATE TRIGGER invoice_allocation_no_update BEFORE UPDATE ON invoice_allocations
BEGIN SELECT RAISE(ABORT,'INVOICE_ALLOCATION_IMMUTABLE'); END;
CREATE TRIGGER invoice_allocation_no_delete BEFORE DELETE ON invoice_allocations
BEGIN SELECT RAISE(ABORT,'INVOICE_ALLOCATION_IMMUTABLE'); END;
