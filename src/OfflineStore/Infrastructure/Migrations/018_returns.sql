INSERT INTO journal_accounts VALUES('PURCHASE_RETURN_VARIANCE','فروق مرتجعات المشتريات','INCOME');
CREATE TABLE return_documents (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 original_document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE RESTRICT,
 reference TEXT NOT NULL UNIQUE CHECK(length(trim(reference)) BETWEEN 1 AND 113),
 return_date TEXT NOT NULL CHECK(length(return_date)=10),
 state TEXT NOT NULL DEFAULT 'DRAFT' CHECK(state IN ('DRAFT','POSTED')),
 cash_cents INTEGER NOT NULL CHECK(typeof(cash_cents)='integer' AND cash_cents>=0),
 debt_cents INTEGER NOT NULL CHECK(typeof(debt_cents)='integer' AND debt_cents>=0),
 total_cents INTEGER NOT NULL CHECK(typeof(total_cents)='integer' AND total_cents>=0 AND total_cents=cash_cents+debt_cents),
 journal_entry_id INTEGER UNIQUE REFERENCES journal_entries(id) ON DELETE RESTRICT,
 created_by_user_id INTEGER NOT NULL REFERENCES users(id),created_utc TEXT NOT NULL,
 memo TEXT NOT NULL CHECK(length(trim(memo)) BETWEEN 1 AND 500)
);
CREATE TABLE return_lines (
 id INTEGER PRIMARY KEY AUTOINCREMENT,
 return_id INTEGER NOT NULL REFERENCES return_documents(id) ON DELETE RESTRICT,
 original_line_id INTEGER NOT NULL REFERENCES document_lines(id) ON DELETE RESTRICT,
 qty_milli INTEGER NOT NULL CHECK(typeof(qty_milli)='integer' AND qty_milli>0),
 amount_cents INTEGER NOT NULL CHECK(typeof(amount_cents)='integer' AND amount_cents>=0),
 cost_cents INTEGER NOT NULL CHECK(typeof(cost_cents)='integer' AND cost_cents>=0),
 cost_basis_millicents INTEGER CHECK(cost_basis_millicents IS NULL OR (typeof(cost_basis_millicents)='integer' AND cost_basis_millicents>=0)),
 name_snapshot TEXT NOT NULL,unit_snapshot TEXT NOT NULL,
 UNIQUE(return_id,original_line_id)
);
CREATE INDEX return_lines_original ON return_lines(original_line_id);
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
CREATE TRIGGER return_line_validate BEFORE INSERT ON return_lines BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM return_documents r JOIN document_lines l ON l.document_id=r.original_document_id
  JOIN documents d ON d.id=l.document_id JOIN products p ON p.id=l.product_id
  WHERE r.id=NEW.return_id AND r.state='DRAFT' AND l.id=NEW.original_line_id AND d.state='POSTED'
  AND (p.is_countable=0 OR NEW.qty_milli%1000=0)) THEN RAISE(ABORT,'INVALID_RETURN_LINE') END;
 SELECT CASE WHEN NEW.qty_milli+COALESCE((SELECT SUM(x.qty_milli) FROM return_lines x JOIN return_documents r ON r.id=x.return_id
  WHERE x.original_line_id=NEW.original_line_id AND (r.state='POSTED' OR r.id=NEW.return_id)),0)>
  (SELECT qty_milli FROM document_lines WHERE id=NEW.original_line_id) THEN RAISE(ABORT,'RETURN_EXCEEDS_ORIGINAL') END;
END;
CREATE TRIGGER return_line_no_update BEFORE UPDATE ON return_lines BEGIN SELECT RAISE(ABORT,'RETURN_LINE_IMMUTABLE'); END;
CREATE TRIGGER return_line_no_delete BEFORE DELETE ON return_lines BEGIN SELECT RAISE(ABORT,'RETURN_LINE_IMMUTABLE'); END;
DROP VIEW v_low_stock;
DROP VIEW v_stock;
CREATE VIEW v_posted_stock_movements AS
 SELECT m.product_id,m.delta_qty_milli,d.doc_code AS reference_code,m.created_utc,m.document_id,d.doc_type
 FROM stock_movements m JOIN documents d ON d.id=m.document_id WHERE d.state='POSTED'
 UNION ALL
 SELECT l.product_id,CASE WHEN d.doc_type='SALE' THEN x.qty_milli ELSE -x.qty_milli END,
 printf('R-%06d',r.id),r.created_utc,NULL,CASE WHEN d.doc_type='SALE' THEN 'SALE_RETURN' ELSE 'PURCHASE_RETURN' END
 FROM return_lines x JOIN return_documents r ON r.id=x.return_id JOIN document_lines l ON l.id=x.original_line_id
 JOIN documents d ON d.id=r.original_document_id WHERE r.state='POSTED';
CREATE VIEW v_stock AS
 SELECT p.id AS product_id,p.sku,p.name,p.unit,p.sale_price_cents,p.last_cost_cents,p.reorder_qty_milli,p.active,
 COALESCE(SUM(m.delta_qty_milli),0) AS qty_milli FROM products p LEFT JOIN v_posted_stock_movements m ON m.product_id=p.id GROUP BY p.id;
CREATE VIEW v_low_stock AS
 SELECT product_id,sku,name,unit,qty_milli,reorder_qty_milli,CASE WHEN qty_milli=0 THEN 'OUT' ELSE 'LOW' END AS alert_state
 FROM v_stock WHERE active=1 AND reorder_qty_milli>0 AND qty_milli<=reorder_qty_milli;
DROP TRIGGER stock_movement_validate;
CREATE TRIGGER stock_movement_validate BEFORE INSERT ON stock_movements BEGIN
 SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM document_lines l JOIN documents d ON d.id=l.document_id
  WHERE l.id=NEW.document_line_id AND l.document_id=NEW.document_id AND l.product_id=NEW.product_id AND d.state='DRAFT'
  AND ((d.doc_type='PURCHASE' AND NEW.delta_qty_milli=l.qty_milli) OR (d.doc_type='SALE' AND NEW.delta_qty_milli=-l.qty_milli)))
  THEN RAISE(ABORT,'INVALID_STOCK_MOVEMENT') END;
 SELECT CASE WHEN NEW.delta_qty_milli<0 AND COALESCE((SELECT qty_milli FROM v_stock WHERE product_id=NEW.product_id),0)
  +COALESCE((SELECT SUM(delta_qty_milli) FROM stock_movements WHERE document_id=NEW.document_id AND product_id=NEW.product_id),0)
  +NEW.delta_qty_milli<0 THEN RAISE(ABORT,'INSUFFICIENT_STOCK') END;
END;
DROP TRIGGER v6_recheck_sale_stock;
CREATE TRIGGER v6_recheck_sale_stock BEFORE UPDATE OF state ON documents WHEN NEW.state='POSTED' AND OLD.state='DRAFT' AND NEW.doc_type='SALE' BEGIN
 SELECT CASE WHEN EXISTS(SELECT 1 FROM (SELECT product_id,SUM(delta_qty_milli) AS outgoing FROM stock_movements WHERE document_id=OLD.id GROUP BY product_id) pending
  WHERE pending.outgoing<0 AND COALESCE((SELECT qty_milli FROM v_stock WHERE product_id=pending.product_id),0)+pending.outgoing<0)
  THEN RAISE(ABORT,'INSUFFICIENT_STOCK_AT_POSTING') END;
END;
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
 OR EXISTS(SELECT 1 FROM journal_lines l WHERE l.entry_id=e.id AND ((d.doc_type='SALE' AND l.account_code NOT IN ('CASH','RECEIVABLES','SALES','COGS','INVENTORY')) OR (d.doc_type='PURCHASE' AND l.account_code NOT IN ('CASH','PAYABLES','INVENTORY','PURCHASE_RETURN_VARIANCE'))
    OR (l.account_code IN ('RECEIVABLES','PAYABLES') AND (l.party_id IS NULL OR l.party_id<>d.party_id))))
 OR COALESCE((SELECT SUM(cash_cents) FROM return_documents q WHERE q.original_document_id=d.id AND q.state='POSTED'),0)>
   COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN l.debit_cents-l.credit_cents ELSE l.credit_cents-l.debit_cents END)
    FROM journal_entries j JOIN journal_lines l ON l.entry_id=j.id WHERE j.document_id=d.id AND j.source_kind=d.doc_type AND j.state='POSTED' AND l.account_code='CASH'),0)
   +COALESCE((SELECT SUM(a.amount_cents) FROM invoice_allocations a JOIN journal_entries j ON j.id=a.entry_id WHERE a.document_id=d.id AND j.source_kind<>'RETURN'),0)
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
 OR NOT ((d.doc_type='SALE' AND (e.source_kind='CUSTOMER_PAYMENT' OR (e.source_kind='RETURN' AND EXISTS(SELECT 1 FROM return_documents r WHERE r.journal_entry_id=e.id AND r.original_document_id=d.document_id)) OR EXISTS(SELECT 1 FROM advance_applications a WHERE a.entry_id=e.id AND a.is_customer=1)))
      OR (d.doc_type='PURCHASE' AND (e.source_kind='SUPPLIER_PAYMENT' OR (e.source_kind='RETURN' AND EXISTS(SELECT 1 FROM return_documents r WHERE r.journal_entry_id=e.id AND r.original_document_id=d.document_id)) OR EXISTS(SELECT 1 FROM advance_applications a WHERE a.entry_id=e.id AND a.is_customer=0))))
 OR EXISTS(SELECT 1 FROM journal_lines l WHERE l.entry_id=e.id AND l.account_code IN ('RECEIVABLES','PAYABLES')
   AND (l.party_id IS NULL OR l.party_id<>d.party_id))
 OR (SELECT SUM(amount_cents) FROM invoice_allocations WHERE entry_id=e.id)>
    COALESCE((SELECT SUM(CASE WHEN d.doc_type='SALE' THEN credit_cents-debit_cents ELSE debit_cents-credit_cents END)
     FROM journal_lines WHERE entry_id=e.id AND account_code=CASE WHEN d.doc_type='SALE' THEN 'RECEIVABLES' ELSE 'PAYABLES' END),0)
 OR d.allocated_cents>d.initial_debt_cents;
