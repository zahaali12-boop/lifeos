-- M5 slice 5.8a: sales returns (RMA) -- DOMAIN_MODEL §11 "sls_returns", POSTING_RULES §2 "Sales return (RMA receipt)",
-- A-163. A return is requested against a customer's posted shipment lines, authorised (or rejected), received into stock
-- through the stock engine as StockEntryTypes.SaleReturn applied to the shipment's own ledger entries (the goods come
-- back at the cost they left with: Dr Inventory / Cr Cogs), inspected with a disposition per line (restock, quarantine,
-- scrap, repair), credited by credit notes against the invoices that billed the shipment (one per invoice, at the
-- invoice's own price and tax, on the SalesReturns role) and closed. A recalled, quarantined or expired lot comes back
-- on hold (the stock engine's own rule) and the line records it; a serial comes back as `returned` and goes on through
-- repair or restock (hard scenarios 12 and 13).

CREATE TABLE app.sls_returns (
  tenant_id           uuid NOT NULL REFERENCES control.tenants (id),
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  branch_id           uuid,
  number              text NOT NULL,
  partner_id          uuid NOT NULL,
  warehouse_id        uuid NOT NULL,
  request_date        date NOT NULL,
  posting_date        date,
  status              text NOT NULL DEFAULT 'requested' CHECK (status IN ('requested', 'authorised', 'received', 'inspected', 'credited', 'closed', 'rejected', 'reversed')),
  cost_policy         text NOT NULL DEFAULT 'original' CHECK (cost_policy IN ('original', 'current', 'specified')),
  reason              text,
  customer_reference  text,
  currency            text,
  total_cost          numeric(24,6) NOT NULL DEFAULT 0,
  total_credited      numeric(24,6) NOT NULL DEFAULT 0,
  credit_note_ids     jsonb NOT NULL DEFAULT '[]'::jsonb,
  stock_posting_id    uuid,
  journal_entry_id    uuid,
  reversal_posting_id uuid,
  reversal_reason     text,
  reversed_at         timestamptz,
  reversed_by         uuid,
  rejection_reason    text,
  authorised_at       timestamptz,
  authorised_by       uuid,
  received_at         timestamptz,
  received_by         uuid,
  inspected_at        timestamptz,
  inspected_by        uuid,
  credited_at         timestamptz,
  closed_at           timestamptz,
  notes               text,
  custom_fields       jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_by          uuid,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id)
);
CREATE INDEX sls_returns_company_idx ON app.sls_returns (tenant_id, company_id, status, request_date);
CREATE INDEX sls_returns_partner_idx ON app.sls_returns (tenant_id, partner_id, status);
CALL app.enable_tenant_rls('app.sls_returns');
CALL app.track_updated_at('app.sls_returns');

CREATE TABLE app.sls_return_lines (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  return_id          uuid NOT NULL,
  line_no            int NOT NULL,
  shipment_line_id   uuid NOT NULL,
  order_line_id      uuid NOT NULL,
  item_id            uuid NOT NULL,
  variant_id         uuid,
  quantity           numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id             uuid NOT NULL,
  bin_id             uuid,
  lot_id             uuid,
  lot_number         text,
  lot_status         text,
  serial_numbers     jsonb NOT NULL DEFAULT '[]'::jsonb,
  reason_code_id     uuid,
  reason_code        text,
  reason             text,
  disposition        text CHECK (disposition IS NULL OR disposition IN ('restock', 'quarantine', 'scrap', 'repair')),
  disposition_note   text,
  unit_cost          numeric(24,6),
  applied            jsonb NOT NULL DEFAULT '[]'::jsonb,
  cost_amount        numeric(24,6) NOT NULL DEFAULT 0,
  sle_id             uuid,
  sle_ids            jsonb NOT NULL DEFAULT '[]'::jsonb,
  scrap_sle_id       uuid,
  scrap_cost_amount  numeric(24,6) NOT NULL DEFAULT 0,
  qty_credited       numeric(24,9) NOT NULL DEFAULT 0,
  net_credited       numeric(24,6) NOT NULL DEFAULT 0,
  created_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, return_id, line_no),
  FOREIGN KEY (tenant_id, return_id) REFERENCES app.sls_returns (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, shipment_line_id) REFERENCES app.sls_shipment_lines (tenant_id, id),
  FOREIGN KEY (tenant_id, order_line_id) REFERENCES app.sls_order_lines (tenant_id, id)
);
CREATE INDEX sls_return_lines_shipment_line_idx ON app.sls_return_lines (tenant_id, shipment_line_id);
CALL app.enable_tenant_rls('app.sls_return_lines');

-- What came back from a shipment line (base units) and how much of that a credit note has credited; the difference is
-- returned stock that was never invoiced, which reduces what the line can still be invoiced.
ALTER TABLE app.sls_shipment_lines
  ADD COLUMN qty_returned numeric(24,9) NOT NULL DEFAULT 0,
  ADD COLUMN qty_return_credited numeric(24,9) NOT NULL DEFAULT 0;

-- A credit note issued by a return names it; it is reversed through the return, never on its own.
ALTER TABLE app.sls_invoices ADD COLUMN return_id uuid;
CREATE INDEX sls_invoices_return_idx ON app.sls_invoices (tenant_id, return_id) WHERE return_id IS NOT NULL;
