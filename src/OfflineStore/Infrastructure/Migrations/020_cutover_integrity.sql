-- Metadata only: preserve the old recorded cutoff; never infer or post historic money.
CREATE TABLE finance_legacy_documents(document_id INTEGER PRIMARY KEY REFERENCES documents(id) ON DELETE RESTRICT);
INSERT INTO finance_legacy_documents SELECT d.id FROM documents d JOIN finance_activation a
 ON d.id<=a.last_legacy_document_id WHERE d.state='POSTED';
CREATE TRIGGER legacy_manifest_insert BEFORE INSERT ON finance_legacy_documents
WHEN EXISTS(SELECT 1 FROM finance_activation)
BEGIN SELECT RAISE(ABORT,'CUTOVER_MANIFEST_CLOSED'); END;
CREATE TRIGGER legacy_manifest_update BEFORE UPDATE ON finance_legacy_documents
BEGIN SELECT RAISE(ABORT,'CUTOVER_MANIFEST_IMMUTABLE'); END;
CREATE TRIGGER legacy_manifest_delete BEFORE DELETE ON finance_legacy_documents
BEGIN SELECT RAISE(ABORT,'CUTOVER_MANIFEST_IMMUTABLE'); END;
CREATE VIEW v_invalid_financial_documents AS
 WITH expected AS (
 SELECT d.*,COALESCE((SELECT GrandTotalCents FROM SupplyVouchers WHERE DocumentID=d.id AND State='POSTED'),d.total_cents) AS financial_total,
 COALESCE((SELECT SUM(s.total_cost_cents) FROM sale_cost_snapshots s JOIN document_lines l ON l.id=s.document_line_id WHERE l.document_id=d.id),0) AS sale_cost,
 COALESCE((SELECT SUM(s.total_cost_cents) FROM purchase_cost_snapshots s JOIN document_lines l ON l.id=s.document_line_id WHERE l.document_id=d.id),0) AS purchase_cost
 FROM documents d WHERE d.state='POSTED' AND EXISTS(SELECT 1 FROM finance_activation)
 AND NOT EXISTS(SELECT 1 FROM finance_legacy_documents x WHERE x.document_id=d.id)
 ) SELECT d.id AS document_id FROM expected d
 LEFT JOIN journal_entries e ON e.document_id=d.id AND e.source_kind=d.doc_type
 WHERE (d.doc_type='PURCHASE' AND d.purchase_cost<>d.financial_total) OR
 (SELECT COUNT(*) FROM document_lines l WHERE l.document_id=d.id)<>
  CASE WHEN d.doc_type='SALE' THEN (SELECT COUNT(*) FROM sale_cost_snapshots s JOIN document_lines l ON l.id=s.document_line_id WHERE l.document_id=d.id)
   ELSE (SELECT COUNT(*) FROM purchase_cost_snapshots s JOIN document_lines l ON l.id=s.document_line_id WHERE l.document_id=d.id) END
 OR (e.id IS NULL AND (d.financial_total<>0 OR d.sale_cost<>0))
 OR (e.id IS NOT NULL AND (e.state<>'POSTED' OR
  (SELECT SUM(debit_cents) FROM journal_lines WHERE entry_id=e.id)<>d.financial_total+CASE WHEN d.doc_type='SALE' THEN d.sale_cost ELSE 0 END
  OR EXISTS(SELECT 1 FROM journal_lines l WHERE l.entry_id=e.id AND (
   (d.doc_type='SALE' AND l.account_code NOT IN ('CASH','RECEIVABLES','SALES','COGS','INVENTORY'))
   OR (d.doc_type='PURCHASE' AND l.account_code NOT IN ('CASH','PAYABLES','INVENTORY'))
   OR (l.account_code IN ('RECEIVABLES','PAYABLES') AND (l.party_id IS NULL OR l.party_id<>d.party_id))
   OR (l.account_code NOT IN ('RECEIVABLES','PAYABLES') AND l.party_id IS NOT NULL)))
  OR d.financial_total<>COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END)
   FROM journal_lines WHERE entry_id=e.id AND account_code=CASE WHEN d.doc_type='SALE' THEN 'SALES' ELSE 'INVENTORY' END),0)
  OR d.financial_total<>COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN debit_cents-credit_cents ELSE credit_cents-debit_cents END)
   FROM journal_lines WHERE entry_id=e.id AND account_code IN ('CASH',CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END)),0)
  OR (d.doc_type='SALE' AND (d.sale_cost<>COALESCE((SELECT SUM(debit_cents-credit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='COGS'),0)
   OR d.sale_cost<>COALESCE((SELECT SUM(credit_cents-debit_cents) FROM journal_lines WHERE entry_id=e.id AND account_code='INVENTORY'),0)))
 ));
