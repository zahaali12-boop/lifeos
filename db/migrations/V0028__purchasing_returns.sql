-- M4 slice 4.6: supplier returns, debit notes and credit application (DOMAIN_MODEL §10, POSTING_RULES "Supplier return").
-- A return takes goods out of stock at the exact cost of the receipt line it returns (the stock engine's purchase
-- return applied to the original entries), with GRNI as the offset; a debit note (an invoice of kind debit_note)
-- credits the supplier for returned quantities or expenses, settling GRNI and reducing what is owed; settlements
-- record credits applied to invoices (and, from 4.7, payments).

CREATE TABLE app.pur_returns (
  tenant_id           uuid NOT NULL REFERENCES control.tenants (id),
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  branch_id           uuid,
  number              text NOT NULL,
  receipt_id          uuid NOT NULL,
  partner_id          uuid NOT NULL,
  warehouse_id        uuid NOT NULL,
  posting_date        date NOT NULL,
  status              text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'posted', 'reversed')),
  currency            text NOT NULL,
  reason              text,
  supplier_rma        text,
  total_cost_fc       numeric(24,6) NOT NULL DEFAULT 0,
  stock_posting_id    uuid,
  journal_entry_id    uuid,
  reversal_posting_id uuid,
  reversal_reason     text,
  reversed_at         timestamptz,
  reversed_by         uuid,
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
  FOREIGN KEY (tenant_id, receipt_id) REFERENCES app.pur_receipts (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CREATE INDEX pur_returns_receipt_idx ON app.pur_returns (tenant_id, receipt_id, status);
CREATE INDEX pur_returns_company_idx ON app.pur_returns (tenant_id, company_id, status, posting_date);
CALL app.enable_tenant_rls('app.pur_returns');
CALL app.track_updated_at('app.pur_returns');

CREATE TABLE app.pur_return_lines (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  return_id            uuid NOT NULL,
  line_no              int NOT NULL,
  receipt_line_id      uuid NOT NULL,
  item_id              uuid NOT NULL,
  variant_id           uuid,
  quantity             numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id               uuid NOT NULL,
  quantity_base        numeric(24,9) NOT NULL CHECK (quantity_base > 0),
  bin_id               uuid,
  lot_number           text,
  serial_numbers       jsonb NOT NULL DEFAULT '[]'::jsonb,
  reason               text,
  cost_amount_fc       numeric(24,6) NOT NULL DEFAULT 0,
  credited_amount_fc   numeric(24,6) NOT NULL DEFAULT 0,
  qty_credited         numeric(24,9) NOT NULL DEFAULT 0,
  sle_id               uuid,
  sle_ids              jsonb NOT NULL DEFAULT '[]'::jsonb,
  created_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, return_id, line_no),
  FOREIGN KEY (tenant_id, return_id) REFERENCES app.pur_returns (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, receipt_line_id) REFERENCES app.pur_receipt_lines (tenant_id, id)
);
CREATE INDEX pur_return_lines_receipt_line_idx ON app.pur_return_lines (tenant_id, receipt_line_id);
CALL app.enable_tenant_rls('app.pur_return_lines');

-- Settlements between payable open items: a credit (debit note, later an advance or payment) applied to an invoice.
CREATE TABLE app.ap_settlements (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  settling_item_id        uuid NOT NULL,
  settled_item_id         uuid NOT NULL,
  settlement_date         date NOT NULL,
  kind                    text NOT NULL CHECK (kind IN ('payment', 'credit_application', 'advance_application', 'netting', 'write_off', 'revaluation')),
  currency                text NOT NULL,
  amount_tc               numeric(24,6) NOT NULL CHECK (amount_tc > 0),
  amount_fc_settled_item  numeric(24,6) NOT NULL,
  amount_fc_settling_item numeric(24,6) NOT NULL,
  fx_gain_loss_fc         numeric(24,6) NOT NULL DEFAULT 0,
  journal_entry_id        uuid,
  reverses_settlement_id  uuid,
  created_by              uuid,
  created_at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, settling_item_id) REFERENCES app.ap_open_items (tenant_id, id),
  FOREIGN KEY (tenant_id, settled_item_id) REFERENCES app.ap_open_items (tenant_id, id)
);
CREATE INDEX ap_settlements_settled_idx ON app.ap_settlements (tenant_id, settled_item_id);
CREATE INDEX ap_settlements_settling_idx ON app.ap_settlements (tenant_id, settling_item_id);
CALL app.enable_tenant_rls('app.ap_settlements');

ALTER TABLE app.pur_invoices DROP CONSTRAINT pur_invoices_kind_check;
ALTER TABLE app.pur_invoices ADD CONSTRAINT pur_invoices_kind_check CHECK (kind IN ('invoice', 'expense', 'debit_note'));
ALTER TABLE app.pur_invoice_lines DROP CONSTRAINT pur_invoice_lines_kind_check;
ALTER TABLE app.pur_invoice_lines ADD CONSTRAINT pur_invoice_lines_kind_check CHECK (kind IN ('receipt', 'order', 'expense', 'charge', 'return'));
ALTER TABLE app.pur_invoice_lines ADD COLUMN return_line_id uuid;
