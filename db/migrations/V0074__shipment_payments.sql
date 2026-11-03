-- Payments made from a shipment (trader edition, ADR-0035 §11, A-193): every payment the trader records on a
-- shipment is a posted supplier payment or advance in Banking; this table keeps which shipment, which cost and which
-- advance item it is, and how much of the advance has been applied to the invoice.
ALTER TABLE app.pur_shipment_costs
  ADD COLUMN landed_cost_charge_id uuid,
  ADD COLUMN invoice_id            uuid;

CREATE TABLE app.pur_shipment_payments (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  shipment_id      uuid NOT NULL,
  line_no          int NOT NULL CHECK (line_no >= 1),
  kind             text NOT NULL CHECK (kind IN ('goods', 'cost')),
  cost_id          uuid,
  partner_id       uuid NOT NULL,
  bank_account_id  uuid NOT NULL,
  payment_id       uuid NOT NULL,
  payment_number   text NOT NULL,
  payment_kind     text NOT NULL CHECK (payment_kind IN ('supplier_advance', 'supplier_payment')),
  currency         text NOT NULL,
  amount_tc        numeric(24,6) NOT NULL CHECK (amount_tc > 0),
  amount_rc        numeric(24,6) NOT NULL,
  paid_on          date NOT NULL,
  advance_item_id  uuid,
  applied_tc       numeric(24,6) NOT NULL DEFAULT 0 CHECK (applied_tc >= 0 AND applied_tc <= amount_tc),
  created_by       uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, shipment_id, line_no),
  FOREIGN KEY (tenant_id, shipment_id) REFERENCES app.pur_shipments (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, cost_id) REFERENCES app.pur_shipment_costs (tenant_id, id),
  CHECK ((kind = 'cost') = (cost_id IS NOT NULL)),
  CHECK ((payment_kind = 'supplier_advance') = (advance_item_id IS NOT NULL))
);
CREATE INDEX pur_shipment_payments_shipment_idx ON app.pur_shipment_payments (tenant_id, shipment_id);
CALL app.enable_tenant_rls('app.pur_shipment_payments');
