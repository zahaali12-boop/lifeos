-- M3 slice 3.7: replenishment — the planning run and its purchase suggestions, each explaining its arithmetic.

CREATE TABLE app.inv_replenishment_runs (
  tenant_id              uuid NOT NULL,
  id                     uuid NOT NULL,
  company_id             uuid NOT NULL,
  warehouse_id           uuid,                     -- null: every warehouse of the company
  ran_at                 timestamptz NOT NULL DEFAULT now(),
  as_of                  date NOT NULL,
  items_checked          int NOT NULL DEFAULT 0,
  suggestions_created    int NOT NULL DEFAULT 0,
  suggestions_refreshed  int NOT NULL DEFAULT 0,
  suggestions_closed     int NOT NULL DEFAULT 0,
  started_by             uuid,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_replenishment_runs');

CREATE TABLE app.inv_replenishment_suggestions (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  item_id                 uuid NOT NULL,
  warehouse_id            uuid NOT NULL,
  run_id                  uuid,
  suggested_qty           numeric(24, 9) NOT NULL CHECK (suggested_qty > 0),
  suggested_supplier_id   uuid,
  needed_by               date,
  explanation             jsonb NOT NULL DEFAULT '{}'::jsonb,
  status                  text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'accepted', 'dismissed', 'superseded')),
  accepted_qty            numeric(24, 9),
  accepted_supplier_id    uuid,
  purchase_order_line_id  uuid,
  decision_note           text,
  decided_by              uuid,
  decided_at              timestamptz,
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  FOREIGN KEY (tenant_id, run_id) REFERENCES app.inv_replenishment_runs (tenant_id, id)
);
-- One open suggestion per item and warehouse: a later run refreshes it instead of adding another.
CREATE UNIQUE INDEX inv_replenishment_open_idx ON app.inv_replenishment_suggestions (tenant_id, company_id, item_id, warehouse_id) WHERE status = 'open';
CALL app.enable_tenant_rls('app.inv_replenishment_suggestions');
CALL app.track_updated_at('app.inv_replenishment_suggestions');
