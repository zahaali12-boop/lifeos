-- M4 slice 4.4: supplier invoices with two- and three-way matching (DOMAIN_MODEL §10, POSTING_RULES "Supplier invoice").
-- An invoice is drafted against posted receipt lines (three-way), order lines for services (two-way) or as free
-- expense lines; matched against the supplier's price and quantity tolerances; blocked breaches wait for a workflow
-- override; posting settles GRNI at the invoiced value, re-prices the receipt entries through the costing engine,
-- credits AP with one open item per payment-terms instalment, withholds tax at invoice when the supplier's code says
-- so, and consumes the order's budget commitments.

-- Serialised receipt lines post one stock entry per unit; invoices re-price every one of them.
ALTER TABLE app.pur_receipt_lines ADD COLUMN sle_ids jsonb NOT NULL DEFAULT '[]'::jsonb;

CREATE TABLE app.pur_invoices (
  tenant_id                uuid NOT NULL REFERENCES control.tenants (id),
  id                       uuid NOT NULL,
  company_id               uuid NOT NULL,
  branch_id                uuid,
  number                   text NOT NULL,
  kind                     text NOT NULL DEFAULT 'invoice' CHECK (kind IN ('invoice', 'expense')),
  partner_id               uuid NOT NULL,
  supplier_invoice_number  text,
  document_date            date NOT NULL,
  posting_date             date NOT NULL,
  due_date                 date,
  currency                 text NOT NULL,
  exchange_rate            numeric(24,12) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
  payment_terms_id         uuid,
  wht_code_id              uuid,
  total_net                numeric(24,6) NOT NULL DEFAULT 0,
  total_tax                numeric(24,6) NOT NULL DEFAULT 0,
  total_wht                numeric(24,6) NOT NULL DEFAULT 0,
  total_gross              numeric(24,6) NOT NULL DEFAULT 0,
  total_payable            numeric(24,6) NOT NULL DEFAULT 0,
  status                   text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'pending_approval', 'blocked', 'approved', 'posted', 'reversed', 'rejected')),
  block_kind               text,
  block_reason             text,
  block_id                 uuid,
  approval_request_id      uuid,
  rejection_reason         text,
  journal_entry_id         uuid,
  reversal_entry_id        uuid,
  reversal_reason          text,
  reversed_at              timestamptz,
  reversed_by              uuid,
  notes                    text,
  custom_fields            jsonb NOT NULL DEFAULT '{}'::jsonb,
  submitted_at             timestamptz,
  submitted_by             uuid,
  approved_at              timestamptz,
  posted_at                timestamptz,
  posted_by                uuid,
  created_by               uuid,
  created_at               timestamptz NOT NULL DEFAULT now(),
  updated_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, payment_terms_id) REFERENCES app.ptr_payment_terms (tenant_id, id),
  FOREIGN KEY (tenant_id, wht_code_id) REFERENCES app.ptr_wht_codes (tenant_id, id)
);
CREATE INDEX pur_invoices_company_idx ON app.pur_invoices (tenant_id, company_id, status, posting_date);
CREATE INDEX pur_invoices_partner_ref_idx ON app.pur_invoices (tenant_id, partner_id, supplier_invoice_number);
CALL app.enable_tenant_rls('app.pur_invoices');
CALL app.track_updated_at('app.pur_invoices');

CREATE TABLE app.pur_invoice_lines (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  invoice_id          uuid NOT NULL,
  line_no             int NOT NULL,
  kind                text NOT NULL CHECK (kind IN ('receipt', 'order', 'expense')),
  receipt_line_id     uuid,
  order_line_id       uuid,
  item_id             uuid,
  account_role        text,
  description         text,
  quantity            numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id              uuid,
  unit_price          numeric(24,6) NOT NULL CHECK (unit_price >= 0),
  discount_pct        numeric(9,6) NOT NULL DEFAULT 0,
  tax_code_id         uuid,
  net_amount          numeric(24,6) NOT NULL DEFAULT 0,
  tax_amount          numeric(24,6) NOT NULL DEFAULT 0,
  wht_amount          numeric(24,6) NOT NULL DEFAULT 0,
  net_amount_fc       numeric(24,6) NOT NULL DEFAULT 0,
  expected_unit_price numeric(24,6),
  price_variance_pct  numeric(12,6),
  qty_variance        numeric(24,9),
  dimension_set_id    uuid,
  created_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, invoice_id, line_no),
  FOREIGN KEY (tenant_id, invoice_id) REFERENCES app.pur_invoices (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, receipt_line_id) REFERENCES app.pur_receipt_lines (tenant_id, id),
  FOREIGN KEY (tenant_id, order_line_id) REFERENCES app.pur_order_lines (tenant_id, id)
);
CREATE INDEX pur_invoice_lines_receipt_line_idx ON app.pur_invoice_lines (tenant_id, receipt_line_id);
CALL app.enable_tenant_rls('app.pur_invoice_lines');

CREATE TABLE app.pur_match_results (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  invoice_id            uuid NOT NULL,
  status                text NOT NULL CHECK (status IN ('matched', 'price_variance', 'qty_variance', 'no_receipt', 'duplicate_suspect')),
  price_tolerance_pct   numeric(9,6) NOT NULL DEFAULT 0,
  qty_tolerance_pct     numeric(9,6) NOT NULL DEFAULT 0,
  price_variance_amount numeric(24,6) NOT NULL DEFAULT 0,
  price_variance_pct    numeric(12,6) NOT NULL DEFAULT 0,
  qty_variance          numeric(24,9) NOT NULL DEFAULT 0,
  details               jsonb NOT NULL DEFAULT '[]'::jsonb,
  override_id           uuid,
  matched_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, invoice_id) REFERENCES app.pur_invoices (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX pur_match_results_invoice_idx ON app.pur_match_results (tenant_id, invoice_id, matched_at);
CALL app.enable_tenant_rls('app.pur_match_results');

-- Accounts payable open items (DOMAIN_MODEL §10 ap_open_items). Invoices create them; payments, debit notes and
-- advances (4.6–4.7) settle them. Amounts in the transaction currency with the booked functional value beside them.
CREATE TABLE app.ap_open_items (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  company_id           uuid NOT NULL,
  partner_id           uuid NOT NULL,
  kind                 text NOT NULL CHECK (kind IN ('invoice', 'debit_note', 'advance', 'payment_on_account', 'adjustment', 'pdc', 'opening')),
  document_type        text NOT NULL,
  document_id          uuid NOT NULL,
  document_number      text NOT NULL,
  instalment           int NOT NULL DEFAULT 1,
  supplier_reference   text,
  posting_date         date NOT NULL,
  document_date        date NOT NULL,
  due_date             date NOT NULL,
  discount_date        date,
  discount_pct         numeric(9,6) NOT NULL DEFAULT 0,
  currency             text NOT NULL,
  original_tc          numeric(24,6) NOT NULL,
  original_fc          numeric(24,6) NOT NULL,
  booked_rate          numeric(24,12) NOT NULL DEFAULT 1,
  settled_tc           numeric(24,6) NOT NULL DEFAULT 0,
  settled_fc           numeric(24,6) NOT NULL DEFAULT 0,
  remaining_tc         numeric(24,6) NOT NULL,
  remaining_fc         numeric(24,6) NOT NULL,
  journal_entry_id     uuid,
  payment_blocked      boolean NOT NULL DEFAULT false,
  block_reason         text,
  branch_id            uuid,
  dimension_set_id     uuid,
  status               text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'partially_settled', 'settled', 'reversed')),
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CREATE INDEX ap_open_items_partner_idx ON app.ap_open_items (tenant_id, company_id, partner_id, status, due_date);
CREATE INDEX ap_open_items_document_idx ON app.ap_open_items (tenant_id, document_type, document_id);
CALL app.enable_tenant_rls('app.ap_open_items');
CALL app.track_updated_at('app.ap_open_items');
