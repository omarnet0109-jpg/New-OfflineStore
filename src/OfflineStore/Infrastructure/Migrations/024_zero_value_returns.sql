-- Zero-money stock documents need no fabricated journal; only verified post-cutover sources qualify.
DROP VIEW v_financial_invoices;
CREATE VIEW v_financial_invoices AS
 SELECT d.id AS document_id,d.doc_code,d.doc_type,d.party_id,e.entry_date,
 COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN l.debit_cents-l.credit_cents ELSE l.credit_cents-l.debit_cents END)
  FROM journal_lines l WHERE l.entry_id=e.id AND l.account_code=CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END),0) AS initial_debt_cents,
 COALESCE((SELECT SUM(x.amount_cents) FROM invoice_allocations x WHERE x.document_id=d.id),0) AS allocated_cents,
 COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN l.debit_cents-l.credit_cents ELSE l.credit_cents-l.debit_cents END)
  FROM journal_lines l WHERE l.entry_id=e.id AND l.account_code=CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END),0)
  -COALESCE((SELECT SUM(x.amount_cents) FROM invoice_allocations x WHERE x.document_id=d.id),0) AS unallocated_invoice_cents
 FROM documents d JOIN journal_entries e ON e.document_id=d.id AND e.source_kind=d.doc_type AND e.state='POSTED'
 WHERE d.state='POSTED'
 UNION ALL
 SELECT d.id,d.doc_code,d.doc_type,d.party_id,COALESCE(date(d.created_utc,'localtime'),a.activated_date),0,
 COALESCE((SELECT SUM(x.amount_cents) FROM invoice_allocations x WHERE x.document_id=d.id),0),
 -COALESCE((SELECT SUM(x.amount_cents) FROM invoice_allocations x WHERE x.document_id=d.id),0)
 FROM documents d JOIN finance_activation a ON a.singleton=1
 WHERE d.state='POSTED' AND NOT EXISTS(SELECT 1 FROM finance_legacy_documents x WHERE x.document_id=d.id)
 AND NOT EXISTS(SELECT 1 FROM journal_entries e WHERE e.document_id=d.id AND e.source_kind=d.doc_type)
 AND EXISTS(SELECT 1 FROM document_lines l WHERE l.document_id=d.id)
 AND COALESCE((SELECT GrandTotalCents FROM SupplyVouchers WHERE DocumentID=d.id AND State='POSTED'),d.total_cents)=0
 AND NOT EXISTS(SELECT 1 FROM sale_cost_snapshots s JOIN document_lines l ON l.id=s.document_line_id WHERE l.document_id=d.id AND s.total_cost_cents<>0)
 AND NOT EXISTS(SELECT 1 FROM v_invalid_financial_documents bad WHERE bad.document_id=d.id);
