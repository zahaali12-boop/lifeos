-- M3 slice 3.4: adjustment documents with reason codes and approval, revaluations and NRV write-downs, one-step
-- transfers and shortage handling on receipt, assembly builds.

-- A document may name the account its movement offsets (a reason code's override); the engine honours it.
ALTER TABLE app.inv_stock_ledger_entries ADD COLUMN offset_role_override text;
-- The offset of a value entry may belong to another item (an assembly's components offset the assembly's stock).
ALTER TABLE app.inv_stock_value_entries ADD COLUMN offset_posting_group_id uuid;

CREATE TABLE app.inv_reason_codes (
  tenant_id              uuid NOT NULL REFERENCES control.tenants (id),
  id                     uuid NOT NULL,
  code                   text NOT NULL,
  name_i18n              jsonb NOT NULL DEFAULT '{}'::jsonb,
  applies_to             text NOT NULL CHECK (applies_to IN ('adjustment', 'count', 'return', 'scrap', 'write_off', 'shortage')),
  account_role_override  text,
  requires_note          boolean NOT NULL DEFAULT false,
  is_active              boolean NOT NULL DEFAULT true,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.inv_reason_codes');
CALL app.track_updated_at('app.inv_reason_codes');

CREATE TABLE app.inv_adjustments (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  number             text,
  warehouse_id       uuid NOT NULL,
  posting_date       date NOT NULL,
  kind               text NOT NULL CHECK (kind IN ('positive', 'negative', 'scrap', 'opening')),
  status             text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'pending_approval', 'approved', 'posted', 'rejected', 'cancelled')),
  reference          text,
  notes              text,
  custom_fields      jsonb NOT NULL DEFAULT '{}'::jsonb,
  stock_posting_id   uuid,
  journal_entry_id   uuid,
  submitted_by       uuid,
  submitted_at       timestamptz,
  approved_by        uuid,
  approved_at        timestamptz,
  rejection_reason   text,
  posted_by          uuid,
  posted_at          timestamptz,
  created_by         uuid,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id)
);
CREATE UNIQUE INDEX inv_adjustments_number_idx ON app.inv_adjustments (tenant_id, company_id, number) WHERE number IS NOT NULL;
CREATE INDEX inv_adjustments_company_idx ON app.inv_adjustments (tenant_id, company_id, status);
CALL app.enable_tenant_rls('app.inv_adjustments');
CALL app.track_updated_at('app.inv_adjustments');

CREATE TABLE app.inv_adjustment_lines (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  adjustment_id    uuid NOT NULL,
  line_no          int NOT NULL,
  item_id          uuid NOT NULL,
  variant_id       uuid,
  bin_id           uuid,
  lot_id           uuid,
  serial_id        uuid,
  quantity         numeric(24, 9) NOT NULL CHECK (quantity > 0),
  uom_id           uuid NOT NULL,
  unit_cost        numeric(24, 10) CHECK (unit_cost IS NULL OR unit_cost >= 0),
  reason_code_id   uuid NOT NULL,
  note             text,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, adjustment_id, line_no),
  FOREIGN KEY (tenant_id, adjustment_id) REFERENCES app.inv_adjustments (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, bin_id) REFERENCES app.inv_bins (tenant_id, id),
  FOREIGN KEY (tenant_id, uom_id) REFERENCES app.org_uoms (tenant_id, id),
  FOREIGN KEY (tenant_id, reason_code_id) REFERENCES app.inv_reason_codes (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_adjustment_lines');

-- Revaluations: an NRV write-down (IAS 2) or a manual revaluation of the stock on hand at a date, per item and scope.
CREATE TABLE app.inv_revaluations (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  number             text,
  posting_date       date NOT NULL,
  kind               text NOT NULL CHECK (kind IN ('nrv_writedown', 'manual')),
  status             text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'posted', 'cancelled')),
  reference          text,
  notes              text,
  run_ids            uuid[] NOT NULL DEFAULT '{}',
  posted_by          uuid,
  posted_at          timestamptz,
  created_by         uuid,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE UNIQUE INDEX inv_revaluations_number_idx ON app.inv_revaluations (tenant_id, company_id, number) WHERE number IS NOT NULL;
CALL app.enable_tenant_rls('app.inv_revaluations');
CALL app.track_updated_at('app.inv_revaluations');

CREATE TABLE app.inv_revaluation_lines (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  revaluation_id     uuid NOT NULL,
  line_no            int NOT NULL,
  item_id            uuid NOT NULL,
  warehouse_id       uuid,                                  -- null when the company costs per company
  quantity           numeric(24, 9) NOT NULL DEFAULT 0,     -- on hand at the posting date, computed at posting
  current_unit_cost  numeric(24, 10) NOT NULL DEFAULT 0,    -- computed at posting
  new_unit_cost      numeric(24, 10) NOT NULL CHECK (new_unit_cost >= 0),
  amount             numeric(24, 6) NOT NULL DEFAULT 0,     -- (new − current) × quantity, computed at posting
  note               text,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, revaluation_id, line_no),
  FOREIGN KEY (tenant_id, revaluation_id) REFERENCES app.inv_revaluations (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_revaluation_lines');

-- Transfers: one step (straight into the destination) or two steps (through transit); shortages on receipt.
ALTER TABLE app.inv_transfers ADD COLUMN kind text NOT NULL DEFAULT 'two_step' CHECK (kind IN ('one_step', 'two_step'));
ALTER TABLE app.inv_transfers ADD COLUMN shortage_posting_ids uuid[] NOT NULL DEFAULT '{}';
ALTER TABLE app.inv_transfer_lines ADD COLUMN qty_shortage numeric(24, 9) NOT NULL DEFAULT 0 CHECK (qty_shortage >= 0);
DO $$
DECLARE received_check text;
BEGIN
  SELECT conname INTO received_check FROM pg_constraint
  WHERE conrelid = 'app.inv_transfer_lines'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%qty_received <= qty_shipped%';
  IF received_check IS NOT NULL THEN
    EXECUTE format('ALTER TABLE app.inv_transfer_lines DROP CONSTRAINT %I', received_check);
  END IF;
END $$;
ALTER TABLE app.inv_transfer_lines ADD CONSTRAINT inv_transfer_lines_received_check CHECK (qty_received >= 0 AND qty_received + qty_shortage <= qty_shipped);

-- Assembly builds: components consumed, the assembly item produced, valued by the costing engine.
CREATE TABLE app.inv_assemblies (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  number             text,
  bom_id             uuid,
  output_item_id     uuid NOT NULL,
  output_variant_id  uuid,
  output_qty         numeric(24, 9) NOT NULL CHECK (output_qty > 0),
  output_uom_id      uuid NOT NULL,
  output_bin_id      uuid,
  warehouse_id       uuid NOT NULL,
  posting_date       date NOT NULL,
  status             text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'posted', 'cancelled')),
  reference          text,
  notes              text,
  stock_posting_id   uuid,
  journal_entry_id   uuid,
  posted_by          uuid,
  posted_at          timestamptz,
  created_by         uuid,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, output_item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, output_variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, output_uom_id) REFERENCES app.org_uoms (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  FOREIGN KEY (tenant_id, bom_id) REFERENCES app.itm_boms (tenant_id, id)
);
CREATE UNIQUE INDEX inv_assemblies_number_idx ON app.inv_assemblies (tenant_id, company_id, number) WHERE number IS NOT NULL;
CALL app.enable_tenant_rls('app.inv_assemblies');
CALL app.track_updated_at('app.inv_assemblies');

CREATE TABLE app.inv_assembly_lines (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  assembly_id         uuid NOT NULL,
  line_no             int NOT NULL,
  component_item_id   uuid NOT NULL,
  component_variant_id uuid,
  quantity            numeric(24, 9) NOT NULL CHECK (quantity > 0),
  uom_id              uuid NOT NULL,
  bin_id              uuid,
  lot_id              uuid,
  serial_id           uuid,
  tracking            jsonb NOT NULL DEFAULT '{}'::jsonb,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, assembly_id, line_no),
  FOREIGN KEY (tenant_id, assembly_id) REFERENCES app.inv_assemblies (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, component_item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, component_variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, uom_id) REFERENCES app.org_uoms (tenant_id, id),
  FOREIGN KEY (tenant_id, bin_id) REFERENCES app.inv_bins (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_assembly_lines');
