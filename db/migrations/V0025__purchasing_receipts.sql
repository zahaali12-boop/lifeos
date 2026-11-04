-- M4 slice 4.3: goods receipts against purchase orders (DOMAIN_MODEL §10, POSTING_RULES §4 "Goods receipt").
-- A receipt is drafted against one order, posted through the stock engine at the expected cost (order price after
-- the line discount, converted at the receipt-date rate) with GRNI as the offset and the receipt as the subledger
-- item, and reversed as a whole. Invoices (4.4) and returns (4.6) settle the lines' invoiced and returned amounts so
-- the GRNI balance always equals the uninvoiced, unreturned receipt value (invariant grni_matches_receipts).

CREATE TABLE app.pur_receipts (
  tenant_id               uuid NOT NULL REFERENCES control.tenants (id),
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  branch_id               uuid,
  number                  text NOT NULL,
  order_id                uuid NOT NULL,
  partner_id              uuid NOT NULL,
  warehouse_id            uuid NOT NULL,
  posting_date            date NOT NULL,
  supplier_delivery_note  text,
  status                  text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'posted', 'reversed')),
  currency                text NOT NULL,
  exchange_rate           numeric(24,12) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
  total_expected_cost     numeric(24,6) NOT NULL DEFAULT 0,
  stock_posting_id        uuid,
  journal_entry_id        uuid,
  reversal_posting_id     uuid,
  reversal_reason         text,
  reversed_at             timestamptz,
  reversed_by             uuid,
  notes                   text,
  custom_fields           jsonb NOT NULL DEFAULT '{}'::jsonb,
  posted_at               timestamptz,
  posted_by               uuid,
  created_by              uuid,
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.pur_orders (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CREATE INDEX pur_receipts_order_idx ON app.pur_receipts (tenant_id, order_id, status);
CREATE INDEX pur_receipts_company_idx ON app.pur_receipts (tenant_id, company_id, status, posting_date);
CALL app.enable_tenant_rls('app.pur_receipts');
CALL app.track_updated_at('app.pur_receipts');

CREATE TABLE app.pur_receipt_lines (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  receipt_id            uuid NOT NULL,
  line_no               int NOT NULL,
  order_line_id         uuid NOT NULL,
  item_id               uuid NOT NULL,
  variant_id            uuid,
  quantity              numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id                uuid NOT NULL,
  quantity_base         numeric(24,9) NOT NULL CHECK (quantity_base > 0),
  qty_in_order_uom      numeric(24,9) NOT NULL CHECK (qty_in_order_uom > 0),
  bin_id                uuid,
  lot_number            text,
  expires_on            date,
  serial_numbers        jsonb NOT NULL DEFAULT '[]'::jsonb,
  unit_price            numeric(24,6) NOT NULL DEFAULT 0,
  expected_unit_cost    numeric(24,9) NOT NULL DEFAULT 0,
  expected_cost_amount  numeric(24,6) NOT NULL DEFAULT 0,
  invoiced_cost_amount  numeric(24,6) NOT NULL DEFAULT 0,
  returned_cost_amount  numeric(24,6) NOT NULL DEFAULT 0,
  qty_invoiced          numeric(24,9) NOT NULL DEFAULT 0,
  qty_returned          numeric(24,9) NOT NULL DEFAULT 0,
  sle_id                uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, receipt_id, line_no),
  FOREIGN KEY (tenant_id, receipt_id) REFERENCES app.pur_receipts (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, order_line_id) REFERENCES app.pur_order_lines (tenant_id, id)
);
CREATE INDEX pur_receipt_lines_order_line_idx ON app.pur_receipt_lines (tenant_id, order_line_id);
CALL app.enable_tenant_rls('app.pur_receipt_lines');
