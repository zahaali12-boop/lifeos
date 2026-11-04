-- M3 slice 3.3: the costing engine (ADR-0008, value side).
-- Value entries are additive, immutable rows next to the quantity entries; FIFO applications link outbound entries to
-- the inbound layers they consumed; daily cost buckets cache the running quantity, value and average per cost scope;
-- adjustment runs log every re-application with the document that triggered it.

-- The stock ledger is append-only, so an inbound entry's remaining (unapplied) quantity is derived from its active
-- applications instead of being kept on the row.
ALTER TABLE app.inv_stock_ledger_entries DROP COLUMN remaining_quantity;
-- Whether an entry was costed at an expected cost is a fact of its value entries, not of the movement.
ALTER TABLE app.inv_stock_ledger_entries DROP COLUMN costed_at_expected;
-- The cost a document entered with the movement (per entered unit, functional currency) and, for returns, the entry
-- the movement reverses at its exact cost: inputs of the valuation, written once with the movement.
ALTER TABLE app.inv_stock_ledger_entries
  ADD COLUMN entered_unit_cost numeric(24, 10) CHECK (entered_unit_cost IS NULL OR entered_unit_cost >= 0),
  ADD COLUMN applies_to_sle_id uuid,
  ADD FOREIGN KEY (tenant_id, applies_to_sle_id) REFERENCES app.inv_stock_ledger_entries (tenant_id, id);

-- One row per cost scope (company, item, [warehouse]): the lock the engine takes before it values or re-applies, and
-- the "valuation pending" flag while a large re-application continues in the background.
CREATE TABLE app.inv_item_cost_scopes (
  tenant_id          uuid NOT NULL REFERENCES control.tenants (id),
  company_id         uuid NOT NULL,
  item_id            uuid NOT NULL,
  warehouse_id       uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  valuation_pending  boolean NOT NULL DEFAULT false,
  pending_run_id     uuid,
  last_cost          numeric(24, 10) NOT NULL DEFAULT 0,
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, company_id, item_id, warehouse_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_item_cost_scopes');
CALL app.track_updated_at('app.inv_item_cost_scopes');

-- Value entries: every cost figure of a movement is a sum of these rows, each with its reason.
CREATE TABLE app.inv_stock_value_entries (
  tenant_id              uuid NOT NULL,
  id                     uuid NOT NULL,
  sle_id                 uuid,            -- null for a revaluation of the stock on hand (no single movement)
  company_id             uuid NOT NULL,
  item_id                uuid NOT NULL,
  warehouse_id           uuid NOT NULL,
  posting_date           date NOT NULL,   -- the GL date (the movement's date, or the first open period when it was closed)
  valuation_date         date NOT NULL,   -- the movement's date
  value_type             text NOT NULL CHECK (value_type IN ('direct_cost', 'indirect_cost', 'expected_cost', 'expected_cost_reversal', 'revaluation', 'variance', 'rounding', 'cost_adjustment')),
  valued_quantity        numeric(24, 9) NOT NULL,
  unit_cost              numeric(24, 10) NOT NULL DEFAULT 0,
  cost_amount_actual     numeric(24, 6) NOT NULL DEFAULT 0,
  cost_amount_expected   numeric(24, 6) NOT NULL DEFAULT 0,
  currency               text NOT NULL,
  account_role           text NOT NULL DEFAULT 'Inventory',  -- Inventory or InventoryInTransit; a variance row names its variance account instead
  offset_role            text NOT NULL,   -- the account role on the other side (Cogs, GRNI, InventoryAdjustment, ...)
  offset_ref             uuid,            -- the subledger item of the offset when it is a control account (the receipt for GRNI, the counterpart item for Inventory)
  item_posting_group_id  uuid,
  gl_journal_entry_id    uuid,
  adjusts_sve_id         uuid,
  adjustment_run_id      uuid,
  source_document_type   text NOT NULL,
  source_document_id     uuid NOT NULL,
  reason                 jsonb NOT NULL DEFAULT '{}'::jsonb,
  costed_at_expected     boolean NOT NULL DEFAULT false,
  created_by             uuid,
  created_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  CHECK (sle_id IS NOT NULL OR value_type = 'revaluation'),
  FOREIGN KEY (tenant_id, sle_id) REFERENCES app.inv_stock_ledger_entries (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  FOREIGN KEY (tenant_id, adjusts_sve_id) REFERENCES app.inv_stock_value_entries (tenant_id, id)
);
CREATE INDEX inv_sve_sle_idx ON app.inv_stock_value_entries (tenant_id, sle_id);
CREATE INDEX inv_sve_item_idx ON app.inv_stock_value_entries (tenant_id, company_id, item_id, warehouse_id, posting_date);
CREATE INDEX inv_sve_run_idx ON app.inv_stock_value_entries (tenant_id, adjustment_run_id) WHERE adjustment_run_id IS NOT NULL;
CALL app.enable_tenant_rls('app.inv_stock_value_entries');
CALL app.make_append_only('app.inv_stock_value_entries');

-- FIFO applications: which inbound layers an outbound entry consumed, and for how much. A re-application supersedes
-- the earlier rows (they stay, marked with the run that replaced them) rather than deleting them.
CREATE TABLE app.inv_item_applications (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  item_id            uuid NOT NULL,
  outbound_sle_id    uuid NOT NULL,
  inbound_sle_id     uuid NOT NULL,
  quantity           numeric(24, 9) NOT NULL CHECK (quantity > 0),
  cost_amount        numeric(24, 6) NOT NULL DEFAULT 0,
  is_reapplication   boolean NOT NULL DEFAULT false,
  run_id             uuid,
  superseded_by      uuid,
  applied_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, outbound_sle_id) REFERENCES app.inv_stock_ledger_entries (tenant_id, id),
  FOREIGN KEY (tenant_id, inbound_sle_id) REFERENCES app.inv_stock_ledger_entries (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id)
);
CREATE INDEX inv_applications_inbound_idx ON app.inv_item_applications (tenant_id, inbound_sle_id) WHERE superseded_by IS NULL;
CREATE INDEX inv_applications_outbound_idx ON app.inv_item_applications (tenant_id, outbound_sle_id);
CREATE INDEX inv_applications_item_idx ON app.inv_item_applications (tenant_id, company_id, item_id);
CALL app.enable_tenant_rls('app.inv_item_applications');

-- Daily cost buckets per cost scope: the running quantity, value and average unit cost at the end of each day that
-- had a movement (the average method values the day's outbound entries at this day's average, ADR-0008).
CREATE TABLE app.inv_item_costs (
  tenant_id          uuid NOT NULL REFERENCES control.tenants (id),
  company_id         uuid NOT NULL,
  item_id            uuid NOT NULL,
  warehouse_id       uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  valuation_date     date NOT NULL,
  quantity           numeric(24, 9) NOT NULL DEFAULT 0,
  value              numeric(24, 6) NOT NULL DEFAULT 0,
  average_unit_cost  numeric(24, 10) NOT NULL DEFAULT 0,
  last_cost          numeric(24, 10) NOT NULL DEFAULT 0,
  standard_cost      numeric(24, 10),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, company_id, item_id, warehouse_id, valuation_date),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_item_costs');
CALL app.track_updated_at('app.inv_item_costs');

-- Every re-application is logged with what triggered it, what it touched and what it posted.
CREATE TABLE app.inv_cost_adjustment_runs (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  item_id                 uuid NOT NULL,
  warehouse_id            uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  trigger_kind            text NOT NULL,
  trigger_document_type   text NOT NULL,
  trigger_document_id     uuid NOT NULL,
  trigger_sle_id          uuid,
  from_date               date NOT NULL,
  status                  text NOT NULL DEFAULT 'running' CHECK (status IN ('queued', 'running', 'completed', 'failed')),
  entries_walked          int NOT NULL DEFAULT 0,
  entries_reapplied       int NOT NULL DEFAULT 0,
  value_entries_created   int NOT NULL DEFAULT 0,
  journal_entries_posted  int NOT NULL DEFAULT 0,
  amount_adjusted         numeric(24, 6) NOT NULL DEFAULT 0,
  job_id                  uuid,
  error                   text,
  started_by              uuid,
  started_at              timestamptz NOT NULL DEFAULT now(),
  completed_at            timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, trigger_sle_id) REFERENCES app.inv_stock_ledger_entries (tenant_id, id)
);
CREATE INDEX inv_cost_runs_item_idx ON app.inv_cost_adjustment_runs (tenant_id, company_id, item_id, started_at DESC);
CALL app.enable_tenant_rls('app.inv_cost_adjustment_runs');

-- Standard cost versions with effective dates; a new version revalues the stock on hand at its effective date.
CREATE TABLE app.inv_standard_cost_versions (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  company_id           uuid NOT NULL,
  item_id              uuid NOT NULL,
  standard_cost        numeric(24, 10) NOT NULL CHECK (standard_cost >= 0),
  effective_from       date NOT NULL,
  reason               text,
  revaluation_run_id   uuid,
  approved_by          uuid,
  created_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, item_id, effective_from),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_standard_cost_versions');
CALL app.make_append_only('app.inv_standard_cost_versions');
