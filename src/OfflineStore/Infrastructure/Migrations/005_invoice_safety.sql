-- v5 adds guards only. Historical invoice snapshots and stock are never rewritten.
CREATE TRIGGER v5_document_starts_draft BEFORE INSERT ON documents
 WHEN NEW.state <> 'DRAFT'
 BEGIN SELECT RAISE(ABORT,'DOCUMENT_MUST_START_DRAFT'); END;

-- A movement's sign is determined by doc_type. Changing the type or supplier
-- after adding lines would reinterpret the historical transaction.
CREATE TRIGGER v5_document_identity BEFORE UPDATE OF doc_type,party_id ON documents
 WHEN (NEW.doc_type IS NOT OLD.doc_type OR NEW.party_id IS NOT OLD.party_id)
 AND EXISTS(SELECT 1 FROM document_lines WHERE document_id=OLD.id)
 BEGIN SELECT RAISE(ABORT,'DOCUMENT_IDENTITY_IMMUTABLE'); END;

CREATE TRIGGER v5_line_amount BEFORE INSERT ON document_lines
BEGIN
 SELECT CASE WHEN typeof(NEW.qty_milli)<>'integer'
   OR typeof(NEW.entered_qty_milli)<>'integer'
   OR typeof(NEW.unit_factor_milli)<>'integer'
   OR typeof(NEW.unit_price_cents)<>'integer'
   OR typeof(NEW.entered_unit_price_cents)<>'integer'
   OR typeof(NEW.line_total_cents)<>'integer'
   OR typeof(NEW.unit_cost_snapshot_cents)<>'integer'
   OR NEW.unit_price_cents<>NEW.entered_unit_price_cents
   THEN RAISE(ABORT,'LINE_AMOUNT_MISMATCH') END;
 -- Exact half-up rounding for nonnegative money, without floating point or
 -- overflowing qty*price. v3 bounds entered_qty_milli to 1,000,000,000.
 SELECT CASE WHEN typeof(
     NEW.entered_qty_milli*(NEW.entered_unit_price_cents/1000)
     + (NEW.entered_qty_milli*(NEW.entered_unit_price_cents%1000)+500)/1000
   )<>'integer' OR NEW.line_total_cents <>
     NEW.entered_qty_milli*(NEW.entered_unit_price_cents/1000)
     + (NEW.entered_qty_milli*(NEW.entered_unit_price_cents%1000)+500)/1000
   THEN RAISE(ABORT,'LINE_AMOUNT_MISMATCH') END;
END;
