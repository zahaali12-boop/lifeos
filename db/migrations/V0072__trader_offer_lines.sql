-- Trader edition (ADR-0035, A-186): an offer line carries the supplier's minimum order quantity, so the comparison can
-- tell an offer the requested quantity cannot take, and a note on the specification offered.
ALTER TABLE app.pur_supplier_quote_lines
  ADD COLUMN moq   numeric(24,9) CHECK (moq IS NULL OR moq > 0),
  ADD COLUMN notes text;
