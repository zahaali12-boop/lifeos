-- M5 slice 5.5a: shipments against confirmed sales orders (DOMAIN_MODEL §11, POSTING_RULES "sale_shipment").
-- A shipment ships a confirmed order's reserved lines (in full or in part) through the stock engine as
-- StockEntryTypes.SaleShipment, which the costing engine (roadmap 3.3) already values at FIFO/average and books to
-- Cost of goods sold / Inventory (AccountRoles.Cogs) -- nothing new to wire there, only the document that calls it.
-- Reservations made at order confirmation (5.4b) are consumed exactly as much as each line ships, so a partial
-- shipment leaves the remainder still reserved for a later one. Pick lists, packages, carrier detail beyond a plain
-- reference, and lot/serial-tracked shipment lines (the service refuses those explicitly) follow in 5.5b; this slice
-- is the shipment and its posting alone.

CREATE TABLE app.sls_shipments (
  tenant_id           uuid NOT NULL REFERENCES control.tenants (id),
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  branch_id           uuid,
  number              text NOT NULL,
  order_id            uuid NOT NULL,
  partner_id          uuid NOT NULL,
  warehouse_id        uuid NOT NULL,
  posting_date        date NOT NULL,
  status              text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'posted', 'reversed')),
  carrier             text,
  tracking_number     text,
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
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.sls_orders (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CREATE INDEX sls_shipments_order_idx ON app.sls_shipments (tenant_id, order_id, status);
CREATE INDEX sls_shipments_company_idx ON app.sls_shipments (tenant_id, company_id, status, posting_date);
CALL app.enable_tenant_rls('app.sls_shipments');
CALL app.track_updated_at('app.sls_shipments');

CREATE TABLE app.sls_shipment_lines (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  shipment_id     uuid NOT NULL,
  line_no         int NOT NULL,
  order_line_id   uuid NOT NULL,
  item_id         uuid NOT NULL,
  variant_id      uuid,
  quantity        numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id          uuid NOT NULL,
  quantity_base   numeric(24,9) NOT NULL CHECK (quantity_base > 0),
  bin_id          uuid,
  cogs_amount     numeric(24,6) NOT NULL DEFAULT 0,
  sle_id          uuid,
  sle_ids         jsonb NOT NULL DEFAULT '[]'::jsonb,
  created_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, shipment_id, line_no),
  FOREIGN KEY (tenant_id, shipment_id) REFERENCES app.sls_shipments (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, order_line_id) REFERENCES app.sls_order_lines (tenant_id, id)
);
CREATE INDEX sls_shipment_lines_order_line_idx ON app.sls_shipment_lines (tenant_id, order_line_id);
CALL app.enable_tenant_rls('app.sls_shipment_lines');
