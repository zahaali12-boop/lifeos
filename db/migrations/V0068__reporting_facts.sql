-- Reports without a programmer (founder programme item 9, ADR-0034, A-182), first slice: the projected facts of the
-- `reporting` schema (ADR-0021), one row per source line with the keys the semantic models join on and the source
-- document for drill-through; a watermark per tenant and fact for the incremental projection; and the views over
-- state the subledgers keep live (open items, stock on hand). Facts are projections: rebuildable, never the books.
CREATE TABLE reporting.projections (
  tenant_id         uuid NOT NULL,
  fact              text NOT NULL CHECK (fact IN ('journal_lines', 'sales_lines', 'purchase_lines', 'stock_movements', 'stock_values')),
  watermark         timestamptz NOT NULL DEFAULT '1970-01-01T00:00:00Z',
  row_count         bigint NOT NULL DEFAULT 0,
  last_run_at       timestamptz,
  last_duration_ms  integer,
  PRIMARY KEY (tenant_id, fact)
);
CALL app.enable_tenant_rls('reporting.projections');

CREATE TABLE reporting.fact_journal_lines (
  tenant_id               uuid NOT NULL,
  line_id                 uuid NOT NULL,
  entry_id                uuid NOT NULL,
  line_no                 integer NOT NULL,
  company_id              uuid NOT NULL,
  branch_id               uuid,
  posting_date            date NOT NULL,
  document_date           date NOT NULL,
  fiscal_year_id          uuid,
  fiscal_period_id        uuid,
  entry_number            text NOT NULL,
  source_module           text,
  source_document_type    text,
  source_document_id      uuid,
  source_document_number  text,
  is_manual               boolean NOT NULL DEFAULT false,
  is_reversal             boolean NOT NULL DEFAULT false,
  is_closing_entry        boolean NOT NULL DEFAULT false,
  is_opening_entry        boolean NOT NULL DEFAULT false,
  account_id              uuid NOT NULL,
  account_role            text NOT NULL,
  partner_id              uuid,
  dimension_set_id        uuid,
  tax_code_id             uuid,
  subledger_type          text,
  currency_tc             text NOT NULL,
  debit_tc                numeric(24,6) NOT NULL DEFAULT 0,
  credit_tc               numeric(24,6) NOT NULL DEFAULT 0,
  currency_fc             text NOT NULL,
  debit_fc                numeric(24,6) NOT NULL DEFAULT 0,
  credit_fc               numeric(24,6) NOT NULL DEFAULT 0,
  currency_rc             text,
  debit_rc                numeric(24,6) NOT NULL DEFAULT 0,
  credit_rc               numeric(24,6) NOT NULL DEFAULT 0,
  description_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  posted_at               timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, line_id)
);
CREATE INDEX fact_journal_lines_company_date_idx ON reporting.fact_journal_lines (tenant_id, company_id, posting_date);
CREATE INDEX fact_journal_lines_account_idx ON reporting.fact_journal_lines (tenant_id, company_id, account_id, posting_date);
CREATE INDEX fact_journal_lines_entry_idx ON reporting.fact_journal_lines (tenant_id, entry_id);
CALL app.enable_tenant_rls('reporting.fact_journal_lines');

CREATE TABLE reporting.fact_sales_lines (
  tenant_id         uuid NOT NULL,
  line_id           uuid NOT NULL,
  invoice_id        uuid NOT NULL,
  line_no           integer NOT NULL,
  company_id        uuid NOT NULL,
  branch_id         uuid,
  invoice_number    text NOT NULL,
  kind              text NOT NULL,
  status            text NOT NULL,
  document_date     date NOT NULL,
  posting_date      date NOT NULL,
  due_date          date,
  partner_id        uuid NOT NULL,
  sales_rep_id      uuid,
  payment_terms_id  uuid,
  price_list_id     uuid,
  currency          text NOT NULL,
  exchange_rate     numeric(24,12) NOT NULL DEFAULT 1,
  item_id           uuid,
  variant_id        uuid,
  uom_id            uuid,
  account_role      text,
  line_kind         text NOT NULL,
  description       text,
  quantity          numeric(24,9) NOT NULL DEFAULT 0,
  unit_price        numeric(24,6) NOT NULL DEFAULT 0,
  discount_pct      numeric(9,6) NOT NULL DEFAULT 0,
  discount_amount   numeric(24,6) NOT NULL DEFAULT 0,
  tax_code_id       uuid,
  tax_rate_pct      numeric(9,6) NOT NULL DEFAULT 0,
  net_amount        numeric(24,6) NOT NULL DEFAULT 0,
  tax_amount        numeric(24,6) NOT NULL DEFAULT 0,
  gross_amount      numeric(24,6) NOT NULL DEFAULT 0,
  net_amount_fc     numeric(24,6) NOT NULL DEFAULT 0,
  dimension_set_id  uuid,
  posted_at         timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, line_id)
);
CREATE INDEX fact_sales_lines_company_date_idx ON reporting.fact_sales_lines (tenant_id, company_id, posting_date);
CREATE INDEX fact_sales_lines_partner_idx ON reporting.fact_sales_lines (tenant_id, company_id, partner_id);
CREATE INDEX fact_sales_lines_item_idx ON reporting.fact_sales_lines (tenant_id, company_id, item_id);
CALL app.enable_tenant_rls('reporting.fact_sales_lines');

CREATE TABLE reporting.fact_purchase_lines (
  tenant_id         uuid NOT NULL,
  line_id           uuid NOT NULL,
  invoice_id        uuid NOT NULL,
  line_no           integer NOT NULL,
  company_id        uuid NOT NULL,
  branch_id         uuid,
  invoice_number    text NOT NULL,
  kind              text NOT NULL,
  status            text NOT NULL,
  document_date     date NOT NULL,
  posting_date      date NOT NULL,
  due_date          date,
  partner_id        uuid NOT NULL,
  payment_terms_id  uuid,
  currency          text NOT NULL,
  exchange_rate     numeric(24,12) NOT NULL DEFAULT 1,
  item_id           uuid,
  uom_id            uuid,
  account_role      text,
  line_kind         text NOT NULL,
  description       text,
  quantity          numeric(24,9) NOT NULL DEFAULT 0,
  unit_price        numeric(24,6) NOT NULL DEFAULT 0,
  discount_pct      numeric(9,6) NOT NULL DEFAULT 0,
  tax_code_id       uuid,
  net_amount        numeric(24,6) NOT NULL DEFAULT 0,
  tax_amount        numeric(24,6) NOT NULL DEFAULT 0,
  wht_amount        numeric(24,6) NOT NULL DEFAULT 0,
  gross_amount      numeric(24,6) NOT NULL DEFAULT 0,
  net_amount_fc     numeric(24,6) NOT NULL DEFAULT 0,
  dimension_set_id  uuid,
  posted_at         timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, line_id)
);
CREATE INDEX fact_purchase_lines_company_date_idx ON reporting.fact_purchase_lines (tenant_id, company_id, posting_date);
CREATE INDEX fact_purchase_lines_partner_idx ON reporting.fact_purchase_lines (tenant_id, company_id, partner_id);
CREATE INDEX fact_purchase_lines_item_idx ON reporting.fact_purchase_lines (tenant_id, company_id, item_id);
CALL app.enable_tenant_rls('reporting.fact_purchase_lines');

CREATE TABLE reporting.fact_stock_movements (
  tenant_id             uuid NOT NULL,
  entry_id              uuid NOT NULL,
  sequence              bigint NOT NULL,
  posting_id            uuid NOT NULL,
  company_id            uuid NOT NULL,
  item_id               uuid NOT NULL,
  variant_id            uuid,
  warehouse_id          uuid NOT NULL,
  bin_id                uuid,
  lot_id                uuid,
  serial_id             uuid,
  entry_type            text NOT NULL,
  quantity              numeric(24,9) NOT NULL DEFAULT 0,
  entered_uom_id        uuid,
  entered_quantity      numeric(24,9) NOT NULL DEFAULT 0,
  posting_date          date NOT NULL,
  fiscal_period_id      uuid,
  source_document_type  text NOT NULL,
  source_document_id    uuid NOT NULL,
  source_line_id        uuid,
  owner_partner_id      uuid,
  posted_at             timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, entry_id)
);
CREATE INDEX fact_stock_movements_company_date_idx ON reporting.fact_stock_movements (tenant_id, company_id, posting_date);
CREATE INDEX fact_stock_movements_item_idx ON reporting.fact_stock_movements (tenant_id, company_id, item_id, warehouse_id);
CALL app.enable_tenant_rls('reporting.fact_stock_movements');

CREATE TABLE reporting.fact_stock_values (
  tenant_id             uuid NOT NULL,
  value_id              uuid NOT NULL,
  entry_id              uuid,
  company_id            uuid NOT NULL,
  item_id               uuid NOT NULL,
  warehouse_id          uuid NOT NULL,
  posting_date          date NOT NULL,
  valuation_date        date NOT NULL,
  value_type            text NOT NULL,
  valued_quantity       numeric(24,9) NOT NULL DEFAULT 0,
  unit_cost             numeric(24,6) NOT NULL DEFAULT 0,
  cost_amount           numeric(24,6) NOT NULL DEFAULT 0,
  currency              text NOT NULL,
  account_role          text NOT NULL,
  offset_role           text NOT NULL,
  item_posting_group_id uuid,
  journal_entry_id      uuid,
  source_document_type  text NOT NULL,
  source_document_id    uuid NOT NULL,
  created_at            timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, value_id)
);
CREATE INDEX fact_stock_values_company_date_idx ON reporting.fact_stock_values (tenant_id, company_id, posting_date);
CREATE INDEX fact_stock_values_item_idx ON reporting.fact_stock_values (tenant_id, company_id, item_id, warehouse_id);
CALL app.enable_tenant_rls('reporting.fact_stock_values');

-- The projections scan their sources by the stamp they were posted or changed at.
CREATE INDEX IF NOT EXISTS gl_journal_entries_posted_at_idx ON app.gl_journal_entries (tenant_id, posted_at);
CREATE INDEX IF NOT EXISTS sls_invoices_updated_at_idx ON app.sls_invoices (tenant_id, updated_at);
CREATE INDEX IF NOT EXISTS pur_invoices_updated_at_idx ON app.pur_invoices (tenant_id, updated_at);
CREATE INDEX IF NOT EXISTS inv_stock_ledger_entries_posted_at_idx ON app.inv_stock_ledger_entries (tenant_id, posted_at);
CREATE INDEX IF NOT EXISTS inv_stock_value_entries_created_at_idx ON app.inv_stock_value_entries (tenant_id, created_at);

-- State the subledgers keep live is read in place; security_invoker keeps the tenant policy of the source tables in force.
CREATE VIEW reporting.v_open_items WITH (security_invoker = true) AS
SELECT i.tenant_id, i.id, 'AR'::text AS side, i.company_id, i.branch_id, i.partner_id, i.kind, i.document_type, i.document_id, i.document_number, i.instalment,
       i.posting_date, i.document_date, i.due_date, i.currency, i.original_tc, i.original_fc, i.settled_tc, i.settled_fc, i.remaining_tc, i.remaining_fc,
       i.status, i.dimension_set_id, i.sales_rep_id, i.on_hold AS is_blocked, i.dunning_level, i.created_at
FROM app.ar_open_items i
UNION ALL
SELECT i.tenant_id, i.id, 'AP'::text AS side, i.company_id, i.branch_id, i.partner_id, i.kind, i.document_type, i.document_id, i.document_number, i.instalment,
       i.posting_date, i.document_date, i.due_date, i.currency, i.original_tc, i.original_fc, i.settled_tc, i.settled_fc, i.remaining_tc, i.remaining_fc,
       i.status, i.dimension_set_id, NULL::uuid AS sales_rep_id, i.payment_blocked AS is_blocked, 0 AS dunning_level, i.created_at
FROM app.ap_open_items i;

CREATE VIEW reporting.v_stock_on_hand WITH (security_invoker = true) AS
SELECT b.tenant_id, b.company_id, b.item_id, b.variant_id, b.warehouse_id, b.bin_id, b.lot_id, b.serial_id, b.on_hand, b.reserved, b.quality_hold, b.last_movement_at
FROM app.inv_stock_balances b;
