-- M5 slice 5.6a: the receivables subledger and sales invoices (DOMAIN_MODEL §11, §13; POSTING_RULES §2; A-159).
-- ar_open_items / ar_settlements mirror the payables tables of V0026/V0029 with the signs the other way round:
-- a positive amount is owed by the customer (a debit on the receivables control), a negative one is the
-- customer's credit, deposit or receipt (a credit). Sales invoices and credit notes open the items; manual journals
-- on the control open adjustments (A-140); receipts and their application follow in 5.7.

CREATE TABLE app.ar_open_items (
  tenant_id              uuid NOT NULL,
  id                     uuid NOT NULL,
  company_id             uuid NOT NULL,
  partner_id             uuid NOT NULL,
  kind                   text NOT NULL CHECK (kind IN ('invoice', 'credit_note', 'deposit', 'receipt_on_account', 'adjustment', 'pdc', 'opening')),
  document_type          text NOT NULL,
  document_id            uuid NOT NULL,
  document_number        text NOT NULL,
  instalment             int NOT NULL DEFAULT 1,
  customer_reference     text,
  posting_date           date NOT NULL,
  document_date          date NOT NULL,
  due_date               date NOT NULL,
  discount_date          date,
  discount_pct           numeric(9,6) NOT NULL DEFAULT 0,
  currency               text NOT NULL,
  original_tc            numeric(24,6) NOT NULL,
  original_fc            numeric(24,6) NOT NULL,
  booked_rate            numeric(24,12) NOT NULL DEFAULT 1,
  settled_tc             numeric(24,6) NOT NULL DEFAULT 0,
  settled_fc             numeric(24,6) NOT NULL DEFAULT 0,
  remaining_tc           numeric(24,6) NOT NULL,
  remaining_fc           numeric(24,6) NOT NULL,
  journal_entry_id       uuid,
  sales_rep_id           uuid,
  on_hold                boolean NOT NULL DEFAULT false,
  hold_reason            text,
  held_by                uuid,
  held_at                timestamptz,
  dunning_level          int NOT NULL DEFAULT 0,
  promised_payment_date  date,
  branch_id              uuid,
  dimension_set_id       uuid,
  status                 text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'partially_settled', 'settled', 'reversed')),
  reversed_on            date,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CREATE INDEX ar_open_items_partner_idx ON app.ar_open_items (tenant_id, company_id, partner_id, status, due_date);
CREATE INDEX ar_open_items_document_idx ON app.ar_open_items (tenant_id, document_type, document_id);
CALL app.enable_tenant_rls('app.ar_open_items');
CALL app.track_updated_at('app.ar_open_items');

-- A reversal is a mirror row (negative amounts) that names the settlement it reverses, so any balance at a date is a plain sum.
CREATE TABLE app.ar_settlements (
  tenant_id                 uuid NOT NULL,
  id                        uuid NOT NULL,
  company_id                uuid NOT NULL,
  settling_item_id          uuid NOT NULL,
  settled_item_id           uuid NOT NULL,
  settlement_date           date NOT NULL,
  kind                      text NOT NULL CHECK (kind IN ('receipt', 'credit_application', 'deposit_application', 'netting', 'write_off', 'revaluation')),
  currency                  text NOT NULL,
  amount_tc                 numeric(24,6) NOT NULL,
  amount_fc_settled_item    numeric(24,6) NOT NULL,
  amount_fc_settling_item   numeric(24,6) NOT NULL,
  settlement_rate           numeric(24,12) NOT NULL DEFAULT 1,
  fx_gain_loss_fc           numeric(24,6) NOT NULL DEFAULT 0,
  discount_taken_tc         numeric(24,6) NOT NULL DEFAULT 0,
  wht_deducted_tc           numeric(24,6) NOT NULL DEFAULT 0,
  write_off_tc              numeric(24,6) NOT NULL DEFAULT 0,
  bank_charge_tc            numeric(24,6) NOT NULL DEFAULT 0,
  journal_entry_id          uuid,
  reverses_settlement_id    uuid,
  reason                    text,
  status                    text NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed')),
  created_by                uuid,
  created_at                timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, settling_item_id) REFERENCES app.ar_open_items (tenant_id, id),
  FOREIGN KEY (tenant_id, settled_item_id) REFERENCES app.ar_open_items (tenant_id, id)
);
CREATE INDEX ar_settlements_settled_idx ON app.ar_settlements (tenant_id, settled_item_id);
CREATE INDEX ar_settlements_settling_idx ON app.ar_settlements (tenant_id, settling_item_id);
CREATE INDEX ar_settlements_journal_idx ON app.ar_settlements (tenant_id, journal_entry_id);
CALL app.enable_tenant_rls('app.ar_settlements');

-- Sales invoices and credit notes (DOMAIN_MODEL §11 sls_invoices): lines against posted shipment lines, service
-- lines of orders, directly priced non-stock items or free lines on an account role; a credit note credits an
-- invoice's lines or stands on its own. Posted documents are reversed, never edited.
CREATE TABLE app.sls_invoices (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  branch_id           uuid,
  number              text NOT NULL,
  kind                text NOT NULL DEFAULT 'invoice' CHECK (kind IN ('invoice', 'credit_note')),
  partner_id          uuid NOT NULL,
  customer_reference  text,
  credits_invoice_id  uuid,
  document_date       date NOT NULL,
  posting_date        date NOT NULL,
  due_date            date,
  currency            text NOT NULL,
  exchange_rate       numeric(24,12) NOT NULL DEFAULT 1,
  payment_terms_id    uuid,
  sales_rep_id        uuid,
  price_list_id       uuid,
  total_net           numeric(24,6) NOT NULL DEFAULT 0,
  total_tax           numeric(24,6) NOT NULL DEFAULT 0,
  total_gross         numeric(24,6) NOT NULL DEFAULT 0,
  tax_rounding_level  text NOT NULL DEFAULT 'line',
  prices_include_tax  boolean NOT NULL DEFAULT false,
  status              text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'cancelled', 'posted', 'reversed')),
  journal_entry_id    uuid,
  reversal_entry_id   uuid,
  reversal_reason     text,
  reversed_at         timestamptz,
  reversed_by         uuid,
  einvoice_status     text NOT NULL DEFAULT 'not_required',
  customer_snapshot   jsonb NOT NULL DEFAULT '{}'::jsonb,
  notes               text,
  custom_fields       jsonb NOT NULL DEFAULT '{}'::jsonb,
  posted_at           timestamptz,
  posted_by           uuid,
  created_by          uuid,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, credits_invoice_id) REFERENCES app.sls_invoices (tenant_id, id)
);
CREATE INDEX sls_invoices_company_idx ON app.sls_invoices (tenant_id, company_id, status, posting_date);
CREATE INDEX sls_invoices_partner_idx ON app.sls_invoices (tenant_id, partner_id, status);
CALL app.enable_tenant_rls('app.sls_invoices');
CALL app.track_updated_at('app.sls_invoices');

CREATE TABLE app.sls_invoice_lines (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  invoice_id          uuid NOT NULL,
  line_no             int NOT NULL,
  kind                text NOT NULL CHECK (kind IN ('shipment', 'order', 'item', 'service', 'invoice')),
  shipment_line_id    uuid,
  order_line_id       uuid,
  invoice_line_id     uuid,
  item_id             uuid,
  variant_id          uuid,
  account_role        text,
  description         text,
  quantity            numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id              uuid,
  quantity_base       numeric(24,9) NOT NULL DEFAULT 0,
  unit_price          numeric(24,6) NOT NULL CHECK (unit_price >= 0),
  discount_pct        numeric(9,6) NOT NULL DEFAULT 0,
  discount_amount     numeric(24,6) NOT NULL DEFAULT 0,
  tax_code_id         uuid,
  tax_rate_pct        numeric(9,6) NOT NULL DEFAULT 0,
  tax_reverse_charge  boolean NOT NULL DEFAULT false,
  tax_recoverable     boolean NOT NULL DEFAULT true,
  tax_reason          text,
  net_amount          numeric(24,6) NOT NULL DEFAULT 0,
  tax_amount          numeric(24,6) NOT NULL DEFAULT 0,
  net_amount_fc       numeric(24,6) NOT NULL DEFAULT 0,
  qty_credited        numeric(24,9) NOT NULL DEFAULT 0,
  net_credited        numeric(24,6) NOT NULL DEFAULT 0,
  dimension_set_id    uuid,
  price_breakdown     jsonb NOT NULL DEFAULT '[]'::jsonb,
  created_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, invoice_id, line_no),
  FOREIGN KEY (tenant_id, invoice_id) REFERENCES app.sls_invoices (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, shipment_line_id) REFERENCES app.sls_shipment_lines (tenant_id, id),
  FOREIGN KEY (tenant_id, order_line_id) REFERENCES app.sls_order_lines (tenant_id, id),
  FOREIGN KEY (tenant_id, invoice_line_id) REFERENCES app.sls_invoice_lines (tenant_id, id)
);
CREATE INDEX sls_invoice_lines_shipment_line_idx ON app.sls_invoice_lines (tenant_id, shipment_line_id);
CREATE INDEX sls_invoice_lines_order_line_idx ON app.sls_invoice_lines (tenant_id, order_line_id);
CREATE INDEX sls_invoice_lines_invoice_line_idx ON app.sls_invoice_lines (tenant_id, invoice_line_id);
CALL app.enable_tenant_rls('app.sls_invoice_lines');

-- What a shipment line has been invoiced so far (base units, like its quantity), and what an order line's net has
-- been invoiced so far (so the last invoice of a line takes the exact remainder rather than a rounded share).
ALTER TABLE app.sls_shipment_lines ADD COLUMN qty_invoiced numeric(24,9) NOT NULL DEFAULT 0;
ALTER TABLE app.sls_order_lines ADD COLUMN net_invoiced numeric(24,6) NOT NULL DEFAULT 0;
