-- Trader edition, T2 (ADR-0035 §4, A-187): a shipment is a purchase order's journey. The order keeps its own status;
-- the shipment adds the stages with their dates and notes, the costs along the way in the currency and at the rate
-- actually paid, what was paid for the goods in the company's currency, and the receipt, landed-cost document and
-- invoice the arrival produced through the purchasing services.
CREATE TABLE app.pur_shipments (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  order_id            uuid NOT NULL,
  status              text NOT NULL DEFAULT 'ordered' CHECK (status IN ('ordered', 'in_production', 'shipped', 'at_port', 'customs', 'received', 'cancelled')),
  expected_arrival    date,
  tracking_reference  text,
  notes               text,
  paid_amount_rc      numeric(24,6) CHECK (paid_amount_rc IS NULL OR paid_amount_rc >= 0),
  paid_on             date,
  receipt_id          uuid,
  landed_cost_id      uuid,
  invoice_id          uuid,
  created_by          uuid,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, order_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.pur_orders (tenant_id, id),
  FOREIGN KEY (tenant_id, receipt_id) REFERENCES app.pur_receipts (tenant_id, id),
  FOREIGN KEY (tenant_id, landed_cost_id) REFERENCES app.pur_landed_cost_docs (tenant_id, id),
  FOREIGN KEY (tenant_id, invoice_id) REFERENCES app.pur_invoices (tenant_id, id)
);
CREATE INDEX pur_shipments_company_idx ON app.pur_shipments (tenant_id, company_id, status, updated_at);
CALL app.enable_tenant_rls('app.pur_shipments');
CALL app.track_updated_at('app.pur_shipments');

CREATE TABLE app.pur_shipment_events (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  shipment_id   uuid NOT NULL,
  status        text NOT NULL,
  occurred_on   date NOT NULL,
  note          text,
  created_by    uuid,
  created_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, shipment_id) REFERENCES app.pur_shipments (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.pur_shipment_events');

CREATE TABLE app.pur_shipment_costs (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  shipment_id     uuid NOT NULL,
  line_no         int NOT NULL,
  charge_type_id  uuid NOT NULL,
  description     text,
  amount          numeric(24,6) NOT NULL CHECK (amount > 0),
  currency        text NOT NULL,
  exchange_rate   numeric(24,12) NOT NULL CHECK (exchange_rate > 0),
  amount_rc       numeric(24,6) NOT NULL,
  paid_on         date,
  partner_id      uuid,
  created_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, shipment_id, line_no),
  FOREIGN KEY (tenant_id, shipment_id) REFERENCES app.pur_shipments (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, charge_type_id) REFERENCES app.pur_charge_types (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pur_shipment_costs');
