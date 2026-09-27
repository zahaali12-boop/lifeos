-- M5 slice 5.4b: sales orders (DOMAIN_MODEL §11), converted from an accepted quotation or created directly.
-- Confirming an order reserves stock through app.inv_reservations (IStockReservations) and checks the customer's
-- credit exposure, routing an excess through the workflow engine's generic block/override mechanism exactly as
-- pur_invoices already do for match variance (ADR-0020) -- A-152 supersedes the Phase-0 sls_credit_holds sketch,
-- since a bespoke table would duplicate what app.wf_blocks/app.wf_overrides already record and log.
-- Line and order statuses include a few values nothing sets yet (partially_shipped, shipped, invoiced, closed,
-- fulfilled), reserved for 5.5-5.7 exactly as sls_quotations.status already reserved 'converted' ahead of this slice.

CREATE TABLE app.sls_orders (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  branch_id             uuid,
  number                text NOT NULL,
  partner_id            uuid NOT NULL,
  quotation_id          uuid,
  currency              text NOT NULL,
  order_date            date NOT NULL,
  pricing_date          date NOT NULL,
  price_list_id         uuid,
  warehouse_id          uuid NOT NULL,
  status                text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'on_hold', 'confirmed', 'partially_shipped', 'shipped', 'invoiced', 'closed', 'cancelled')),
  block_kind            text,
  block_id              uuid,
  block_reason          text,
  override_id           uuid,
  total_net             numeric(24,6) NOT NULL DEFAULT 0,
  total_tax             numeric(24,6) NOT NULL DEFAULT 0,
  total_gross           numeric(24,6) NOT NULL DEFAULT 0,
  customer_snapshot     jsonb NOT NULL DEFAULT '{}'::jsonb,
  notes                 text,
  custom_fields         jsonb NOT NULL DEFAULT '{}'::jsonb,
  confirmed_at          timestamptz,
  cancelled_at          timestamptz,
  cancel_reason         text,
  created_by            uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, quotation_id) REFERENCES app.sls_quotations (tenant_id, id),
  FOREIGN KEY (tenant_id, price_list_id) REFERENCES app.prc_price_lists (tenant_id, id)
);
CREATE INDEX sls_orders_status_idx ON app.sls_orders (tenant_id, company_id, status);
CREATE INDEX sls_orders_customer_idx ON app.sls_orders (tenant_id, partner_id);
CALL app.enable_tenant_rls('app.sls_orders');
CALL app.track_updated_at('app.sls_orders');

CREATE TABLE app.sls_order_lines (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  order_id          uuid NOT NULL,
  line_no           int NOT NULL,
  item_id           uuid NOT NULL,
  variant_id        uuid,
  description       text,
  quantity          numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id            uuid NOT NULL,
  quantity_base     numeric(24,9) NOT NULL CHECK (quantity_base > 0),
  unit_price        numeric(24,6) NOT NULL DEFAULT 0,
  discount_pct      numeric(9,6) NOT NULL DEFAULT 0 CHECK (discount_pct >= 0 AND discount_pct <= 100),
  discount_amount   numeric(24,6) NOT NULL DEFAULT 0,
  tax_code_id       uuid,
  tax_rate_pct      numeric(9,6) NOT NULL DEFAULT 0,
  tax_reverse_charge boolean NOT NULL DEFAULT false,
  tax_recoverable   boolean NOT NULL DEFAULT true,
  tax_reason        text,
  net_amount        numeric(24,6) NOT NULL DEFAULT 0,
  tax_amount        numeric(24,6) NOT NULL DEFAULT 0,
  promotion_id      uuid,
  price_breakdown   jsonb NOT NULL DEFAULT '[]'::jsonb,
  warehouse_id      uuid,
  qty_reserved      numeric(24,9) NOT NULL DEFAULT 0,
  qty_shipped       numeric(24,9) NOT NULL DEFAULT 0,
  qty_invoiced      numeric(24,9) NOT NULL DEFAULT 0,
  qty_cancelled     numeric(24,9) NOT NULL DEFAULT 0,
  status            text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'backordered', 'fulfilled', 'cancelled')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, order_id, line_no),
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.sls_orders (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX sls_order_lines_item_idx ON app.sls_order_lines (tenant_id, item_id);
CALL app.enable_tenant_rls('app.sls_order_lines');
