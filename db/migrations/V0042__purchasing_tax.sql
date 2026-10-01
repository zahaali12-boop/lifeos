-- M5 slice 5.3b: purchase orders and supplier invoices taxed by the tax engine (A-147). Each line keeps what the engine
-- decided when the document was saved (code, rate, whether the supplier charges it or the company self-assesses it,
-- whether it is recoverable, and why that code) so a later change to the tax set-up never rewrites a document; the
-- document keeps the rounding level it was taxed at (ADR-0005). Journal lines and document lines now name real codes.

ALTER TABLE app.pur_orders
  ADD COLUMN tax_rounding_level text NOT NULL DEFAULT 'line' CHECK (tax_rounding_level IN ('line', 'document'));

ALTER TABLE app.pur_order_lines
  ADD COLUMN tax_rate_pct        numeric(9,4) NOT NULL DEFAULT 0,
  ADD COLUMN tax_reverse_charge  boolean NOT NULL DEFAULT false,
  ADD COLUMN tax_recoverable     boolean NOT NULL DEFAULT true,
  ADD COLUMN tax_reason          text CHECK (tax_reason IN ('exemption', 'rule', 'chosen', 'not_registered')),
  ADD FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id);

ALTER TABLE app.pur_invoices
  ADD COLUMN tax_rounding_level        text NOT NULL DEFAULT 'line' CHECK (tax_rounding_level IN ('line', 'document')),
  ADD COLUMN total_reverse_charge_tax  numeric(24,6) NOT NULL DEFAULT 0;

ALTER TABLE app.pur_invoice_lines
  ADD COLUMN tax_rate_pct        numeric(9,4) NOT NULL DEFAULT 0,
  ADD COLUMN tax_reverse_charge  boolean NOT NULL DEFAULT false,
  ADD COLUMN tax_recoverable     boolean NOT NULL DEFAULT true,
  ADD COLUMN tax_reason          text CHECK (tax_reason IN ('exemption', 'rule', 'chosen', 'not_registered')),
  ADD FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id);

ALTER TABLE app.gl_journal_lines ADD FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id);
